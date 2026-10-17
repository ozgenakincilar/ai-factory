param([Parameter(Mandatory)][string]$Database, [ValidateRange(1,8)][int]$Concurrency = 1, [ValidateSet('openai','anthropic')][string]$Provider = 'openai', [switch]$Verification, [switch]$Development, [string]$StrongModel = '', [string]$GitHubRepository = '', [switch]$Deployment)
$ErrorActionPreference='Stop'
# Local development worker against a dedicated, initialized M7 database. Never the owner login.
if($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z' -or $Database -eq 'factory_review_5a'){throw 'Invalid isolated database name.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$worker=Get-Content "$repo/artifacts/review-postgres/orchestration.json" -Raw | ConvertFrom-Json
$values=@{
    'ConnectionStrings__Orchestration'="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_orchestration_worker;Password=$($worker.Password)"
    # The generic host loads Development User Secrets (OpenAI:ApiKey, Anthropic:ApiKey) only in Development.
    'DOTNET_ENVIRONMENT'='Development'
    'Generation__Provider'=$Provider
    'Worker__Concurrency'="$Concurrency"
}
# -Verification also runs M9.3 verification jobs in the Docker sandbox (Docker Desktop running and
# the runner image built by scripts/Build-FactoryRunner.ps1).
if($Verification){
    if(-not(Test-Path "$repo/artifacts/runner/image.json")){throw 'Build the runner image first (scripts/Build-FactoryRunner.ps1).'}
    $values['Verification__Enabled']='true'
    $values['Templates__Root']="$repo/templates"
    $values['Verification__RunnerImage']=(Get-Content "$repo/artifacts/runner/image.json" -Raw | ConvertFrom-Json).ImageId
}
# -Development also runs M10.3 development jobs (Developer Agent, gpt-5.4-mini, verified prices). The
# total cap is USD 2 minus the spend already recorded by scripts/Test-DeveloperAgentLive.ps1, so both
# paths share one USD 2 budget (user decision of 2026-10-16); the database adds its own recorded spend.
if($Development){
    if(-not $Verification){throw 'Development needs -Verification (template root and runner image).'}
    $spent=0; $ledger="$repo/artifacts/developer-live/ledger.json"
    if(Test-Path $ledger){foreach($run in @(Get-Content $ledger -Raw | ConvertFrom-Json)){$spent+=[long]$run.CostMicros}}
    $values['Development__Enabled']='true'
    $values['Development__Model']='gpt-5.4-mini'
    $values['Development__InputMicrosPerMillion']='750000'
    $values['Development__OutputMicrosPerMillion']='4500000'
    $values['Development__JobCapMicros']='400000'
    $values['Development__TotalCapMicros']="$(2000000-$spent)"
}
# -StrongModel maps the routing policy's Strong tier (Architecture, Planning; M8.5a) to an OpenAI model
# whose verified price is in scripts/model-prices.sql; without it every tier uses the default model.
if($StrongModel){
    if($StrongModel -cnotmatch '\A[a-z0-9.\-]{1,64}\z'){throw 'Invalid model name.'}
    $values['Generation__Tiers__Strong']="openai:$StrongModel"
}
# -GitHubRepository opens pull requests for verified development jobs (M11.3) with the repository-scoped
# token in the worker User Secrets (GitHub:Token). The Factory never merges.
if($GitHubRepository){
    if(-not $Development){throw 'Pull requests need -Development.'}
    if($GitHubRepository -cnotmatch '\A[A-Za-z0-9_.-]{1,39}/[A-Za-z0-9_.-]{1,100}\z'){throw 'Invalid repository.'}
    $values['GitHub__Repository']=$GitHubRepository
}
# -Deployment also plans and applies approved deployments (M13A.2) with the deployment identity in the
# worker User Secrets (Azure:*, scripts/Initialize-AzureStaging.ps1); targets are registered per project
# with scripts/Register-DeploymentTarget.ps1. Infrastructure changes only after an Owner/Admin approval.
if($Deployment){
    if(-not $Verification){throw 'Deployment needs -Verification (template root and runner image).'}
    $values['Deployment__Enabled']='true'
    $values['Deployment__ModulesRoot']="$repo/deploy"
    $values['Deployment__PluginCache']="$repo/artifacts/terraform/plugin-cache"
}
$saved=@{}
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    dotnet run --project "$repo/src/Factory.Worker" -p:OutputPath=bin/Milestone7/net10.0/
    if($LASTEXITCODE){throw 'Factory worker exited with an error.'}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
