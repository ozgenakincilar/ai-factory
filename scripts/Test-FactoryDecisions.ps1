param([switch]$IncludeKeycloak, [string]$Filter = '', [string]$Database = 'factory_review_5a')
$ErrorActionPreference='Stop'
if($Database -notmatch '^factory_[a-z0-9_]+$'){throw 'Invalid local verification database name.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$reader=Get-Content "$repo/artifacts/review-postgres/authorization.json" -Raw | ConvertFrom-Json
$decision=Get-Content "$repo/artifacts/review-postgres/decisions.json" -Raw | ConvertFrom-Json
$values=@{
    FACTORY_AUTH_OWNER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_owner;Password=$($db.OwnerPassword)"
    FACTORY_AUTH_READER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_authorization_reader;Password=$($reader.ReaderPassword)"
    FACTORY_REVIEW_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_review_writer;Password=$($db.WriterPassword)"
    FACTORY_DECISION_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_decision_runtime;Password=$($decision.Password)"
    FACTORY_KEYCLOAK_SETTINGS=$null; FACTORY_KEYCLOAK_HUMAN_TOKENS=$null
}
if($IncludeKeycloak){$values.FACTORY_KEYCLOAK_SETTINGS="$repo/artifacts/keycloak/development.json";$values.FACTORY_KEYCLOAK_HUMAN_TOKENS="$repo/artifacts/keycloak/human-tokens.json"}
$saved=@{}
Push-Location $repo
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    dotnet build -p:OutputPath=bin/Milestone5B2/net10.0/
    if($LASTEXITCODE){throw 'Build failed.'}
    $testArgs=@('test','--no-build','--no-restore','-p:OutputPath=bin/Milestone5B2/net10.0/','--logger','trx','--results-directory','artifacts/decision-tests')
    if($Filter){$testArgs+=@('--filter',$Filter)}
    & dotnet @testArgs
    if($LASTEXITCODE){throw 'Tests failed.'}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])};Pop-Location}
