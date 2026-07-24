param(
    [Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear,
    [string]$RevitApiDir
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path -Parent $PSScriptRoot
$properties = @("-p:RevitYear=$RevitYear")
if ($RevitApiDir) {
    $api = Join-Path $RevitApiDir 'RevitAPI.dll'
    if (-not (Test-Path -LiteralPath $api)) { throw "Revit $RevitYear API not found at $RevitApiDir" }
    $properties += '-p:UseInstalledRevitApi=true'
    $properties += "-p:RevitApiDir=$RevitApiDir"
}
dotnet build (Join-Path $root 'src/RevitMcp.RoslynProvider/RevitMcp.RoslynProvider.csproj') -c Release @properties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build (Join-Path $root 'src/RevitMcp.Bridge/RevitMcp.Bridge.csproj') -c Release @properties
exit $LASTEXITCODE
