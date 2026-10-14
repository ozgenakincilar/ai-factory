param([string]$Database = 'factory_review_5a')
$ErrorActionPreference='Stop'
if($Database -notmatch '^factory_[a-z0-9_]+$'){throw 'Invalid local verification database name.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
& "$PSScriptRoot/Protect-FactoryDevelopmentSecrets.ps1"
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$private="$repo/artifacts/review-postgres/decisions.json"
if(-not(Test-Path $private)){
    [IO.File]::WriteAllText($private,(@{Password=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'))}|ConvertTo-Json))
}
$secret=Get-Content $private -Raw | ConvertFrom-Json
$oldConnection=$env:FACTORY_REVIEW_CONNECTION; $oldPassword=$env:PGPASSWORD
Push-Location $repo
try {
    $env:PGPASSWORD=$db.OwnerPassword
    $psql="$repo/artifacts/postgres/distribution/pgsql/bin/psql.exe"
    $exists=& $psql -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -tAc "SELECT 1 FROM pg_roles WHERE rolname='factory_decision_runtime'"
    if($LASTEXITCODE){throw 'Role probe failed.'}
    if($exists -ne '1'){
        "CREATE ROLE factory_decision_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$($secret.Password)';" |
            & $psql -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f - 2>$null | Out-Null
        if($LASTEXITCODE){throw 'Decision role provisioning failed; SQL detail suppressed.'}
    }
    $env:FACTORY_REVIEW_CONNECTION="Host=127.0.0.1;Port=$($db.Port);Database=$Database;Username=factory_owner;Password=$($db.OwnerPassword)"
    & "$repo/artifacts/tools/dotnet-ef.exe" database update --project src/Factory.Infrastructure --startup-project src/Factory.Infrastructure --context ReviewDbContext
    if($LASTEXITCODE){throw 'Decision migration failed.'}
    & $psql -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -v ON_ERROR_STOP=1 -f scripts/decision-runtime-grants.sql
    if($LASTEXITCODE){throw 'Decision grants failed.'}
} finally {$env:FACTORY_REVIEW_CONNECTION=$oldConnection;$env:PGPASSWORD=$oldPassword;Pop-Location}
