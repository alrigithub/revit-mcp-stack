$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/instances'
Get-ChildItem -LiteralPath $root -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $record = Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json
        $process = Get-Process -Id $record.pid -ErrorAction Stop
        if ($process.StartTime.ToFileTimeUtc() -ne [long]$record.process_start_utc_ticks) { Remove-Item -LiteralPath $_.FullName -Force }
    } catch { Remove-Item -LiteralPath $_.FullName -Force }
}
