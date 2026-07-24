param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw 'Close Revit before uninstalling Revit MCP v0.9.'
}

$root = $PSScriptRoot
& (Join-Path $root 'revit-c-bridge/scripts/uninstall.ps1') -RevitYear $RevitYear
& (Join-Path $root 'revit-pyrevit-extention/scripts/uninstall.ps1')
& (Join-Path $root 'revit-mcp/scripts/uninstall.ps1')
Write-Host "Revit MCP v0.9 removed for Revit $RevitYear."
