$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
& (Join-Path $PSScriptRoot 'test.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$stage = Join-Path $root 'artifacts/RevitMCP.extension'
Reset-Directory (Join-Path $root 'artifacts') $root
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stage) | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'RevitMCP.extension') -Destination (Split-Path -Parent $stage) -Recurse -Force
Get-ChildItem -LiteralPath $stage -Recurse -Directory -Filter '__pycache__' | ForEach-Object {
    $cache = Assert-ChildPath $_.FullName $stage
    Remove-Item -LiteralPath $cache -Recurse -Force
}
$files = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { [ordered]@{path=$_.FullName.Substring($stage.Length + 1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$_.Length} }
[ordered]@{schema=1;files=$files} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'artifacts/uninstall-manifest.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'sbom.json') -Destination (Join-Path $root 'artifacts/sbom.json') -Force
Write-Host "Packaged zero-dependency extension at $stage"
