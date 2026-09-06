param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025, [switch]$SkipClaude)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'scripts/common.ps1')
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before installing 3XN-RevitMCP.' }
if (-not (Test-Path -LiteralPath "$env:ProgramFiles/Autodesk/Revit $RevitYear/Revit.exe")) { throw "Install Revit $RevitYear first." }
if (Test-Path -LiteralPath (Join-Path $root 'sha256-manifest.json')) {
    Test-PackageHashes $root
    $release = Get-Content -LiteralPath (Join-Path $root 'sha256-manifest.json') -Raw | ConvertFrom-Json
    if ($release.revit_year -ne $RevitYear) { throw "This package is for Revit $($release.revit_year)." }
}
Test-PackageHashes (Join-Path $root "revit-c-bridge/artifacts/$RevitYear") 'uninstall-manifest.json'
Test-PackageHashes (Join-Path $root 'revit-pyrevit-extention/artifacts/RevitMCP.extension') '../uninstall-manifest.json'
Test-PackageHashes (Join-Path $root 'revit-mcp/artifacts')
& (Join-Path $root 'revit-c-bridge/scripts/install.ps1') -RevitYear $RevitYear
& (Join-Path $root 'revit-pyrevit-extention/scripts/install.ps1')
& (Join-Path $root 'revit-mcp/scripts/install.ps1')
if (-not $SkipClaude) { & (Join-Path $root 'scripts/register-claude.ps1') }
Write-Host "Installed for Revit $RevitYear. Open Revit, choose 3XN RevitMCP, and turn Bridge ON."
Write-Host 'Python also needs pyRevit 6.4 and the Python ON button. Restart Claude Code after installing.'
