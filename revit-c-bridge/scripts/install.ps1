param(
    [Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear,
    [string]$PackagePath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before installing the bridge.' }
if (-not $PackagePath) { $PackagePath = Join-Path $root "artifacts/$RevitYear" }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
Test-PackageHashes $PackagePath 'uninstall-manifest.json'
foreach ($required in @('RevitMcp','RevitMcp.addin','uninstall-manifest.json')) { if (-not (Test-Path -LiteralPath (Join-Path $PackagePath $required))) { throw "Reviewed package is missing $required" } }
$targetRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitYear"
$target = Join-Path $targetRoot 'RevitMcp'
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
Install-Tree (Join-Path $PackagePath 'RevitMcp') $target
$manifest = (Get-Content -LiteralPath (Join-Path $PackagePath 'RevitMcp.addin') -Raw).Replace('{{ASSEMBLY}}', (Join-Path $target 'RevitMcp.Bridge.dll'))
$manifest | Set-Content -LiteralPath (Join-Path $targetRoot 'RevitMcp.addin') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PackagePath 'uninstall-manifest.json') -Destination (Join-Path $target 'uninstall-manifest.json') -Force
Write-Host "Installed without elevation at $targetRoot. Restart Revit; Bridge defaults OFF."
