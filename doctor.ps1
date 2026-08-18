# One-shot health check: the three installed components, live bridge instances, runtime settings,
# and drift between the repo and its deployed copies. Read-only; fix drift with ./sync.ps1.
param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appData = [Environment]::GetFolderPath('ApplicationData')
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')

function Show([string]$label, [bool]$ok, [string]$detail = '') {
    $state = if ($ok) { 'ok      ' } else { 'MISSING ' }
    Write-Host ("  {0}{1}{2}" -f $state, $label, $(if ($detail) { " ($detail)" } else { '' }))
}

Write-Host "Bridge install (Revit $RevitYear)"
$addins = Join-Path $appData "Autodesk/Revit/Addins/$RevitYear"
Show 'RevitMcp.addin manifest' (Test-Path -LiteralPath (Join-Path $addins 'RevitMcp.addin'))
Show 'bridge DLL' (Test-Path -LiteralPath (Join-Path $addins 'RevitMcp/RevitMcp.Bridge.dll'))
Show 'Roslyn provider DLL' (Test-Path -LiteralPath (Join-Path $addins 'RevitMcp/providers/roslyn/1/RevitMcp.RoslynProvider.dll'))

Write-Host 'MCP server install'
$mcpRoot = Join-Path $localAppData 'RevitMcp/mcp/0.9.0'
Show 'bundled Python runtime' (Test-Path -LiteralPath (Join-Path $mcpRoot 'runtime/Scripts/python.exe'))
Show 'server source' (Test-Path -LiteralPath (Join-Path $mcpRoot 'revit_mcp/server.py'))
Show 'client config' (Test-Path -LiteralPath (Join-Path $mcpRoot 'client-config.json'))

Write-Host 'pyRevit extension install'
$extension = Join-Path $appData 'pyRevit/Extensions/RevitMCP.extension'
Show 'startup.py' (Test-Path -LiteralPath (Join-Path $extension 'startup.py'))
Show 'Python ON button' (Test-Path -LiteralPath (Join-Path $extension 'Revit MCP.tab/Runtime.panel/Python ON.pushbutton/script.py'))
Show 'Python OFF button' (Test-Path -LiteralPath (Join-Path $extension 'Revit MCP.tab/Runtime.panel/Python OFF.pushbutton/script.py'))

Write-Host 'Live bridge instances'
$records = @(Get-ChildItem -LiteralPath (Join-Path $localAppData 'RevitMcp/instances') -Filter '*.json' -ErrorAction SilentlyContinue)
if ($records.Count -eq 0) {
    Write-Host '  none (start Revit and click Bridge ON)'
}
foreach ($record in $records) {
    try {
        $data = Get-Content -LiteralPath $record.FullName -Raw | ConvertFrom-Json
        $alive = $null -ne (Get-Process -Id $data.pid -ErrorAction SilentlyContinue)
        $state = if ($alive) { 'live ' } else { 'STALE' }
        Write-Host ("  {0} Revit {1} pid {2} bridge {3}" -f $state, $data.revit_year, $data.pid, $data.bridge_state)
    }
    catch { Write-Host "  UNREADABLE $($record.Name)" }
}

Write-Host 'Runtime settings'
$settingsPath = Join-Path $localAppData 'RevitMcp/settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        Write-Host "  saved_tools_root: $($settings.saved_tools_root)"
        $disabled = @($settings.disabled_mcp_tools)
        Write-Host "  disabled_mcp_tools: $(if ($disabled.Count) { $disabled -join ', ' } else { 'none' })"
    }
    catch { Write-Host "  UNREADABLE $settingsPath" }
}
else {
    Write-Host '  no settings.json (defaults apply)'
}

Write-Host 'Repo-to-deployed drift'
$toolsRoot = Join-Path $localAppData 'RevitMcp/tools'
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $configured = (Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).saved_tools_root
        if ($configured) { $toolsRoot = $configured }
    }
    catch { }
}
$mirrors = @(
    @{ Name = 'revit-mcp server'
       Source = [IO.Path]::GetFullPath((Join-Path $root 'revit-mcp/src/revit_mcp'))
       Target = Join-Path $mcpRoot 'revit_mcp' },
    @{ Name = 'pyRevit extension'
       Source = [IO.Path]::GetFullPath((Join-Path $root 'revit-pyrevit-extention/RevitMCP.extension'))
       Target = $extension },
    @{ Name = 'saved tools (sync.ps1 -Tools)'
       Source = [IO.Path]::GetFullPath((Join-Path $root 'saved-tools'))
       Target = $toolsRoot }
)
foreach ($mirror in $mirrors) {
    if (-not (Test-Path -LiteralPath $mirror.Target)) {
        Write-Host "  $($mirror.Name): not installed; skipped."
        continue
    }
    $stale = @(Get-ChildItem -LiteralPath $mirror.Source -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' } | ForEach-Object {
        $relative = $_.FullName.Substring($mirror.Source.Length + 1)
        $destination = Join-Path $mirror.Target $relative
        $same = (Test-Path -LiteralPath $destination) -and
            ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)
        if (-not $same) { $relative }
    })
    if ($stale.Count -eq 0) {
        Write-Host "  $($mirror.Name): in sync."
    }
    else {
        Write-Host "  $($mirror.Name): $($stale.Count) file(s) differ (run ./sync.ps1):"
        $stale | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
        if ($stale.Count -gt 10) { Write-Host "    ... and $($stale.Count - 10) more" }
    }
}
Write-Host '  bridge: compare pane footer version against revit-c-bridge/version.txt; reinstall via package + install with Revit closed.'
