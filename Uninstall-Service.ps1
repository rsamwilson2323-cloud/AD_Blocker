#Requires -RunAsAdministrator
<# Removes ONLY the 'AdBlocker' service. If Windows DNS still points at Sam, it is restored from the backup first. #>
[CmdletBinding()]
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$name = 'AdBlocker'; $data = Join-Path $env:ProgramData 'AdBlocker'
$netps = Join-Path $PSScriptRoot 'bin\scripts\Ad-Network.ps1'

if (Get-Service $name -ErrorAction SilentlyContinue) {
  Stop-Service $name -ErrorAction SilentlyContinue
  (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
}
if (Test-Path $netps) {
  & $netps -Action Restore -DataDir $data     # no-op when nothing was changed
} else {
  Write-Warning "Could not find $netps. Check DNS manually: Get-DnsClientServerAddress ; reset with Set-DnsClientServerAddress -InterfaceIndex <n> -ResetServerAddresses"
}
if (Get-Service $name -ErrorAction SilentlyContinue) {
  sc.exe delete $name | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE" }
  Write-Host "Service '$name' removed."
} else { Write-Host "Service '$name' was not installed." }
if ($RemoveData) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "Removed $data" }
else { Write-Host "Settings and your allow/block lists were kept in $data (use -RemoveData to delete them)." }
Write-Host "The Chrome extension is separate: remove it at chrome://extensions if you no longer want it."
