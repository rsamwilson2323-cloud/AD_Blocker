# Manual test protocol (what cannot be automated honestly)

Record the Chrome version, Windows version, date, and extension version with every run.
Use a **fresh Chrome profile with only the Sam extension** (your own profile is fine, but disable other blockers).
Do not report a result you did not observe.

## A. YouTube playback not broken (check 7)
1. Open 5 different videos (music, long-form, a live stream, a Short, one with chapters). Each must play, seek, change quality, and show captions.
2. Sign-in state, comments, and the home feed must load.
3. With the extension **paused** (toolbar button -> badge "OFF"), repeat step 1 once as a control.
Pass = all behave the same as the control.

## B. YouTube ad formats (check 8)
Test **>= 20 videos across >= 3 days** (YouTube changes delivery often; one lucky run proves nothing). For each video record, per format:
`Blocked` (never appeared) / `Reduced` (appeared but was skipped or sped up) / `Unaffected`.

| Format | How to observe | What Sam does | Honest expectation |
|---|---|---|---|
| Pre-roll / mid-roll video ads | Does the player show "Ad" / yellow ad bar? | (1) strips `adPlacements`/`playerAds` from player JSON; (2) fallback: skip button, mute + 16x + seek to end | Experimental. May fully prevent, may only shorten, may fail after a YouTube change. The ad bytes are served from the same hosts as the video, so network blocking cannot be used. |
| Banner / overlay ads | Bottom overlay on the player | CSS hiding (+ JSON pruning) | Hidden, not network-blocked |
| Home/search feed ads | "Sponsored" tiles | JSON pruning + CSS hiding | Usually reduced |
| Masthead / sidebar promos | Top banner / right rail | CSS hiding | Hidden, not network-blocked |

Tally results in a table like `docs/youtube-results-<date>.md`. The extension popup/app counters ("ad fields pruned", "ads sped up / skipped") are *indicators*, not proof: always trust the visual observation.
Never write "100%" unless every one of the >= 20 videos over >= 3 days was `Blocked`.

## C. Restore behaviour (check 10)
1. `Get-DnsClientServerAddress -AddressFamily IPv4` -> save output (BEFORE).
2. Enable "Route Windows DNS through AdBlocker", confirm browsing works and `Validate.ps1` passes.
3. Click **Stop protection** in the AdBlocker UI. Verify that DNS is restored to the previous configuration. There is no Stop.bat.
4. Repeat with a statically configured adapter (e.g. 8.8.8.8). It must return to the static servers.
5. Run `Uninstall-Service.ps1` while routed: DNS must be restored automatically and `sc query AdBlocker` must report "does not exist".
6. Kill the engine process while routed (`taskkill /f /im AdBlocker.Service.exe`): the service recovery restarts it in ~5 s; during that window DNS lookups fail (known limitation).

## D. Restart persistence (check 9)
Install with "start with Windows = yes", reboot, wait 1 minute, run `tests\Validate.ps1`; check `9` shows PASS.

## E. Chrome without a separate profile (checks 5, 6)
Open your normal Chrome. Confirm no extra Chrome window appeared when `Run.bat` ran, the extension is listed at chrome://extensions, and `Validate.ps1` check `6` shows the heartbeat.
