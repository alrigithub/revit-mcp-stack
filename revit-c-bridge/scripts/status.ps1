param([Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear)
$targetRoot = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitYear"
[pscustomobject]@{
    RevitYear = $RevitYear
    Manifest = Test-Path -LiteralPath (Join-Path $targetRoot 'RevitMcp.addin')
    BridgeDll = Test-Path -LiteralPath (Join-Path $targetRoot 'RevitMcp/RevitMcp.Bridge.dll')
    RoslynProvider = Test-Path -LiteralPath (Join-Path $targetRoot 'RevitMcp/providers/roslyn/1/RevitMcp.RoslynProvider.dll')
    DiscoveryRecords = @(Get-ChildItem -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/instances') -Filter '*.json' -ErrorAction SilentlyContinue).Count
} | Format-List
