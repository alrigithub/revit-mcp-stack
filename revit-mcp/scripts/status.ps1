param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp'))
[pscustomobject]@{ InstallRoot=$InstallRoot; Python=(Test-Path -LiteralPath (Join-Path $InstallRoot 'runtime/python.exe')); Server=(Test-Path -LiteralPath (Join-Path $InstallRoot 'revit_mcp/server.py')); ClientConfig=(Test-Path -LiteralPath (Join-Path $InstallRoot 'client-config.json')) } | Format-List
