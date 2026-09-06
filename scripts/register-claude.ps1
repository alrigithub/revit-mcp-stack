param([string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp'))
$ErrorActionPreference = 'Stop'
$python = Join-Path $InstallRoot 'runtime/python.exe'
if (-not (Test-Path -LiteralPath $python)) { throw 'Install the MCP runtime first.' }
$claude = Get-Command claude -ErrorAction SilentlyContinue
if (-not $claude -and (Test-Path -LiteralPath "$env:USERPROFILE/.local/bin/claude.exe")) {
    $claude = Get-Command "$env:USERPROFILE/.local/bin/claude.exe"
}
if (-not $claude) {
    Write-Warning "Claude Code was not found. Install it, then run scripts/register-claude.ps1. Manual config: $InstallRoot/client-config.json"
    return
}
$previousPreference = $ErrorActionPreference
try { $ErrorActionPreference = 'Continue'; $existing = (& $claude.Source mcp get revit 2>&1 | Out-String); $getExit = $LASTEXITCODE }
finally { $ErrorActionPreference = $previousPreference }
if ($getExit -eq 0) {
    if ($existing -notmatch '(?i)RevitMcp[\\/]+mcp[\\/]') {
        throw "Claude already has a different MCP server named 'revit'. Rename that entry before registering this bridge."
    }
    & $claude.Source mcp remove --scope user revit
    if ($LASTEXITCODE -ne 0) { throw 'Could not update the user-scope Revit registration. Check claude mcp get revit.' }
}
& $claude.Source mcp add --transport stdio --scope user revit -- $python -B -m revit_mcp.server
if ($LASTEXITCODE -ne 0) { throw 'Claude registration failed. Runtime remains installed; rerun scripts/register-claude.ps1.' }
