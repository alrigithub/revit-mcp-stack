param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before uninstalling.' }
& (Join-Path $PSScriptRoot 'revit-c-bridge/scripts/uninstall.ps1') -RevitYear $RevitYear
$others = @(Get-ChildItem -Path "$env:APPDATA/Autodesk/Revit/Addins/*/RevitMcp.addin" -ErrorAction SilentlyContinue)
if ($others.Count) { Write-Host 'Shared MCP runtime and Python extension retained for other Revit versions.'; return }
& (Join-Path $PSScriptRoot 'revit-pyrevit-extention/scripts/uninstall.ps1')
& (Join-Path $PSScriptRoot 'revit-mcp/scripts/uninstall.ps1')
Write-Host 'Uninstalled. Remove the revit entry from Claude Code with: claude mcp remove --scope user revit'
