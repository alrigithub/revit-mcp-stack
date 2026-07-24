param([Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear)
$ErrorActionPreference = 'Stop'
$targetRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitYear"
$target = [IO.Path]::GetFullPath((Join-Path $targetRoot 'RevitMcp'))
$expectedRoot = [IO.Path]::GetFullPath($targetRoot)
if (-not $target.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing unexpected uninstall target.' }
Remove-Item -LiteralPath (Join-Path $targetRoot 'RevitMcp.addin') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Removed the per-user add-in files. Reinstall from the reviewed package if recovery is needed.'
