# SmartBird Thermostat e2e

Run from the repository root after the shared Debug build of `tests/PersonalUx.Tests`:

```powershell
npx e2e run tests/e2e/smartbird-thermostat.e2e.ts --output .e2e/tools/smartbird-thermostat
```

The Windows workflow target executes seven e2e cases backed by 36 Python and 13 .NET behavioral regressions. `MPT_E2E_PYTHON` optionally selects the Python interpreter. Python uses `-B` and standard-library dependencies. The .NET helper requires a TRX report, the expected executed count, and every test passing with zero skips.

| Flow | Evidence |
|---|---|
| Dew point, hot/cold thresholds, minimum off time, ADB failure and fallback serial | Real thermostat controller with fake clock, switch and thermal readings; Python suite |
| Experiment start and cooling force-on, return to protection, event/history persistence | Controller suite with temporary JSONL files |
| Dashboard, status, history/events, session start/stop/pause/end and manual modes | Real Python HTTP server on a random loopback port and fake controller |
| Manual device switch, invalid fractional/boolean keys, malformed control JSON | Real HTTP requests; rejected requests leave the fake device call history untouched |
| Energy readings/history, partial status, persistence and notifications | Python service with fake Energy Server responses and captured email sender; no actual mail |
| Saved custom HTTP endpoint, online → malformed status → HTTP 503 | Real .NET HTTP client and local TCP HTTP responder on a random port |
| Test connection with empty ADB list | Real TCP connection to fake loopback device; no configuration write |
| Save/reload configuration, recipients and password-free notification JSON | Temporary data directory; existing credential values are read only |
| Timing/nonfinite numeric validation | Nine invalid inputs; both saved files must remain unchanged |
| Settings/console navigation, save-only and validation feedback | Avalonia headless commands and real settings persistence |

## Confirmed fixes

Final run on 2026-10-04: **7 e2e cases passed**, **49 underlying regressions executed**, no skips; 33.27 seconds. Report: `.e2e/tools/smartbird-thermostat/report.json`, JUnit: `.e2e/tools/smartbird-thermostat/junit.xml`.

1. `/api/switch` truncated fractional JSON numbers and accepted booleans as switch commands. The HTTP API now accepts only integer 0/1 or their exact string representation. Before the fix, all three invalid-key subcases returned 200 and reached the fake device; they now return 400 without a device call.
2. Array/string control request bodies caused HTTP 500. JSON request bodies now require an object and return HTTP 400 for other shapes. All three malformed-body subcases failed before the fix and pass afterward.
3. Configuration accepted zero/negative weather and notification periods, negative cooldowns and negative expected device counts. Nonfinite values failed later in JSON serialization. Validation now checks finite numeric values and positive/nonnegative timing bounds before writing either file. Baseline: 9 failed, 4 passed; corrected regression count: 13.

## Coverage limits

The tools-only e2e target executes native workflow APIs and headless Avalonia commands. It does not drive the installed Shell or embedded WebView2 pixels. Physical SmartBird TCP firmware, actual cooling, real Android devices/ADB, USB energy meters, SMTP authentication and delivery, external weather lookup, installed scheduled-task registration/restart, and embedded browser focus/navigation require separate installed Dev and hardware acceptance. Tests never restart real thermostat tasks, change stored credentials, send mail, or switch actual hardware.

The Python suite imports the tool's owned source runtime. Updating the adapter/Surface alone does not refresh an installed `Runtimes/SmartBird` Python service. Installed runtime updates must carry `original-source/test_tools/smartbird_thermostat_service.py` as well.
