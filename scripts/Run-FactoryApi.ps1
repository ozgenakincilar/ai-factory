$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$reader=Get-Content "$repo/artifacts/review-postgres/authorization.json" -Raw | ConvertFrom-Json
$values=@{
    'Authentication__Authority'='http://127.0.0.1:58080/realms/factory-dev'
    'Authentication__Audience'='factory-api'; 'Authentication__HumanClient'='factory-dev-cli'; 'Authentication__ServiceClient'='factory-agent'
    'Authentication__AllowDevelopmentLoopbackHttp'='true'
    'ConnectionStrings__Authorization'="Host=127.0.0.1;Port=$($db.Port);Database=factory_review_5a;Username=factory_authorization_reader;Password=$($reader.ReaderPassword)"
    'ASPNETCORE_ENVIRONMENT'='Development'
}
$saved=@{}
try {
    foreach($key in $values.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$values[$key])}
    # Explicit loopback binding; do not disturb the pre-existing API on port 5050.
    dotnet run --project "$repo/src/Factory.Api" --no-launch-profile -p:OutputPath=bin/Milestone5B1/net10.0/ -- --urls http://127.0.0.1:5168
    if($LASTEXITCODE){throw 'Factory API exited with an error.'}
} finally {foreach($key in $values.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])}}
