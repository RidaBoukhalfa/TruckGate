# TruckGate - removes the Windows services, firewall rule and backup task. Keeps all data
# (%ProgramData%\TruckGate: database, server keys, pictures, configuration, backups).
param([string]$AppDir = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-TgLog (Join-Path ([IO.Path]::GetTempPath()) 'truckgate-uninstall.log')

foreach ($name in @('TruckGate', 'TruckGateDB')) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
        Invoke-TgTool -Exe "$env:SystemRoot\System32\sc.exe" -Arguments @('delete', $name) -AllowFailure | Out-Null
    }
}
Get-NetFirewallRule -Name 'TruckGate-HTTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Unregister-ScheduledTask -TaskName 'TruckGate daily backup' -TaskPath '\TruckGate\' -Confirm:$false -ErrorAction SilentlyContinue
Write-TgLog ('Services removed. Data kept in ' + (Get-TgRoot))
exit 0
