param([string]$PackagePath, [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/mcp/0.9.0'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PackagePath) { $PackagePath = Join-Path $root 'artifacts' }
$source = [IO.Path]::GetFullPath($PackagePath)
foreach ($required in @('runtime/Scripts/python.exe','revit_mcp/server.py','sha256-manifest.json')) { if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Frozen package missing $required" } }
$target = [IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
$config = [ordered]@{ mcpServers = [ordered]@{ revit = [ordered]@{ command = (Join-Path $target 'runtime/Scripts/python.exe'); args = @('-m','revit_mcp.server'); env = [ordered]@{ PYTHONPATH = $target } } } }
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'client-config.json') -Encoding utf8
Write-Host "Copied frozen environment without dependency resolution to $target"
Write-Host "Merge $(Join-Path $target 'client-config.json') into the owner's MCP client configuration."
