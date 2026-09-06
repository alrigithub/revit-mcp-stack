param([ValidateSet(2025, 2026, 2027)][int]$RevitYear = 2025)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'scripts/common.ps1')
$version = (Get-Content -LiteralPath (Join-Path $root 'revit-c-bridge/version.txt') -Raw).Trim()
foreach ($component in @('revit-c-bridge','revit-pyrevit-extention','revit-mcp')) {
    $arguments = @{}
    if ($component -eq 'revit-c-bridge') { $arguments.RevitYear = $RevitYear }
    & (Join-Path $root "$component/scripts/package.ps1") @arguments
    if ($LASTEXITCODE -ne 0) { throw "$component package failed." }
}
$dist = Join-Path $root 'dist'
Reset-Directory $dist $root
$name = "RevitMcp-v$version-Revit$RevitYear"
$stage = Join-Path $dist $name
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($file in @('Install.cmd','install.ps1','uninstall.ps1','doctor.ps1','README.md','THIRD-PARTY.md')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage
}
New-Item -ItemType Directory -Path (Join-Path $stage 'scripts'),(Join-Path $stage 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts/common.ps1'),(Join-Path $root 'scripts/register-claude.ps1') -Destination (Join-Path $stage 'scripts')
Copy-Item -LiteralPath (Join-Path $root 'docs/tools.md'),(Join-Path $root 'docs/release.md') -Destination (Join-Path $stage 'docs')
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $stage -Recurse
foreach ($component in @('revit-c-bridge','revit-pyrevit-extention','revit-mcp')) {
    $target = Join-Path $stage $component
    New-Item -ItemType Directory -Path (Join-Path $target 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root "$component/sbom.json") -Destination $target
    foreach ($script in @('install.ps1','uninstall.ps1','status.ps1')) {
        Copy-Item -LiteralPath (Join-Path $root "$component/scripts/$script") -Destination (Join-Path $target 'scripts')
    }
    if ($component -eq 'revit-c-bridge') {
        New-Item -ItemType Directory -Path (Join-Path $target 'artifacts') | Out-Null
        Copy-Item -LiteralPath (Join-Path $root "$component/artifacts/$RevitYear") -Destination (Join-Path $target 'artifacts') -Recurse
    } else { Copy-Item -LiteralPath (Join-Path $root "$component/artifacts") -Destination $target -Recurse }
}
Write-PackageHashes $stage 'sha256-manifest.json' @{ version=$version; revit_year=$RevitYear }
$zip = Join-Path $dist ($name + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
$verifiedStage = Assert-ChildPath $stage $dist
Remove-Item -LiteralPath $verifiedStage -Recurse -Force
Write-Host "Release ready: $zip"
