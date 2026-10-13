param([string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin")
$ErrorActionPreference='Stop'
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
& "$PSScriptRoot/Protect-FactoryDevelopmentSecrets.ps1"
$settings = Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$private = "$repo/artifacts/review-postgres/authorization.json"
if (-not (Test-Path $private)) {
    [IO.File]::WriteAllText($private, (@{ReaderPassword=([Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N'))} | ConvertTo-Json))
}
$reader = Get-Content $private -Raw | ConvertFrom-Json
$oldConnection=$env:FACTORY_REVIEW_CONNECTION; $oldPassword=$env:PGPASSWORD
Push-Location $repo
try {
    $env:FACTORY_REVIEW_CONNECTION="Host=127.0.0.1;Port=$($settings.Port);Database=factory_review_5a;Username=factory_owner;Password=$($settings.OwnerPassword)"
    & artifacts/tools/dotnet-ef.exe database update --project src/Factory.Infrastructure --startup-project src/Factory.Infrastructure --context ReviewDbContext
    if ($LASTEXITCODE) { throw 'Authorization migration failed.' }
    $env:PGPASSWORD=$settings.OwnerPassword
    $psql=Join-Path $BinaryDirectory 'psql.exe'
    $exists=& $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d factory_review_5a -tAc "SELECT 1 FROM pg_roles WHERE rolname='factory_authorization_reader'"
    if ($LASTEXITCODE) { throw 'Role probe failed.' }
    if ($exists -ne '1') {
        # Suppress SQL error detail because it could contain the generated password.
        "CREATE ROLE factory_authorization_reader LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '$($reader.ReaderPassword)';" |
            & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d factory_review_5a -v ON_ERROR_STOP=1 -f - 2>$null | Out-Null
        if ($LASTEXITCODE) { throw 'Reader provisioning failed; no SQL detail printed.' }
    }
    & $psql -h 127.0.0.1 -p $settings.Port -U factory_owner -d factory_review_5a -v ON_ERROR_STOP=1 -f scripts/authorization-runtime-grants.sql
    if ($LASTEXITCODE) { throw 'Authorization grants failed.' }
} finally { $env:FACTORY_REVIEW_CONNECTION=$oldConnection; $env:PGPASSWORD=$oldPassword; Pop-Location }
