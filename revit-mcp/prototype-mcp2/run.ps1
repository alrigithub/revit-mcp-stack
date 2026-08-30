# PROTOTYPE — THROWAWAY. One command: ./run.ps1
# Bootstraps a wipeable venv with mcp==2.1.1 (never touches the bundled runtime)
# and drives probe_server.py over real stdio.
$venv = "$env:LOCALAPPDATA\Temp\PROTOTYPE-revitmcp-mcp2-venv-wipe-me"
if (-not (Test-Path "$venv\Scripts\python.exe")) {
    & "$env:LOCALAPPDATA\RevitMcp\mcp\runtime\Scripts\python.exe" -m venv $venv
    & "$venv\Scripts\python.exe" -m pip install --quiet "mcp==2.1.1"
}
& "$venv\Scripts\python.exe" "$PSScriptRoot\probe_client.py"
