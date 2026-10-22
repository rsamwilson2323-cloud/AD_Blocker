<#
 AdBlocker - network configuration helper (Apply | Restore | Status).
 SAFETY MODEL
  * Apply  : records each active adapter's ORIGINAL IPv4 DNS (and whether it was static or DHCP) to
             network-backup.json BEFORE changing anything, and never overwrites an existing backup.
  * Restore: puts back exactly what was recorded (static -> same servers, DHCP -> automatic), but only on
             adapters that still point at 127.0.0.1, so changes you made in the meantime are not clobbered.
  * Status : prints {"applied":true|false}. Changes nothing.
 Known limitation: IPv6 DNS servers are not modified. A machine with IPv6 DNS configured can bypass filtering over IPv6.
#>
param(
  [Parameter(Mandatory)][ValidateSet('Apply','Restore','Status')][string]$Action,
  [string]$DataDir = (Join-Path $env:ProgramData 'AdBlocker')
)
$ErrorActionPreference = 'Stop'
$backupFile = Join-Path $DataDir 'network-backup.json'
$loop = '127.0.0.1'

function Get-UpAdapters { Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.HardwareInterface } }
function Test-StaticDns([string]$guid) {
  $k = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$guid"
  $v = (Get-ItemProperty -Path $k -Name NameServer -ErrorAction SilentlyContinue).NameServer
  return -not [string]::IsNullOrWhiteSpace($v)
}
function Get-V4Dns([int]$idx) { @((Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4).ServerAddresses) }
function Test-Applied { foreach ($a in Get-UpAdapters) { if ((Get-V4Dns $a.ifIndex) -contains $loop) { return $true } } return $false }

switch ($Action) {
  'Status' { Write-Output ('{"applied":' + (Test-Applied).ToString().ToLower() + '}'); exit 0 }
  'Apply' {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    if (-not (Test-Path $backupFile)) {
      $snap = foreach ($a in Get-UpAdapters) {
        $dns = Get-V4Dns $a.ifIndex
        if ($dns -contains $loop) { continue }   # already ours; never record our own address as "original"
        [pscustomobject]@{ InterfaceIndex=$a.ifIndex; Alias=$a.Name; Guid=$a.InterfaceGuid; Static=(Test-StaticDns $a.InterfaceGuid); Servers=@($dns) }
      }
      if (-not $snap) { Write-Error 'No active adapters found to configure.'; exit 3 }
      $snap | ConvertTo-Json -Depth 4 | Set-Content -Path $backupFile -Encoding UTF8
    }
    foreach ($a in Get-UpAdapters) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses @($loop) }
    Clear-DnsClientCache
    Write-Output 'Applied: Windows DNS now points at 127.0.0.1 (AdBlocker). Original settings saved.'
    exit 0
  }
  'Restore' {
    if (-not (Test-Path $backupFile)) { Write-Output 'Nothing to restore (no backup file).'; exit 0 }
    $snap = @(Get-Content $backupFile -Raw | ConvertFrom-Json)
    foreach ($s in $snap) {
      $cur = Get-V4Dns $s.InterfaceIndex
      if ($cur -notcontains $loop) { continue }
      if ($s.Static -and $s.Servers.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $s.InterfaceIndex -ServerAddresses @($s.Servers) }
      else { Set-DnsClientServerAddress -InterfaceIndex $s.InterfaceIndex -ResetServerAddresses }   # back to DHCP-provided DNS
    }
    # Any other adapter we changed that is not in the backup (e.g. a new connection) falls back to automatic DNS.
    foreach ($a in Get-UpAdapters) { if ((Get-V4Dns $a.ifIndex) -contains $loop) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses } }
    Remove-Item $backupFile -Force
    Clear-DnsClientCache
    Write-Output 'Restored previous DNS settings.'
    exit 0
  }
}
