$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
& (Get-BuildPython) -m unittest discover -s (Join-Path $root 'tests') -v
exit $LASTEXITCODE
