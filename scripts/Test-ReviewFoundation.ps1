param([string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin")
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$settings = Get-Content -Raw (Join-Path $repoPath 'artifacts/review-postgres/connection.json') | ConvertFrom-Json
$oldOwner = $env:FACTORY_REVIEW_CONNECTION
$oldTest = $env:FACTORY_REVIEW_TEST_CONNECTION
$oldPassword = $env:PGPASSWORD
Push-Location $repoPath
try {
    $env:FACTORY_REVIEW_CONNECTION = "Host=127.0.0.1;Port=$($settings.Port);Database=factory_review_5a;Username=factory_owner;Password=$($settings.OwnerPassword)"
    & artifacts/tools/dotnet-ef.exe database update --project src/Factory.Infrastructure --startup-project src/Factory.Infrastructure --context ReviewDbContext
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
    $env:PGPASSWORD = $settings.OwnerPassword
    & (Join-Path $BinaryDirectory 'psql.exe') -h 127.0.0.1 -p $settings.Port -U factory_owner -d factory_review_5a -v ON_ERROR_STOP=1 -f scripts/review-runtime-grants.sql
    if ($LASTEXITCODE -ne 0) { throw 'Runtime grants failed.' }
    $env:FACTORY_REVIEW_TEST_CONNECTION = "Host=127.0.0.1;Port=$($settings.Port);Database=factory_review_5a;Username=factory_review_writer;Password=$($settings.WriterPassword)"
    dotnet build -p:OutputPath=bin/Milestone5A/net10.0/
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test --no-restore -p:OutputPath=bin/Milestone5A/net10.0/
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally {
    $env:FACTORY_REVIEW_CONNECTION = $oldOwner
    $env:FACTORY_REVIEW_TEST_CONNECTION = $oldTest
    $env:PGPASSWORD = $oldPassword
    Pop-Location
}
