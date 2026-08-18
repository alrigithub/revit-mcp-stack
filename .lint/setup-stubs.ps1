$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stubs = Join-Path $root '.lint/stubs'
$rvt25 = Join-Path $stubs 'RVT25'
$rvt26 = Join-Path $stubs 'RVT26'

New-Item -ItemType Directory -Force -Path $rvt25, $rvt26 | Out-Null
python -m pip install --disable-pip-version-check --no-deps --upgrade --require-hashes --target $rvt25 -r (Join-Path $PSScriptRoot 'stubs-rvt25.lock')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
python -m pip install --disable-pip-version-check --no-deps --upgrade --require-hashes --target $rvt26 -r (Join-Path $PSScriptRoot 'stubs-rvt26.lock')
exit $LASTEXITCODE
