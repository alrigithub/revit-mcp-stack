param([string]$ExtensionsPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'pyRevit/Extensions'), [string]$PackagePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
if (-not $PackagePath) { $PackagePath = Join-Path $root 'artifacts' }
$source = Join-Path ([IO.Path]::GetFullPath($PackagePath)) 'RevitMCP.extension'
Test-PackageHashes $source '../uninstall-manifest.json'
if (-not (Test-Path -LiteralPath (Join-Path $source 'startup.py'))) { throw 'Reviewed pyRevit package is missing startup.py.' }
$targetRoot = [IO.Path]::GetFullPath($ExtensionsPath)
New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null
Install-Tree $source (Join-Path $targetRoot 'RevitMCP.extension')
Write-Host "Installed without elevation at $targetRoot. Reload pyRevit; Python defaults OFF."
