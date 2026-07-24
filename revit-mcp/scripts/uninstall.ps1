param([string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/mcp/0.9.0'))
$ErrorActionPreference = 'Stop'
$parent = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/mcp'))
$target = [IO.Path]::GetFullPath($InstallRoot)
if (-not $target.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unexpected uninstall target.' }
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Removed the frozen MCP environment. It can be recovered from the reviewed package.'
