$root = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'RevitMcp/instances'
foreach ($file in @(Get-ChildItem -LiteralPath $root -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
    $stale = $false
    try {
        $record = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
        $process = Get-Process -Id $record.pid -ErrorAction Stop
        $stale = $process.StartTime.ToFileTimeUtc() -ne [long]$record.process_start_utc_ticks
    } catch { $stale = $true }
    if ($stale) { Remove-Item -LiteralPath $file.FullName -Force }
}
