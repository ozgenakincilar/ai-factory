param([Parameter(Mandatory)][string]$Database, [ValidateRange(1,8)][int]$Concurrency = 1, [ValidateSet('openai','anthropic')][string]$Provider = 'openai', [switch]$Verification)
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
$saved=@{}
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    dotnet run --project "$repo/src/Factory.Worker" -p:OutputPath=bin/Milestone7/net10.0/
    if($LASTEXITCODE){throw 'Factory worker exited with an error.'}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
