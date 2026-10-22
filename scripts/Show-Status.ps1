# Prints the engine's real status as reported by its own API. Exit code: 0 Active, 1 Error/Disabled, 2 engine not answering.
param([int]$Port = 47653, [int]$WaitSeconds = 15)
$deadline = (Get-Date).AddSeconds($WaitSeconds); $s = $null
while ((Get-Date) -lt $deadline) {
  try { $s = Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $s) {
  Write-Host "[ERROR] The service is running but the engine did not answer on 127.0.0.1:$Port within $WaitSeconds s." -ForegroundColor Red
  Write-Host "        Protection is NOT confirmed. See Event Viewer > Application for 'AdBlocker'." ; exit 2
}
$color = switch ($s.state) { 'Active' { 'Green' } 'Error' { 'Red' } default { 'Yellow' } }
Write-Host ("Protection status : {0}" -f $s.state) -ForegroundColor $color
if ($s.error) { Write-Host ("Error             : {0}" -f $s.error) -ForegroundColor Red }
Write-Host ("DNS proxy         : {0} on {1}; blocking {2}" -f $(if ($s.dns.listening) {'listening'} else {'NOT listening'}), $s.dns.listen, $(if ($s.dns.filteringEnabled) {'ON'} else {'OFF'}))
Write-Host ("Windows DNS routed: {0}" -f $(if ($s.dns.systemDnsApplied) {'yes - all apps using Windows DNS are filtered'} else {'NO - only apps configured to use 127.0.0.1 are filtered'}))
Write-Host ("Rules loaded      : {0} block / {1} allow ({2} rejected lines)" -f $s.rules.blocked, $s.rules.allowed, $s.rules.rejectedLines)
Write-Host ("Counters          : {0} queries, {1} blocked" -f $s.stats.totalQueries, $s.stats.blockedTotal)
Write-Host ("Chrome extension  : {0}" -f $(if ($s.browser.connected) {"connected (v$($s.browser.extensionVersion))"} else {'not detected (install it: see README)'}))
if ($s.state -eq 'Active') { exit 0 } else { exit 1 }
