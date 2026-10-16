param([string]$Distribution = "$PSScriptRoot/../artifacts/keycloak/distribution/keycloak-26.8.0")
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot/Protect-FactoryDevelopmentSecrets.ps1"
$local = [IO.Path]::GetFullPath("$PSScriptRoot/../artifacts/keycloak")
if (Get-NetTCPConnection -LocalPort 58080 -State Listen -ErrorAction SilentlyContinue) { throw 'Keycloak port already in use.' }
if (-not (Test-Path "$Distribution/bin/kc.bat")) { throw 'Verified Keycloak 26.8.0 distribution required.' }
$settingsPath = Join-Path $local 'development.json'
if (-not (Test-Path $settingsPath)) {
    $settings = @{
        AdminPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        HumanPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        ServiceSecret = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
        HumanSubject = [Guid]::NewGuid().ToString(); ServiceSubject = [Guid]::NewGuid().ToString()
    }
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json))
    function Mappers([string]$kind) {
        @(
            @{name='factory-api-audience'; protocol='openid-connect'; protocolMapper='oidc-audience-mapper'; config=@{'included.client.audience'='factory-api'; 'access.token.claim'='true'; 'id.token.claim'='false'}}
            @{name='factory-kind'; protocol='openid-connect'; protocolMapper='oidc-hardcoded-claim-mapper'; config=@{'claim.name'='factory_kind'; 'claim.value'=$kind; 'jsonType.label'='String'; 'access.token.claim'='true'; 'id.token.claim'='false'; 'userinfo.token.claim'='false'}}
        )
    }
    $realm = @{
        realm='factory-dev'; enabled=$true; registrationAllowed=$false; resetPasswordAllowed=$false
        accessTokenLifespan=300; sslRequired='external'
        clients=@(
            @{clientId='factory-api'; enabled=$true; protocol='openid-connect'; bearerOnly=$true}
            @{clientId='factory-dev-cli'; enabled=$true; protocol='openid-connect'; publicClient=$true; standardFlowEnabled=$true
              directAccessGrantsEnabled=$false; implicitFlowEnabled=$false; serviceAccountsEnabled=$false; fullScopeAllowed=$false
              redirectUris=@('http://127.0.0.1:58081/callback'); webOrigins=@(); protocolMappers=@(Mappers 'Human')
              attributes=@{'pkce.code.challenge.method'='S256'; 'standard.token.exchange.enabled'='false'}}
            # Product Workspace (M12a): public PKCE client on the loopback dev origin; humans only.
            @{clientId='factory-web'; enabled=$true; protocol='openid-connect'; publicClient=$true; standardFlowEnabled=$true
              directAccessGrantsEnabled=$false; implicitFlowEnabled=$false; serviceAccountsEnabled=$false; fullScopeAllowed=$false
              redirectUris=@('http://127.0.0.1:4200/*'); webOrigins=@('http://127.0.0.1:4200'); protocolMappers=@(Mappers 'Human')
              attributes=@{'pkce.code.challenge.method'='S256'; 'post.logout.redirect.uris'='http://127.0.0.1:4200/*'; 'standard.token.exchange.enabled'='false'}}
            @{clientId='factory-agent'; enabled=$true; protocol='openid-connect'; publicClient=$false; secret=$settings.ServiceSecret
              standardFlowEnabled=$false; directAccessGrantsEnabled=$false; implicitFlowEnabled=$false; serviceAccountsEnabled=$true
              fullScopeAllowed=$false; protocolMappers=@(Mappers 'Service'); attributes=@{'standard.token.exchange.enabled'='false'}}
        )
        users=@(
            @{id=$settings.HumanSubject; username='factory-reviewer'; enabled=$true; firstName='Local'; lastName='Reviewer'; email='reviewer@example.invalid'; emailVerified=$true
              credentials=@(@{type='password'; value=$settings.HumanPassword; temporary=$false})}
            @{id=$settings.ServiceSubject; username='service-account-factory-agent'; enabled=$true; serviceAccountClientId='factory-agent'}
        )
    }
    New-Item -ItemType Directory -Force "$Distribution/data/import" | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath("$Distribution/data/import/factory-dev-realm.json"), ($realm | ConvertTo-Json -Depth 15))
}
$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$oldUser = $env:KC_BOOTSTRAP_ADMIN_USERNAME; $oldPassword = $env:KC_BOOTSTRAP_ADMIN_PASSWORD; $oldJava = $env:JAVA_HOME
try {
    $env:JAVA_HOME = Split-Path (Split-Path (Get-Command java.exe).Source)
    $env:KC_BOOTSTRAP_ADMIN_USERNAME = 'factory-local-admin'; $env:KC_BOOTSTRAP_ADMIN_PASSWORD = $settings.AdminPassword
    # No secret in command line. All HTTP interfaces bind explicitly to loopback.
    $process = Start-Process -FilePath "$Distribution/bin/kc.bat" -WorkingDirectory $Distribution -WindowStyle Hidden -PassThru `
        -ArgumentList @('start-dev','--http-host=127.0.0.1','--http-port=58080','--hostname=http://127.0.0.1:58080','--import-realm') `
        -RedirectStandardOutput "$local/server.log" -RedirectStandardError "$local/server-error.log"
    [IO.File]::WriteAllText((Join-Path $local 'launcher.pid'), [string]$process.Id)
} finally { $env:KC_BOOTSTRAP_ADMIN_USERNAME=$oldUser; $env:KC_BOOTSTRAP_ADMIN_PASSWORD=$oldPassword; $env:JAVA_HOME=$oldJava }
Write-Output 'Keycloak launch requested on 127.0.0.1:58080. Private development settings remain under ignored artifacts/keycloak.'
