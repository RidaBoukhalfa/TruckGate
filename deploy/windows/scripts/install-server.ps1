# TruckGate - server setup, run by the installer (elevated) after the files are copied.
# Idempotent: a second run (upgrade or repair) keeps the database, keys and configuration.
#
#   install-server.ps1 -AppDir "C:\Program Files\TruckGate" [-ServerUrl http://192.168.1.10:5080 | auto]
#
# What it does: PostgreSQL 16 cluster on 127.0.0.1:5433 (service TruckGateDB, NetworkService),
# database roles with random passwords (stored only in %ProgramData%\TruckGate\config, readable by
# administrators; the service reads the app password only), migrations, Windows service TruckGate
# (virtual account NT SERVICE\TruckGate), firewall rule for TCP 5080 from the local subnet, daily backup task.
param(
    [Parameter(Mandatory = $true)][string]$AppDir,
    [string]$ServerUrl = 'auto',
    [int]$HttpPort = 5080,
    [int]$DbPort = 5433
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Get-TgRoot
$cfgDir = Join-Path $root 'config'
$dataDir = Join-Path $root 'data'
$pgData = Join-Path $root 'pgdata'
$logDir = Join-Path $root 'logs'
$backupDir = Join-Path $root 'backups'
$serverDir = Join-Path $AppDir 'server'
$pgBin = Join-Path $AppDir 'pgsql\bin'
$exe = Join-Path $serverDir 'TruckGate.Api.exe'
$secretsFile = Join-Path $cfgDir 'secrets.json'
$siteConfig = Join-Path $cfgDir 'appsettings.json'

function Main {
    foreach ($f in @($exe, (Join-Path $pgBin 'postgres.exe'), (Join-Path $serverDir 'wwwroot\index.html'))) {
        if (-not (Test-Path $f)) { throw "Installation is incomplete: $f is missing." }
    }

    # ---- folders: only SYSTEM and Administrators, then each service gets what it needs
    foreach ($d in @($root, $cfgDir, $dataDir, $logDir, $backupDir)) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }
    Set-TgAcl -Path $root -Grants @("$($TgSidSystem):F", "$($TgSidAdmins):F")

    if (-not [Diagnostics.EventLog]::SourceExists('TruckGate')) { New-EventLog -LogName Application -Source 'TruckGate' }

    # ---- secrets (generated once; never in the installer, never in the log)
    if (Test-Path $secretsFile) {
        $s = Get-Content $secretsFile -Raw | ConvertFrom-Json
        Write-TgLog 'Existing database credentials kept.'
    } else {
        if (Test-Path (Join-Path $pgData 'PG_VERSION')) {
            throw "A database exists in $pgData but $secretsFile is missing. Restore secrets.json from your backup, or move the old pgdata folder away to start a new empty database."
        }
        $s = [pscustomobject]@{ superuserPassword = New-TgSecret; ownerPassword = New-TgSecret; appPassword = New-TgSecret; dbPort = $DbPort }
        Write-TgUtf8 $secretsFile ($s | ConvertTo-Json)
        Write-TgLog 'New random database passwords generated.'
    }
    Set-TgAcl -Path $secretsFile -Grants @("$($TgSidSystem):F", "$($TgSidAdmins):F") -File
    $DbPort = [int]$s.dbPort

    Initialize-Database $s
    Register-DatabaseService
    Start-DatabaseService

    # ---- roles and database (idempotent); passwords via environment only
    $pgEnv = @{ PGPASSWORD = $s.superuserPassword; TG_OWNER_PW = $s.ownerPassword; TG_APP_PW = $s.appPassword; PGCLIENTENCODING = 'UTF8' }
    Invoke-TgTool -Exe (Join-Path $pgBin 'psql.exe') -Environment $pgEnv -WorkingDirectory $PSScriptRoot -Arguments @(
        '-X', '-q', '-v', 'ON_ERROR_STOP=1', '-h', '127.0.0.1', '-p', "$DbPort", '-U', 'postgres', '-d', 'postgres',
        '-f', (Join-Path $PSScriptRoot 'db-setup-env.sql')) | Out-Null
    Write-TgLog 'Database roles and database ready.'

    Write-SiteConfig $s
    $serviceSid = Register-ServerService

    # service account: read the configuration, write data and logs; nothing else
    Add-TgAclGrant -Path $cfgDir -Sid $serviceSid -Rights 'RX'
    Set-TgAcl -Path $secretsFile -Grants @("$($TgSidSystem):F", "$($TgSidAdmins):F") -File
    Add-TgAclGrant -Path $dataDir -Sid $serviceSid -Rights 'M'
    Add-TgAclGrant -Path $logDir -Sid $serviceSid -Rights 'M'

    # ---- migrations, as the owner role, for this run only
    $ownerConn = "Host=127.0.0.1;Port=$DbPort;Database=truckgate;Username=truckgate_owner;Password=$($s.ownerPassword)"
    Invoke-TgTool -Exe $exe -Arguments @('migrate') -WorkingDirectory $serverDir -Environment @{
        ConnectionStrings__Owner = $ownerConn; Background__Disabled = 'true' } | Out-Null

    Set-Firewall
    Register-BackupTask

    $owner = Get-TgPortOwner $HttpPort
    if ($owner -and $owner -notlike 'TruckGate.Api*') {
        throw "TCP port $HttpPort is already used by $owner. Stop that program or change Urls in $siteConfig."
    }
    try { Start-Service -Name 'TruckGate' } catch { Write-TgLog ('Start-Service TruckGate: ' + $_.Exception.Message) }
    $h = Wait-TgHealth -Url "http://127.0.0.1:$HttpPort" -Seconds 120
    if (-not $h.Ok) {
        Write-TgLog ('Server did not become healthy: ' + $h.Message)
        $latest = Get-ChildItem $logDir -Filter 'truckgate-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($latest) { Get-Content $latest.FullName -Tail 40 | ForEach-Object { Write-TgLog ('  | ' + $_) } }
        Get-EventLog -LogName Application -Source 'TruckGate' -Newest 5 -ErrorAction SilentlyContinue | ForEach-Object { Write-TgLog ('  event: ' + $_.Message) }
        throw 'The TruckGate service did not start correctly (see above).'
    }
    Write-TgLog $h.Message
    $cfg = Get-Content $siteConfig -Raw | ConvertFrom-Json
    Write-TgLog ('Phones will use: ' + $cfg.Server.PublicUrl)
    Write-TgLog 'Server setup finished.'
}

function Initialize-Database($s) {
    if (Test-Path (Join-Path $pgData 'PG_VERSION')) {
        Write-TgLog "Existing database cluster kept: $pgData"
        return
    }
    if ((Test-Path $pgData) -and @(Get-ChildItem $pgData -Force).Count -gt 0) {
        throw "$pgData is not empty but is not a PostgreSQL data folder. Move it away and run the installer again."
    }
    $owner = Get-TgPortOwner $DbPort
    if ($owner) { throw "TCP port $DbPort (TruckGate database) is already used by $owner." }

    New-Item -ItemType Directory -Path $pgData -Force | Out-Null
    # initdb drops administrator rights (restricted token), so the installing user and the database
    # service account need explicit rights on the folder; the user's right is removed afterwards.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    Set-TgAcl -Path $pgData -Grants @("$($TgSidSystem):F", "$($TgSidAdmins):F", "$($TgSidNetworkService):F", "$($me):F")

    $pwFile = Join-Path ([IO.Path]::GetTempPath()) ('tg-initdb-' + [guid]::NewGuid().ToString('N') + '.txt')
    try {
        Write-TgUtf8 $pwFile $s.superuserPassword
        $common = @('-D', $pgData, '-U', 'postgres', '-E', 'UTF8', '--auth=scram-sha-256', "--pwfile=$pwFile")
        $r = Invoke-TgTool -Exe (Join-Path $pgBin 'initdb.exe') -Arguments ($common + @('--locale-provider=icu', '--icu-locale=und', '--locale=C')) -AllowFailure
        if ($r.ExitCode -ne 0) {
            Write-TgLog 'initdb with ICU failed; retrying with the C locale.'
            Get-ChildItem $pgData -Force | Remove-Item -Recurse -Force
            Invoke-TgTool -Exe (Join-Path $pgBin 'initdb.exe') -Arguments ($common + @('--locale=C')) | Out-Null
        }
    } finally {
        if (Test-Path $pwFile) { Remove-Item $pwFile -Force }
    }
    Set-TgAcl -Path $pgData -Grants @("$($TgSidSystem):F", "$($TgSidAdmins):F", "$($TgSidNetworkService):F")

    Add-Content -Path (Join-Path $pgData 'postgresql.conf') -Encoding ASCII -Value @"

# ---- TruckGate (local only; the server program is the only client)
listen_addresses = '127.0.0.1'
port = $DbPort
max_connections = 60
shared_buffers = 256MB
logging_collector = on
log_directory = 'log'
log_filename = 'postgresql-%a.log'
log_truncate_on_rotation = on
log_rotation_age = 1d
log_rotation_size = 0
"@
    Write-TgLog "Database cluster created in $pgData (port $DbPort, local only)."
}

function Register-DatabaseService {
    $pgCtl = Join-Path $pgBin 'pg_ctl.exe'
    $existing = Get-Service -Name 'TruckGateDB' -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name 'TruckGateDB' -Force }
        Invoke-TgTool -Exe $pgCtl -Arguments @('unregister', '-N', 'TruckGateDB') -AllowFailure | Out-Null
    }
    Invoke-TgTool -Exe $pgCtl -Arguments @('register', '-N', 'TruckGateDB', '-U', 'NT AUTHORITY\NetworkService',
        '-D', $pgData, '-S', 'auto', '-w', '-t', '120') | Out-Null
    $sc = "$env:SystemRoot\System32\sc.exe"
    Invoke-TgTool -Exe $sc -Arguments @('config', 'TruckGateDB', 'DisplayName=', 'TruckGate Database (PostgreSQL 16)') -Quiet | Out-Null
    Invoke-TgTool -Exe $sc -Arguments @('description', 'TruckGateDB', 'TruckGate evidence database. Listens on 127.0.0.1 only.') -Quiet | Out-Null
    Invoke-TgTool -Exe $sc -Arguments @('failure', 'TruckGateDB', 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -Quiet | Out-Null
}

function Start-DatabaseService {
    Start-Service -Name 'TruckGateDB'
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $r = Invoke-TgTool -Exe (Join-Path $pgBin 'pg_isready.exe') -Arguments @('-h', '127.0.0.1', '-p', "$DbPort") -AllowFailure -Quiet
        if ($r.ExitCode -eq 0) { Write-TgLog 'Database service is running.'; return }
        Start-Sleep -Seconds 2
    }
    $log = Get-ChildItem (Join-Path $pgData 'log') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($log) { Get-Content $log.FullName -Tail 30 | ForEach-Object { Write-TgLog ('  | ' + $_) } }
    throw "The database service TruckGateDB did not start (port $DbPort)."
}

function Resolve-PublicUrl($existing) {
    if ($ServerUrl -and $ServerUrl -ne 'auto') {
        $problem = Test-TgUrlFormat $ServerUrl
        if ($problem) { throw "Server address '$ServerUrl' $problem." }
        return $ServerUrl.TrimEnd('/')
    }
    if ($existing) { return $existing }
    $ip = Get-TgLanIPv4
    if ($ip) { return "http://$($ip):$HttpPort" }
    Write-TgLog 'WARNING: no network address found; phones cannot enroll until the address is set (Start menu > TruckGate > Change server address).'
    return "http://$($env:COMPUTERNAME):$HttpPort"
}

function Write-SiteConfig($s) {
    $existingUrl = $null
    if (Test-Path $siteConfig) {
        try { $existingUrl = (Get-Content $siteConfig -Raw | ConvertFrom-Json).Server.PublicUrl } catch { }
    }
    $publicUrl = Resolve-PublicUrl $existingUrl
    $cfg = [ordered]@{
        Urls = "http://0.0.0.0:$HttpPort"
        ConnectionStrings = [ordered]@{ App = "Host=127.0.0.1;Port=$DbPort;Database=truckgate;Username=truckgate_app;Password=$($s.appPassword);Timeout=10" }
        Server = [ordered]@{ PublicUrl = $publicUrl }
        Data = [ordered]@{ Directory = $dataDir }
        Logging = [ordered]@{ File = [ordered]@{ Path = $logDir } }
    }
    if (Test-Path $siteConfig) {
        # keep settings added by hand (for example Telegram/WhatsApp base URLs); update the managed ones
        try {
            $old = Get-Content $siteConfig -Raw | ConvertFrom-Json
            foreach ($p in $old.PSObject.Properties) { if (-not $cfg.Contains($p.Name)) { $cfg[$p.Name] = $p.Value } }
            if ($old.PSObject.Properties['Urls']) { $cfg.Urls = $old.Urls }
        } catch { Write-TgLog 'Existing configuration could not be read; rewritten.' }
    }
    Write-TgUtf8 $siteConfig ($cfg | ConvertTo-Json -Depth 8)
    Write-TgLog "Configuration written: $siteConfig (phones use $publicUrl)."
}

function Register-ServerService {
    $sc = "$env:SystemRoot\System32\sc.exe"
    $bin = '"' + $exe + '"'
    $existing = Get-Service -Name 'TruckGate' -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name 'TruckGate' -Force }
        Invoke-TgTool -Exe $sc -Arguments @('config', 'TruckGate', 'binPath=', $bin) -Quiet | Out-Null
    } else {
        New-Service -Name 'TruckGate' -BinaryPathName $bin -DisplayName 'TruckGate Server' -StartupType Automatic | Out-Null
    }
    Invoke-TgTool -Exe $sc -Arguments @('config', 'TruckGate', 'obj=', 'NT SERVICE\TruckGate', 'depend=', 'TruckGateDB', 'start=', 'auto') -Quiet | Out-Null
    Invoke-TgTool -Exe $sc -Arguments @('description', 'TruckGate', 'TruckGate gate station server (web UI, phone sync, cameras, alerts). TCP 5080.') -Quiet | Out-Null
    Invoke-TgTool -Exe $sc -Arguments @('failure', 'TruckGate', 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000') -Quiet | Out-Null
    $sid = Get-TgServiceSid 'TruckGate'
    Write-TgLog "Service TruckGate registered (account NT SERVICE\TruckGate, $sid)."
    return $sid
}

function Set-Firewall {
    try {
        Get-NetFirewallRule -Name 'TruckGate-HTTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -Name 'TruckGate-HTTP' -DisplayName "TruckGate server (TCP $HttpPort)" -Group 'TruckGate' `
            -Direction Inbound -Action Allow -Protocol TCP -LocalPort $HttpPort -RemoteAddress LocalSubnet -Profile Any `
            -Program $exe | Out-Null
        Write-TgLog "Firewall: TCP $HttpPort allowed from the local subnet."
    } catch {
        Write-TgLog ('WARNING: firewall rule not created (' + $_.Exception.Message + '). Allow TCP ' + $HttpPort + ' by hand if phones cannot connect.')
    }
}

function Register-BackupTask {
    try {
        $script = Join-Path $PSScriptRoot 'backup.ps1'
        $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $script + '"')
        $trigger = New-ScheduledTaskTrigger -Daily -At '02:30'
        $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName 'TruckGate daily backup' -TaskPath '\TruckGate\' -Action $action -Trigger $trigger `
            -Principal $principal -Description 'Database dump and server keys into ProgramData\TruckGate\backups (14 kept).' -Force | Out-Null
        Write-TgLog 'Daily backup task registered (02:30).'
    } catch {
        Write-TgLog ('WARNING: backup task not registered: ' + $_.Exception.Message)
    }
}

Initialize-TgLog (Join-Path (Join-Path (Get-TgRoot) 'logs') 'install.log')
try {
    Main
    exit 0
} catch {
    Write-TgLog ('FAILED: ' + $_.Exception.Message)
    Write-TgLog $_.ScriptStackTrace
    exit 1
}
