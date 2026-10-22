#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$oldName = 'SamAdBlocker'
$oldData = Join-Path $env:ProgramData 'SamAdBlocker'
$svc = Get-CimInstance Win32_Service -Filter "Name='$oldName'" -ErrorAction SilentlyContinue
if (-not $svc) { Write-Host 'No previous SamAdBlocker service found; migration not needed.'; exit 0 }

$exe = $null
if ($svc.PathName -match '^\s*"([^\"]+\.exe)"') { $exe = $Matches[1] }
elseif ($svc.PathName -match '^\s*(.+?\.exe)(?:\s|$)') { $exe = $Matches[1] }
$oldBin = if ($exe) { Split-Path -Parent $exe } else { $null }
$helper = if ($oldBin) { Join-Path $oldBin 'scripts\Sam-Network.ps1' } else { $null }

Write-Host 'Previous SamAdBlocker service detected. Preparing safe migration...'
Stop-Service -Name $oldName -Force -ErrorAction SilentlyContinue
try { (Get-Service $oldName -ErrorAction Stop).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) } catch {}

if (-not $helper -or -not (Test-Path $helper)) {
  # A copy of the previous project may be available in the usual working folder.
  $candidates = @(
    'D:\github coding\SamAdBlocker_YouTube_v0.2.1\bin\scripts\Sam-Network.ps1',
    'D:\github coding\SamAdBlocker\bin\scripts\Sam-Network.ps1'
  )
  $helper = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $helper) {
  Start-Service -Name $oldName -ErrorAction SilentlyContinue
  throw "Cannot locate the old Sam-Network.ps1 helper. The old service was restarted. Run the previous project's Uninstall-Service.ps1 to restore DNS, then run AdBlocker again."
}

try {
  & $helper -Action Restore -DataDir $oldData
  if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Old DNS restore helper returned a failure code.' }
  $status = & $helper -Action Status -DataDir $oldData 2>&1
  if (($status -join "`n") -match '127\.0\.0\.1') { throw 'DNS status still appears routed to the old local proxy; refusing to remove the old service.' }
} catch {
  Start-Service -Name $oldName -ErrorAction SilentlyContinue
  throw "DNS restoration failed. The old service was restarted to avoid leaving DNS pointed at a stopped proxy. Details: $($_.Exception.Message)"
}

sc.exe delete $oldName | Out-Null
Start-Sleep -Seconds 2
Write-Host 'Previous service removed. Original DNS settings restored. Old data kept at:' $oldData
