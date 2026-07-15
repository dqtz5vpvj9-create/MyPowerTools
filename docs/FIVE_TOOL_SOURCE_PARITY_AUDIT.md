# Five-tool source parity audit

Audit date: 2026-07-11

Delivery scope:

- Remote Notifications
- ADB Forwarder
- ScreenEase
- SmartBird Thermostat
- Doubao Computer Use

`Process Monitor` and `Remote Commands` stay outside this delivery because the user explicitly paused them.

## Source-of-truth roots

| Tool | Original source of truth | Current MyPowerTools implementation |
| --- | --- | --- |
| Remote Notifications | `C:\Users\lixinrui\repo\androidtools\powertool\page1.py`, `qt.py`, `windows_notifications.py`, `py_modules\simple_http_notification_receiver.py` | `src\MyPowerTools.Shell.Avalonia\Views\RemoteNotificationsView.axaml`, `ViewModels\RemoteNotificationsViewModels.cs`, `Services\RemoteNotificationHttpPoller.cs`, `Services\RemoteNotificationsLegacyStore.cs` |
| ADB Forwarder | `C:\Users\lixinrui\repo\androidtools\aosp_setup_forwarding.ps1` and identical `aosp_setup_forwarding_lab.ps1`; aliases come from `pwsh_modules\lib_aosp_base.ps1` | `src\AdbForwarder.MyPowerTools`, `src\MyPowerTools.Shell.Avalonia\Views\AdbForwarderView.axaml`, `Services\AdbForwarderToolService.cs`, `ViewModels\AdbForwarderViewModels.cs` |
| ScreenEase | `C:\Users\lixinrui\Documents\Codex\2026-07-01\careueyes-ida-pro-core-service\outputs\ScreenEase` | `src\ScreenEase.MyPowerTools`, `src\MyPowerTools.Platform.Windows`, `src\MyPowerTools.Shell.Avalonia\Views\ScreenEaseView.axaml`, `Services\ScreenEaseToolService.cs`, `ViewModels\ScreenEaseViewModels.cs` |
| SmartBird | `C:\Users\lixinrui\repo\androidtools\test_tools\smartbird_thermostat.py`, `smartbird_thermostat_service.py`, `smartbird_thermostat_task.py`, and `scripts\install-smartbird-thermostat-task.ps1` | `src\SmartBirdThermostat.MyPowerTools`, `src\MyPowerTools.WebToolHost`, `Services\SmartBirdThermostatToolService.cs`, `Views\SmartBirdWebView.cs`, `ViewModels\SmartBirdThermostatViewModel.cs` |
| Doubao | `C:\Users\lixinrui\Documents\Codex\2026-07-02\c-users-lixinrui-downloads-doubao-computer\work\doubao-computer-use-local-gh\computer_use` | Audited and implemented in the parallel Doubao workstream. Its source-derived endpoint map is Tool Server `38102`, MCP SSE `38080`, Planner `38189`. |

## Priority definition

- **P0**: a primary user workflow has different semantics, cannot complete, or controls the wrong backend operation.
- **P1**: a user-visible state, secondary workflow, configuration surface, or durability behavior is absent or misleading.
- **P2**: polish or diagnostic parity that does not block the main operation.

## Remote Notifications

### Original behavior inventory

| Area | Original behavior and source evidence |
| --- | --- |
| Polling | `Page1Worker` polls `/pull` every five seconds with `since` and `limit=20`, retries transport errors three times, distinguishes authentication failures, rejects insane timestamps, advances the newest sane waterline, and emits oldest-to-newest (`page1.py:315-411`). |
| Toolbar/status | Clear with confirmation, Persistent toast mode, Polling/Connected/Idle/Auth failed/Error state, and visible message count (`page1.py:674-703`, `1180-1215`). |
| Diagnostics | Server, Last poll, Fetched, Shown, Latest, and Last error (`page1.py:710-744`). |
| Topic filters | Bracket-prefix extraction, All chip, most-recently-used chip order, unread chip state, filtered count, and persisted selected filter (`page1.py:64-79`, `885-1034`). |
| Message stream | Newest-first insertion, scroll-position preservation, icon/channel/time metadata, Markdown, syntax highlighting, task lists, links, tables, context-menu copy, and double-click detail (`page1.py:1041-1150`, `MessageBrowser` and `MessageDetailDialog`). |
| Persistence/dedup | Up to 500 stored messages, 200 recent hashes, 5000 seen IDs, restored oldest-first, stable server ID with legacy hash fallback (`page1.py:604-662`, `1152-1178`). |
| Desktop notification | Native Windows toast, Persistent mapped to Toast `reminder` scenario, exact-message activation URI, singleton forwarding over `\\.\pipe\AndroidToolsToastActivation`, and click-to-open detail (`qt.py:317-375`, `411-529`; `windows_notifications.py:48-178`, `436-455`). |
| Tray lifecycle | Close hides the main window; tray opens or quits; shutdown persists and stops poll workers (`qt.py:546-565`, `page1.py:1282-1293`). |

### Current parity and gaps

| Requirement | Current state | Gap | Priority |
| --- | --- | --- | --- |
| Incremental authenticated poll | Implemented in `RemoteNotificationHttpPoller`, including limit 20, three attempts, timeout and signature redaction. | No primary gap found. | - |
| Status, diagnostics, clear confirmation | Implemented. Error details and retry are clearer than the original. | No primary gap found. | - |
| Topic chips/unread/filter count | Implemented in `RemoteNotificationsViewModel` and bound through `Classes.unread`/`Classes.selected`. | No primary gap found. | - |
| Markdown stream/detail/copy | `MarkdownTextBlock` renders headings, lists, task items, quotes, inline and fenced code, syntax tokens, tables, Markdown links and bare HTTP links. Detail, copy and double-click behavior are wired. | No source-parity gap found in the original message workflows. | - |
| Persistence/dedup | The legacy registry store keeps 500 visible messages, 200 recent hashes and an independent 5000-ID seen ring; both stable server IDs and fallback IDs are restored and persisted. | No primary gap found. | - |
| Windows toast | Accepted deduplicated messages flow to the WinRT ABI. The visible Persistent setting maps to Toast `scenario="reminder"`; the off state emits the normal transient Toast XML. | No primary gap found. | - |
| Toast activation | `mypowertools://remote-notification?id=...` is registered and parsed, then forwarded over a named pipe. The Shell is restored, navigates to Remote Notifications and opens/acknowledges the exact persisted message. | No primary gap found. | - |
| Tray/singleton lifecycle | A named mutex protects the main Shell instance. Secondary starts retry the activation pipe; normal starts restore the existing Shell and command-palette starts preserve the palette action. | No primary gap found. | - |

### Closure evidence

1. `RemoteNotificationsViewModel.PollAsync` publishes only after the stable/fallback ID ring accepts a message.
2. `RemoteNotificationWindowsToastPublisher.BuildEnvelope` maps Persistent to `reminder` and the off state to a transient Toast.
3. `RemoteNotificationActivationCoordinator` restores the Shell and delegates to `ShellWorkspaceController.OpenRemoteNotificationAsync`, which selects the inbox and opens the exact ID.
4. `RemoteNotificationsLegacyStore` persists the independent 5000-ID ring.
5. `RemoteNotificationsProductTests` covers links, fenced code, task items, tables, native envelope modes, real WinRT object construction, named-pipe transport and the singleton mutex.

## ADB Forwarder

### Original workflow inventory

The original source is a runnable workflow script rather than a panel. Every step below is user-visible through progress/output and contributes to the completed operation:

| Step | Original operation and source evidence |
| --- | --- |
| Windows guard | Exit outside Windows (`aosp_setup_forwarding.ps1:55-58`). |
| Existing tunnel cleanup | Find and stop `ssh-forward` before building a new tunnel (`:51-61`). |
| Portproxy setup | Ensure `0.0.0.0:15557 -> 127.0.0.1:15556` exists through elevated `netsh` (`:8-13`, `:63`). |
| Device readiness | Wait for the selected USB ADB device through the `aa` alias (`:64`). |
| ADB-over-TCP configuration | Read `persist.adb.tcp.port`; set it to `5555` and run `adb tcpip 5555` when needed (`:40-48`, `:65`). |
| Host ADB forward | Create `tcp:15556 -> device tcp:5555` using `adb -a forward` (`:66`). |
| Connection recovery | Detect `offline`, disconnect it, repeatedly `adb connect` until both `127.0.0.1:15556` and `127.0.0.1:15557` report `device` (`:16-38`, `:67-68`). |
| Local verification | Print `adb devices` (`:69`). |
| Optional local-only exit | `-NoSsh` completes after local forwarding (`:70-72`). |
| SSH reverse tunnel | Start `ssh-forward.exe -CfNg -R 15557:127.0.0.1:15557 r743` (`:73-76`). |
| Remote AOSP bootstrap | On `r743`, kill the AOSP adb server, connect to `127.0.0.1:15557`, and remount the device (`:77-81`). |

### Current parity and gaps

| Requirement | Current state | Gap | Priority |
| --- | --- | --- | --- |
| Read ADB devices | Implemented with `adb devices -l`; page shows device/model/state. | No primary gap found. | - |
| Read/manage Windows portproxy | Implemented with editable mappings, validation, import, preview, brokered apply/revert and rollback data. This is stronger than the fixed original netsh rule. | No primary gap found for this sub-step. | - |
| Configure device ADB TCP port | Absent. | The workflow never checks or sets `persist.adb.tcp.port`, so the device-side prerequisite can remain unsatisfied. | **P0** |
| Create ADB host forward | Absent. | Generic portproxy mappings do not create `adb forward tcp:15556 tcp:5555`. | **P0** |
| Repair offline connections | Absent. | No disconnect/reconnect loop exists for `offline` endpoints. | **P0** |
| Connect both local endpoints | Absent. | The page lists current devices but cannot complete `adb connect 127.0.0.1:15556/15557`. | **P0** |
| SSH reverse tunnel | Absent. | No tunnel state, host setting, start/stop or failure output exists. | **P0** |
| Remote AOSP connect/remount | Absent. | No remote host operation or result is represented. | **P0** |
| `NoSsh` workflow choice | Absent. | User cannot choose local-only versus full remote path. | P1 |
| End-to-end progress/rollback | Portproxy alone has preview/rollback; the composed workflow does not exist. | Build a staged state machine with explicit current step, retry, cancel, and reversible tunnel/portproxy changes. | **P0** |

### Required product shape

The Rules page can stay as the advanced Windows mapping editor. Add a primary **Forward device** workspace with:

- device selector and authorization state;
- local ports `15556` and `15557` with editable defaults;
- remote host (`r743`) and **Include SSH remote access** toggle;
- preflight covering ADB, device state, device TCP port, port availability, `ssh-forward`, SSH reachability and NetworkBroker availability;
- staged execution: device TCP -> ADB forward -> portproxy -> local connects -> optional reverse SSH -> remote adb connect/remount;
- live log, per-step status, retry and cleanup;
- final verification for both local endpoints and the remote AOSP adb device.

Do not execute the original script directly from the UI: it contains unbounded connection loops and shell composition. Reimplement its semantics with structured process arguments, timeouts, cancellation, broker approval and a recorded rollback plan.

## ScreenEase

### Original user-facing inventory

| Area | Original behavior and source evidence |
| --- | --- |
| Connection/header | Named-pipe endpoint by default (`pipe:screenease.core`), auto-start local CoreService, refresh/connect, online/error/last-updated state, current effect summary (`MainViewModel.cs:294-521`; `MainWindow.xaml:297-318`, `569-593`). |
| Profiles | Seven built-in profiles, each with day and night values; select/apply; save selected; add custom; manual adjustment profile (`Defaults.cs:8-49`, `MainWindow.xaml:338-486`). |
| Master eye-care action | `ToggleFilter` calls CoreService `disable` when active and `apply` when inactive; state comes back from the controller (`MainViewModel.cs:392-447`). |
| Display effect | Core `DisplayEffect` owns enabled, profile, kelvin, brightness, night-value flag and applied timestamp; Windows driver uses gamma ramps with smooth transitions (`Models.cs`, `DisplayDrivers.cs`, `EyeCareController.cs`). |
| Rest timer | Service-owned state machine: stopped/work/short break/long break/paused, start/pause/resume/reset, completed-work count and persisted settings (`Models.cs`, `RestTimerEngine.cs`, `EyeCareController.cs:191-247`, `MainWindow.xaml:495-558`). |
| Monitor diagnostics | Device, bounds and primary monitor, plus service endpoint and explicit connect (`MainWindow.xaml:569-593`). |
| Additional source surfaces | Day/night scheduling, sunrise/sunset, smooth transitions, overlay dimming, global hotkeys and CareUEyes-style legacy INI import are supported through the core API/native protocol even though the compact WPF main page exposes only part of them (`Defaults.cs`, `EyeCareController.cs:148-190`, `247-317`, `docs\API.md`). |

### Current parity and gaps

| Requirement | Current state | Gap | Priority |
| --- | --- | --- | --- |
| Visual profile/manual/rest layout | The tool page follows the original profile rail, day/night values, effect controls and persisted rest-timer workspace. | No primary gap found. | - |
| Master eye-care action | The page calls the logical `effect.toggle`/apply/disable workflow and preserves effect state when the display writer is unavailable. | No primary gap found. | - |
| Display backend | Windows uses the original gamma-ramp algorithm and identity reset. RDP/read-only sessions expose truthful unsupported state while logical controls continue to work. | Physical-display gamma acceptance remains a local-hardware release check. | P2 |
| Profile data model | All seven source profiles, original ordering, separate day/night values, personal mode and manual adjustment are represented. | No primary gap found. | - |
| Rest timer | Reminder settings and stopped/work/short-break/long-break/paused runtime state live in the module store and survive navigation/restart. | No primary gap found. | - |
| Schedule/night | Sunrise, sunset, schedule enablement and current day/night effect transitions are persisted and applied. | No primary gap found. | - |
| Overlay | A per-monitor, layered, click-through Windows overlay implements the original opacity/color controls and cleans up on shutdown. | No primary gap found. | - |
| Hotkeys | All eight source actions and gestures are declared, default disabled, editable through Settings, synchronized with Runtime and protected from destructive partial settings saves. | No primary gap found. | - |
| Legacy import | Original `settings.json` and CareUEyes-compatible INI settings map profiles, timer, schedule, overlay, transition metadata and hotkeys. | No primary gap found. | - |
| Partial settings safety | Module settings merge against current complete state; hotkey-only patches contain only `$hotkeys`. | No primary gap found. | - |
| Smooth-transition duration | The value is stored, migrated and shown. The original Windows gamma writer also applies immediately and does not interpolate the forwarded duration. | Source-compatible inherited limitation; an interpolated writer is a future enhancement. | P2 |

### Closure evidence

1. The original ScreenEase solution builds with zero warnings/errors and its 17 core tests pass.
2. MyPowerTools ScreenEase source-parity tests cover profiles, day/night scheduling, logical effect persistence, timer lifecycle, gamma reset, overlay, hotkey actions, JSON/INI migration, concurrent state writes and corrupt-state recovery.
3. Settings/Hotkey tests prove an empty Runtime settings store plus a hotkey-only edit cannot clear profiles, effect, reminder, schedule or overlay state.
4. The expanded advanced workspace and its eight shortcut rows render through Avalonia.Headless without creating a visible local window.

## SmartBird embedded dashboard

### Original visible dashboard

The original dashboard is the HTML document returned by `smartbird_thermostat_service.py` at `/`. Embedding this document in WebView2 preserves these panels and operations without reimplementing them:

- Mode, active sessions, Switch, TCP clients, Surface/sensor, Dew Point, off threshold, restart threshold, runtime, event/history counts.
- Energy Server status, endpoint, backend, HID state/candidates, devices, session, capabilities, thermal link and error.
- **Read Meter** -> `POST /api/energy/read` and formatted per-device values.
- Energy device history chart and table -> `/api/energy/history`.
- Temperature history range buttons 15m/1h/6h/24h/All -> `/api/history`.
- Manual Switch toggle -> `POST /api/switch`.
- Switch-state chart and thermostat events table -> `/api/events`.
- Two-second refresh and responsive chart redraw.

`MyPowerTools.WebToolHost.exe` embeds the original dashboard directly. The dedicated WinExe owns the child HWND, WebView2 controller, user-data directory and navigation/resource/permission/download policy. It pins the source to `http://127.0.0.1:19002/` and communicates with the Shell through bounded, versioned JSON-line input/output. The Shell control starts and monitors the host, sends clipped bounds/visibility/focus/reload commands, hides the native surface below global overlays, forwards global keyboard shortcuts, and switches to the existing browser fallback when the host exits.

This crash-containment boundary covers the SmartBird web surface. SmartBird's typed backend facade still runs through the selected Runner module host, currently `inproc-dotnet`; the other pure Avalonia tool views remain inside the Shell process. The WebToolHost uses the same user token and integrity level as the Shell, so this boundary does not form a sandbox for untrusted UI code.

| Requirement | Current state | Gap | Priority |
| --- | --- | --- | --- |
| Real service/dashboard | Live task, root HTML and `/api/status` are verified on this PC. The fallback names a missing task and the recovery action runs the installed task without a console window. | Complete. | done |
| Dashboard embed | The fixed-origin WebView2 controller runs in `MyPowerTools.WebToolHost.exe`. A hidden cross-process probe verifies controller creation and a host-owned child HWND under a foreign parent HWND. Avalonia.Headless renders the truthful fallback because it has no native Shell HWND. | Complete for automated coverage; attached-hardware POST actions remain an explicit manual acceptance step. | done |
| UI crash containment | The Shell project contains no WebView2 package or `NativeControlHost`. Host launch uses redirected standard I/O with `CreateNoWindow`; unexpected exit produces a fallback state while the Shell stays alive. | Complete for the SmartBird web surface. | done |
| Native surface integration | Ancestor clips are converted into a Win32 window region; command and permission overlays hide the native surface; global accelerators and Tab/Shift+Tab focus moves bridge back to the Shell. | Complete. | done |
| Build and publish closure | Local builds, RID builds and direct Shell publish place the host only under `WebToolHost/`. The Shell output root contains no duplicate partial host executable. | Complete. | done |
| Source maintenance mapping | Logs read the original service log, config reports local/source runtime values, and restart produces a scheduled-task broker request. No maintenance call targets a route absent from the source handler. | Complete. | done |
| Energy diagnostics URL | The thermostat bridge serves `/api/energy/status` and reports its configured Energy Server, whose task default is `http://127.0.0.1:18988`. | Complete. | done |

## Doubao handoff note

The Doubao workstream owns its detailed matrix. The source audit established one blocking invariant that every module, service and page must share:

```text
Tool Server  http://127.0.0.1:38102  /config and /?Action=...
MCP Server   http://127.0.0.1:38080  /sse
Planner      http://127.0.0.1:38189  /health, /models, /run/task
```

The original WPF workflows are service start/status, model selection, task prompt, advanced system prompt, SSE run/stop, screenshot preview, trace list/detail/raw JSON, overlay show/hide/self-test, clear trace and diagnostics.

## Evidence executed on 2026-07-11

### Original Remote Notifications contract

```powershell
cd C:\Users\lixinrui\repo\androidtools
python -m pytest tests/test_desktop_notification_contract.py tests/test_notification_queue.py -q
```

Result: `8 passed in 0.09s`.

### MyPowerTools Remote Notifications closure

```powershell
dotnet build .\src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj --configuration Release --no-restore
dotnet test .\src\MyPowerTools.Tests\MyPowerTools.Tests.csproj --configuration Release --filter FullyQualifiedName~RemoteNotification --no-restore
```

Result: Shell build succeeded with zero warnings and zero errors; all 26 Remote Notification tests passed. The Windows-only test constructs a real WinRT `XmlDocument`, `ToastNotification` and notifier while deliberately skipping the final `Show` ABI call, so verification produces no visible banner.

The combined Remote Notification plus product visual-acceptance filter passed 34/34, covering default, wide, scroll, filter, detail and activation headless paths.

A read-only live pull used the current UTC time as its waterline and returned `state=idle`, `count=0`, `error=""`. It did not write the legacy store or publish a Toast.

The installed legacy Page1 registry state was also loaded read-only through the managed store: 500 visible messages, 3846 independent seen IDs, 200 recent hashes, 130 labels and Persistent enabled. This proves the compatibility reader handles the user's real retained dataset rather than only synthetic fixtures.

Headless Avalonia artifacts:

- `artifacts/remote-notifications-source-parity-default/real-remote-notifications-inbox.light.normal.1920x1080.png`
- `artifacts/remote-notifications-source-parity/real-remote-notifications-inbox.detail.light.normal.1920x1080.png`
- `artifacts/remote-notifications-live-readonly/real-remote-notifications-inbox.light.normal.1920x1080.png`
- `artifacts/remote-notifications-live-readonly-detail/real-remote-notifications-inbox.detail.light.normal.1920x1080.png`
- `artifacts/remote-notifications-live-readonly-activation/real-remote-notifications-inbox.activation.light.normal.1920x1080.png`

The fixture detail scenario double-clicked `remote-message-001`, opened `RemoteNotificationDetailWindow`, and rendered the clickable link, table, task item, inline code and fenced code sample. The live-service artifacts copied the current Page1 registry snapshot into memory, used a no-op poller and no-op Toast publisher, and verified the filtered inbox plus exact real-message detail without mutating retained state. The activation scenario built the real `mypowertools://remote-notification?id=...` URI, parsed the same stable ID and opened that record's detail window; the three steps are recorded in its manifest.

### Original ScreenEase build and core tests

```powershell
cd C:\Users\lixinrui\Documents\Codex\2026-07-01\careueyes-ida-pro-core-service\outputs\ScreenEase
dotnet build .\ScreenEase.sln --configuration Release --artifacts-path C:\Users\lixinrui\repo\MyPowerTools\artifacts\source-parity\screenease-original
dotnet C:\Users\lixinrui\repo\MyPowerTools\artifacts\source-parity\screenease-original\bin\ScreenEase.Tests\release\ScreenEase.Tests.dll
```

Result: build succeeded with zero warnings and zero errors; all 17 original core tests passed.

### Live SmartBird source service

```powershell
Get-ScheduledTask -TaskName SmartBirdThermostat
Get-ScheduledTaskInfo -TaskName SmartBirdThermostat
Invoke-RestMethod -Uri http://127.0.0.1:19002/api/status -TimeoutSec 3
```

Result: task `Running`; live response reported protection mode, one Smart-Bird TCP client, 7200 history points and 519 events. `/api/energy/status` reported `online=true` with source URL `http://127.0.0.1:18988`.

The source test suite passed 30 tests. The MyPowerTools SmartBird product suite passed 15 tests, including fixed-origin probing, cross-origin redirect rejection, WebView resource/navigation policy, crash fallback/retry source coverage, hidden scheduled-task recovery, external-browser origin checks, module loopback validation and ADB endpoint redaction.

Read-only, no-window artifacts:

- `artifacts/smartbird-final-review/original-dashboard-live-1600x1400.png` — the original live dashboard, including mode/switch/surface/dew-point metrics, Energy Server and Read Meter, temperature and switch charts, and source event rows.
- `artifacts/smartbird-final-review/shell-live-fallback/real-smartbird-thermostat-live.light.normal.2048x1152.png` — the complete Shell page in Avalonia.Headless, truthfully showing that native WebView2 is unavailable in the headless renderer while retaining the live status and exact browser fallback URL.

The live CLI status and event probes completed successfully. Their verification output contained the loopback thermostat and Energy Server URLs, bounded event results, and no private ADB endpoint; source event device endpoints were replaced with `<device-endpoint>`.

### Live ADB/portproxy state

```powershell
adb version
adb devices -l
netsh interface portproxy show v4tov4
```

Result: ADB 34.0.5 available, four devices connected, and six live v4tov4 rules detected. These commands verify that the current page has real data to display; they do not close the missing composed forwarding workflow.

The original workflow was also parsed without executing its mutating commands:

```powershell
$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    'C:\Users\lixinrui\repo\androidtools\aosp_setup_forwarding.ps1',
    [ref]$tokens,
    [ref]$errors) | Out-Null
if ($errors.Count -gt 0) { throw ($errors.Message -join [Environment]::NewLine) }
```

Result: PowerShell AST parse passed with 332 tokens.

## Release gate

Before declaring the five tools complete, run all of the following from the MyPowerTools repository:

```powershell
dotnet build .\MyPowerTools.slnx --configuration Release
dotnet test .\MyPowerTools.slnx --configuration Release --no-build
dotnet run --project .\src\MyPowerTools.Cli -- validate modules
dotnet run --project .\src\MyPowerTools.Cli -- validate contracts
dotnet run --project .\src\MyPowerTools.Runner -- --once
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\scripts\smoke.ps1
```

Additional source-parity gates:

- Remote Notifications: inject one signed notification, observe one native toast, click it, and verify the exact detail opens; repeat with Persistent on and off.
- ADB Forwarder: execute local-only and SSH-enabled plans against a disposable port set; verify device TCP state, both local ADB endpoints, reverse tunnel, remote AOSP connect/remount, cancellation and cleanup.
- ScreenEase: run the original 17 tests plus MyPowerTools integration tests; use the memory driver for apply/disable/timer/schedule/overlay/hotkey/import, then test Windows gamma on local physical hardware.
- SmartBird: open the real installed Shell, verify WebView2 loads `/`, change time ranges, read the meter, toggle the switch and confirm `/api/events`; repeat with the scheduled task stopped to validate fallback/start/recovery.
- Doubao: start the real three-service stack, fetch models, stream one task, cancel one task, render screenshot/trace/raw JSON, exercise overlay show/hide/self-test, then stop all services and verify truthful offline state.
