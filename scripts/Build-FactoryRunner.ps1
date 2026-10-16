param([string]$Tag = '')
$ErrorActionPreference = 'Stop'
# Builds the M9.2 runner image (runner/Dockerfile) from the registered starter template and records
# its immutable image id in ignored artifacts/runner/image.json. The Factory and the Docker tests run
# the sandbox only by that id. Requires Docker Desktop to be running.
$repo = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$registry = Get-Content "$repo/templates/registry.json" -Raw | ConvertFrom-Json
$template = $registry.templates | Where-Object { $_.id -eq 'dotnet-angular-postgres-ddd' -and $_.version -eq '1.0.0' }
if (-not $template) { throw 'Starter template 1.0.0 is not registered.' }
if (-not $Tag) { $Tag = 'factory-runner:1.0.0-' + $template.contentSha256.Substring(0, 12) }
& docker build --pull=false --tag $Tag --file "$repo/runner/Dockerfile" "$repo/templates/$($template.directory)"
if ($LASTEXITCODE) { throw 'Runner image build failed.' }
$id = (& docker image inspect --format '{{.Id}}' $Tag).Trim()
if ($id -notmatch '\Asha256:[0-9a-f]{64}\z') { throw 'Unexpected runner image id.' }
New-Item -ItemType Directory -Force "$repo/artifacts/runner" | Out-Null
[ordered]@{ ImageId = $id; Tag = $Tag; TemplateId = $template.id; TemplateVersion = $template.version
    TemplateSha256 = $template.contentSha256; BuiltAt = (Get-Date).ToUniversalTime().ToString('o') } |
    ConvertTo-Json | Set-Content -Encoding utf8 "$repo/artifacts/runner/image.json"
Write-Output "Runner image $Tag = $id"
