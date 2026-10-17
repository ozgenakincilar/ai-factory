param(
    [Parameter(Mandatory)][ValidatePattern('\A[a-z][a-z0-9-]{1,30}[a-z0-9]\z')][string]$Application,
    [Parameter(Mandatory)][string]$AlertEmail,
    [ValidateSet('francecentral','swedencentral','italynorth','polandcentral','westeurope')][string]$Location = 'francecentral',
    [ValidateRange(1,50)][int]$BudgetUsd = 5,
    [ValidateRange(1,90)][int]$SecretDays = 30
)
$ErrorActionPreference='Stop'
# M13A operator bootstrap for one BYOC staging target, run once with the operator's own `az login`
# (subscription Owner). It creates what the deployment identity itself must not be able to create:
#   - the target resource group and a separate state resource group, storage account and a container
#     for this target only (Entra ID access only: shared keys, public blob access disabled; versioning);
#   - a service principal with Contributor on the target resource group and Storage Blob Data
#     Contributor on this target's state container, and nothing else; its secret expires after
#     -SecretDays and is written to the worker User Secrets, never printed;
#   - a monthly cost budget with e-mail alerts on the subscription (covers both resource groups);
#   - the resource provider registrations the module needs.
# Re-running is safe: existing resources are kept; the secret is replaced.
# Windows PowerShell 5.1 turns any stderr line of a native command (az prints warnings there) into an
# error that 'Stop' would make fatal, so success is judged by the exit code alone. AzTry tolerates failure.
$azCli=(Get-Command az -CommandType Application | Select-Object -First 1).Source
function AzTry {
    $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
    try { $out = & $azCli @args 2>$null; [pscustomobject]@{Exit=$LASTEXITCODE; Out=$out} } finally { $ErrorActionPreference=$previous }
}
function Invoke-AzCli {
    $previous=$ErrorActionPreference; $ErrorActionPreference='Continue'
    $errors=New-TemporaryFile
    try {
        $out = & $azCli @args 2>$errors
        if($LASTEXITCODE){ Get-Content $errors | Where-Object { $_ -notmatch '^WARNING' } | Write-Host; throw "az $($args[0]) $($args[1]) failed" }
        $out
    } finally { $ErrorActionPreference=$previous; Remove-Item $errors }
}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$account = Invoke-AzCli account show -o json | ConvertFrom-Json
$subscription=$account.id; $tenant=$account.tenantId
$hash=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("$subscription/$Application/staging"))) -replace '-','').ToLowerInvariant()
$suffix=$hash.Substring(0,8)
# West Europe refused new free-trial resources on 2026-10-17 (RequestDisallowedByAzure); France Central is
# the user's choice. The region code keeps resource group names unambiguous.
$code=@{francecentral='frc';swedencentral='sdc';italynorth='itn';polandcentral='plc';westeurope='weu'}[$Location]
$targetGroup="rg-$Application-staging-$code"
$stateGroup="rg-factory-tfstate-$code"
$stateAccount='stfactorytf'+$subscription.Replace('-','').Substring(0,12)
$container="tfstate-$Application-staging"
$identityName="factory-deploy-$Application-staging"

Write-Host "Subscription $($account.name) ($subscription), target $targetGroup in $Location"
foreach($ns in 'Microsoft.Web','Microsoft.DBforPostgreSQL','Microsoft.KeyVault','Microsoft.Storage'){
    Invoke-AzCli provider register --namespace $ns --wait | Out-Null
}
Invoke-AzCli group create --name $targetGroup --location $Location --tags factory-application=$Application factory-environment=staging -o none
Invoke-AzCli group create --name $stateGroup --location $Location --tags factory-purpose=terraform-state -o none
if((AzTry storage account show --name $stateAccount --resource-group $stateGroup --query name -o tsv).Exit){
    Invoke-AzCli storage account create --name $stateAccount --resource-group $stateGroup --location $Location --sku Standard_LRS --kind StorageV2 `
        --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false --https-only true -o none
}
Invoke-AzCli storage account blob-service-properties update --account-name $stateAccount --resource-group $stateGroup `
    --enable-versioning true --enable-delete-retention true --delete-retention-days 14 -o none
# Created through the management plane: the operator needs no data-plane role.
if((Invoke-AzCli storage container-rm exists --storage-account $stateAccount --resource-group $stateGroup --name $container --query exists -o tsv) -ne 'true'){
    Invoke-AzCli storage container-rm create --storage-account $stateAccount --resource-group $stateGroup --name $container --public-access off -o none
}
$containerScope = "$(Invoke-AzCli storage account show --name $stateAccount --resource-group $stateGroup --query id -o tsv)/blobServices/default/containers/$container"
$groupScope = Invoke-AzCli group show --name $targetGroup --query id -o tsv

$appId = Invoke-AzCli ad app list --display-name $identityName --query '[0].appId' -o tsv
if(-not $appId){ $appId = Invoke-AzCli ad app create --display-name $identityName --sign-in-audience AzureADMyOrg --query appId -o tsv }
$principal = Invoke-AzCli ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if(-not $principal){ $principal = Invoke-AzCli ad sp create --id $appId --query id -o tsv }
foreach($assignment in @(@('Contributor',$groupScope),@('Storage Blob Data Contributor',$containerScope))){
    $existing = Invoke-AzCli role assignment list --assignee $principal --role $assignment[0] --scope $assignment[1] --query 'length(@)' -o tsv
    if($existing -eq '0'){
        # A new service principal can take a short while to replicate.
        for($i=0;;$i++){
            if(-not (AzTry role assignment create --assignee-object-id $principal --assignee-principal-type ServicePrincipal --role $assignment[0] --scope $assignment[1] -o none).Exit){break}; if($i -ge 6){throw "Role assignment $($assignment[0]) failed."}; Start-Sleep -Seconds 10
        }
    }
}
$end=(Get-Date).ToUniversalTime().AddDays($SecretDays).ToString('yyyy-MM-ddTHH:mm:ssZ')
$secret = Invoke-AzCli ad app credential reset --id $appId --display-name 'factory-worker' --end-date $end --query password -o tsv
try {
    # Through standard input, so the secret never appears on a command line.
    @{'Azure:TenantId'=$tenant;'Azure:SubscriptionId'=$subscription;'Azure:ClientId'=$appId;'Azure:ClientSecret'=$secret} | ConvertTo-Json -Compress |
        dotnet user-secrets set --project "$repo/src/Factory.Worker" | Out-Null
    if($LASTEXITCODE){throw 'dotnet user-secrets failed.'}
} finally { $secret=$null }

$targetFile="$repo/artifacts/deployments/targets/$Application-staging.json"
New-Item -ItemType Directory -Force (Split-Path $targetFile) | Out-Null
[ordered]@{
    Application=$Application; Environment='staging'; Cloud='azure'; SubscriptionId=$subscription; TenantId=$tenant; Location=$Location
    ResourceGroup=$targetGroup; NameSuffix=$suffix; StateResourceGroup=$stateGroup; StateAccount=$stateAccount; StateContainer=$container
    StateKey="$Application-staging.tfstate"; DeploymentClientId=$appId; SecretExpiresUtc=$end; BudgetUsd=$BudgetUsd
} | ConvertTo-Json | Set-Content -Encoding utf8 $targetFile
Write-Host "Target written to artifacts/deployments/targets/$Application-staging.json; secret expires $end (User Secrets of Factory.Worker)."

$start=(Get-Date).ToUniversalTime().ToString('yyyy-MM-01T00:00:00Z')
$budget=@{properties=@{category='Cost';amount=$BudgetUsd;timeGrain='Monthly';timePeriod=@{startDate=$start;endDate='2028-12-31T00:00:00Z'};notifications=@{
    actual80=@{enabled=$true;operator='GreaterThanOrEqualTo';threshold=80;thresholdType='Actual';contactEmails=@($AlertEmail)}
    forecast100=@{enabled=$true;operator='GreaterThanOrEqualTo';threshold=100;thresholdType='Forecasted';contactEmails=@($AlertEmail)}}}} | ConvertTo-Json -Depth 8 -Compress
$body=New-TemporaryFile
try {
    [IO.File]::WriteAllText($body,$budget)
    $put = AzTry rest --method put --url "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.Consumption/budgets/factory-monthly?api-version=2023-11-01" --body "@$body" -o none
    if($put.Exit){ Write-Warning "The USD $BudgetUsd budget could not be created through the API; create it in the portal (Cost Management > Budgets)." }
    else { Write-Host "Budget factory-monthly: USD $BudgetUsd per month, alerts at 80% actual and 100% forecast." }
} finally { Remove-Item $body }
