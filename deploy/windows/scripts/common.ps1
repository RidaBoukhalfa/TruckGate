# TruckGate - helpers shared by the Windows setup scripts (Windows PowerShell 5.1).
# Accounts are given by SID so that the scripts work on French, Arabic or English Windows alike.

Set-StrictMode -Version 3

$script:TgSidSystem = 'S-1-5-18'
$script:TgSidAdmins = 'S-1-5-32-544'
$script:TgSidUsers = 'S-1-5-32-545'
$script:TgSidNetworkService = 'S-1-5-20'
$script:TgLogFile = $null
# Factory LAN checks must not go through a proxy configured for the internet.
[System.Net.WebRequest]::DefaultWebProxy = $null

function Get-TgRoot { Join-Path $env:ProgramData 'TruckGate' }

function Initialize-TgLog([string]$Path) {
    $dir = Split-Path $Path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $script:TgLogFile = $Path
    Write-TgLog ('---- ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $MyInvocation.PSCommandPath)
}

function Write-TgLog([string]$Message) {
    $line = (Get-Date -Format 'HH:mm:ss') + '  ' + $Message
    Write-Host $line
    if ($script:TgLogFile) { Add-Content -Path $script:TgLogFile -Value $line -Encoding UTF8 }
}

function ConvertTo-TgArg([string]$a) {
    # Quoting rules of CommandLineToArgvW / the MSVC runtime.
    if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { return $a }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $bs = 0
    foreach ($ch in $a.ToCharArray()) {
        if ($ch -eq '\') { $bs++; continue }
        if ($ch -eq '"') { [void]$sb.Append(('\' * ($bs * 2 + 1)) + '"'); $bs = 0; continue }
        [void]$sb.Append(('\' * $bs) + $ch); $bs = 0
    }
    [void]$sb.Append(('\' * ($bs * 2)) + '"')
    return $sb.ToString()
}

# Runs a program, logs its output, throws on a non-zero exit code. Secrets go in -Environment
# (only for that child process), never on the command line or in the log.
function Invoke-TgTool {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [string[]]$Arguments = @(),
        [string]$WorkingDirectory,
        [hashtable]$Environment,
        [string]$InputText,
        [switch]$AllowFailure,
        [switch]$Quiet
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = (($Arguments | ForEach-Object { ConvertTo-TgArg $_ }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $true
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    if ($Environment) { foreach ($k in $Environment.Keys) { $psi.EnvironmentVariables[$k] = [string]$Environment[$k] } }
    Write-TgLog ('> ' + [IO.Path]::GetFileName($Exe) + ' ' + $psi.Arguments)
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    if ($InputText) { $p.StandardInput.Write($InputText) }
    $p.StandardInput.Close()
    $p.WaitForExit()
    $out = $outTask.Result
    $err = $errTask.Result
    if (-not $Quiet) {
        foreach ($l in ($out -split "`r?`n")) { if ($l.Trim()) { Write-TgLog ('  ' + $l) } }
        foreach ($l in ($err -split "`r?`n")) { if ($l.Trim()) { Write-TgLog ('  ! ' + $l) } }
    }
    if ($p.ExitCode -ne 0 -and -not $AllowFailure) {
        throw ([IO.Path]::GetFileName($Exe) + ' failed with exit code ' + $p.ExitCode + '. ' + $err.Trim())
    }
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out; Error = $err }
}

function Get-TgServiceSid([string]$Name) {
    # Per-service SID (NT SERVICE\<name>): S-1-5-80- followed by the SHA-1 of the upper-case name.
    $r = Invoke-TgTool -Exe "$env:SystemRoot\System32\sc.exe" -Arguments @('showsid', $Name) -AllowFailure -Quiet
    $m = [regex]::Match($r.Output, 'S-1-5-80(-\d+){5}')
    if ($m.Success) { return $m.Value }
    $sha = [Security.Cryptography.SHA1]::Create()
    $h = $sha.ComputeHash([Text.Encoding]::Unicode.GetBytes($Name.ToUpperInvariant()))
    $parts = 0..4 | ForEach-Object { [BitConverter]::ToUInt32($h, $_ * 4) }
    return 'S-1-5-80-' + ($parts -join '-')
}

# Replaces the permissions of a folder (or file) with exactly the given SID:rights entries.
function Set-TgAcl([string]$Path, [string[]]$Grants, [switch]$File) {
    $icacls = "$env:SystemRoot\System32\icacls.exe"
    Invoke-TgTool -Exe $icacls -Arguments @($Path, '/inheritance:r') -Quiet | Out-Null
    $icArgs = @($Path, '/grant:r')
    foreach ($g in $Grants) {
        $sid, $rights = $g.Split(':', 2)
        if ($File) { $icArgs += ('*' + $sid + ':' + $rights) } else { $icArgs += ('*' + $sid + ':(OI)(CI)' + $rights) }
    }
    Invoke-TgTool -Exe $icacls -Arguments $icArgs -Quiet | Out-Null
    # Remove whatever else was explicitly there before (for example the creating user).
    $keep = $Grants | ForEach-Object { $_.Split(':')[0] }
    $acl = Get-Acl -Path $Path
    foreach ($rule in @($acl.Access)) {
        if ($rule.IsInherited) { continue }
        try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { continue }
        if ($keep -notcontains $sid) { Invoke-TgTool -Exe $icacls -Arguments @($Path, '/remove:g', ('*' + $sid)) -Quiet | Out-Null }
    }
}

function Add-TgAclGrant([string]$Path, [string]$Sid, [string]$Rights, [switch]$File) {
    $spec = if ($File) { '*' + $Sid + ':' + $Rights } else { '*' + $Sid + ':(OI)(CI)' + $Rights }
    Invoke-TgTool -Exe "$env:SystemRoot\System32\icacls.exe" -Arguments @($Path, '/grant', $spec) -Quiet | Out-Null
}

function New-TgSecret([int]$Bytes = 24) {
    $b = New-Object byte[] $Bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($b)
    return (($b | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Write-TgUtf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding $false))
}

function Get-TgLanIPv4 {
    $private = '^(10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)'
    try {
        $configs = @(Get-NetIPConfiguration -ErrorAction Stop | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' })
        foreach ($c in $configs) { foreach ($a in @($c.IPv4Address)) { if ($a.IPAddress -match $private) { return $a.IPAddress } } }
        foreach ($c in $configs) { foreach ($a in @($c.IPv4Address)) { if ($a.IPAddress -notlike '169.254.*') { return $a.IPAddress } } }
    } catch { }
    try {
        $a = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -match $private -and $_.AddressState -eq 'Preferred' } | Select-Object -First 1
        if ($a) { return $a.IPAddress }
    } catch { }
    return $null
}

# Checks the form of a server address: http(s)://host[:port], nothing after it.
function Test-TgUrlFormat([string]$Url) {
    if (-not $Url) { return 'empty' }
    $u = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$u)) { return 'not a valid address' }
    if ($u.Scheme -ne 'http' -and $u.Scheme -ne 'https') { return 'must start with http:// or https://' }
    if ($u.AbsolutePath -ne '/' -or $u.Query -or $u.Fragment -or $u.UserInfo) { return 'must be only http://address:port (nothing after the port)' }
    return $null
}

# Calls <url>/health. Returns an object with Ok and a message an operator can act on.
function Test-TgServer([string]$Url, [int]$TimeoutSec = 6) {
    $base = $Url.TrimEnd('/')
    try {
        $r = Invoke-WebRequest -Uri ($base + '/health') -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
        $j = $null
        try { $j = $r.Content | ConvertFrom-Json } catch { }
        if ($j -eq $null -or -not $j.PSObject.Properties['service'] -or $j.service -ne 'truckgate') {
            return [pscustomobject]@{ Ok = $false; Message = "$base answers, but it is not a TruckGate server (wrong address or port?)." }
        }
        return [pscustomobject]@{ Ok = $true; Message = "TruckGate server $($j.version) at $base - database $($j.database)." }
    } catch [System.Net.WebException] {
        $e = $_.Exception
        if ($e.Response -ne $null) {
            $code = [int]$e.Response.StatusCode
            if ($code -eq 503) { return [pscustomobject]@{ Ok = $false; Message = "$base is running but its database is not reachable (service TruckGateDB stopped?)." } }
            return [pscustomobject]@{ Ok = $false; Message = "$base answered HTTP $code - not a TruckGate server, or wrong port." }
        }
        switch ($e.Status) {
            'NameResolutionFailure' { $m = "The name in $base cannot be resolved. Use the server's IP address." }
            'ConnectFailure' { $m = "Nothing answers at $base. Check that the server PC is on, that the TruckGate service is running, the port, and the Windows firewall on the server." }
            'Timeout' { $m = "No answer from $base within $TimeoutSec s. Check the network cable/Wi-Fi and the IP address of the server." }
            default { $m = "Cannot reach $base ($($e.Status)): $($e.Message)" }
        }
        return [pscustomobject]@{ Ok = $false; Message = $m }
    } catch {
        return [pscustomobject]@{ Ok = $false; Message = "Cannot reach $base : $($_.Exception.Message)" }
    }
}

function Wait-TgHealth([string]$Url, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Test-TgServer -Url $Url -TimeoutSec 5
        if ($last.Ok) { return $last }
        Start-Sleep -Seconds 2
    }
    return $last
}

function Get-TgPortOwner([int]$Port) {
    try {
        $c = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
        if ($c) {
            $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
            if ($p) { return $p.ProcessName + ' (pid ' + $p.Id + ')' }
            return 'pid ' + $c.OwningProcess
        }
    } catch { }
    return $null
}
