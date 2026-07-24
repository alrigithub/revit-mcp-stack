$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:PYTHONPATH = Join-Path $root 'src'
python -m unittest discover -s (Join-Path $root 'tests') -v
exit $LASTEXITCODE
