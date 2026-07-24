$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet run --project (Join-Path $root 'tests/RevitMcp.Core.Tests/RevitMcp.Core.Tests.csproj') -c Release
exit $LASTEXITCODE
