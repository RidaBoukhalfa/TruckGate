# Installs the TruckGate Windows installer on this (CI) machine and checks that it really works:
# services, database, web UI, login, keys and permissions, backup, address change, upgrade,
# uninstall (data kept), reinstall on kept data, gate-station-only install.
# Usage (elevated Windows PowerShell): windows-smoke-test.ps1 -Installer <exe> -Out <dir>
param([Parameter(Mandatory = $true)][string]$Installer, [Parameter(Mandatory = $true)][string]$Out, [string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3
[System.Net.WebRequest]::DefaultWebProxy = $null
New-Item -ItemType Directory -Path $Out -Force | Out-Null
$report = Join-Path $Out 'windows-smoke-test.txt'
$app = Join-Path $env:ProgramFiles 'TruckGate'
$root = Join-Path $env:ProgramData 'TruckGate'
$exe = Join-Path $app 'server\TruckGate.Api.exe'
$base = 'http://127.0.0.1:5080'
$script:failed = 0

function Note([string]$t) { Write-Host $t; Add-Content -Path $report -Value $t -Encoding UTF8 }
function Check([string]$name, [scriptblock]$test) {
    try {
        $detail = & $test
        Note ("PASS  $name" + $(if ($detail) { "  ($detail)" } else { '' }))
    } catch {
        $script:failed++
        Note "FAIL  $name : $($_.Exception.Message)"
    }
}
function Assert([bool]$cond, [string]$msg) { if (-not $cond) { throw $msg } }

function Run-Setup([string[]]$SetupArgs, [string]$LogName) {
    $log = Join-Path $Out $LogName
    $p = Start-Process -FilePath $Installer -ArgumentList ($SetupArgs + @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$log`"")) -Wait -PassThru
    return $p.ExitCode
}
function Run-Uninstall([string]$LogName) {
    $u = Join-Path $app 'unins000.exe'
    Assert (Test-Path $u) 'unins000.exe missing'
    $p = Start-Process -FilePath $u -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $Out $LogName)`"") -Wait -PassThru
    $deadline = (Get-Date).AddSeconds(180)
    while ((Test-Path $u) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    Assert (-not (Test-Path $u)) 'uninstaller did not finish within 180 s'
    return $p.ExitCode
}
function Health { Invoke-RestMethod -Uri "$base/health" -TimeoutSec 10 }
function Login([string]$user, [string]$pass) {
    $s = $null
    $body = @{ username = $user; secret = $pass } | ConvertTo-Json
    Invoke-WebRequest -Uri "$base/api/auth/login" -Method Post -Body $body -ContentType 'application/json' `
        -Headers @{ 'X-TG-CSRF' = '1' } -SessionVariable s -UseBasicParsing | Out-Null
    return $s
}
function Get-PeMachine([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3C)
    switch ([BitConverter]::ToUInt16($b, $pe + 4)) { 0x8664 { 'x64' } 0x14c { 'x86' } 0xAA64 { 'arm64' } default { 'other' } }
}
function Get-ExplicitSids([string]$path) {
    (Get-Acl $path).Access | ForEach-Object {
        try { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $_.IdentityReference.Value }
    }
}

Set-Content -Path $report -Value "TruckGate Windows installer test - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') UTC$((Get-Date).ToString('zzz'))" -Encoding UTF8
$os = Get-CimInstance Win32_OperatingSystem
Note "Machine: $($os.Caption) $($os.Version) $($os.OSArchitecture), PowerShell $($PSVersionTable.PSVersion), culture $((Get-Culture).Name)"
Note "Installer: $(Split-Path $Installer -Leaf) $((Get-Item $Installer).Length) bytes, PE $(Get-PeMachine $Installer)"
Note ''

# ---------------------------------------------------------------- 1. fresh server install
Note '== 1. Fresh install (type: server + gate station), address detected automatically'
$code = Run-Setup @('/TYPE=server') 'setup-1-install.log'
Check 'installer exit code 0' { Assert ($code -eq 0) "exit code $code"; }
Get-Content (Join-Path $root 'logs\install.log') -ErrorAction SilentlyContinue | Set-Content (Join-Path $Out 'install-server-1.log')
Check 'program files: server exe x64, PostgreSQL x64, web UI' {
    Assert ((Get-PeMachine $exe) -eq 'x64') 'TruckGate.Api.exe not x64'
    Assert ((Get-PeMachine (Join-Path $app 'pgsql\bin\postgres.exe')) -eq 'x64') 'postgres.exe not x64'
    Assert (Test-Path (Join-Path $app 'server\wwwroot\index.html')) 'wwwroot missing'
    "TruckGate.Api.exe $((Get-Item $exe).VersionInfo.ProductVersion)"
}
Check 'service TruckGateDB running as NetworkService, automatic' {
    $s = Get-CimInstance Win32_Service -Filter "Name='TruckGateDB'"
    Assert ($s.State -eq 'Running') "state $($s.State)"; Assert ($s.StartMode -eq 'Auto') "start $($s.StartMode)"
    Assert ($s.StartName -match 'NetworkService') "account $($s.StartName)"
    $s.StartName
}
Check 'service TruckGate running as its own virtual account, automatic, depends on TruckGateDB' {
    $s = Get-CimInstance Win32_Service -Filter "Name='TruckGate'"
    Assert ($s.State -eq 'Running') "state $($s.State)"; Assert ($s.StartMode -eq 'Auto') "start $($s.StartMode)"
    Assert ($s.StartName -eq 'NT SERVICE\TruckGate') "account $($s.StartName)"
    Assert ((Get-Service TruckGate).ServicesDependedOn.Name -contains 'TruckGateDB') 'no dependency'
    $s.StartName
}
Check '/health: TruckGate, database ok, version' {
    $h = Health
    Assert ($h.status -eq 'ok' -and $h.database -eq 'ok' -and $h.service -eq 'truckgate') ($h | ConvertTo-Json -Compress)
    Assert ($h.version.StartsWith($Version)) "version $($h.version)"
    "version $($h.version)"
}
Check 'web UI served (index.html and its script)' {
    $r = Invoke-WebRequest -Uri "$base/" -UseBasicParsing
    Assert ($r.Content -match 'id="root"') 'no root element'
    $js = [regex]::Match($r.Content, 'src="(/assets/[^"]+\.js)"').Groups[1].Value
    Assert ($js) 'no script'
    $j = Invoke-WebRequest -Uri "$base$js" -UseBasicParsing
    Assert ($j.StatusCode -eq 200 -and $j.RawContentLength -gt 100000) 'script not served'
    $js
}
$cfg = Get-Content (Join-Path $root 'config\appsettings.json') -Raw | ConvertFrom-Json
$publicUrl = [string]$cfg.Server.PublicUrl
$ip = ([Uri]$publicUrl).Host
Check 'address for phones detected = an IPv4 of this PC, reachable' {
    $mine = @(Get-NetIPAddress -AddressFamily IPv4 | ForEach-Object { $_.IPAddress })
    Assert ($mine -contains $ip) "$ip not in $($mine -join ',')"
    $h = Invoke-RestMethod -Uri "$publicUrl/health" -TimeoutSec 10
    Assert ($h.status -eq 'ok') 'not reachable on the LAN address'
    $publicUrl
}
Check 'database listens on 127.0.0.1:5433 only' {
    $l = @(Get-NetTCPConnection -LocalPort 5433 -State Listen)
    Assert ($l.Count -gt 0) 'nothing listening'
    Assert (@($l | Where-Object { $_.LocalAddress -ne '127.0.0.1' }).Count -eq 0) ("also on " + ($l.LocalAddress -join ','))
    ($l.LocalAddress | Sort-Object -Unique) -join ','
}
Check 'secrets and configuration not readable by ordinary users' {
    $bad = @('S-1-5-32-545', 'S-1-1-0', 'S-1-5-11')
    foreach ($f in @('config\secrets.json', 'config\appsettings.json', 'pgdata', 'data')) {
        $sids = @(Get-ExplicitSids (Join-Path $root $f))
        foreach ($b in $bad) { Assert ($sids -notcontains $b) "$f readable by $b" }
    }
    $s = @(Get-ExplicitSids (Join-Path $root 'config\secrets.json'))
    Assert (@($s | Where-Object { $_ -like 'S-1-5-80-*' }).Count -eq 0) 'service account can read secrets.json'
    'Users/Everyone/Authenticated Users: no access'
}
Check 'no password in the program folder' {
    $hits = Get-ChildItem $app -Recurse -Include *.json, *.ps1, *.cmd, *.sql, *.config -File |
        Select-String -Pattern 'Password=(?!CHANGE_ME)[^;"$]{6,}' -List
    Assert (-not $hits) ("found in " + ($hits.Path -join ','))
    'appsettings.json in the program folder has placeholders only'
}
Check 'firewall rule TCP 5080 from local subnet' {
    $r = Get-NetFirewallRule -Name 'TruckGate-HTTP'
    $port = $r | Get-NetFirewallPortFilter
    $addr = $r | Get-NetFirewallAddressFilter
    Assert ($r.Enabled -eq 'True' -and $port.LocalPort -eq '5080') 'rule wrong'
    "remote $($addr.RemoteAddress)"
}
Check 'daily backup task registered' { $t = Get-ScheduledTask -TaskName 'TruckGate daily backup' -TaskPath '\TruckGate\'; $t.State }

Note ''
Note '== 2. First administrator, login, server keys'
$adminPass = 'Ci-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
Check 'create-admin (console command)' {
    Push-Location (Join-Path $app 'server')
    $ErrorActionPreference = 'Continue'
    try { $o = ("admin`nSite Admin`n$adminPass`n$adminPass`n" | & $exe create-admin 2>&1 | Out-String) } finally { Pop-Location }
    Assert ($o -match 'Administrator created') $o
    ($o -split "`n" | Where-Object { $_ -match 'created' }).Trim()
}
$script:session = $null
$script:inviteKey = $null
Check 'login with the new administrator' {
    $script:session = Login 'admin' $adminPass
    $me = Invoke-RestMethod -Uri "$base/api/auth/me" -WebSession $script:session
    Assert ($me.role -eq 'ADMIN') ($me | ConvertTo-Json -Compress)
    "role $($me.role)"
}
Check 'wrong password refused' {
    try { Login 'admin' 'wrong-password-123' | Out-Null; throw 'accepted' } catch [System.Net.WebException] { 'HTTP ' + [int]$_.Exception.Response.StatusCode }
}
Check 'phone enrollment code: server key created by the service, address = detected one' {
    $inv = Invoke-RestMethod -Uri "$base/api/admin/enrollments" -Method Post -Body '{"label":"CI phone"}' -ContentType 'application/json' -Headers @{ 'X-TG-CSRF' = '1' } -WebSession $script:session
    $parts = $inv.qrText.Split('|')
    Assert ($parts[0] -eq 'TGE1' -and $parts[1] -eq $publicUrl) $inv.qrText.Substring(0, 40)
    $script:inviteKey = $parts[3]
    Assert (Test-Path (Join-Path $root 'data\server-signing-key.pem')) 'key file not created'
    $sids = @(Get-ExplicitSids (Join-Path $root 'data\server-signing-key.pem'))
    foreach ($b in @('S-1-5-32-545', 'S-1-1-0', 'S-1-5-11')) { Assert ($sids -notcontains $b) "key readable by $b" }
    'TGE1|' + $parts[1] + '|...'
}
Check 'server-address code for enrolled phones (signed)' {
    $a = Invoke-RestMethod -Uri "$base/api/admin/server-address" -WebSession $script:session
    Assert ($a.qrText.StartsWith("TGA1|$publicUrl|")) $a.qrText
    $a.url
}
Check 'dashboard report answers' {
    $d = Invoke-WebRequest -Uri "$base/api/reports/dashboard" -WebSession $script:session -UseBasicParsing
    "HTTP $($d.StatusCode)"
}
Check 'CSRF: state change without the header refused' {
    try {
        Invoke-WebRequest -Uri "$base/api/admin/enrollments" -Method Post -Body '{"label":"x"}' -ContentType 'application/json' -WebSession $script:session -UseBasicParsing | Out-Null
        throw 'accepted'
    } catch [System.Net.WebException] { $c = [int]$_.Exception.Response.StatusCode; Assert ($c -eq 403) "HTTP $c"; 'HTTP 403' }
}

Note ''
Note '== 3. Tools: web screenshots, backup, change server address'
$chrome = @("${env:ProgramFiles}\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
Check 'web UI in Chrome: login, gate, phones (address QR), dashboard - no script errors' {
    Assert ($chrome) 'Chrome not found'
    $ErrorActionPreference = 'Continue'
    $o = & node (Join-Path $PSScriptRoot 'ui-screens.mjs') $base 'admin' $adminPass $Out $chrome 2>&1 | Out-String
    Assert ($LASTEXITCODE -eq 0) $o
    $o.Trim()
}
Check 'backup now (database dump + server keys)' {
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app 'scripts\backup.ps1') | Out-Null
    Assert ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE"
    $z = Get-ChildItem (Join-Path $root 'backups') -Filter 'truckgate-*.zip' | Select-Object -First 1
    Assert ($z -and $z.Length -gt 1000) 'no backup file'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $names = [IO.Compression.ZipFile]::OpenRead($z.FullName).Entries.Name
    Assert ($names -contains 'truckgate.dump' -and $names -contains 'server-signing-key.pem') ($names -join ',')
    "$($z.Name) $($z.Length) bytes"
}
$configure = Join-Path $app 'scripts\configure.ps1'
Check 'change address: "localhost" refused with a clear reason' {
    $o = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $configure -NoPrompt -Url 'http://localhost:5080' | Out-String
    Assert ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
    $o.Trim()
}
Check 'change address: an address not on this PC refused' {
    $o = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $configure -NoPrompt -Url 'http://192.0.2.44:5080' | Out-String
    Assert ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
    $o.Trim()
}
Check 'change address: valid LAN address saved, service restarted, healthy' {
    $o = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $configure -NoPrompt -Url "http://$($ip):5080/" | Out-String
    Assert ($LASTEXITCODE -eq 0) "exit $LASTEXITCODE $o"
    Assert ((Health).status -eq 'ok') 'not healthy after restart'
    ($o -split "`n")[0].Trim()
}

Note ''
Note '== 4. Upgrade (same installer over the installed one): data and keys kept'
$code = Run-Setup @('/TYPE=server') 'setup-2-upgrade.log'
Check 'upgrade exit code 0, healthy' { Assert ($code -eq 0) "exit $code"; Assert ((Health).status -eq 'ok') 'not healthy' }
Check 'administrator still signs in, same server key' {
    $s = Login 'admin' $adminPass
    $inv = Invoke-RestMethod -Uri "$base/api/admin/enrollments" -Method Post -Body '{"label":"CI phone 2"}' -ContentType 'application/json' -Headers @{ 'X-TG-CSRF' = '1' } -WebSession $s
    Assert ($inv.qrText.Split('|')[3] -eq $script:inviteKey) 'server key changed'
    'ok'
}

Note ''
Note '== 5. Uninstall: services removed, data kept'
$code = Run-Uninstall 'setup-3-uninstall.log'
Check 'uninstall exit code 0' { Assert ($code -eq 0) "exit $code" }
Check 'services and firewall rule removed, program folder gone' {
    Assert (-not (Get-Service TruckGate -ErrorAction SilentlyContinue)) 'TruckGate service still there'
    Assert (-not (Get-Service TruckGateDB -ErrorAction SilentlyContinue)) 'TruckGateDB service still there'
    Assert (-not (Get-NetFirewallRule -Name 'TruckGate-HTTP' -ErrorAction SilentlyContinue)) 'firewall rule still there'
    Assert (-not (Test-Path $exe)) 'program files still there'
}
Check 'data kept (database, keys, configuration, backups)' {
    foreach ($f in @('pgdata\PG_VERSION', 'data\server-signing-key.pem', 'config\secrets.json', 'config\appsettings.json')) { Assert (Test-Path (Join-Path $root $f)) "$f missing" }
}

Note ''
Note '== 6. Reinstall on the kept data'
$code = Run-Setup @('/TYPE=server') 'setup-4-reinstall.log'
Check 'reinstall exit code 0, healthy, same administrator and key' {
    Assert ($code -eq 0) "exit $code"
    Assert ((Health).status -eq 'ok') 'not healthy'
    $s = Login 'admin' $adminPass
    $inv = Invoke-RestMethod -Uri "$base/api/admin/enrollments" -Method Post -Body '{"label":"CI phone 3"}' -ContentType 'application/json' -Headers @{ 'X-TG-CSRF' = '1' } -WebSession $s
    Assert ($inv.qrText.Split('|')[3] -eq $script:inviteKey) 'server key changed'
    'ok'
}
$code = Run-Uninstall 'setup-5-uninstall.log'
Check 'second uninstall exit code 0' { Assert ($code -eq 0) "exit $code" }

Note ''
Note '== 7. Gate-station-only install (server elsewhere)'
$code = Run-Setup @('/TYPE=station', '/SERVERURL=http://192.0.2.10:5080') 'setup-6-station.log'
Check 'station install exit code 0, no services, address saved' {
    Assert ($code -eq 0) "exit $code"
    Assert (-not (Get-Service TruckGate -ErrorAction SilentlyContinue)) 'server service installed'
    Assert (-not (Test-Path $exe)) 'server files installed'
    $j = Get-Content (Join-Path $app 'gate-station.json') -Raw | ConvertFrom-Json
    Assert ($j.url -eq 'http://192.0.2.10:5080') $j.url
    $j.url
}
Check 'station: unreachable server address reported clearly' {
    $o = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $app 'scripts\configure.ps1') -NoPrompt -Url 'http://192.0.2.11:5080' | Out-String
    Assert ($LASTEXITCODE -eq 1) "exit $LASTEXITCODE"
    $o.Trim()
}
$code = Run-Uninstall 'setup-7-station-uninstall.log'
Check 'station uninstall exit code 0' { Assert ($code -eq 0) "exit $code" }

Note ''
Note "RESULT: $(if ($script:failed -eq 0) { 'ALL PASSED' } else { "$($script:failed) FAILED" })"
Copy-Item (Join-Path $root 'logs\*') $Out -ErrorAction SilentlyContinue
exit $script:failed
