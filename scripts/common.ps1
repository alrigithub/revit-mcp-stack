$ErrorActionPreference = 'Stop'

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing path outside $Parent" }
    return $full
}

function Reset-Directory([string]$Path, [string]$Parent) {
    $full = Assert-ChildPath $Path $Parent
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

function Test-PackageHashes([string]$Root, [string]$Manifest = 'sha256-manifest.json') {
    $full = [IO.Path]::GetFullPath($Root)
    $data = Get-Content -LiteralPath (Join-Path $full $Manifest) -Raw | ConvertFrom-Json
    if (-not $data.files -or $data.files.Count -eq 0) { throw "Empty package manifest at $full" }
    foreach ($file in $data.files) {
        $path = Assert-ChildPath (Join-Path $full $file.path) $full
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $($file.path)" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Package hash mismatch: $($file.path)" }
    }
}

function Write-PackageHashes([string]$Root, [string]$Manifest = 'sha256-manifest.json', [hashtable]$Metadata = @{}) {
    $full = [IO.Path]::GetFullPath($Root)
    $files = @(Get-ChildItem -LiteralPath $full -Recurse -File | Where-Object { $_.FullName -ne (Join-Path $full $Manifest) } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($full.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = $_.Length }
    })
    $data = [ordered]@{ schema = 1; files = $files }
    foreach ($key in $Metadata.Keys) { $data[$key] = $Metadata[$key] }
    $data | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $full $Manifest) -Encoding utf8
}

function Get-BuildPython {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp/runtime/python.exe'),
        (Join-Path $env:LOCALAPPDATA 'RevitMcp/mcp/runtime/Scripts/python.exe')
    )
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { return $candidate } }
    throw 'Bundled Python is missing. Install the release first, or pass -BuildPython to the MCP packager.'
}

function Install-Tree([string]$Source, [string]$Target) {
    $targetPath = [IO.Path]::GetFullPath($Target)
    $parent = Split-Path -Parent $targetPath
    $targetPath = Assert-ChildPath $targetPath $parent
    $incoming = Assert-ChildPath ($targetPath + '.incoming-' + [Guid]::NewGuid().ToString('N')) $parent
    $previous = Assert-ChildPath ($targetPath + '.previous-' + [Guid]::NewGuid().ToString('N')) $parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $incoming -Recurse -Force
    try {
        if (Test-Path -LiteralPath $targetPath) { Move-Item -LiteralPath $targetPath -Destination $previous }
        Move-Item -LiteralPath $incoming -Destination $targetPath
    } catch {
        if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $targetPath)) { Move-Item -LiteralPath $previous -Destination $targetPath }
        throw "Install could not replace $targetPath. Close Revit and MCP clients, then retry. $($_.Exception.Message)"
    } finally {
        if (Test-Path -LiteralPath $incoming) { Remove-Item -LiteralPath $incoming -Recurse -Force }
    }
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
}
