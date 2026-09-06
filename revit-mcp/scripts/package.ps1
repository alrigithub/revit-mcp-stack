param([string]$BuildPython)
$ErrorActionPreference = 'Stop'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root '../scripts/common.ps1')
if (-not $BuildPython) { $BuildPython = Get-BuildPython }
$cache = Join-Path $env:LOCALAPPDATA 'RevitMcp/build-cache'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$lock = Get-Content -LiteralPath (Join-Path $root 'runtime-lock.json') -Raw | ConvertFrom-Json
function Get-LockedDownload([string]$Url, [string]$Hash) {
    $path = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$Url).AbsolutePath))
    if (-not (Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $Url -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Hash) { throw "Download hash mismatch: $path" }
    return $path
}
$archive = Get-LockedDownload $lock.python_url $lock.python_sha256
$pipWheel = Get-LockedDownload $lock.pip_url $lock.pip_sha256
function Invoke-LockedPip([string[]]$PipArgs) {
    & $BuildPython -c "import sys,runpy; sys.path.insert(0,sys.argv.pop(1)); runpy.run_module('pip',run_name='__main__')" $pipWheel @PipArgs
    if ($LASTEXITCODE -ne 0) { throw 'Hash-locked dependency packaging failed.' }
}
$stage = Join-Path $root 'artifacts'
Reset-Directory $stage $root
$runtime = Join-Path $stage 'runtime'
Expand-Archive -LiteralPath $archive -DestinationPath $runtime
$wheelhouse = Join-Path $cache 'wheels'
New-Item -ItemType Directory -Path $wheelhouse -Force | Out-Null
Invoke-LockedPip @('download','--only-binary=:all:','--require-hashes','-r',(Join-Path $root 'requirements.lock'),'-d',$wheelhouse)
Invoke-LockedPip @('install','--no-index','--find-links',$wheelhouse,'--require-hashes','--no-compile','--target',(Join-Path $runtime 'Lib/site-packages'),'-r',(Join-Path $root 'requirements.lock'))
# Relative paths make this runtime relocatable and independent of PATH, registry and PYTHONPATH.
@('python312.zip','.','Lib/site-packages','..','import site') | Set-Content -LiteralPath (Join-Path $runtime 'python312._pth') -Encoding ascii
Copy-Item -LiteralPath (Join-Path $root 'src/revit_mcp') -Destination $stage -Recurse -Force
Get-ChildItem -LiteralPath $stage -Recurse -Directory -Filter '__pycache__' | ForEach-Object {
    $path = Assert-ChildPath $_.FullName $stage
    Remove-Item -LiteralPath $path -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $root 'requirements.lock'), (Join-Path $root 'runtime-lock.json'), (Join-Path $root 'sbom.json') -Destination $stage
$python = Join-Path $runtime 'python.exe'
& $python -B -c "import sys,mcp,pydantic,win32api,revit_mcp.server; assert sys.version_info[:2]==(3,12); print('PORTABLE IMPORT PASS')"
if ($LASTEXITCODE -ne 0) { throw 'Portable runtime import failed.' }
& $python -B -c "import sys,unittest; sys.path.insert(0,sys.argv[1]); suite=unittest.defaultTestLoader.discover(sys.argv[2]); r=unittest.TextTestRunner().run(suite); sys.exit(not r.wasSuccessful())" (Join-Path $root 'src') (Join-Path $root 'tests')
if ($LASTEXITCODE -ne 0) { throw 'MCP tests failed.' }
& $python -B (Join-Path $root 'validation/smoke_stdio.py') --python $python --source $stage
if ($LASTEXITCODE -ne 0) { throw 'MCP stdio smoke failed.' }
Get-ChildItem -LiteralPath $stage -Recurse -Directory -Filter '__pycache__' | ForEach-Object {
    $path = Assert-ChildPath $_.FullName $stage
    Remove-Item -LiteralPath $path -Recurse -Force
}
Write-PackageHashes $stage -Metadata @{ python = $lock.python_version; platform = 'win-x64' }
Write-Host "Self-contained, hash-verified MCP runtime packaged at $stage"
