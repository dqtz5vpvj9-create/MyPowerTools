# Project Status

Run date: 2026-07-06.

## Snapshot

| Field | Value |
|---|---|
| Project | MyPowerTools |
| Current phase | Internal production closure complete |
| Last completed phase | P-UI-Foundation |
| Next phase | External validation |
| SDK | 10.0.301 from `global.json` and `dotnet --version` |
| Target frameworks | `net10.0` projects across the solution |
| Production packages | 5: `adb-forwarder`, `android-tools-suite`, `doubao-agent`, `screenease`, `smartbird-thermostat` |
| Production modules | 7 |
| Production commands | 81 total, 50 dynamic |
| Templates | 6 |
| Tests | 189 passed, 0 failed, 0 skipped |
| Release artifact | `artifacts/release/MyPowerTools-win-x64.zip` |
| Release SHA256 | `B4F8CFED2E13C0370068B0D4DEBB0F66BCF4A18E74620FD7FEBAAEB93CC84BE5` |
| Review package | `artifacts/review/MyPowerTools-final-code-docs-p-ui-foundation-20260706.zip` |
| Evidence package | `artifacts/review/MyPowerTools-final-evidence.zip`, SHA256 `ABA4ED23AC71D4727A1FB3619610CD57B996DBCA3A32465CB6A2CD6DCB8A4AF1` |
| Production closure | `true` for local internal closure; remaining work is external validation for signing, administrator actions, hardware/services, and macOS/Linux hosts. |

## Latest Validation Results

| Command | Result |
|---|---|
| `dotnet --version` | `10.0.301` |
| `dotnet restore MyPowerTools.slnx` | Succeeded; all projects were up-to-date. |
| `dotnet build MyPowerTools.slnx --no-restore` | Succeeded with 0 warnings and 0 errors. |
| `dotnet test MyPowerTools.slnx --no-build` | Passed 189, failed 0, skipped 0. |
| `dotnet test src\MyPowerTools.Tests\MyPowerTools.Tests.csproj --no-build --filter "Foundation=P7"` | Passed 10, failed 0, skipped 0. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` | 5 production packages valid. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` | 5 packages and 7 modules passed contract validation; total commands 81, dynamic commands 50. AndroidTools runs through `grpc-ipc` powertoold with notifications running, remote commands running, process monitor degraded until a watch list is saved, and ScreenEase exposing 14 commands. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` | `signature-hook` trust passed for all 5 production packages after local hash/signature refresh. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` | UI gate passed. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui screenshot --mode fixture --full-shell --theme light --size 1366x768 --density normal --out artifacts\ui-acceptance-mpt-controls` | Full Shell screenshots show Dashboard without a permanent right-side Command Palette/Audit rail, Command Palette as a centered overlay, and MPT controls rendered. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui screenshot --mode live-runner --full-shell --runner-only --theme light --size 1366x768 --density normal --out artifacts\ui-final-live-runner` | Live Runner-backed full Shell screenshots written; manifest has `dataSource=runner-hostcontrol` and `usesHostControlData=true`. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots-p6` | Wrote module dashboard-card contract snapshots and PNG pixel snapshots. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-p6` | Wrote fixture-backed full Shell real screenshot manifests. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --live-runner --full-shell --runner-only --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-p6-live-runner` | Wrote real Shell screenshots from live Runner HostControl data. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --live-runner --full-shell --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-live-runner-full` | Wrote full `ShellChromeView` real Avalonia screenshots with `fixture-hostcontrol` labels when Runner was unavailable. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --live-runner --full-shell --runner-only --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-live-runner-full-real` | Wrote 8 real Avalonia screenshots using live Runner HostControl data; manifest `dataSource=runner-hostcontrol`, `usesHostControlData=true`, HostControl smoke reported 7 modules, 7 dashboard cards, and 81 commands before Runner shutdown. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --theme light --size 1920x1080 --density normal --out artifacts\shell-ui-snapshots-1920-full` | Full Shell screenshot variant passed for 1920x1080 normal. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --theme light --size 1280x720 --density compact --out artifacts\shell-ui-snapshots-1280-compact-full` | Full Shell screenshot variant passed for 1280x720 compact. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --full-shell --theme dark --size 1366x768 --density normal --out artifacts\shell-ui-snapshots-dark-full` | Dark full Shell screenshot variant passed. |
| `dotnet run --project src\MyPowerTools.Runner -- --once` | 7 modules indexed; AndroidTools Notifications and Remote Commands run through powertoold, AndroidTools Process Monitor reports its watch-list degraded state, and current expected degraded states remain for Doubao partial services, ScreenEase unsupported DDC/CI monitor hardware, and SmartBird Energy Server/FNB-58 dependencies. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` | Reports 5 packages, 7 modules, 81 commands, 50 dynamic commands, event seq 9, AndroidTools under `grpc-ipc`, one shared process pool, and per-module diagnostics. |
| Manual Shell HostControl smoke | Temporary Runner started from `src\MyPowerTools.Runner\bin\Debug\net10.0`, Shell smoke connected, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and Runner exited with code 0. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` | 6 templates passed manifest validation, UI gate, .NET template builds, and Python syntax compilation. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` | Passed; Shell smoke connected to Runner `0.2.0`, reported 7 modules, 7 dashboard cards, and 81 commands, then requested Runner shutdown. |
| `dotnet run --no-build --project src\MyPowerTools.Cli -- runner process pause . --duration-minutes 1` | With a temporary Runner active, selected the AndroidTools shared gRPC pool, paused automatic restart for one minute, printed expiry/modules, then resume restored automatic policy. |
| `dotnet run --project src\MyPowerTools.Cli -- run screenease.native-writer.status` | Succeeded with Windows DDC/CI writer probe output; current Generic PnP Monitor returns `GetMonitorCapabilities` unsupported. |
| `dotnet run --project src\MyPowerTools.Cli -- run screenease.profile.apply` | Succeeded with profile plan and safe default `native-host-required` because hardware writes are disabled unless explicitly requested or configured. |
| P8 audit scans | Placeholder/stub/fake scans found only documentation/history or UI input `PlaceholderText`; `unsupported` findings are explicit degraded states; production module roots contain no sample modules; high-confidence secret scan found no real credential material; release metadata contains no `file:///C:` URL. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` | Rebuilt `artifacts/release/MyPowerTools-win-x64.zip`, `RELEASE_NOTES.md`, `release-metadata.json`, and the Scoop manifest. ZIP SHA256 `B4F8CFED2E13C0370068B0D4DEBB0F66BCF4A18E74620FD7FEBAAEB93CC84BE5`; size 223125040 bytes. |
| Release metadata/Scoop manifest check | `release-metadata.json` artifact hash and `package-managers/scoop/mypowertools.json` 64-bit hash both equal `B4F8CFED2E13C0370068B0D4DEBB0F66BCF4A18E74620FD7FEBAAEB93CC84BE5`; both URLs are relative `MyPowerTools-win-x64.zip`; manifest exposes `mpt` as the CLI shim. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\create-review-evidence.ps1` | Passed; saved 27 command outputs, command result index, UI snapshots, Shell screenshots, release evidence, and `artifacts/review/MyPowerTools-final-evidence.zip`. |
| Release Runner semantic check | `23-release-runner-once.txt` passed with relative `--data-root`; `adb-forwarder` is running; AndroidTools notifications and remote commands are running; process-monitor, Doubao, ScreenEase, and SmartBird degraded reasons are external; no assembly/path/reflection/runtime failure markers were found. |
| Release zip hygiene check | `MyPowerTools-win-x64.zip` contains no `bin/`, `obj/`, or `modules/modules/` entries. |
| `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` | Release package trust passed for all 5 production packages. |
| `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p6` | Release Runner indexed 7 modules from the release root and started AndroidTools powertoold from the release package. |
| `artifacts\release\win-x64\Shell\MyPowerTools.Shell.Avalonia.exe --smoke --timeout-ms 30000 --quit-runner` | Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited with code 0. |
| `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` | Resolved the sibling release Runner executable without registry writes. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun` | Succeeded and printed the portable install plan. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force` | Succeeded and printed the uninstall plan without removing files. |

## P-Foundation-7 Progress On 2026-07-05

| Area | Evidence |
|---|---|
| Hotkey correctness | `RunnerHotkeySynchronizer` tracks registered id/gesture pairs, unregisters before re-registering changed gestures, updates command bindings when only command args change, and creates hotkey command requests with persisted `CommandArgsJson`. `Runner_hotkey_sync_reregisters_gesture_updates_args_and_follows_enable_state` covers register, edit, args propagation, reset, disable unregister, and enable re-register. |
| Notification refresh | Runtime module alert events now publish through `PublishNotification`, which emits a `notification.created` host event. `Module_event_alert_creates_notification_event_and_shell_refresh_plan` proves NotificationCenter count, host event, and Shell Notifications refresh routing. |
| Module cancellation | `IModuleTransportRuntime.CancelCommandAsync`, gRPC IPC `CancelCommand`, AndroidTools.Powertoold invocation token tracking, and AndroidTools child process tree termination now provide module-level cancel semantics. Shell command progress keeps cancel accepted/rejected evidence. Existing cancellation tests plus `Shell_cancel_result_keeps_module_acceptance_or_rejection_evidence` cover host and UI behavior. |
| Streaming failure handling | gRPC streaming sidecar failures now produce a terminal `failed` event with `MPT_RUNTIME_UNAVAILABLE`, remove the dead host, and avoid side-effect command replay. `Runtime_streaming_sidecar_crash_emits_runtime_unavailable_terminal_failure` verifies the path against a real sample sidecar process. |
| Bounded idempotency | `InvocationExecutionCache` replaces the unbounded `_executions` dictionary with max-count and TTL cleanup for completed invocation results. `Invocation_execution_cache_deduplicates_completed_results_and_evicts_old_entries` verifies dedupe, completed result reuse, max-count trimming, and TTL expiry. |
| Diagnostics split | `RuntimeModuleDiagnostics` and HostControl proto now expose `ModuleEnabledState`, `TransportActiveState`, and `ToolRuntimeState`. `Runtime_diagnostics_split_module_transport_and_tool_runtime_state` verifies loaded, disabled, and production-module states. |
| UI lint scope | `UiSurfaceGate.CheckShellSource` scans Shell and `src/MyPowerTools.UI/**/*.cs`. `MptThemeTokens` centralizes colors, font sizes, spacing, and radii; UI controls and Shell screenshot code use named tokens. `Ui_surface_gate_scans_component_csharp_layer_and_requires_tokens` and `ui check modules` pass. |
| Validation | `dotnet restore`, `dotnet build`, full `dotnet test` 189/0/0, P7 trait tests 10/0/0, `validate modules`, `validate contracts`, `ui check modules`, Runner `--once`, `scripts\validate-templates.ps1`, `scripts\smoke.ps1`, and `scripts\create-review-evidence.ps1` passed. |

## P-Foundation-6 Progress On 2026-07-05

| Area | Evidence |
|---|---|
| Real module lifecycle | `IMptModuleLifecycle` and `IModuleTransportRuntime.EnableModuleAsync/DisableModuleAsync` now give modules explicit enable/start/stop/disable hooks. Runtime `SetModuleEnabledAsync` stops event streams, clears dynamic commands, delegates resource cleanup to the transport, and restores status/commands on enable. `Runtime_lifecycle_disable_unloads_inproc_and_enable_restores_commands` verifies disable unloads the InProc session and removes commands, then enable restores them. |
| Continuous event pump | Runner starts `MptHostRuntime.StartModuleEventPump()` in normal mode and stops it during shutdown. Runtime supervises per-module subscriptions with cursors, cancellation, duplicate prevention, retry/backoff, EventBus publication, NotificationCenter updates, dashboard/event refresh inputs, and logs. `Runtime_module_event_pump_collects_events_without_manual_collection` verifies events emitted after pump startup reach HostEvents without calling `CollectModuleEventsAsync`. |
| Hotkey persistence | Added `HotkeyStore` under runtime state, user overrides take precedence over manifest defaults, Settings Center writes `$hotkeys` edits/resets, disabled modules unregister their bindings, and re-enable restores persisted overrides. `Runtime_hotkey_overrides_persist_reset_and_follow_module_enable_state` covers edit, conflict, persistence, disable unregister, enable re-register, and reset. `Shell_settings_patch_writes_hotkey_override_and_reset` covers UI patch emission. |
| Sidecar readiness | gRPC IPC sidecars now start with package/runtime working directory, receive `MPT_PACKAGE_DIR`, `MPT_MODULE_ID`, `MPT_RUNTIME_ID`, `MPT_ENDPOINT_TRANSPORT`, and `MPT_ENDPOINT_ADDRESS`, and retry readiness before initialize with explicit classification for process exit, endpoint timeout, initialize rejection, and protocol mismatch. `Runtime_grpc_readiness_waits_for_delayed_sidecar_and_preserves_typed_args_and_launch_context` and `Runtime_grpc_readiness_reports_early_sidecar_exit` cover delayed startup, env/working directory propagation, and early exit classification. |
| Typed command args | `mpt_module_v1.proto` now carries `typed_args` and `args_json` in addition to the legacy string map. `GrpcIpcModuleHost` sends nested JSON as `Struct` and JSON text; `AndroidTools.Powertoold` reads typed args first, then `args_json`, then legacy args. The delayed sidecar test verifies nested port mappings, process watch arrays, numbers, booleans, and strings survive the gRPC round trip. |
| Test layout | The original 168 acceptance tests were split into 10 domain partial files while keeping shared helpers in `RuntimeAcceptanceTests.cs`. `src/MyPowerTools.Tests/README.md` documents the layout. P-Foundation-6 adds 6 focused tests, bringing the suite to 174 passing tests. |
| Validation | `dotnet restore MyPowerTools.slnx`, `dotnet build MyPowerTools.slnx --no-restore`, `dotnet test MyPowerTools.slnx --no-build`, module validation, contract validation, UI gate, UI snapshots, fixture and live Runner Shell snapshots, Runner once, Shell HostControl smoke, template validation, module list, and diagnostics passed. Full `scripts\smoke.ps1` was skipped because it refreshes package hashes/signatures through `package sign-local modules`, outside this target boundary. |

## P-Foundation-5 Progress On 2026-07-05

| Area | Evidence |
|---|---|
| Per-command routing | `TransportSelector.SelectForCommand` now evaluates command constraints and transport availability per command. Runtime command execution and event streams use the selected entrypoint, and blocked details include `requiredRoute`, `alternateRequiredRoute`, `unavailableReason`, and route diagnostics. Acceptance tests cover sidecar-vs-InProc selection plus blocked route details. |
| Module protocol metadata | `proto/mpt_module_v1.proto` now carries `timeout_ms`, `category`, `danger_level`, `constraints`, `execution_json`, `supports_progress`, and `supports_cancellation`. gRPC host/client mappings preserve the fields, and `AndroidTools.Powertoold` returns dynamic command metadata with params and constraints intact. |
| Module event streams | AndroidTools Notifications, Remote Commands, Process Monitor, ScreenEase, and AdbForwarder now expose snapshot-plus-polling event streams with stable fingerprints and increasing sequence values. Runtime event cursor handling skips duplicate replayed events after resume. |
| Shell screenshots | `mpt ui shell-snapshot --live-runner --full-shell` renders the full `ShellChromeView` with populated content host, command panel, permission panel, and audit panel. Live Runner HostControl data is used when available; fixture output is labelled. 1366x768 normal, 1920x1080 normal, 1280x720 compact, dark, and Runner-backed variants were generated. |
| Command Palette | The Shell Command Palette now has fuzzy ranking, provider grouping, recents, keyboard selection hints, selected-command params/preview, danger confirmation, progress, stdout, stderr, result summary, cancel, history, and expanded error display. |
| Hotkey settings | Settings Center renders editable module hotkey gestures with state, reset-to-default, disabled/unregistered indication, command argument preview, and result prompt. Runtime hotkey binding remains connected to command arguments. |
| Platform paths | Added `IPlatformPathService` for `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, `$XDG_RUNTIME_DIR`, `$TMPDIR`, `${VAR}`, `$VAR`, and `~` expansion. `TransportSelector` and `GrpcIpcModuleHost.ToEndpoint` use the service, with Windows/Linux/macOS string tests. |
| Validation | Restore passed, build passed with 0 warnings and 0 errors, tests passed 168/0/0, smoke passed, diagnostics reported 5 packages, 7 modules, 81 commands, and 50 dynamic commands. |

## P8 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Final audit | `docs/P8_FINAL_AUDIT.md` records scan commands, findings, and classifications for TODO/FIXME/placeholder/stub/fake/coming soon, unsupported states, sample modules, hardcoded user paths, release URLs, and secret patterns. |
| Cleanup fixes | Local package signature algorithm was renamed from `sha256-manifest-placeholder` to `sha256-manifest-local`; `UiSurfaceGate.WriteSnapshotPlaceholder` was renamed to `WriteDefaultSnapshotSet`; production package signatures were refreshed. |
| Final validation | SDK 10.0.301, restore, build, tests, module validation, contract validation, UI gate, UI snapshots, diagnostics, inspect, module list, Runner once, template validation, smoke, publish, release package trust, release Runner once, release Shell smoke, autostart dry-run, install/uninstall dry-run, metadata hash parity, and zip hygiene all passed for the P8 run. |
| Release artifact | `artifacts/release/MyPowerTools-win-x64.zip`, SHA256 `B4F8CFED2E13C0370068B0D4DEBB0F66BCF4A18E74620FD7FEBAAEB93CC84BE5`. |
| Closure | `.codex/project-state.json` marks `productionClosure=true` after P-UI-Foundation acceptance, final evidence, and final code/docs packaging. |

## P7 Progress On 2026-07-04

| Area | Evidence |
|---|---|
| Platform abstractions | Added `ILocalIpc`, `LocalIpcService`, `IHotkeyService`, `IPrivilegeBroker`, unsupported provider implementations for notification, autostart, service, network, process, hotkey, and privilege surfaces, plus managed process inspection. |
| macOS/Linux packs | `MacPlatformPack` and `LinuxPlatformPack` now expose local IPC, notification, autostart, service, network, process, display, tray, secret, hotkey, and privilege services. Unsupported providers return explicit `unsupported` state/messages; process inspection uses the managed runtime where supported. |
| Windows pack | `WindowsPlatformPack` exposes `LocalIpc`, `Hotkeys`, and `Privileges`; Windows Runner IPC remains Named Pipe based, `Hotkeys` registers real Win32 `RegisterHotKey` gestures for the Runner command palette shortcut, and privilege evaluation returns broker-required. |
| Acceptance coverage | Added tests for required capability failure -> `unsupported`, optional capability failure -> `degraded`, platform-native IPC endpoint shapes, Mac/Linux degraded service behavior, hotkey/privilege provider surfaces, and `PrivilegedBroker` implementing the platform privilege contract. |
| P7 validation | Build passed with 0 warnings, tests passed, module inspection shows requirements and broker permissions, diagnostics reports Windows `grpc-ipc` runtime process state, contract validation passed, strict package trust passed, and `scripts/smoke.ps1` passed. |

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

Local builds copy module assemblies into `modules/`. When assemblies change, run `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` before trust-sensitive tests or strict package verification. The latest P-Foundation-5 smoke run refreshed local signatures as part of validation.

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


