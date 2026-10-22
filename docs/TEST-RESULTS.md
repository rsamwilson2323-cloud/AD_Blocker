# Test results (honest status)

Environment where this package was authored: Linux container with Node.js 22 and **no .NET SDK and no Windows**.
That limits what could be executed. The table separates what was run from what was only written.

## Executed here (Node.js 22) - 8/8 passing
Command: `node --test extension-tests/extension.test.js`

| Test | Result |
|---|---|
| Generated `ads.json` is structurally valid, ids unique, and is up to date with `rules/*.txt` | PASS |
| Ad/tracker URLs are blocked (googlesyndication, doubleclick, google-analytics, youtube.com/pagead, youtube.com/api/stats/ads) | PASS (against a *model* of Chrome's declarativeNetRequest matching) |
| Normal sites and YouTube playback endpoints (`watch`, `youtubei/v1/player`, `googlevideo.com/videoplayback`, `ytimg`, `api/stats/playback`, `s/player/base.js`) are not blocked | PASS (same model) |
| Top-level navigation blocked only for popup networks | PASS (same model) |
| No critical domain (googlevideo, youtube, google, gstatic, microsoft, github ...) appears in any blocklist | PASS |
| List parser: comments, hosts format, `||domain^`, rejects unsupported syntax | PASS |
| YouTube JSON pruning removes ad fields/feed ad items and leaves content untouched | PASS (synthetic JSON, **not** real YouTube responses) |
| Manifest is MV3, referenced files exist, no broad host permissions / history / cookies / webRequest | PASS |

## Written but NOT compiled or run (needs .NET 8 SDK on Windows)
All C# (`src/`, `tests/AdBlocker.Tests`) and all PowerShell/BAT. They have been desk-checked but **may contain compile errors or bugs**.
First step on your machine: `.\Build.ps1 -Test`. Expect to fix a few small issues; please treat every item below as **unverified** until then.

| Area | Status |
|---|---|
| Rule engine, DNS parsing/blocking/cache/proxy (xUnit: ~30 tests incl. loopback UDP integration) | Unverified - tests written, never run |
| Windows service, control API, token ACL | Unverified |
| WPF UI | Unverified (never rendered) |
| `Ad-Network.ps1` apply/restore on static + DHCP adapters | Unverified - **test on a non-critical machine first** |
| Run.bat / Install / Uninstall | Unverified |
| Validation checks 1-10 from the spec | Not run. `tests/Validate.ps1` automates 1-6, 9; checks 7, 8, 10 are manual (`docs/MANUAL-TESTS.md`) |

## Capability classification
| Capability | Class |
|---|---|
| DNS-level domain blocking (all apps using Windows DNS) | Designed + tested by unit tests, **unverified on Windows** |
| Chrome network blocking via declarativeNetRequest | Rule logic verified against a model; **real-Chrome behaviour unverified** |
| Cosmetic hiding | Unverified in a real browser |
| YouTube ad-field pruning | **Experimental**: pure function tested on synthetic data only |
| YouTube skip / speed-up fallback | **Experimental**: untested against live YouTube; reduces, does not block |
| YouTube "100% blocked" | **Not claimed. No evidence exists.** |


## Rebuild note for renamed 0.2.2 package

The JavaScript extension tests are run locally before packaging. The Windows app/service cannot be compiled in this authoring environment because the .NET SDK is not installed here. Build and live browser tests remain required on Windows.
