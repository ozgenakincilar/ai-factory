param([string]$BinaryDirectory = "$PSScriptRoot/../artifacts/postgres/distribution/pgsql/bin")
$dataPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/review-postgres/data'))
& (Join-Path $BinaryDirectory 'pg_ctl.exe') -D $dataPath -m fast -w stop
if ($LASTEXITCODE -ne 0) { throw 'Cluster stop failed.' }
