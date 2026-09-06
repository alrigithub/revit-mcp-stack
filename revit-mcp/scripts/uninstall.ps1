param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/common.ps1')
$expected = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp'))
$target = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
if ($target -ne $expected) { throw 'Uninstall is restricted to the standard per-user MCP installation.' }
$target = Assert-ChildPath $target (Split-Path -Parent $expected)
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Write-Host 'Removed the MCP runtime. Settings, saved tools and captures are retained.'
