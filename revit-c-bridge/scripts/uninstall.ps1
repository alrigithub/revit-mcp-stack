param([Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/common.ps1')
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before uninstalling.' }
$root = Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitYear"
$target = Assert-ChildPath (Join-Path $root 'RevitMcp') $root
$manifest = Assert-ChildPath (Join-Path $root 'RevitMcp.addin') $root
if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest -Force }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Write-Host "Removed the Revit $RevitYear bridge."
