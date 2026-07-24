param([string]$ExtensionsPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'pyRevit/Extensions'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ExtensionsPath)
$target = [IO.Path]::GetFullPath((Join-Path $root 'RevitMCP.extension'))
if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unexpected uninstall target.' }
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Removed the per-user pyRevit extension. It can be recovered from the reviewed package.'
