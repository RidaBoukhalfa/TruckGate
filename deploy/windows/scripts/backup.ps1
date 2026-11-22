# TruckGate - database dump plus the server keys, into %ProgramData%\TruckGate\backups.
# Run daily by the task "TruckGate daily backup" (SYSTEM) or from the Start menu (administrator).
# Camera pictures (data\snapshots) are NOT included: copy that folder to another disk yourself.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Get-TgRoot
$appDir = Split-Path $PSScriptRoot -Parent
Initialize-TgLog (Join-Path $root 'logs\backup.log')
try {
    $s = Get-Content (Join-Path $root 'config\secrets.json') -Raw | ConvertFrom-Json
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $work = Join-Path $root "backups\truckgate-$stamp"
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Invoke-TgTool -Exe (Join-Path $appDir 'pgsql\bin\pg_dump.exe') -Environment @{ PGPASSWORD = $s.ownerPassword } -Arguments @(
        '-h', '127.0.0.1', '-p', "$($s.dbPort)", '-U', 'truckgate_owner', '-d', 'truckgate', '-Fc', '-f', (Join-Path $work 'truckgate.dump')) | Out-Null
    foreach ($f in @('server-signing-key.pem', 'secrets.key')) {
        $src = Join-Path $root "data\$f"
        if (Test-Path $src) { Copy-Item $src $work }
    }
    $dp = Join-Path $root 'data\dp-keys'
    if (Test-Path $dp) { Copy-Item $dp (Join-Path $work 'dp-keys') -Recurse }
    Copy-Item (Join-Path $root 'config\appsettings.json') $work
    Copy-Item (Join-Path $root 'config\secrets.json') $work
    $zip = "$work.zip"
    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
    Remove-Item $work -Recurse -Force
    Get-ChildItem (Join-Path $root 'backups') -Filter 'truckgate-*.zip' | Sort-Object Name -Descending | Select-Object -Skip 14 | Remove-Item -Force
    Write-TgLog "Backup written: $zip"
    exit 0
} catch {
    Write-TgLog ('BACKUP FAILED: ' + $_.Exception.Message)
    try { Write-EventLog -LogName Application -Source 'TruckGate' -EntryType Error -EventId 900 -Message ('Backup failed: ' + $_.Exception.Message) } catch { }
    exit 1
}
