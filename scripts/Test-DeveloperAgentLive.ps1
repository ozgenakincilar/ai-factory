param([decimal]$JobCapUsd = 0.40, [decimal]$TotalCapUsd = 2.00, [string]$SdkDll = 'C:/Program Files/dotnet/sdk/10.0.103/dotnet.dll')
$ErrorActionPreference = 'Stop'
# M10.2 paid live run of the Developer Agent (gpt-5.4-mini). The agent's cost guard stops before any
# call that could exceed USD 0.40; this script refuses to start when the ledger of earlier live runs
# leaves less than one job cap under the USD 2 total (user decision of 2026-10-16).
# The OpenAI key comes from the worker's Development User Secrets and is never printed or written.
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$dir = "$repo/artifacts/developer-live"
New-Item -ItemType Directory -Force $dir | Out-Null
$ledger = "$dir/ledger.json"
$runs = @(); if (Test-Path $ledger) { $runs = @(Get-Content $ledger -Raw | ConvertFrom-Json) }
$spent = [decimal]0; foreach ($r in $runs) { $spent += [decimal]$r.CostMicros / 1000000 }
if ($spent + $JobCapUsd -gt $TotalCapUsd) { throw "Live budget exhausted: USD $spent of $TotalCapUsd spent." }
if (-not (Test-Path "$repo/artifacts/runner/image.json")) { throw 'Build the runner image first (scripts/Build-FactoryRunner.ps1).' }
$secret = (& dotnet user-secrets list --project "$repo/src/Factory.Worker" | Where-Object { $_ -like 'OpenAI:ApiKey = *' })
if (-not $secret) { throw 'OpenAI:ApiKey is not set in the worker User Secrets.' }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$evidence = "$dir/run-$stamp.json"
$values = @{
    FACTORY_LIVE_DEVELOPER = '1'
    FACTORY_LIVE_OPENAI_KEY = $secret.Substring('OpenAI:ApiKey = '.Length)
    FACTORY_RUNNER_IMAGE = (Get-Content "$repo/artifacts/runner/image.json" -Raw | ConvertFrom-Json).ImageId
    FACTORY_LIVE_EVIDENCE = $evidence
}
$secret = $null
$saved = @{}
Push-Location $repo
try {
    foreach ($key in $values.Keys) { $saved[$key] = [Environment]::GetEnvironmentVariable($key); [Environment]::SetEnvironmentVariable($key, $values[$key]) }
    $values = $null
    & dotnet exec $SdkDll test tests/Factory.Tests --filter 'FullyQualifiedName~LiveDeveloperAgentTests'
    $exit = $LASTEXITCODE
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    Pop-Location
}
if (Test-Path $evidence) {
    $run = Get-Content $evidence -Raw | ConvertFrom-Json
    $runs += [pscustomobject]@{ Run = $stamp; Status = $run.status; CostMicros = $run.costMicros }
    ConvertTo-Json @($runs) | Set-Content -Encoding utf8 $ledger
    Write-Output "Live run $stamp : $($run.status) $($run.failureCode), turns $($run.turns), estimated USD $([decimal]$run.costMicros / 1000000)"
} else {
    # Without evidence the spend is unknown; record the cap so the total stays conservative.
    $runs += [pscustomobject]@{ Run = $stamp; Status = 'unknown'; CostMicros = [int]($JobCapUsd * 1000000) }
    ConvertTo-Json @($runs) | Set-Content -Encoding utf8 $ledger
    Write-Output "Live run $stamp produced no evidence; USD $JobCapUsd recorded as spent."
}
if ($exit) { throw 'Live developer test failed.' }
