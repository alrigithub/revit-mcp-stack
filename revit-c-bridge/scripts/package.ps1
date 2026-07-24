param(
    [Parameter(Mandatory=$true)][ValidateSet(2025, 2026, 2027)][int]$RevitYear,
    [string]$RevitApiDir,
    [string]$CertificateThumbprint
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$buildArguments = @{ RevitYear = $RevitYear }
if ($RevitApiDir) { $buildArguments.RevitApiDir = $RevitApiDir }
& (Join-Path $PSScriptRoot 'build.ps1') @buildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$stage = Join-Path $root "artifacts/$RevitYear"
$addinDir = Join-Path $stage 'RevitMcp'
$providerDir = Join-Path $addinDir 'providers/roslyn/1'
New-Item -ItemType Directory -Force -Path $providerDir | Out-Null
$targetFramework = if ($RevitYear -eq 2027) { 'net10.0-windows' } else { 'net8.0-windows' }
$bridgeOut = Join-Path $root "src/RevitMcp.Bridge/bin/Release/$targetFramework"
foreach ($name in @('RevitMcp.Bridge.dll','RevitMcp.Bridge.pdb','RevitMcp.Contracts.dll','RevitMcp.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $bridgeOut $name) -Destination $addinDir -Force
}
$providerOut = Join-Path $root "src/RevitMcp.RoslynProvider/bin/Release/$targetFramework"
Get-ChildItem -LiteralPath $providerOut -Filter '*.dll' | Where-Object { $_.Name -ne 'RevitMcp.Contracts.dll' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $providerDir -Force
}

$installAssembly = Join-Path ([Environment]::GetFolderPath('ApplicationData')) "Autodesk/Revit/Addins/$RevitYear/RevitMcp/RevitMcp.Bridge.dll"
$manifest = (Get-Content -Raw -LiteralPath (Join-Path $root "manifests/$RevitYear.addin")).Replace('{{ASSEMBLY}}', $installAssembly)
$manifest | Set-Content -LiteralPath (Join-Path $stage 'RevitMcp.addin') -Encoding utf8

if ($CertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$CertificateThumbprint"
    Get-ChildItem -LiteralPath $addinDir -Recurse -Filter '*.dll' | ForEach-Object { Set-AuthenticodeSignature -FilePath $_.FullName -Certificate $certificate -HashAlgorithm SHA256 | Out-Null }
}

$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Name -ne 'uninstall-manifest.json' } | ForEach-Object {
    [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
}
[ordered]@{ schema = 1; revit_year = $RevitYear; signed = [bool]$CertificateThumbprint; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'uninstall-manifest.json') -Encoding utf8

$versionFile = Join-Path $root 'version.txt'
$packagedVersion = (Get-Content -Raw -LiteralPath $versionFile).Trim()
$parts = $packagedVersion.Split('.')
$parts[-1] = ([int]$parts[-1] + 1).ToString('00')
($parts -join '.') | Set-Content -LiteralPath $versionFile -NoNewline
Write-Host "Packaged reviewed Revit $RevitYear files at $stage (v$packagedVersion; next build v$($parts -join '.'))"
