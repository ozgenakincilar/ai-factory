param(
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][Guid]$Job,
    [Parameter(Mandatory)][string]$OutFile
)
$ErrorActionPreference='Stop'
# Operator export of one development job's exact evidence bytes from a local database (owner login),
# for scripts/Deploy-FactoryStaging.ps1. Prints the SHA-256 that the database recorded and checked.
# Users without database access download the same bytes from
# GET /api/projects/{id}/developments/{job}/evidence (the ETag carries the SHA-256).
if($Database -cnotmatch '\Afactory_[A-Za-z0-9_]+\z'){throw 'Invalid database name.'}
$repo=[IO.Path]::GetFullPath("$PSScriptRoot/..")
$db=Get-Content "$repo/artifacts/review-postgres/connection.json" -Raw | ConvertFrom-Json
$psql="$repo/artifacts/postgres/distribution/pgsql/bin/psql.exe"
$query="SELECT pg_catalog.encode(e.""Content"",'base64') || '|' || e.""ContentSha256"" FROM orchestration.""DevelopmentEvidence"" e WHERE e.""JobId""='$Job' AND e.""Status""='Verified'"
$saved=$env:PGPASSWORD
# From a file: Windows PowerShell 5.1 drops the double quotes of native command arguments.
$sql=New-TemporaryFile
try {
    [IO.File]::WriteAllText($sql,$query)
    $env:PGPASSWORD=$db.OwnerPassword
    $row = & $psql -h 127.0.0.1 -p $db.Port -U factory_owner -d $Database -A -t -f $sql
    if($LASTEXITCODE){throw 'psql failed.'}
} finally { $env:PGPASSWORD=$saved; Remove-Item $sql }
$row = ($row -join '').Replace("`n",'').Replace("`r",'')
if(-not $row){throw 'No verified evidence for this job.'}
$base64,$sha = $row.Split('|')
$bytes=[Convert]::FromBase64String($base64)
$actual=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace '-','').ToLowerInvariant()
if($actual -ne $sha){throw 'Evidence hash mismatch.'}
$OutFile=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutFile)
New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
[IO.File]::WriteAllBytes($OutFile,$bytes)
Write-Output $sha
