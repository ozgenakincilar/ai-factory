param(
    [Parameter(Mandatory,ParameterSetName='Plan')][switch]$Plan,
    [Parameter(Mandatory,ParameterSetName='Plan')][string]$Target,
    [Parameter(Mandatory,ParameterSetName='Plan')][string]$Evidence,
    [Parameter(Mandatory,ParameterSetName='Plan')][ValidatePattern('\A[0-9a-f]{64}\z')][string]$EvidenceSha256,
    [Parameter(Mandatory,ParameterSetName='Apply')][switch]$Apply,
    [Parameter(Mandatory,ParameterSetName='Apply')][string]$PlanDirectory,
    [Parameter(Mandatory,ParameterSetName='Apply')][ValidatePattern('\A[0-9a-f]{64}\z')][string]$Approve,
    [Parameter(Mandatory,ParameterSetName='Apply')][string]$ApprovedBy,
    [Parameter(ParameterSetName='Apply')][switch]$AllowDestructive
)
$ErrorActionPreference='Stop'
# M13A.1 staging deployment in two human-separated steps:
#   -Plan   builds the release of a verified development job in the offline sandbox, runs terraform plan
#           in the pinned Terraform container and prints the plan and its binding SHA-256;
#   -Apply  applies exactly that saved plan after the human passes the same SHA-256, then checks /health.
# Needs Docker, the runner image (scripts/Build-FactoryRunner.ps1) and the deployment identity in the
# Factory.Worker User Secrets (scripts/Initialize-AzureStaging.ps1). Plans go to artifacts/deployments.
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
function Full($path){$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)}
if(-not(Test-Path "$repo/artifacts/runner/image.json")){throw 'Build the runner image first (scripts/Build-FactoryRunner.ps1).'}
$values=@{
    'DOTNET_ENVIRONMENT'='Development'
    'Templates__Root'="$repo/templates"
    'Verification__RunnerImage'=(Get-Content "$repo/artifacts/runner/image.json" -Raw | ConvertFrom-Json).ImageId
    'Deployment__ModulesRoot'="$repo/deploy"
    'Deployment__PluginCache'="$repo/artifacts/terraform/plugin-cache"
}
if($Plan){
    $arguments=@('deploy','plan','--target',(Full $Target),'--evidence',(Full $Evidence),
        '--evidence-sha256',$EvidenceSha256,'--out',"$repo/artifacts/deployments/plans")
} else {
    $arguments=@('deploy','apply','--plan',(Full $PlanDirectory),'--approve',$Approve,'--approved-by',$ApprovedBy)
    if($AllowDestructive){$arguments+='--allow-destructive'}
}
$saved=@{}
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    dotnet run --project "$repo/src/Factory.Worker" -p:OutputPath=bin/Milestone7/net10.0/ -- @arguments
    if($LASTEXITCODE){throw "Deployment command failed (exit $LASTEXITCODE)."}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
