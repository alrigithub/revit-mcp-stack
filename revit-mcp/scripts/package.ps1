$ErrorActionPreference = 'Stop'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root 'artifacts'
$runtime = Join-Path $stage 'runtime'
$wheelhouse = Join-Path $stage 'wheelhouse'
New-Item -ItemType Directory -Force -Path $stage, $wheelhouse | Out-Null
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'Scripts/python.exe'))) { python -m venv $runtime }
python -m pip download --only-binary=:all: --require-hashes -r (Join-Path $root 'requirements.lock') -d $wheelhouse
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $runtime 'Scripts/python.exe') -m pip install --no-index --find-links $wheelhouse --require-hashes -r (Join-Path $root 'requirements.lock')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$appTarget = Join-Path $stage 'revit_mcp'
if (Test-Path -LiteralPath $appTarget) { Remove-Item -LiteralPath $appTarget -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'src/revit_mcp') -Destination $stage -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'requirements.lock') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $root 'sbom.json') -Destination $stage -Force
$env:PYTHONPATH = Join-Path $root 'src'
& (Join-Path $runtime 'Scripts/python.exe') -m unittest discover -s (Join-Path $root 'tests') -v
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $runtime 'Scripts/python.exe') (Join-Path $root 'validation/smoke_stdio.py') --python (Join-Path $runtime 'Scripts/python.exe') --source (Join-Path $root 'src')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$config = [ordered]@{ mcpServers = [ordered]@{ revit = [ordered]@{ command = (Join-Path $runtime 'Scripts/python.exe'); args = @('-m','revit_mcp.server'); env = [ordered]@{ PYTHONPATH = $stage } } } }
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'client-config.json') -Encoding utf8
$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'sha256-manifest.json' } | ForEach-Object { [ordered]@{path=$_.FullName.Substring($stage.Length + 1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length} }
[ordered]@{schema=1;python='3.12';platform='win-x64';files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'sha256-manifest.json') -Encoding utf8
Write-Host "Frozen, hash-verified MCP environment created at $stage"
