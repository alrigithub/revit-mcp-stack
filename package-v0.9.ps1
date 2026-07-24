param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
& (Join-Path $root 'revit-c-bridge/scripts/package.ps1') -RevitYear $RevitYear
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'revit-pyrevit-extention/scripts/package.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'revit-mcp/scripts/package.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$distRoot = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
$stage = [IO.Path]::GetFullPath((Join-Path $distRoot 'RevitMcp-v0.9.0'))
if (-not $stage.StartsWith($distRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing unexpected release staging path.'
}
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'install-v0.9.ps1'), (Join-Path $root 'uninstall-v0.9.ps1'), (Join-Path $root 'README.md'), (Join-Path $root 'RELEASE-NOTES-v0.9.md') -Destination $stage -Force

foreach ($component in @('revit-c-bridge','revit-pyrevit-extention','revit-mcp')) {
    $componentStage = Join-Path $stage $component
    New-Item -ItemType Directory -Force -Path (Join-Path $componentStage 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "$component/scripts/install.ps1"), (Join-Path $root "$component/scripts/uninstall.ps1"), (Join-Path $root "$component/scripts/status.ps1") -Destination (Join-Path $componentStage 'scripts') -Force
    Copy-Item -LiteralPath (Join-Path $root "$component/artifacts") -Destination $componentStage -Recurse -Force
}

$files = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
    [ordered]@{
        path = $_.FullName.Substring($stage.Length + 1)
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = $_.Length
    }
}
[ordered]@{ schema = 1; version = '0.9.0'; revit_year = $RevitYear; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'sha256-manifest.json') -Encoding utf8

$zip = Join-Path $distRoot 'RevitMcp-v0.9.0.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Revit MCP v0.9 release staged at $stage"
Write-Host "ZIP created at $zip"
