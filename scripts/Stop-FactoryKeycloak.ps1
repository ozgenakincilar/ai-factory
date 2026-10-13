param([string]$Distribution = "$PSScriptRoot/../artifacts/keycloak/distribution/keycloak-26.8.0")
$ErrorActionPreference='Stop'
$listeners=@(Get-NetTCPConnection -LocalPort 58080 -State Listen -ErrorAction SilentlyContinue)
if(-not $listeners.Count){Write-Output 'Keycloak is not listening.'; return}
$ids=@($listeners | Select-Object -ExpandProperty OwningProcess -Unique)
if($ids.Count -ne 1 -or @($listeners | Where-Object LocalAddress -ne '127.0.0.1').Count){throw 'Unexpected listener; refusing to stop.'}
$process=Get-CimInstance Win32_Process -Filter "ProcessId=$($ids[0])"
$expected=[IO.Path]::GetFullPath($Distribution)
if($process.Name -ne 'java.exe' -or -not $process.CommandLine.Contains($expected)){throw 'Process does not match dedicated Keycloak distribution.'}
# Windows development process termination only; no service or database files are removed.
Stop-Process -Id $ids[0] -Force
Write-Output 'Dedicated local Keycloak process stopped; development data preserved.'
