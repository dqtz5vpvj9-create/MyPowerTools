# P-Foundation-6 Review Notes

Run date: 2026-07-05.

## Scope

P-Foundation-6 covers real module lifecycle, continuous event pumping, persisted hotkey overrides, sidecar readiness, typed ModuleControl command arguments, and acceptance test layout split.

Package hash manifests, signatures, and full `scripts\smoke.ps1` were kept out of this iteration because `scripts\smoke.ps1` runs `package sign-local modules` and mutates package hash/signature artifacts.

## Completion Rationale

| Subgoal | Why It Is Complete |
|---|---|
| Real module lifecycle | `IMptModuleLifecycle` and transport lifecycle hooks exist. Runtime enable/disable now calls transport resource start/stop paths, clears dynamic commands on disable, stops event streams, unloads InProc sessions, disposes gRPC module sessions, and keeps shared gRPC pools alive only while enabled modules still need them. `Runtime_lifecycle_disable_unloads_inproc_and_enable_restores_commands` proves disable unloads and removes commands, then enable restores commands/status. |
| Continuous event pump | Runner starts `StartModuleEventPump()` in normal mode. Runtime owns supervised per-module subscriptions with cursor resume, cancellation, duplicate filtering, retry/backoff, EventBus publication, notification updates, dashboard refresh inputs, and log records. `Runtime_module_event_pump_collects_events_without_manual_collection` proves an event emitted after pump startup reaches HostEvents without manual collection. |
| Hotkey persistence | `HotkeyStore` persists user overrides under runtime state. Runtime lists overrides before manifest defaults, reset removes overrides, disabled modules unregister by leaving the binding list, and enable re-registers the persisted override. Settings Center writes `$hotkeys` edits and resets. Runtime and Shell tests cover edit, conflict, persistence, reset, disable, and enable. |
| Sidecar readiness | `GrpcIpcModuleHost` launches sidecars from package/runtime directories, supplies `MPT_PACKAGE_DIR`, `MPT_MODULE_ID`, `MPT_RUNTIME_ID`, `MPT_ENDPOINT_TRANSPORT`, and `MPT_ENDPOINT_ADDRESS`, retries initialize until ready, and classifies early exit, timeout, rejected initialize, and protocol mismatch. Tests cover delayed readiness and early process exit. |
| Typed command args | `ExecuteCommandRequest` now carries `typed_args` and `args_json` alongside the legacy map. gRPC host sends nested JSON; AndroidTools.Powertoold reads typed args first. The P6 gRPC test verifies nested port mappings, process watch arrays, numbers, booleans, and strings survive the sidecar round trip. |
| Test split | The original 168 acceptance tests were moved into 10 domain partial files, with shared helpers kept in `RuntimeAcceptanceTests.cs`. `src/MyPowerTools.Tests/README.md` documents the layout. P6 adds 6 focused tests; the full suite passes 174/0/0. |

## Validation

| Command | Result |
|---|---|
| `dotnet restore MyPowerTools.slnx` | Passed; all projects up-to-date. |
| `dotnet build MyPowerTools.slnx --no-restore` | Passed, 0 warnings, 0 errors. |
| `dotnet test MyPowerTools.slnx --no-build` | Passed 174, failed 0, skipped 0. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` | 5 production packages valid. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` | 5 packages and 7 modules passed contract validation. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` | UI gate passed. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots-p6` | Snapshot manifest written. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-p6` | Fixture-backed Shell real screenshot manifests written. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --live-runner --full-shell --runner-only --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-p6-live-runner` | Live Runner HostControl Shell screenshot manifests written. |
| `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` | Indexed 7 modules and reported expected degraded states. |
| Manual Shell HostControl smoke | Shell connected to temporary Runner, reported 7 modules, 7 dashboard cards, 81 commands, requested shutdown, and Runner exited with code 0. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` | 6 templates passed manifest validation, UI gate, .NET builds, and Python syntax compilation. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- module list --include-disabled` | Listed all 7 modules. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` | Reported 5 packages, 7 modules, 81 commands, 50 dynamic commands, and AndroidTools shared gRPC process diagnostics. |
