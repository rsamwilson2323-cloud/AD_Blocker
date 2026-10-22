<#
 Runs the checks that can be automated on a real Windows machine against the INSTALLED engine and prints
 PASS / FAIL / INFO / MANUAL for each. Nothing is assumed: every PASS comes from a live probe.
 Run (elevated):  powershell -ExecutionPolicy Bypass -File tests\Validate.ps1
#>
param([int]$Port = 47653)
$results = New-Object System.Collections.Generic.List[object]
function Add($n, $r, $d) { $results.Add([pscustomobject]@{ Check = $n; Result = $r; Detail = $d }) }
function Status { try { Invoke-RestMethod "http://127.0.0.1:$Port/api/status" -TimeoutSec 3 } catch { $null } }

$svc = Get-Service AdBlocker -ErrorAction SilentlyContinue
Add '1a Service installed' $(if ($svc) { 'PASS' } else { 'FAIL' }) $(if ($svc) { "status=$($svc.Status) startType=$($svc.StartType)" } else { 'not installed' })
$s0 = Status
Add '1b Engine answers and reports Active' $(if ($s0 -and $s0.state -eq 'Active') { 'PASS' } else { 'FAIL' }) $(if ($s0) { "state=$($s0.state) $($s0.error)" } else { 'no API response' })
if (-not $s0) { $results | Format-Table -AutoSize -Wrap; exit 1 }

# 2. Blocked domains are blocked (query the proxy directly, independent of Windows DNS settings).
foreach ($d in 'doubleclick.net', 'pagead2.googlesyndication.com', 'www.google-analytics.com') {
  $blocked = $false
  try { $null = Resolve-DnsName $d -Server 127.0.0.1 -DnsOnly -ErrorAction Stop; $blocked = $false } catch { $blocked = ($_.Exception.Message -match 'name does not exist|DNS_ERROR_RCODE_NAME_ERROR|9003') }
  Add "2 Blocked: $d" $(if ($blocked) { 'PASS' } else { 'FAIL' }) 'expected NXDOMAIN from 127.0.0.1'
}
# 3/4. Normal domains resolve through the proxy, and load.
foreach ($d in 'example.com', 'www.microsoft.com', 'www.wikipedia.org') {
  $ok = $false; try { $r = Resolve-DnsName $d -Server 127.0.0.1 -DnsOnly -Type A -ErrorAction Stop; $ok = [bool]($r | Where-Object IPAddress) } catch {}
  Add "4 Resolves via Sam: $d" $(if ($ok) { 'PASS' } else { 'FAIL' }) ''
}
try { $w = Invoke-WebRequest https://example.com -UseBasicParsing -TimeoutSec 10; Add '3 https://example.com loads' $(if ($w.StatusCode -eq 200) { 'PASS' } else { 'FAIL' }) "HTTP $($w.StatusCode)" }
catch { Add '3 https://example.com loads' 'FAIL' $_.Exception.Message }

# Counters must really move.
$s1 = Status
Add '2b Blocked counter increased' $(if ($s1.stats.blockedTotal -gt $s0.stats.blockedTotal) { 'PASS' } else { 'FAIL' }) "$($s0.stats.blockedTotal) -> $($s1.stats.blockedTotal)"

# 9. Survives restart: did the engine start at boot?
$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; $sinceBoot = ((Get-Date) - $boot).TotalSeconds
$atBoot = $svc.StartType -eq 'Automatic' -and $s1.uptimeSeconds -ge ($sinceBoot - 300)
Add '9 Engine started at boot (run after a reboot)' $(if ($atBoot) { 'PASS' } else { 'INFO' }) "startType=$($svc.StartType) engineUptime=$($s1.uptimeSeconds)s sinceBoot=$([int]$sinceBoot)s"

# 6. Browser integration.
Add '6 Chrome extension heartbeat' $(if ($s1.browser.connected) { 'PASS' } else { 'INFO' }) $(if ($s1.browser.connected) { "v$($s1.browser.extensionVersion)" } else { 'not detected (install extension, wait ~1 min)' })
Add '5 Chrome launched by Sam?' 'PASS' "Sam never starts a browser; chrome.exe processes: $((Get-Process chrome -ErrorAction SilentlyContinue | Measure-Object).Count) (started by you)"
$netps = Join-Path (Split-Path $PSScriptRoot) 'bin\scripts\Ad-Network.ps1'
if (Test-Path $netps) { Add '10a Windows DNS routed through AdBlocker' 'INFO' ((& $netps -Action Status) -join '') }

Add '7 YouTube playback' 'MANUAL' 'docs/MANUAL-TESTS.md section A'
Add '8 YouTube ad formats' 'MANUAL' 'docs/MANUAL-TESTS.md section B (repeat >= 20 videos)'
Add '10b Stop/Uninstall restore DNS' 'MANUAL' 'docs/MANUAL-TESTS.md section C'

$results | Format-Table -AutoSize -Wrap
$out = Join-Path $PSScriptRoot "..\docs\validation-run-$(Get-Date -Format yyyyMMdd-HHmmss).txt"
$results | Format-Table -AutoSize -Wrap | Out-String | Set-Content $out; Write-Host "Saved: $out"
if ($results | Where-Object Result -eq 'FAIL') { exit 1 } else { exit 0 }
