param([Parameter(Mandatory)][string]$Database, [int]$Port = 5168)
$ErrorActionPreference = 'Stop'
# Local Factory API for the Product Workspace (src/Factory.Web, http://127.0.0.1:4200) against an
# initialized isolated database: restricted runtime logins only, loopback HTTP, factory-web client.
# The web dev server proxies /api and /hubs here. Never the owner login; never factory_review_5a.
if ($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z' -or $Database -eq 'factory_review_5a') { throw 'Invalid isolated database name.' }
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$db = Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$reader = Get-Content "$repo/artifacts/review-postgres/authorization.json" -Raw | ConvertFrom-Json
$decision = Get-Content "$repo/artifacts/review-postgres/decisions.json" -Raw | ConvertFrom-Json
$lifecycle = Get-Content "$repo/artifacts/review-postgres/lifecycle.json" -Raw | ConvertFrom-Json
$base = "Host=127.0.0.1;Port=$($db.Port);Database=$Database"
$values = @{
    'ConnectionStrings__Reviews' = "$base;Username=factory_decision_runtime;Password=$($decision.Password)"
    'ConnectionStrings__Authorization' = "$base;Username=factory_authorization_reader;Password=$($reader.ReaderPassword)"
    'ConnectionStrings__Lifecycle' = "$base;Username=factory_lifecycle_runtime;Password=$($lifecycle.Password)"
    'Authentication__Authority' = 'http://127.0.0.1:58080/realms/factory-dev'
    'Authentication__Audience' = 'factory-api'; 'Authentication__HumanClient' = 'factory-dev-cli'; 'Authentication__ServiceClient' = 'factory-agent'
    'Authentication__WebClient' = 'factory-web'
    'Authentication__AllowDevelopmentLoopbackHttp' = 'true'
    'ASPNETCORE_ENVIRONMENT' = 'Development'
}
$saved = @{}
try {
    foreach ($key in $values.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $values[$key]) }
    dotnet run --project "$repo/src/Factory.Api" --no-launch-profile -p:OutputPath=bin/Workspace/net10.0/ -- --urls "http://127.0.0.1:$Port"
    if ($LASTEXITCODE) { throw 'Factory API exited with an error.' }
} finally { foreach ($key in $values.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) } }
