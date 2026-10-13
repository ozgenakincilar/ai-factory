param([string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin", [int]$Port = 55432)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Windows PostgreSQL initialization can fail when native paths contain non-ASCII names.
$fileSystem = New-Object -ComObject Scripting.FileSystemObject
$repoPath = $fileSystem.GetFolder($repoPath).ShortPath
$BinaryDirectory = $fileSystem.GetFolder([IO.Path]::GetFullPath($BinaryDirectory)).ShortPath
$localPath = Join-Path $repoPath 'artifacts/review-postgres'
$dataPath = Join-Path $localPath 'data'
$configPath = Join-Path $localPath 'connection.json'
if (-not (Test-Path (Join-Path $BinaryDirectory 'initdb.exe'))) { throw 'Supply an installed PostgreSQL binary directory.' }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw 'Requested database port is already in use.' }
New-Item -ItemType Directory -Force $localPath | Out-Null
if (-not (Test-Path $configPath)) {
    if (Test-Path $dataPath) { throw 'Existing data without connection metadata; manual review required.' }
    $ownerPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $writerPassword = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $settings = @{ Port=$Port; OwnerPassword=$ownerPassword; WriterPassword=$writerPassword }
    [IO.File]::WriteAllText($configPath, ($settings | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $localPath 'owner-password'), $ownerPassword)
}
$settings = Get-Content -Raw $configPath | ConvertFrom-Json
if ($settings.Port -ne $Port) { throw 'Port differs from saved configuration.' }
if (-not (Test-Path (Join-Path $dataPath 'PG_VERSION'))) {
    $passwordPath = Join-Path $localPath 'owner-password'
    & (Join-Path $BinaryDirectory 'initdb.exe') -D $dataPath -U factory_owner --auth=scram-sha-256 --encoding=UTF8 --locale=C "--pwfile=$passwordPath"
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL initialization failed.' }
}
# pg_ctl starts only this isolated cluster, not a Windows service.
$start = Start-Process -FilePath (Join-Path $BinaryDirectory 'pg_ctl.exe') -ArgumentList @('-D', ('"'+$dataPath+'"'), '-l', ('"'+(Join-Path $localPath 'server.log')+'"'), '-o', ('"-h 127.0.0.1 -p '+$Port+'"'), '-w', 'start') -WindowStyle Hidden -PassThru
$start.WaitForExit()
if ($start.ExitCode -ne 0) { throw 'PostgreSQL startup failed; inspect ignored local log.' }
$oldPassword = $env:PGPASSWORD
try {
    $env:PGPASSWORD = $settings.OwnerPassword
    $psql = Join-Path $BinaryDirectory 'psql.exe'
    $exists = & $psql -h 127.0.0.1 -p $Port -U factory_owner -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='factory_review_5a'"
    if ($LASTEXITCODE -ne 0) { throw 'Database probe failed.' }
    if ($exists -ne '1') {
        & $psql -h 127.0.0.1 -p $Port -U factory_owner -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE factory_review_5a'
        if ($LASTEXITCODE -ne 0) { throw 'Database creation failed.' }
    }
    $role = & $psql -h 127.0.0.1 -p $Port -U factory_owner -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname='factory_review_writer'"
    if ($role -ne '1') {
        # Generated hexadecimal password only; send via stdin, never command line/output.
        "CREATE ROLE factory_review_writer LOGIN PASSWORD '$($settings.WriterPassword)' NOSUPERUSER NOCREATEDB NOCREATEROLE;" | & $psql -h 127.0.0.1 -p $Port -U factory_owner -d postgres -v ON_ERROR_STOP=1 -f -
        if ($LASTEXITCODE -ne 0) { throw 'Writer role creation failed.' }
    }
} finally { $env:PGPASSWORD = $oldPassword }
Write-Output "Isolated PostgreSQL ready on loopback port $Port. Credentials are in ignored artifacts/review-postgres/connection.json; do not print or commit them."
