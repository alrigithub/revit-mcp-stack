$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
python -m unittest discover -s (Join-Path $root 'tests') -v
exit $LASTEXITCODE
