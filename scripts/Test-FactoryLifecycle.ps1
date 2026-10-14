param([string]$Filter = '', [switch]$IncludeKeycloak, [string]$Database = 'factory_m6', [string]$LegacyDatabase = 'factory_m6_legacy', [string]$SdkDll = '')
$ErrorActionPreference='Stop'
if($Database -notmatch '^factory_[A-Za-z0-9_]+$'){throw 'Invalid isolated database name.'}
if($LegacyDatabase -notmatch '^factory_[A-Za-z0-9_]+$' -or $LegacyDatabase -eq 'factory_review_5a' -or $Database -eq 'factory_review_5a'){
    throw 'Use separate isolated M6 and historical-regression databases; the existing verification database is protected.'
}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$reader=Get-Content "$repo/artifacts/review-postgres/authorization.json" -Raw | ConvertFrom-Json
$decision=Get-Content "$repo/artifacts/review-postgres/decisions.json" -Raw | ConvertFrom-Json
$lifecycle=Get-Content "$repo/artifacts/review-postgres/lifecycle.json" -Raw | ConvertFrom-Json
$values=@{
    FACTORY_AUTH_OWNER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$LegacyDatabase;Username=factory_owner;Password=$($db.OwnerPassword)"
    FACTORY_AUTH_READER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$LegacyDatabase;Username=factory_authorization_reader;Password=$($reader.ReaderPassword)"
    FACTORY_REVIEW_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$LegacyDatabase;Username=factory_review_writer;Password=$($db.WriterPassword)"
    FACTORY_DECISION_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$LegacyDatabase;Username=factory_decision_runtime;Password=$($decision.Password)"
    FACTORY_LIFECYCLE_OWNER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_owner;Password=$($db.OwnerPassword)"
    FACTORY_LIFECYCLE_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_lifecycle_runtime;Password=$($lifecycle.Password)"
    FACTORY_KEYCLOAK_SETTINGS=$null; FACTORY_KEYCLOAK_HUMAN_TOKENS=$null
}
if($IncludeKeycloak){$values.FACTORY_KEYCLOAK_SETTINGS="$repo/artifacts/keycloak/development.json";$values.FACTORY_KEYCLOAK_HUMAN_TOKENS="$repo/artifacts/keycloak/human-tokens.json"}
$saved=@{}
Push-Location $repo
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    $sdkArgs=@();if($SdkDll){if(-not(Test-Path -LiteralPath $SdkDll)){throw 'Requested SDK CLI assembly does not exist.'};$sdkArgs=@('exec',$SdkDll)}
    & dotnet @sdkArgs build --no-restore -p:OutputPath=bin/Milestone6/net10.0/
    if($LASTEXITCODE){throw 'Build failed.'}
    $testArgs=@('test','--no-build','--no-restore','-p:OutputPath=bin/Milestone6/net10.0/','--logger','trx','--results-directory','artifacts/m6-tests')
    if($Filter){$testArgs+=@('--filter',$Filter)}
    & dotnet @sdkArgs @testArgs
    if($LASTEXITCODE){throw 'Tests failed.'}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])};Pop-Location}
