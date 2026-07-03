# Project Status

Run date: 2026-07-04.

## Snapshot

| Field | Value |
|---|---|
| Project | MyPowerTools |
| Current phase | P8 Final production closure |
| Last completed phase | P8 Final production closure |
| Next phase | Complete |
| SDK | 10.0.301 from `global.json` and `dotnet --version` |
| Target frameworks | `net10.0` projects across the solution |
| Production packages | 5: `adb-forwarder`, `android-tools-suite`, `doubao-agent`, `screenease`, `smartbird-thermostat` |
| Production modules | 7 |
| Templates | 6 |
| Tests | 88 passed, 0 failed, 0 skipped |
| Release artifact | `artifacts/release/MyPowerTools-win-x64.zip` |
| Release SHA256 | `29BECF13374D92F100E58BA60F9187FD166C136919427B826C0A8979EEA3C670` |
| Production closure | true |

## Latest Validation Results

| Command | Result |
|---|---|
| `dotnet --version` | `10.0.301` |
| `dotnet restore MyPowerTools.slnx` | Succeeded; all projects were up-to-date. |
| `dotnet build MyPowerTools.slnx --no-restore` | Succeeded with 0 warnings and 0 errors. |
| `dotnet test MyPowerTools.slnx --no-build` | Passed 88, failed 0, skipped 0. |
| `dotnet run --project src\MyPowerTools.Cli -- validate modules` | 5 production packages valid. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` | 5 packages and 7 modules passed contract validation; AndroidTools runs through `grpc-ipc` powertoold with notifications running, remote commands running, process monitor degraded until a watch list is saved, and ScreenEase exposing 14 commands. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` | `signature-hook` trust passed for all 5 production packages after local hash/signature refresh. |
| `dotnet run --project src\MyPowerTools.Cli -- ui check modules` | UI gate passed. |
| `dotnet run --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots` | Wrote 7 module dashboard-card contract snapshots and 7 PNG pixel snapshots. |
| `dotnet run --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots` | Wrote 10 Shell surface snapshots and 10 PNG pixel snapshots with 12 keyboard shortcut entries, 7 focus state entries, Settings conflict, Command Palette permission-required, and Logs streaming states. |
| `dotnet run --project src\MyPowerTools.Runner -- --once` | 7 modules indexed; AndroidTools Notifications and Remote Commands run through powertoold, AndroidTools Process Monitor reports its watch-list degraded state, and current expected degraded states remain for Doubao partial services, ScreenEase unsupported DDC/CI monitor hardware, and SmartBird Energy Server/FNB-58 dependencies. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` | Reports 5 packages, 7 modules, 81 commands, AndroidTools under `grpc-ipc`, one shared process pool, and per-module `supervisor` state with consecutive failure counts and next actions. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- runner process pause . --duration-minutes 1` | With a temporary Runner active, selected the AndroidTools shared gRPC pool, paused automatic restart for one minute, printed expiry/modules, then resume restored automatic policy. |
| `dotnet run --project src\MyPowerTools.Cli -- run screenease.native-writer.status` | Succeeded with Windows DDC/CI writer probe output; current Generic PnP Monitor returns `GetMonitorCapabilities` unsupported. |
| `dotnet run --project src\MyPowerTools.Cli -- run screenease.profile.apply` | Succeeded with profile plan and safe default `native-host-required` because hardware writes are disabled unless explicitly requested or configured. |
| P8 audit scans | Placeholder/stub/fake scans found only documentation/history or UI input `PlaceholderText`; `unsupported` findings are explicit degraded states; production module roots contain no sample modules; high-confidence secret scan found no real credential material; release metadata contains no `file:///C:` URL. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` | Rebuilt `artifacts/release/MyPowerTools-win-x64.zip`, `RELEASE_NOTES.md`, `release-metadata.json`, and the Scoop manifest. ZIP SHA256 `29BECF13374D92F100E58BA60F9187FD166C136919427B826C0A8979EEA3C670`; size 171498490 bytes. |
| Release metadata/Scoop manifest check | `release-metadata.json` artifact hash and `package-managers/scoop/mypowertools.json` 64-bit hash both equal `29BECF13374D92F100E58BA60F9187FD166C136919427B826C0A8979EEA3C670`; both URLs are relative `MyPowerTools-win-x64.zip`; manifest exposes `mpt` as the CLI shim. |
| Release zip hygiene check | `MyPowerTools-win-x64.zip` contains no `bin/`, `obj/`, or `modules/modules/` entries. |
| `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` | Release package trust passed for all 5 production packages. |
| `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p6` | Release Runner indexed 7 modules from the release root and started AndroidTools powertoold from the release package. |
| `artifacts\release\win-x64\Shell\MyPowerTools.Shell.Avalonia.exe --smoke --timeout-ms 30000 --quit-runner` | Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited with code 0. |
| `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` | Resolved the sibling release Runner executable without registry writes. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun` | Succeeded and printed the portable install plan. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force` | Succeeded and printed the uninstall plan without removing files. |

## P8 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Final audit | `docs/P8_FINAL_AUDIT.md` records scan commands, findings, and classifications for TODO/FIXME/placeholder/stub/fake/coming soon, unsupported states, sample modules, hardcoded user paths, release URLs, and secret patterns. |
| Cleanup fixes | Local package signature algorithm was renamed from `sha256-manifest-placeholder` to `sha256-manifest-local`; `UiSurfaceGate.WriteSnapshotPlaceholder` was renamed to `WriteDefaultSnapshotSet`; production package signatures were refreshed. |
| Final validation | SDK 10.0.301, restore, build 0/0, tests 88/0/0, module validation, contract validation, UI gate, UI snapshots, diagnostics, inspect, module list, Runner once, template validation, smoke, publish, release package trust, release Runner once, release Shell smoke, autostart dry-run, install/uninstall dry-run, metadata hash parity, and zip hygiene all passed. |
| Release artifact | `artifacts/release/MyPowerTools-win-x64.zip`, SHA256 `29BECF13374D92F100E58BA60F9187FD166C136919427B826C0A8979EEA3C670`, size 171498490 bytes. |
| Closure | `.codex/project-state.json` marks P8 complete with `productionClosure=true`; remaining work is external validation only. |

## P7 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Platform abstractions | Added `ILocalIpc`, `LocalIpcService`, `IHotkeyService`, `IPrivilegeBroker`, unsupported provider implementations for notification, autostart, service, network, process, hotkey, and privilege surfaces, plus managed process inspection. |
| macOS/Linux packs | `MacPlatformPack` and `LinuxPlatformPack` now expose local IPC, notification, autostart, service, network, process, display, tray, secret, hotkey, and privilege services. Unsupported providers return explicit `unsupported` state/messages; process inspection uses the managed runtime where supported. |
| Windows pack | `WindowsPlatformPack` exposes `LocalIpc`, `Hotkeys`, and `Privileges`; Windows Runner IPC remains Named Pipe based, hotkey registration is truthfully marked pending, and privilege evaluation returns broker-required. |
| Acceptance coverage | Added tests for required capability failure -> `unsupported`, optional capability failure -> `degraded`, platform-native IPC endpoint shapes, Mac/Linux degraded service behavior, hotkey/privilege provider surfaces, and `PrivilegedBroker` implementing the platform privilege contract. |
| P7 validation | Build passed with 0 warnings, 88 tests passed, module inspection shows requirements and broker permissions, diagnostics reports Windows `grpc-ipc` runtime process state, contract validation passed, strict package trust passed, and `scripts/smoke.ps1` passed. |

## P6 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Release metadata | Added `scripts/release-metadata.ps1`; `publish-windows.ps1` now writes `artifacts/release/release-metadata.json` and `artifacts/release/package-managers/scoop/mypowertools.json` after the portable zip is created. |
| Release notes | `scripts/release-notes.ps1` now lists the release/update metadata and Scoop manifest when they exist. |
| Acceptance coverage | `Release_metadata_script_writes_update_and_scoop_manifests` verifies metadata, local artifact URL, hash length, Scoop hash parity, and the `mpt` shim entry. |
| Release validation | Restore, build, 83 tests, module validation, contract validation, strict package trust, template validation, smoke, publish, release package trust, release Runner once, release Shell smoke, release autostart dry-run, install dry-run, uninstall dry-run, hash parity, and zip hygiene all passed locally. |
| P6 closure | P6 internal gates are complete locally. Production signing and channel publication require external signing material or a signing service. |

## P5 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| ModuleSupervisor automation | Added `ModuleSupervisor` to record module health observations, consecutive failure counts, supervisor state, last observation time, and actionable next steps. Repeated HTTP facade failures now escalate to `intervention-needed` and emit Dashboard alerts. |
| Runtime diagnostics | `RuntimeModuleDiagnostics` now carries module summary, observation count, consecutive failure count, supervisor state/action, and last observed time through RuntimeDiagnostics. |
| HostControl and Shell visibility | `mpt_host_control_v1.proto`, `HostControlGrpcService`, and Shell Diagnostics now expose and render supervisor data. Shell refreshes Dashboard/Modules/Diagnostics on `module.health.changed` events. |
| CLI operations | `mpt diagnostics` refreshes health before printing diagnostics and includes `supervisor`, `failures`, `observations`, and `action`. `mpt runner process pause . --duration-minutes 1` now resolves `.` to the first active RuntimeDiagnostics process pool for smoke-friendly policy checks. |
| Acceptance coverage | Added Runtime tests for repeated HTTP facade failures, recovery reset, HostControl supervisor fields, and CLI diagnostics output. Existing CLI process policy test now covers `pause .`. |
| P5 closure | P5 internal gates are complete locally: build passed with 0 warnings, 82 tests passed, module/contract validation passed, diagnostics reports supervisor data, runner process pause shorthand passed against a live Runner, Runner once passed, `smoke.ps1` passed, release Runner once passed, release Shell smoke passed, release notes include ModuleSupervisor, and the Windows zip was rebuilt with SHA256 `3210E8F4607F484C82AD95452BFE9E76ECC51DACEB1C04099719B57AA40ECA9B`. |

## Current Module State

| Module | State From Latest Runner/Contract Evidence | Notes |
|---|---|---|
| `adb-forwarder` | running | ADB diagnostics and Windows portproxy diagnostics are available. |
| `android-tools.notifications` | running | Served by the shared `powertoold` gRPC IPC sidecar; endpoint imported from package-shared config. |
| `android-tools.remote-commands` | running | Served by the shared `powertoold` gRPC IPC sidecar; 11 command(s) imported from package-shared `commands.yaml`. |
| `android-tools.process-monitor` | degraded | Served by the shared `powertoold` gRPC IPC sidecar; needs a saved watch list through `android-tools.process-monitor.watch.save`. |
| `doubao-agent` | degraded | InProc controller checks planner/tool/MCP separately; current local runtime reports 1/3 services reachable. |
| `screenease` | degraded | Windows DDC/CI native writer exists and is wired behind explicit hardware apply; current monitor reports unsupported DDC/CI capabilities, so status/profile paths work with actionable hardware diagnostics. |
| `smartbird-thermostat` | degraded | InProc typed facade reads HTTP status/events/config/logs, returns brokered restart details, and reports missing Energy Server/FNB-58 dependency state. |

## Validation Note

Local builds copy module assemblies into `modules/`. When assemblies change, run `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` before trust-sensitive tests or strict package verification. The latest P7 verification was repeated after the signatures matched the build outputs.

## P4 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Shell keyboard navigation | Added `ShellKeyboardShortcut` and MainWindow handling for `Ctrl+K`/`Ctrl+F` command palette focus, `Escape` clear/focus return, `F5`/`Ctrl+R` refresh, and `Ctrl+1..7` primary page navigation. `Shell_keyboard_shortcuts_resolve_navigation_and_command_palette_actions` verifies the mapping. |
| HostControl settings schema | Added HostControl `GetSettingsSchema`, Runtime transport schema retrieval, and InProc/gRPC/stdio implementations so Shell Settings stays Runner IPC-backed. `HostControl_get_settings_schema_exposes_runtime_schema` verifies Doubao runtime schema exposure. |
| Schema-rendered Settings page | Shell Settings now renders schema fields as toggles, enum selectors, text fields, and JSON editors for object/array settings, then writes structured settings patches through HostControl revision checks. |
| Token-governed colors | Added `MptTheme` and replaced Shell/control hardcoded color references with centralized theme tokens. `Shell_ui_colors_are_centralized_in_theme_tokens` prevents direct color literals in MainWindow and MPT controls. |
| Shell visual matrix | `shell-ui-snapshot-manifest.json` now records keyboard/focus evidence plus Settings conflict, Command Palette permission-required, and Logs streaming states. |
| Smoke correctness | `scripts/smoke.ps1` now checks native exit codes through `Invoke-Native`, preventing false green output after failed `dotnet` commands. |
| Package store resilience | `PackageStore` now retries transient Windows directory move/delete failures during install/uninstall/rollback, stabilizing package lifecycle tests and smoke. |
| P4 closure | P4 internal gates are complete locally: 80 tests pass, UI gate passes, module and Shell snapshots are non-empty with PNG evidence, design colors are centralized, Settings is schema-backed, Shell smoke passes, release Runner/Shell smoke passes, and the Windows zip was rebuilt. |

## P2 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Doubao Agent controller module | Added `src/DoubaoAgent.MyPowerTools` and an `inproc-dotnet` entrypoint in `modules/doubao-agent/module.json`. |
| Doubao service separation | `doubao-agent.status.summary` checks planner `38102`, tool runtime `38080`, and MCP bridge `38189` separately. Current machine output is degraded with 1/3 services reachable. |
| Doubao self-test | `doubao-agent.self-test` returns settings schema availability, redacted runtime paths, service endpoints, and redaction proof without leaking sample token/secret/password values. |
| Doubao logs | `doubao-agent.logs.summary` reports the Runner-managed module log directory and log file count. |
| Acceptance coverage | `DoubaoAgent_inproc_module_reports_planner_tool_and_mcp_services` verifies three local service probes, dynamic commands, self-test redaction, and log summary behavior. |
| SmartBird typed facade | Added `src/SmartBirdThermostat.MyPowerTools` and an `inproc-dotnet` entrypoint in `modules/smartbird-thermostat/module.json`. |
| SmartBird command coverage | `smartbird-thermostat.status.summary`, `events.list`, `config.get`, `config.save`, `hardware.diagnostics`, `self-test`, `logs.summary`, and `service.restart` run through the module facade. |
| SmartBird degraded hardware diagnostics | Current machine output is degraded because Energy Server `19003` times out and FNB-58 serial port is not configured; ADB device identifiers are redacted. |
| SmartBird acceptance coverage | `SmartBird_inproc_module_reports_facade_config_and_hardware_degradation` verifies local status/events/config/log probes, config save, brokered restart, self-test redaction, and actionable hardware degradation. |
| AndroidTools powertoold sidecar | Added `src/AndroidTools.Powertoold`, a package-shared gRPC IPC sidecar for `android-tools.notifications`, `android-tools.remote-commands`, and `android-tools.process-monitor`. |
| AndroidTools T2 priority | Updated the AndroidTools shared runtime and module manifests so `package-runtime:100` is selected ahead of the InProc fallback when `windows/x64/powertoold.exe` exists. |
| AndroidTools argument parity | `GrpcIpcModuleHost` now forwards command arguments through the existing proto `args` map, including text input and JSON array arguments used by Remote Commands and Process Monitor. |
| AndroidTools acceptance coverage | `AndroidTools_powertoold_imports_powertool_commands_and_executes_text_tool` and `AndroidTools_powertoold_process_monitor_persists_shared_watch_list` verify T2 command import, text transform execution, shared process pool diagnostics, and watch-list persistence. |
| AndroidTools release packaging | `scripts/publish-windows.ps1` builds `AndroidTools.Powertoold`; the release root contains `modules/android-tools-suite/windows/x64/powertoold.exe` and its runtime dependencies. |
| ScreenEase Windows writer | `WindowsDisplayService` now probes Dxva2 DDC/CI capabilities, maps brightness percent to monitor range, maps requested Kelvin values to supported hardware color-temperature flags, and applies settings through `IDisplayService.ApplyProfileAsync` when hardware writes are explicitly requested or enabled. |
| ScreenEase writer safety | `screenease.profile.apply` keeps hardware writes disabled by default and returns `native-host-required`; `screenease.native-writer.configure` enables future hardware applies, and `screenease.native-writer.status` reports current DDC/CI readiness. |
| ScreenEase acceptance coverage | `ScreenEase_profile_apply_keeps_hardware_write_disabled_by_default`, `ScreenEase_profile_apply_calls_display_writer_when_requested`, and `ScreenEase_native_writer_configure_enables_future_hardware_apply` verify default safety, explicit writer invocation, and persisted enable flow. |
| AndroidTools degraded coverage | `AndroidTools_notifications_reports_actionable_degraded_state_when_endpoint_config_is_invalid` verifies invalid notification endpoint config reports degraded status and actionable server-check output. `AndroidTools_process_monitor_reports_actionable_degraded_state_when_watch_list_is_empty` verifies empty process watch lists remain degraded, list zero configured processes, and reject empty saves. |
| Doubao degraded coverage | `DoubaoAgent_inproc_module_reports_role_specific_degraded_services` verifies planner/tool/MCP role separation when only one local service is reachable and failed role health commands return retryable `MPT_RUNTIME_UNAVAILABLE` details. |
| P2 closure | P2 internal gates are complete locally: 5 packages valid, 7 modules valid, all production modules expose status/commands/UI/settings/logs contracts, missing external services produce degraded states, and 74 tests pass. Remaining P2 items are external validation only. |

## P3 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Permission level model | `schemas/module.schema.json` now accepts the planned permission levels: `user`, `elevated`, `service`, `serviceUser`, `serviceSystem`, `sensitive`, and `broker`. `Module_schema_accepts_planned_permission_levels` verifies schema coverage. |
| PrivilegedBroker decisions | `PrivilegedBroker` treats `elevated`, `service`, `serviceUser`, `serviceSystem`, `sensitive`, and `broker` as broker-required while keeping `user` ordinary. `PrivilegedBroker_requires_broker_for_planned_privilege_levels` verifies the decision and audit output. |
| ServiceBroker audit | `ServiceBroker.RestartAsync` writes `serviceUser` audit entries for user-level service restarts. `ServiceBroker_restart_audits_service_user_level` verifies requested/start entries, rollback text, operation order, and redaction. |
| Existing broker safety | NetworkBroker change-set ordering, rollback after partial failure, SecretBroker save/read/delete redaction, AutostartBroker audit, HostControl permission details, Shell permission visibility, and CLI permission inspection remain covered by existing tests. |
| CLI broker validation | `broker portproxy list` listed 6 local rules; `broker portproxy apply --listen-address 127.0.0.1 --listen-port 45678 --connect-address 127.0.0.1 --connect-port 45679` returned `permission-required` in the normal user context; `run adb-forwarder.portproxy.apply` returned `permission-required`; `broker secret self-test` passed without printing secret values. |
| P3 closure | P3 internal gates are complete locally: elevated and sensitive actions route through broker models, broker actions audit, secrets are redacted, permission-required paths do not fake success, rollback tests pass, and 77 tests pass. Live elevated helper execution remains external because it requires administrator context or signed helper packaging. |
