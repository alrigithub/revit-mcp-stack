param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'
$checks = @{
    'Revit' = "$env:ProgramFiles/Autodesk/Revit $RevitYear/Revit.exe"
    'Add-in manifest' = "$env:APPDATA/Autodesk/Revit/Addins/$RevitYear/RevitMcp.addin"
    'Bridge' = "$env:APPDATA/Autodesk/Revit/Addins/$RevitYear/RevitMcp/RevitMcp.Bridge.dll"
    'C# provider' = "$env:APPDATA/Autodesk/Revit/Addins/$RevitYear/RevitMcp/providers/roslyn/1/RevitMcp.RoslynProvider.dll"
    'Python extension' = "$env:APPDATA/pyRevit/Extensions/RevitMCP.extension/lib/revit_mcp_provider.py"
    'Bundled runtime' = "$env:LOCALAPPDATA/RevitMcp/mcp/runtime/python.exe"
}
foreach ($name in $checks.Keys | Sort-Object) {
    $present = Test-Path -LiteralPath $checks[$name]
    Write-Host ($(if ($present) { 'OK      ' } else { 'MISSING ' }) + $name)
}
$python = $checks['Bundled runtime']
if (Test-Path -LiteralPath $python) {
    & $python -B -c "from revit_mcp import __version__; from revit_mcp.discovery import list_instances; print('MCP version:',__version__); print('Live bridge instances:',len(list_instances()))"
    if ($LASTEXITCODE -ne 0) { throw 'The installed runtime failed its import check.' }
}
$mirrors = @(
    @{ source='revit-mcp/src/revit_mcp'; target="$env:LOCALAPPDATA/RevitMcp/mcp/revit_mcp" },
    @{ source='revit-pyrevit-extention/RevitMCP.extension'; target="$env:APPDATA/pyRevit/Extensions/RevitMCP.extension" }
)
foreach ($mirror in $mirrors) {
    $source = Join-Path $PSScriptRoot $mirror.source
    if (-not (Test-Path -LiteralPath $source)) { continue }
    $different = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' } | Where-Object {
        $destination = Join-Path $mirror.target $_.FullName.Substring($source.Length + 1)
        -not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $_.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash
    })
    Write-Host "$($mirror.source): $($different.Count) source files differ from installed copies."
}
Write-Host 'If no bridge is listed: open Revit and turn Bridge ON. For Python, also turn Python ON.'
Write-Host 'Claude connection: claude mcp get revit. Installed config: %LOCALAPPDATA%\RevitMcp\mcp\client-config.json'
