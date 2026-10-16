param([string]$Origin = 'http://127.0.0.1:4200')
$ErrorActionPreference = 'Stop'
# Adds the public PKCE client of the Product Workspace (factory-web) to the running local
# factory-dev realm; idempotent. Admin credentials come from ignored artifacts/keycloak and are never
# printed. Fresh realms already contain the client (Start-FactoryKeycloak.ps1).
if ($Origin -notmatch '^http://(127\.0\.0\.1|localhost):[0-9]{2,5}$') { throw 'Origin must be a loopback http origin.' }
$settings = Get-Content ([IO.Path]::GetFullPath("$PSScriptRoot/../artifacts/keycloak/development.json")) -Raw | ConvertFrom-Json
$base = 'http://127.0.0.1:58080'
$token = (Invoke-RestMethod -Method Post -Uri "$base/realms/master/protocol/openid-connect/token" -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'password'; client_id = 'admin-cli'; username = 'factory-local-admin'; password = $settings.AdminPassword }).access_token
$headers = @{ Authorization = "Bearer $token" }
$existing = Invoke-RestMethod -Uri "$base/admin/realms/factory-dev/clients?clientId=factory-web" -Headers $headers
if (@($existing).Count -gt 0) { Write-Output 'factory-web client already registered.'; return }
function Mapper([string]$name, [string]$type, [hashtable]$config) { @{ name = $name; protocol = 'openid-connect'; protocolMapper = $type; config = $config } }
$client = @{
    clientId = 'factory-web'; enabled = $true; protocol = 'openid-connect'; publicClient = $true; standardFlowEnabled = $true
    directAccessGrantsEnabled = $false; implicitFlowEnabled = $false; serviceAccountsEnabled = $false; fullScopeAllowed = $false
    redirectUris = @("$Origin/*"); webOrigins = @($Origin)
    attributes = @{ 'pkce.code.challenge.method' = 'S256'; 'post.logout.redirect.uris' = "$Origin/*"; 'standard.token.exchange.enabled' = 'false' }
    protocolMappers = @(
        (Mapper 'factory-api-audience' 'oidc-audience-mapper' @{ 'included.client.audience' = 'factory-api'; 'access.token.claim' = 'true'; 'id.token.claim' = 'false' }),
        (Mapper 'factory-kind' 'oidc-hardcoded-claim-mapper' @{ 'claim.name' = 'factory_kind'; 'claim.value' = 'Human'; 'jsonType.label' = 'String';
            'access.token.claim' = 'true'; 'id.token.claim' = 'false'; 'userinfo.token.claim' = 'false' })
    )
}
Invoke-RestMethod -Method Post -Uri "$base/admin/realms/factory-dev/clients" -Headers $headers -ContentType 'application/json' `
    -Body ($client | ConvertTo-Json -Depth 8) | Out-Null
Write-Output "factory-web client registered for $Origin."
