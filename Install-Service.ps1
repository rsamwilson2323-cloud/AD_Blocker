#Requires -RunAsAdministrator
<#
 Installs ONLY the 'AdBlocker' Windows service. It does not start it, change your network settings,
 touch antivirus/firewall/browser settings, or install anything else. Run Build.ps1 first.
#>
[CmdletBinding()]
param([switch]$NoPrompt, [switch]$StartWithWindows, [switch]$ApplySystemDns, [switch]$Force)
$ErrorActionPreference = 'Stop'
$name = 'AdBlocker'
$bin = Join-Path $PSScriptRoot 'bin'; $exe = Join-Path $bin 'AdBlocker.Service.exe'
$data = Join-Path $env:ProgramData 'AdBlocker'

function Ask([string]$q, [bool]$default) {
  if ($NoPrompt) { return $default }
  $suffix = if ($default) { '[Y/n]' } else { '[y/N]' }
  $a = Read-Host "$q $suffix"
  if ([string]::IsNullOrWhiteSpace($a)) { return $default }
  return $a.Trim().ToLower().StartsWith('y')
}

if (-not (Test-Path $exe)) { throw "Missing $exe. Run .\Build.ps1 first (needs the .NET 8 SDK)." }

if (Get-Service $name -ErrorAction SilentlyContinue) {
  if (-not $Force) { Write-Host "Service '$name' is already installed. Use -Force to reinstall."; exit 0 }
  Stop-Service $name -ErrorAction SilentlyContinue; sc.exe delete $name | Out-Null; Start-Sleep 2
}

# Port 53 is needed for the DNS component. Report conflicts now instead of failing mysteriously later.
$busy = Get-NetUDPEndpoint -LocalPort 53 -ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -in '127.0.0.1', '0.0.0.0', '::' }
if ($busy) {
  $pids = $busy.OwningProcess | Sort-Object -Unique
  $who = ($pids | ForEach-Object { (Get-Process -Id $_ -ErrorAction SilentlyContinue).ProcessName }) -join ', '
  Write-Warning "UDP port 53 on this PC is already used by: $who. The engine will report 'Error' until that is stopped or ListenPort in settings.json is changed (note: Windows DNS can only point at port 53)."
}

$auto = $StartWithWindows -or (Ask 'Start AdBlocker automatically when Windows starts?' $true)
$route = $ApplySystemDns -or (Ask 'Route Windows DNS through AdBlocker (changes your adapters'' DNS to 127.0.0.1; originals are backed up and restorable)?' $false)

New-Item -ItemType Directory -Force -Path $data | Out-Null
# Only SYSTEM and Administrators may read/modify settings, the DNS backup and the API token.
icacls $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

$sf = Join-Path $data 'settings.json'
$src = if (Test-Path $sf) { $sf } else { Join-Path $bin 'config\settings.default.json' }
$s = Get-Content $src -Raw | ConvertFrom-Json
if ($null -eq $s.PSObject.Properties['applySystemDns']) { $s | Add-Member applySystemDns $false }
$s.applySystemDns = [bool]$route
$s | ConvertTo-Json | Set-Content $sf -Encoding UTF8

New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'AdBlocker Engine' `
  -Description 'AdBlocker local DNS filtering engine and control API (127.0.0.1 only).' `
  -StartupType $(if ($auto) { 'Automatic' } else { 'Manual' }) | Out-Null
# Self-heal: restart after crashes so a dead proxy can't leave Windows DNS pointing at nothing.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null

Write-Host "Installed service '$name' (startup: $(if ($auto) {'Automatic'} else {'Manual'}))."
Write-Host "System DNS routing: $(if ($route) {'will be applied when the engine starts'} else {'OFF (enable later in the app, or re-run with -ApplySystemDns -Force)'})"
Write-Host "Next: run Run.bat. Install the Chrome extension once - see README."
