# TruckGate - change the server address, with a connection test.
#  * On the server PC: the address phones use (Server:PublicUrl), then restarts the service.
#  * On a gate-station-only PC: the address of the TruckGate server this PC opens.
# Without -Url a small window asks for it. -NoPrompt (with -Url) is for scripts; exit code 0 = saved.
param([string]$Url, [switch]$NoPrompt)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    if ($NoPrompt) { Write-Error 'Run as administrator.'; exit 3 }
    Start-Process -Verb RunAs -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    exit 0
}

$appDir = Split-Path $PSScriptRoot -Parent
$root = Get-TgRoot
$siteConfig = Join-Path $root 'config\appsettings.json'
$isServer = (Test-Path (Join-Path $appDir 'server\TruckGate.Api.exe')) -and (Test-Path $siteConfig)
$station = Join-Path $appDir 'gate-station.json'
Initialize-TgLog (Join-Path $root 'logs\configure.log')

function Get-Current {
    if ($isServer) { return [string](Get-Content $siteConfig -Raw | ConvertFrom-Json).Server.PublicUrl }
    if (Test-Path $station) { return [string](Get-Content $station -Raw | ConvertFrom-Json).url }
    return ''
}

# Returns $null when the address can be saved, else the reason.
function Test-Address([string]$u) {
    $problem = Test-TgUrlFormat $u
    if ($problem) { return "The address $problem. Example: http://192.168.1.10:5080" }
    $host_ = ([Uri]$u).Host
    if ($isServer) {
        if ($host_ -eq 'localhost' -or $host_ -like '127.*' -or $host_ -eq '::1') {
            return 'Phones cannot reach "localhost": use this PC''s IP address on the factory network.'
        }
        $mine = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object { $_.IPAddress })
        if ($host_ -match '^\d+\.\d+\.\d+\.\d+$' -and $mine -notcontains $host_) {
            return "$host_ is not an address of this PC (this PC has: $($mine -join ', '))."
        }
        $port = ([Uri]$u).Port
        $r = Test-TgServer -Url ("http://127.0.0.1:$port") -TimeoutSec 5
        if (-not $r.Ok) { return 'The TruckGate service on this PC does not answer on port ' + $port + ': ' + $r.Message }
        return $null
    }
    $r = Test-TgServer -Url $u -TimeoutSec 6
    if (-not $r.Ok) { return $r.Message }
    return $null
}

function Save-Address([string]$u) {
    $u = $u.TrimEnd('/')
    if ($isServer) {
        $cfg = Get-Content $siteConfig -Raw | ConvertFrom-Json
        $cfg.Server.PublicUrl = $u
        Write-TgUtf8 $siteConfig ($cfg | ConvertTo-Json -Depth 8)
        Restart-Service -Name 'TruckGate' -Force
        $port = ([Uri]$u).Port
        $h = Wait-TgHealth -Url "http://127.0.0.1:$port" -Seconds 60
        if (-not $h.Ok) { throw 'Saved, but the service did not restart correctly: ' + $h.Message }
        $h2 = Test-TgServer -Url $u -TimeoutSec 6
        Write-TgLog "Phones address set to $u. Check from this PC: $($h2.Message)"
        return "Saved. New phones will enroll with $u.`nPhones already enrolled: Admin > Phones > Server address QR, scan it in each phone's settings."
    }
    Write-TgUtf8 $station ((@{ url = $u } | ConvertTo-Json))
    Write-TgLog "Gate station server set to $u"
    return "Saved: this gate station opens $u"
}

if ($NoPrompt) {
    if (-not $Url) { Write-Error '-Url is required with -NoPrompt'; exit 2 }
    $why = Test-Address $Url
    if ($why) { Write-TgLog ('Not saved: ' + $why); Write-Output $why; exit 1 }
    Write-Output (Save-Address $Url)
    exit 0
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object Windows.Forms.Form
$form.Text = 'TruckGate - server address / عنوان الخادم'
$form.Size = New-Object Drawing.Size(560, 300)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$label = New-Object Windows.Forms.Label
$label.Location = New-Object Drawing.Point(14, 12)
$label.Size = New-Object Drawing.Size(520, 58)
if ($isServer) {
    $label.Text = "Address the quarry phones use to reach THIS server (this PC's IP on the factory network):`nالعنوان الذي تستعمله هواتف المقلع للوصول إلى هذا الخادم (عنوان IP لهذا الحاسوب على شبكة المصنع)"
} else {
    $label.Text = "Address of the TruckGate server this gate station opens:`nعنوان خادم TruckGate الذي تفتحه محطة البوابة هذه"
}
$box = New-Object Windows.Forms.TextBox
$box.Location = New-Object Drawing.Point(14, 76)
$box.Size = New-Object Drawing.Size(516, 24)
$box.Text = $(if ($Url) { $Url } else { Get-Current })
if (-not $box.Text) { $ip = Get-TgLanIPv4; $box.Text = $(if ($isServer -and $ip) { "http://$($ip):5080" } else { 'http://192.168.1.10:5080' }) }
$result = New-Object Windows.Forms.Label
$result.Location = New-Object Drawing.Point(14, 108)
$result.Size = New-Object Drawing.Size(520, 100)
$test = New-Object Windows.Forms.Button
$test.Text = 'Test / اختبار'
$test.Location = New-Object Drawing.Point(14, 214)
$test.Size = New-Object Drawing.Size(140, 30)
$save = New-Object Windows.Forms.Button
$save.Text = 'Save / حفظ'
$save.Location = New-Object Drawing.Point(250, 214)
$save.Size = New-Object Drawing.Size(140, 30)
$close = New-Object Windows.Forms.Button
$close.Text = 'Close / إغلاق'
$close.Location = New-Object Drawing.Point(396, 214)
$close.Size = New-Object Drawing.Size(134, 30)
$close.Add_Click({ $form.Close() })
$test.Add_Click({
    $form.Cursor = 'WaitCursor'
    $why = Test-Address $box.Text.Trim()
    $form.Cursor = 'Default'
    if ($why) { $result.ForeColor = 'DarkRed'; $result.Text = $why } else { $result.ForeColor = 'DarkGreen'; $result.Text = 'OK - ' + (Test-TgServer -Url $box.Text.Trim()).Message }
})
$save.Add_Click({
    $form.Cursor = 'WaitCursor'
    try {
        $why = Test-Address $box.Text.Trim()
        if ($why) {
            $form.Cursor = 'Default'
            $a = [Windows.Forms.MessageBox]::Show($why + "`n`nSave anyway? / حفظ رغم ذلك؟", 'TruckGate', 'YesNo', 'Warning')
            if ($a -ne 'Yes') { return }
            if (Test-TgUrlFormat $box.Text.Trim()) { return }
        }
        $result.ForeColor = 'DarkGreen'
        $result.Text = Save-Address $box.Text.Trim()
    } catch {
        $result.ForeColor = 'DarkRed'; $result.Text = $_.Exception.Message
    } finally { $form.Cursor = 'Default' }
})
$form.Controls.AddRange(@($label, $box, $result, $test, $save, $close))
[void]$form.ShowDialog()
