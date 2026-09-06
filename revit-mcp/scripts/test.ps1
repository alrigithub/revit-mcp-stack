$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
$env:PYTHONPATH = Join-Path $root 'src'
& (Get-BuildPython) -c "import sys,unittest; sys.path.insert(0,sys.argv[1]); suite=unittest.defaultTestLoader.discover(sys.argv[2]); result=unittest.TextTestRunner(verbosity=2).run(suite); sys.exit(not result.wasSuccessful())" (Join-Path $root 'src') (Join-Path $root 'tests')
exit $LASTEXITCODE
