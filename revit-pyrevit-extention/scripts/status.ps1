param([string]$ExtensionsPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'pyRevit/Extensions'))
$target = Join-Path ([IO.Path]::GetFullPath($ExtensionsPath)) 'RevitMCP.extension'
[pscustomobject]@{ExtensionPath=$target;Installed=(Test-Path -LiteralPath (Join-Path $target 'startup.py'));PythonOnControl=(Test-Path -LiteralPath (Join-Path $target 'Revit MCP.tab/Runtime.panel/Python ON.pushbutton/script.py'));PythonOffControl=(Test-Path -LiteralPath (Join-Path $target 'Revit MCP.tab/Runtime.panel/Python OFF.pushbutton/script.py'))} | Format-List
