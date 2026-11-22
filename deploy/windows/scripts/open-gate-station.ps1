# TruckGate gate station: opens the TruckGate web page in Chrome (or Edge) with silent receipt
# printing (--kiosk-printing: prints to the Windows default printer, so make the receipt printer
# the default). Uses its own browser profile so the option applies even if the browser is open.
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $PSScriptRoot 'common.ps1')
$appDir = Split-Path $PSScriptRoot -Parent
$station = Join-Path $appDir 'gate-station.json'
$url = ''
if (Test-Path $station) { try { $url = [string](Get-Content $station -Raw | ConvertFrom-Json).url } catch { } }
if (-not $url) {
    [Windows.Forms.MessageBox]::Show("No TruckGate server address is set.`nStart menu > TruckGate > Change server address.`n`nلم يُحدَّد عنوان خادم TruckGate. استعمل: تغيير عنوان الخادم.", 'TruckGate', 'OK', 'Warning') | Out-Null
    exit 1
}
$check = Test-TgServer -Url $url -TimeoutSec 5
if (-not $check.Ok) {
    $answer = [Windows.Forms.MessageBox]::Show($check.Message + "`n`nخادم TruckGate لا يجيب. هل تريد فتح الصفحة رغم ذلك؟`nOpen the page anyway?", 'TruckGate', 'YesNo', 'Warning')
    if ($answer -ne 'Yes') { exit 1 }
}
$browser = $null
foreach ($name in @('chrome.exe', 'msedge.exe')) {
    foreach ($hive in @('HKLM:', 'HKCU:')) {
        $k = "$hive\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$name"
        if (-not $browser -and (Test-Path $k)) {
            $p = (Get-ItemProperty $k).'(default)'
            if ($p -and (Test-Path $p.Trim('"'))) { $browser = $p.Trim('"') }
        }
    }
}
if (-not $browser) {
    foreach ($p in @("${env:ProgramFiles}\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe")) {
        if (-not $browser -and (Test-Path $p)) { $browser = $p }
    }
}
if (-not $browser) { Start-Process $url; exit 0 }
$profileDir = Join-Path $env:LOCALAPPDATA 'TruckGate\gate-browser'
Start-Process -FilePath $browser -ArgumentList @("--user-data-dir=`"$profileDir`"", '--kiosk-printing', '--no-first-run',
    '--no-default-browser-check', '--disable-translate', "--app=$url")
