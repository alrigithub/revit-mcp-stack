param([string]$ExtensionsPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'pyRevit/Extensions'))
$target = Join-Path ([IO.Path]::GetFullPath($ExtensionsPath)) 'RevitMCP.extension'
[pscustomobject]@{ExtensionPath=$target;Installed=(Test-Path -LiteralPath (Join-Path $target 'startup.py'));Provider=(Test-Path -LiteralPath (Join-Path $target 'lib/revit_mcp_provider.py'));LegacyTabPresent=(Test-Path -LiteralPath (Join-Path $target 'Revit MCP.tab'))} | Format-List
