param([Parameter(Mandatory=$true)][string]$Model, [Parameter(Mandatory=$true)][string]$Output,
      [ValidateSet('contracts','captures','extras')][string]$Phase = 'contracts', [Alias('Pid')][int]$RevitProcessId)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/common.ps1')
$python = Get-BuildPython
$arguments = @('-B',(Join-Path $PSScriptRoot 'live_release.py'),'--model',$Model,'--output',$Output,'--phase',$Phase)
if ($RevitProcessId) { $arguments += @('--pid',"$RevitProcessId") }
& $python @arguments
if ($LASTEXITCODE -ne 0) { throw 'Live release validation failed. Inspect the output folder before retrying.' }
