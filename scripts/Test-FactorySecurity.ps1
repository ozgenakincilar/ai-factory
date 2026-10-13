param([switch]$IncludeKeycloak)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$reader=Get-Content "$repo/artifacts/review-postgres/authorization.json" -Raw | ConvertFrom-Json
$names=@('FACTORY_AUTH_OWNER_CONNECTION','FACTORY_AUTH_READER_CONNECTION','FACTORY_REVIEW_TEST_CONNECTION','FACTORY_KEYCLOAK_SETTINGS','FACTORY_KEYCLOAK_HUMAN_TOKENS')
$saved=@{}; foreach($name in $names){$saved[$name]=[Environment]::GetEnvironmentVariable($name)}
Push-Location $repo
try {
    $env:FACTORY_AUTH_OWNER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=factory_review_5a;Username=factory_owner;Password=$($db.OwnerPassword)"
    $env:FACTORY_AUTH_READER_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=factory_review_5a;Username=factory_authorization_reader;Password=$($reader.ReaderPassword)"
    $env:FACTORY_REVIEW_TEST_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=factory_review_5a;Username=factory_review_writer;Password=$($db.WriterPassword)"
    $env:FACTORY_KEYCLOAK_SETTINGS=$null; $env:FACTORY_KEYCLOAK_HUMAN_TOKENS=$null
    if($IncludeKeycloak){
        $env:FACTORY_KEYCLOAK_SETTINGS="$repo/artifacts/keycloak/development.json"
        $env:FACTORY_KEYCLOAK_HUMAN_TOKENS="$repo/artifacts/keycloak/human-tokens.json"
    }
    dotnet build -p:OutputPath=bin/Milestone5B1/net10.0/
    if($LASTEXITCODE){throw 'Build failed.'}
    dotnet test --no-restore -p:OutputPath=bin/Milestone5B1/net10.0/
    if($LASTEXITCODE){throw 'Tests failed.'}
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$saved[$name])}
    Pop-Location
}
