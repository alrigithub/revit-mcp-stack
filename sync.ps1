# Copies the two plain-source deployed mirrors from the repo: the Python MCP server and the pyRevit extension.
# The C# bridge is not handled here; it needs package.ps1 + install.ps1 with Revit closed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pairs = @(
    @{ Name = 'revit-mcp server'
       Source = [IO.Path]::GetFullPath((Join-Path $root 'revit-mcp/src/revit_mcp'))
       Target = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/mcp/0.9.0/revit_mcp' },
    @{ Name = 'pyRevit extension'
       Source = [IO.Path]::GetFullPath((Join-Path $root 'revit-pyrevit-extention/RevitMCP.extension'))
       Target = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'pyRevit/Extensions/RevitMCP.extension' }
)

foreach ($pair in $pairs) {
    if (-not (Test-Path -LiteralPath $pair.Target)) {
        Write-Host "$($pair.Name): not installed ($($pair.Target)); skipped."
        continue
    }
    $updated = 0
    $checked = 0
    Get-ChildItem -LiteralPath $pair.Source -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' } | ForEach-Object {
        $relative = $_.FullName.Substring($pair.Source.Length + 1)
        $destination = Join-Path $pair.Target $relative
        $checked++
        $same = (Test-Path -LiteralPath $destination) -and
            ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash)
        if (-not $same) {
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
            Write-Host "  updated $relative"
            $updated++
        }
    }
    Write-Host "$($pair.Name): $updated of $checked files updated."
}

Write-Host ''
Write-Host 'After a sync:'
Write-Host '- Server changes load when the MCP client restarts its stdio server.'
Write-Host '- Provider changes load after the Python ON click in Revit.'
Write-Host '- Bridge (C#) changes: revit-c-bridge/scripts/package.ps1, close Revit, install.ps1, reopen.'
