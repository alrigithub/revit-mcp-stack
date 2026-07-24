param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw 'Close Revit before installing Revit MCP v0.9.'
}

$root = $PSScriptRoot
& (Join-Path $root 'revit-c-bridge/scripts/install.ps1') -RevitYear $RevitYear
& (Join-Path $root 'revit-pyrevit-extention/scripts/install.ps1')
& (Join-Path $root 'revit-mcp/scripts/install.ps1')

Write-Host "Revit MCP v0.9 installed for Revit $RevitYear without elevation."
Write-Host 'Restart Revit, enable Bridge and Python, and merge the generated MCP client-config.json if needed.'
