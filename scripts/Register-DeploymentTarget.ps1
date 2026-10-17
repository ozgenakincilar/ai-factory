param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][ValidatePattern('\A[a-z0-9-]{3,60}\z')][string]$Project,
    [Parameter(Mandatory)][string]$TargetFile,
    [ValidatePattern('\A[a-z][a-z0-9-]{1,62}[a-z0-9]\z')][string]$Name = ''
)
$ErrorActionPreference='Stop'
# M13A.2 operator step (owner login): binds one bootstrapped target (artifacts/deployments/targets/*.json,
# written by scripts/Initialize-AzureStaging.ps1) to exactly one project. Rows never change; a changed
# target needs a new name. The descriptor holds no credentials.
if($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z' -or $Database -eq 'factory_review_5a'){throw 'Invalid isolated database name.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$file=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TargetFile)
$t=Get-Content $file -Raw | ConvertFrom-Json
if(-not $Name){$Name="$($t.Application)-$($t.Environment)"}
$descriptor=[ordered]@{
    cloud=$t.Cloud; environment=$t.Environment; application=$t.Application; subscriptionId=$t.SubscriptionId; tenantId=$t.TenantId
    location=$t.Location; resourceGroup=$t.ResourceGroup; nameSuffix=$t.NameSuffix; stateResourceGroup=$t.StateResourceGroup
    stateAccount=$t.StateAccount; stateContainer=$t.StateContainer; stateKey=$t.StateKey
} | ConvertTo-Json -Compress
foreach($value in @($Name,$t.Cloud,$t.Environment,$t.Application,$descriptor)){ if("$value".Contains("'") -or "$value".Contains('$')){throw 'Unexpected characters in the target.'} }
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$sql=New-TemporaryFile
try {
    [IO.File]::WriteAllText($sql, @"
INSERT INTO orchestration."DeploymentTargets" ("Name","ProjectId","Cloud","Environment","Application","Descriptor","CreatedAt")
VALUES ('$Name','$Project','$($t.Cloud)','$($t.Environment)','$($t.Application)','$descriptor'::jsonb,pg_catalog.clock_timestamp());
"@)
    $saved=$env:PGPASSWORD; $env:PGPASSWORD=$db.OwnerPassword
    & "$repo/artifacts/postgres/distribution/pgsql/bin/psql.exe" -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -q -f $sql
    if($LASTEXITCODE){throw 'Target registration failed.'}
} finally { $env:PGPASSWORD=$saved; Remove-Item $sql }
Write-Host "Target $Name registered for project $Project."
