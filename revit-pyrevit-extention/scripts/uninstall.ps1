param([string]$ExtensionsPath = (Join-Path $env:APPDATA 'pyRevit/Extensions'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/common.ps1')
if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'Close Revit before uninstalling.' }
$target = Assert-ChildPath (Join-Path $ExtensionsPath 'RevitMCP.extension') $ExtensionsPath
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Write-Host 'Removed the Revit MCP extension. pyRevit itself is retained.'
