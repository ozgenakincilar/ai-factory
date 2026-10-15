param([string]$Database = 'factory_m6', [switch]$BaselineOnly)
$ErrorActionPreference='Stop'
# ASCII allowlist keeps SQL literals and connection values safe; preserve exact identifier case.
# PostgreSQL identifiers must fit 63 bytes (ASCII characters are one UTF-8 byte each).
if($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z' -or [Text.Encoding]::UTF8.GetByteCount($Database) -gt 63){throw 'Invalid isolated database name.'}
if($Database -eq 'factory_review_5a'){throw 'The existing verification database is protected; choose a separate M6 database.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
& "$PSScriptRoot/Protect-FactoryDevelopmentSecrets.ps1"
$settings=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$private="$repo/artifacts/review-postgres/lifecycle.json"
if(-not(Test-Path $private)){
    [IO.File]::WriteAllText($private,(@{Password=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'))}|ConvertTo-Json))
}
$workerPrivate="$repo/artifacts/review-postgres/orchestration.json"
if(-not(Test-Path $workerPrivate)){
    [IO.File]::WriteAllText($workerPrivate,(@{Password=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'))}|ConvertTo-Json))
}
& "$PSScriptRoot/Protect-FactoryDevelopmentSecrets.ps1"
$secret=Get-Content $private -Raw | ConvertFrom-Json
$workerSecret=Get-Content $workerPrivate -Raw | ConvertFrom-Json
$oldConnection=$env:FACTORY_REVIEW_CONNECTION; $oldPassword=$env:PGPASSWORD
Push-Location $repo
try {
    $env:PGPASSWORD=$settings.OwnerPassword
    $psql="$repo/artifacts/postgres/distribution/pgsql/bin/psql.exe"
    $exists=& $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$Database'"
    if($LASTEXITCODE){throw 'Database probe failed.'}
    if($exists -ne '1'){
        # stdin preserves SQL quotes across Windows PowerShell's native argument handling.
        "CREATE DATABASE `"$Database`";" |
            & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d postgres -v ON_ERROR_STOP=1 -f -
        if($LASTEXITCODE){throw 'Database creation failed.'}
    }
    $exists=& $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -tAc "SELECT 1 FROM pg_roles WHERE rolname='factory_lifecycle_runtime'"
    if($LASTEXITCODE){throw 'Role probe failed.'}
    if($exists -ne '1'){
        "CREATE ROLE factory_lifecycle_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$($secret.Password)';" |
            & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f - 2>$null | Out-Null
        if($LASTEXITCODE){throw 'Lifecycle role provisioning failed; SQL detail suppressed.'}
    }
    $exists=& $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -tAc "SELECT 1 FROM pg_roles WHERE rolname='factory_orchestration_worker'"
    if($LASTEXITCODE){throw 'Role probe failed.'}
    if($exists -ne '1'){
        "CREATE ROLE factory_orchestration_worker LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$($workerSecret.Password)';" |
            & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f - 2>$null | Out-Null
        if($LASTEXITCODE){throw 'Orchestration role provisioning failed; SQL detail suppressed.'}
    }
    $env:FACTORY_REVIEW_CONNECTION="Host=127.0.0.1;Port=$($settings.Port);Database=$Database;Username=factory_owner;Password=$($settings.OwnerPassword)"
    $migrationArgs=@('database','update','--project','src/Factory.Infrastructure','--startup-project','src/Factory.Infrastructure','--context','ReviewDbContext')
    if($BaselineOnly){$migrationArgs=@('database','update','20261014010250_AlignDecisionReasonPolicy','--project','src/Factory.Infrastructure','--startup-project','src/Factory.Infrastructure','--context','ReviewDbContext')}
    & "$repo/artifacts/tools/dotnet-ef.exe" @migrationArgs
    if($LASTEXITCODE){throw 'Lifecycle migration failed.'}
    $grants=@('review-runtime-grants.sql','authorization-runtime-grants.sql','decision-runtime-grants.sql')
    if(-not $BaselineOnly){$grants+=@('lifecycle-runtime-grants.sql','orchestration-runtime-grants.sql','tenancy-runtime-grants.sql')}
    foreach($file in $grants){
        & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f "scripts/$file"
        if($LASTEXITCODE){throw 'Runtime grants failed.'}
    }
    if($BaselineOnly -and (Test-Path "$repo/artifacts/keycloak/development.json")){
        # Synthetic identities in this new regression database only; never re-enroll in the original database.
        $identity=Get-Content "$repo/artifacts/keycloak/development.json" -Raw | ConvertFrom-Json
        $human=([Guid]::Parse($identity.HumanSubject)).ToString(); $service=([Guid]::Parse($identity.ServiceSubject)).ToString()
        @"
BEGIN;
INSERT INTO access."Projects" ("Id","IsActive","Revision") VALUES ('factory-local',true,1) ON CONFLICT DO NOTHING;
INSERT INTO access."Principals" ("Id","Issuer","Subject","Kind","IsActive","Revision") VALUES
('$human','http://127.0.0.1:58080/realms/factory-dev','$human','Human',true,1),
('$service','http://127.0.0.1:58080/realms/factory-dev','$service','Service',true,1) ON CONFLICT DO NOTHING;
INSERT INTO access."ProjectMemberships" ("ProjectId","PrincipalId","PrincipalKind","Role","IsActive","Revision") VALUES
('factory-local','$human','Human','Generator',true,1),('factory-local','$human','Human','Reviewer',true,1),
('factory-local','$service','Service','Generator',true,1) ON CONFLICT DO NOTHING;
COMMIT;
"@ | & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f -
        if($LASTEXITCODE){throw 'Synthetic regression identity provisioning failed.'}
    }
} finally {$env:FACTORY_REVIEW_CONNECTION=$oldConnection;$env:PGPASSWORD=$oldPassword;Pop-Location}
