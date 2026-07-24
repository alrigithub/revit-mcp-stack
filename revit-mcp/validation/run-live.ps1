param([Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear, [int]$Pid)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Pid) {
    $records = Get-ChildItem -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/instances') -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json } | Where-Object { $_.revit_year -eq "$RevitYear" -and $_.bridge_state -eq 'on' }
    if (@($records).Count -ne 1) { throw 'Pass -Pid when exactly one matching Bridge ON instance is not discoverable.' }
    $Pid = [int]$records.pid
}
$python = Join-Path $root 'artifacts/runtime/Scripts/python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Run scripts/package.ps1 first to create the frozen runtime.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$output = Join-Path $PSScriptRoot "results/$stamp-revit-$RevitYear"
$env:PYTHONPATH = (Join-Path $root 'src')
& $python (Join-Path $PSScriptRoot 'live_harness.py') --pid $Pid --output $output
exit $LASTEXITCODE
