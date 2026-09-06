param([string]$PackagePath, [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/mcp'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
if (-not $PackagePath) { $PackagePath = Join-Path $root 'artifacts' }
$source = [IO.Path]::GetFullPath($PackagePath)
Test-PackageHashes $source
foreach ($required in @('runtime/python.exe','revit_mcp/server.py')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) { throw "Portable package missing $required" }
}
& (Join-Path $source 'runtime/python.exe') -B -c "import mcp,pydantic,win32api,revit_mcp.server"
if ($LASTEXITCODE -ne 0) { throw 'Packaged runtime does not start on this computer.' }
$target = [IO.Path]::GetFullPath($InstallRoot)
Install-Tree $source $target
$config = [ordered]@{ mcpServers = [ordered]@{ revit = [ordered]@{ command = (Join-Path $target 'runtime/python.exe'); args = @('-B','-m','revit_mcp.server') } } }
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $target 'client-config.json') -Encoding utf8
Write-Host "Installed self-contained MCP runtime at $target. No Python installation or dependency download is needed."
