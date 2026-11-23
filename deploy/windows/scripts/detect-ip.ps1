# Prints the address phones should use: the configured one if the server is already installed,
# otherwise http://<this PC's LAN IPv4>:5080. Used to pre-fill the installer page.
param([string]$OutFile, [int]$Port = 5080)
. (Join-Path $PSScriptRoot 'common.ps1')
$url = ''
$cfg = Join-Path (Get-TgRoot) 'config\appsettings.json'
if (Test-Path $cfg) { try { $url = [string](Get-Content $cfg -Raw | ConvertFrom-Json).Server.PublicUrl } catch { } }
if (-not $url) { $ip = Get-TgLanIPv4; if ($ip) { $url = "http://$($ip):$Port" } }
if ($OutFile) { Write-TgUtf8 $OutFile $url } else { $url }
