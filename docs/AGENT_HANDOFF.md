# Agent Handoff

## Current Direction

The active objective is to turn MyPowerTools into a production-grade PowerToys-style personal tools platform based on the v3 plan package.

## Implemented Since First Prototype

- Added platform packs:
  - `MyPowerTools.Platform.Windows`
  - `MyPowerTools.Platform.Mac`
  - `MyPowerTools.Platform.Linux`
- Added capability registry abstractions and platform service interfaces.
- Added persistent `SettingsStore` with atomic writes, backup, export, import, and rollback.
- Added `LogRouter`, `NotificationCenter`, and `CommandHistory`.
- Upgraded gRPC, HTTP, and stdio module host classes from descriptions to executable host clients.
- Added broker audit log plus service, network, secret, and autostart brokers.
- Added package hash manifest, install, uninstall, rollback, and repair infrastructure.
- Expanded CLI with `run`, `package hash`, `install`, `uninstall`, `update`, `rollback`, `repair`, `broker audit`, `broker portproxy`, and `doctor`.
- Added reusable Avalonia MPT controls.
- Added tests for persistence, broker audit redaction, package store repair/rollback, capability degradation, and log redaction.
- Moved sample module manifests to `tests/fixtures/modules`; production `modules/` now contains real tool manifests only.
- Added HTTP health refresh for Dashboard snapshots and `Runner --once`.
- Added static `commands.index.json` support for production modules and schema validation for command descriptors.
- Added Host runtime command execution for declared open, host status/log/settings/notification, HTTP request, and broker-request command descriptors.
- Added `IModuleTransportRuntime` and wired Runtime dynamic command refresh plus command execution through registered transport hosts.
- Registered InProc, gRPC IPC, and stdio transport hosts in Runner and CLI.
- Added gRPC IPC runtime wrapper with cached native IPC sidecar connections.
- Added gRPC sidecar restart-on-crash behavior with cached host cleanup and one retry after connection failure.
- Added package shared runtime pooling for gRPC IPC through `packageId + runtimeId`, with per-module initialization and startup throttling.
- Added dynamic InProc command, gRPC sidecar native IPC, gRPC sidecar crash recovery, and package shared runtime pool acceptance tests.
- Added `AdbForwarder.MyPowerTools` as a production InProc module with ADB diagnostics, Windows portproxy inspection, redacted diagnostic output, and dynamic commands.
- Added AdbForwarder port mapping model, Windows `netsh portproxy` parser, apply/revert planner, rollback steps, detailed `NetworkBroker` permission request payloads, and acceptance tests for parsing, planning, and broker request output.
- Added NetworkBroker portproxy change-set execution with PrivilegedBroker evaluation, per-step audit, automatic rollback after partial failure, Windows `netsh.exe` apply/delete execution when elevated, and CLI `broker portproxy list/apply/remove` commands.
- Added HostControl command error details, `ListBrokerAudit` IPC, Shell permission prompt rendering, and Broker Audit side-panel visibility for permission-required commands.
- Added HostControl `ListNotifications` IPC and `HostControlClient` methods for module detail, settings, logs, and notifications.
- Added HostControl `ListPackages` IPC so the Shell package page uses Runtime package summaries with version, hash manifest, shared runtime count, module count, and module ids.
- Expanded the Avalonia Shell into real HostControl-backed pages for Dashboard, Modules, Settings, Logs, Notifications, Packages, and Diagnostics. Settings saves use revision-protected `UpdateSettings`; logs use `TailLogs`; notifications use typed `NotificationItem` records; packages use typed package summaries.
- Replaced `mpt ui snapshot` placeholder output with deterministic UI contract and PNG pixel snapshots. The command scans module surfaces and writes a manifest plus per-surface `.snapshot.json` and `.snapshot.png` files containing layout, components, states, theme, density, size, source SHA256, pixel SHA256, dimensions, unique-color counts, and nonblank pixel counts.
- Added `AndroidTools.MyPowerTools` as a production shared InProc facade for the three AndroidTools modules. It imports packaged powertool `commands.yaml`, generates dynamic Remote Commands, executes migrated text tools, tracks shared command history, imports notification endpoint config, probes the notification server, and persists/scans the Process Monitor watch list.
- Added package-shared AndroidTools `commands.yaml` and default notification endpoint config so release artifacts do not depend on the legacy source checkout for these defaults.
- Added `AndroidTools.Powertoold` as the package-shared T2 gRPC IPC sidecar for the three AndroidTools modules, with `package-runtime:100` priority, command argument forwarding through ModuleControl `args`, shared process diagnostics, and release packaging under `modules/android-tools-suite/windows/x64/powertoold.exe`.
- Added `IDisplayService` to platform abstractions plus Windows monitor enumeration and macOS/Linux degraded providers.
- Added `ScreenEase.MyPowerTools` as a production InProc module with dynamic status, display enumeration, profile list/plan/apply/save, native writer status/configure, and rule status commands. Profile apply persists the active profile; Windows hardware writes route through the DDC/CI display writer when `hardwareWrite=true` is passed or the native writer is enabled, while unsupported monitors return hardware-level diagnostics.
- Updated `InProcDotNetModuleHost` to resolve module-local dependency DLLs from the module directory, then updated ScreenEase packaging to copy required platform assemblies.
- Updated `scripts/publish-windows.ps1` to build AdbForwarder, AndroidTools, AndroidTools.Powertoold, and ScreenEase module assemblies, refresh package hashes, and clean `artifacts\release\win-x64` before copying modules into the release zip.
- Added real static command indexes for Doubao Agent, SmartBird Thermostat, ScreenEase, AdbForwarder, and AndroidTools submodules.
- Refreshed `shared/package.hashes.json` for all 5 production package roots.
- Added `scripts/smoke.ps1`, `scripts/publish-windows.ps1`, and `CHANGELOG.md`.
- Added `scripts/release-notes.ps1` and wired it into the Windows publish script so release artifacts include generated notes with SHA256, size, verification commands, and external requirements.
- Added `scripts/install-windows.ps1` and `scripts/uninstall-windows.ps1`, then wired both scripts into the Windows portable zip root for current-user install, shortcut creation, optional autostart, optional Runner launch, uninstall, and dry-run validation.
- Added `.github/workflows/ci.yml` for Windows restore, build, test, module validation, UI gate, UI contract snapshots, template validation, Runner once, smoke, publish, and artifact upload.
- Added production `README.md` with architecture, requirements, build/test/run/publish commands, package lifecycle commands, module authoring, and troubleshooting.
- Added six validated module templates under `templates/` plus `scripts/validate-templates.ps1` for manifest validation, UI gate, .NET template builds, and Python syntax compilation.
- Added Shell `--smoke --timeout-ms` HostControl verification and upgraded `scripts/smoke.ps1` to start Runner, run Shell smoke, and report module/dashboard/command counts.
- Wired HostControl `QuitRunner` to Runner host lifetime and updated Shell smoke with `--quit-runner` so smoke-owned Runner processes exit cleanly.
- Added persistent module enable/disable state via `ModuleStateStore`, Runtime filtering, HostControl `SetModuleEnabled`, CLI `mpt module list|enable|disable`, Shell toggles, and Runtime/HostControl/CLI acceptance tests.
- Added typed RuntimeDiagnostics via HostControl `GetRuntimeDiagnostics`, Shell Diagnostics rendering, CLI `mpt diagnostics`, and Runtime/HostControl diagnostics acceptance tests.
- Added `ModuleContractValidator`, CLI `mpt validate contracts`, child-process acceptance coverage, CI gating, smoke script coverage, and release-note verification for per-module manifest/dashboard/settings/commands/health/logs contracts.
- Added CLI package rollback through `mpt rollback`, `--store-root` support for install/uninstall/repair/rollback, and acceptance tests for isolated install -> uninstall -> rollback -> repair flows.
- Added local HTTP facade integration coverage for Runtime health refresh, static `http.request` command execution, sensitive output redaction, and log correlation.
- Added broader CLI process coverage for `mpt inspect`, `mpt package hash`, and `mpt doctor`.
- Added gRPC IPC sidecar crash-policy coverage for restart process replacement, invocation/event log correlation, and restart-limit enforcement.
- Added nonblank PNG pixel snapshot assertions to `Ui_snapshot_writes_contract_manifest`, and wired `mpt ui snapshot` into `scripts/smoke.ps1`.
- Added Windows `WindowsAutostartService` for current-user HKCU Run entries, expanded `AutostartBroker` with status/disable audit coverage, added CLI `mpt runner autostart status|enable|disable`, wired autostart status into smoke, and updated Runner/CLI root discovery for portable release layout.
- Added gRPC IPC runtime process diagnostics for active module/runtime pools, including pool key, PID, endpoint, start count, restart limit, last start time, and module membership through Runtime, HostControl, Shell Diagnostics, CLI diagnostics, and acceptance tests.
- Added `MptHostRuntime` async disposal and wired Runner/CLI runtime shutdown paths to dispose transport runtimes, so CLI diagnostics and command probes clean up sidecar processes.
- Added Runner tray infrastructure: `ITrayService` options/actions/results, macOS/Linux unsupported tray services, Windows native `Shell_NotifyIcon` tray with Open Shell/Quit Runner actions, Runner background startup integration, and `--no-tray` for headless runs.
- Added runtime process restart controls: Runtime `RestartRuntimeProcessAsync`, HostControl `RestartRuntimeProcess`, Shell Diagnostics Restart action, CLI `mpt runner process restart`, and Runtime/HostControl/CLI acceptance tests for gRPC IPC pool restarts.
- Added runtime process restart-policy controls: Runtime `SetRuntimeProcessRestartPolicyAsync`, HostControl `SetRuntimeProcessRestartPolicy`, policy fields on process diagnostics, paused-pool diagnostic rows, Shell Diagnostics Pause/Resume actions, CLI `mpt runner process pause|resume`, and Runtime/HostControl/CLI acceptance tests for paused sidecar restart behavior.
- Added persistent restart-policy state and history: `RuntimeProcessPolicyStore` writes `state/runtime.process-policies.json`, Runtime reapplies paused gRPC IPC pool policies after reload, HostControl carries policy source, CLI diagnostics prints `process-policy` rows, Shell Diagnostics renders Process Policy History, and `Runtime_persists_grpc_restart_policy_across_runtime_reload` verifies reload behavior.
- Added restart-policy maintenance windows: HostControl carries typed `expires_at`, CLI supports `--until` and `--duration-minutes`, Shell Diagnostics adds `Pause 1h`, Runtime expires elapsed policies before normal activity, and `Runtime_expires_grpc_restart_policy_and_recovers_pool` verifies automatic recovery.
- Added SecretBroker-backed OS secret storage: `SecretReference` validates safe `secret://module/name` references, Windows uses Credential Manager through `WindowsCredentialSecretStore`, tests use `InMemorySecretStore`, macOS/Linux expose degraded `UnsupportedSecretStore`, SecretBroker audits save/read/delete, and CLI exposes `mpt broker secret self-test`.
- Added module permission visibility: HostControl `ModuleSummary` and `ModuleDetail` now carry typed permissions and capability requirements, Shell module cards/detail pages render the declarations, CLI `mpt inspect modules` prints capabilities/requires/permissions, and acceptance tests cover HostControl plus CLI visibility.
- Added local package trust hooks: `PackageTrustVerifier` verifies hash manifests and `shared/package.signature.json`, `mpt package sign-local` writes local trust metadata, `mpt package trust --strict` validates packages, `PackageStore.Install` verifies source packages before copying, and `PackageStore.Repair` re-runs trust verification.
- Added package trust visibility: Runtime package summaries now include trust state, policy, signature path, and trust issue count; HostControl maps those fields; Shell Packages renders the trust badge and signature path; `HostControl_lists_package_summaries` verifies the IPC contract.
- Added Shell UI snapshot matrix through `mpt ui shell-snapshot`; it writes a manifest plus PNG snapshots for required Shell surfaces including Dashboard, Command Palette, Settings Center, Module Detail, Logs Viewer, Notification Center, Permission Prompt, and Degraded Module.
- Added `HostControlConnectionMonitor` for Shell Runner IPC resilience; MainWindow starts it, shows offline state, and refreshes current Shell data after reconnection. `Shell_connection_monitor_reports_offline_then_restored` verifies the transition.
- Added typed HostControl package lifecycle operations: install, repair, uninstall, and rollback now call `PackageStore`, reload Runtime after package changes, and surface through Shell Packages actions. Package discovery skips `.rollback` backups so uninstalled packages remain available for rollback without appearing as active modules.
- Added Shell HostControl event stream consumption: `HostControlClient.SubscribeHostEventsAsync`, `HostControlEventStreamMonitor`, and MainWindow event handling now resume by sequence after faults, skip duplicate replayed events, and refresh affected Shell pages from Runner snapshots. `Shell_event_stream_monitor_resumes_after_fault_and_tracks_seq` verifies the path.
- Added P0 phase ledger for the active 9-phase objective: `docs/PHASES.md`, `docs/PROJECT_STATUS.md`, `docs/PHASE_HISTORY.md`, `docs/OPEN_BLOCKERS.md`, `docs/EXTERNAL_VALIDATION.md`, and `.codex/project-state.json`.
- Added `DoubaoAgent.MyPowerTools` as a production InProc controller module. It checks planner `38102`, tool runtime `38080`, and MCP bridge `38189` separately, exposes per-role health commands, status summary, self-test, log summary, runtime settings schema, and degraded status when only part of the runtime is reachable.
- Added `SmartBirdThermostat.MyPowerTools` as a production InProc typed facade. It wraps SmartBird HTTP status/events/config/log endpoints, exposes config save, self-test, log summary, bounded event output, ServiceBroker restart request details, runtime settings schema, local path redaction, and degraded Energy Server/FNB-58/ADB diagnostics.
- Hardened `LogRouter` for concurrent CLI/runtime log writes with per-file in-process locking, read-shared/exclusive writer handles, retry, and blank-line-safe tailing.
- Added P2 closure acceptance coverage for AndroidTools invalid notification endpoint config, AndroidTools empty process watch-list degradation, and Doubao role-specific partial service outages.
- Marked P2 complete locally; remaining P2 concerns require external hardware, connected devices, or documented production service APIs and are tracked in `docs/OPEN_BLOCKERS.md` plus `docs/EXTERNAL_VALIDATION.md`.
- Completed P3 broker/security closure locally: schema now accepts all planned permission levels, `PrivilegedBroker` requires broker handling for elevated/service/sensitive levels, `ServiceBroker` audits restarts as `serviceUser`, and tests cover schema, broker decisions, service audit, rollback, redaction, and permission-required CLI paths.
- Completed P4 Shell UI closure locally: Shell keyboard shortcuts are modeled and wired, HostControl exposes runtime settings schemas, Shell Settings renders schema-backed controls, Shell/control colors route through `MptTheme`, Shell snapshots include keyboard/focus/state matrix evidence, `scripts/smoke.ps1` checks native exit codes, and PackageStore retries transient Windows directory move/delete operations.
- Completed P5 reliability and observability closure locally: `ModuleSupervisor` records module health observations, consecutive failures, supervisor state, last observation time, and next actions; RuntimeDiagnostics, HostControl, CLI diagnostics, Shell Diagnostics, and Dashboard alerts expose the data; `mpt runner process pause . --duration-minutes 1` resolves the first active process pool for smoke-friendly policy validation.
- Completed P6 packaging, templates, CLI, install, and release closure locally: publish now writes release/update metadata and a Scoop package-manager manifest, release notes list both artifacts, tests cover metadata/hash parity, the portable zip passes hygiene checks, and release Runner/Shell/autostart/install/uninstall dry-runs are verified.
- P7 progress: added `ILocalIpc`, platform-native endpoint selection, Mac/Linux degraded service providers for notification/autostart/service/network/process surfaces, managed process inspection, and tests for required/optional capability resolution plus UDS/Named Pipe endpoint shape.

## Last Verified State

- SDK: `dotnet --version` returns `10.0.301`; `global.json` pins `10.0.301`; all projects target `net10.0`.
- Restore: `dotnet restore MyPowerTools.slnx` succeeded.
- Build: `dotnet build MyPowerTools.slnx` succeeded with 0 warnings and 0 errors.
- Tests: `dotnet test MyPowerTools.slnx --no-build` passed 86 tests, 0 failed, 0 skipped.
- Phase state: P0, P1, P2, P3, P4, P5, and P6 done; P7 selected as the next active phase in `.codex/project-state.json`.
- Module validation: `dotnet run --project src\MyPowerTools.Cli -- validate modules` passed all 5 production packages.
- Module contract validation: `dotnet run --project src\MyPowerTools.Cli -- validate contracts` passed all 5 production packages and 7 modules.
- Package trust: `dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict` reports `signature-hook` for all 5 production packages.
- Module state CLI: `dotnet run --project src\MyPowerTools.Cli -- module list --include-disabled` lists all 7 modules with enabled/disabled state.
- Module inspection CLI: `dotnet run --project src\MyPowerTools.Cli -- inspect modules` lists capabilities, required/optional capability requirements, `apply-portproxy` broker permission, and `restart-service` broker permission.
- Runtime diagnostics CLI: `dotnet run --project src\MyPowerTools.Cli -- diagnostics` reports Runner `0.2.0`, protocols `1.0`, 5 packages, 7 modules, 81 commands, paths, transports, per-module state, ModuleSupervisor state/action/failure counts, and AndroidTools `grpc-ipc` process pool `package:android-tools-suite:runtime:powertoold` with all three AndroidTools modules.
- Runner process policy shorthand: `dotnet run --no-build --project src\MyPowerTools.Cli -- runner process pause . --duration-minutes 1` passed against a temporary Runner, selected the AndroidTools shared gRPC process pool, printed expiry/modules, and resume restored automatic policy.
- UI gate: `dotnet run --project src\MyPowerTools.Cli -- ui check modules` passed.
- UI snapshots: `dotnet run --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots` wrote 7 contract snapshots and 7 PNG pixel snapshots; first PNG reported 21 unique colors and 876888 non-background pixels.
- Shell UI snapshots: `dotnet run --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots` wrote 10 Shell surface snapshots and 10 PNG pixel snapshots covering 8 required Shell surfaces, 12 keyboard shortcuts, 7 focus states, Settings conflict, Command Palette permission-required, and Logs streaming states.
- Runner autostart: `dotnet run --project src\MyPowerTools.Cli -- runner autostart status` reports the current HKCU Run state through `AutostartBroker`; `dotnet run --project src\MyPowerTools.Cli -- runner autostart enable --dry-run` prints the resolved Runner command without registry writes.
- Template validation: `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` passed for 6 templates.
- Runner snapshot: `dotnet run --project src\MyPowerTools.Runner -- --once` indexed 7 modules. AdbForwarder, AndroidTools Notifications, and AndroidTools Remote Commands are runnable; AndroidTools modules use powertoold when the packaged sidecar command exists; Doubao Agent is degraded with 1/3 services reachable; AndroidTools Process Monitor is degraded until a watch list is saved; ScreenEase is degraded on the current monitor because DDC/CI capabilities are unsupported; SmartBird is degraded until Energy Server and FNB-58 are configured.
- Command execution:
  - `dotnet run --project src\MyPowerTools.Cli -- run adb-forwarder.diagnostics.summary` returned redacted ADB and Windows portproxy diagnostics.
  - `dotnet run --project src\MyPowerTools.Cli -- run adb-forwarder.portproxy.plan` returned structured current Windows portproxy state, default empty desired mappings, warnings, and no planned changes.
  - `dotnet run --project src\MyPowerTools.Cli -- run doubao-agent.health.check` returned typed degraded status with 1/3 Doubao services reachable on the current machine.
  - `dotnet run --project src\MyPowerTools.Cli -- run doubao-agent.self-test` returned settings schema availability, redacted `%LOCALAPPDATA%` paths, role endpoints, and token/secret/password redaction proof.
  - `dotnet run --project src\MyPowerTools.Cli -- run smartbird-thermostat.status.summary` returned SmartBird HTTP status, redacted local paths and ADB device identifiers, and degraded Energy Server/FNB-58 diagnostics.
  - `dotnet run --project src\MyPowerTools.Cli -- run smartbird-thermostat.events.list` returned latest 25 of 200 events with `truncated=true`.
  - `dotnet run --project src\MyPowerTools.Cli -- run smartbird-thermostat.service.restart` returned expected `permission-required` ServiceBroker output.
  - `dotnet run --project src\MyPowerTools.Cli -- run screenease.status.summary` returned active profile, 1 detected Windows display, default profiles/rules, and DDC/CI writer status.
  - `dotnet run --project src\MyPowerTools.Cli -- run screenease.native-writer.status` returned an unsupported-monitor `GetMonitorCapabilities` diagnostic from the Windows DDC/CI writer.
  - `dotnet run --project src\MyPowerTools.Cli -- run screenease.profile.apply` returned a profile application plan plus safe default `native-host-required` because hardware writes remain disabled unless explicitly requested or configured.
  - `dotnet run --project src\MyPowerTools.Cli -- run adb-forwarder.portproxy.apply` returned `permission-required`, as expected for brokered network changes. Runtime tests verify detailed `expectedChange` and `rollback` payloads when mappings are supplied.
  - `dotnet run --project src\MyPowerTools.Cli -- broker portproxy list` listed 6 local Windows portproxy rules.
  - `dotnet run --project src\MyPowerTools.Cli -- broker secret self-test --module cli.secret-self-test --name self-test-codex` passed through Windows Credential Manager, verified round-trip read, deleted the secret, and printed no secret value.
- Release Shell smoke: release Shell connected to Runner `0.2.0`, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited cleanly.
- Published Runner release-root discovery: release `Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data` indexed 7 modules from `artifacts\release\win-x64\modules` without an explicit `--modules` argument.
- Published Runner/Shell smoke: release `Runner\MyPowerTools.Runner.exe` plus release `Shell\MyPowerTools.Shell.Avalonia.exe --smoke --timeout-ms 30000 --quit-runner` connected to Runner `0.2.0`, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited cleanly.
- Published CLI autostart dry-run: release `Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` resolved the sibling release Runner command.
- Published CLI secret self-test: release `Cli\MyPowerTools.Cli.exe broker secret self-test --module cli.secret-self-test --name self-test-release-codex` passed through Windows Credential Manager, verified round-trip read, deleted the secret, and printed no secret value.
- Published CLI permission inspection: release `Cli\MyPowerTools.Cli.exe inspect modules` printed module capabilities, required/optional capability requirements, `apply-portproxy`, and `restart-service` broker permissions.
- Published package trust verification: release `Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` reported `signature-hook` for all 5 production packages.
- Release artifact: `artifacts/release/MyPowerTools-win-x64.zip` was rebuilt on 2026-07-04; SHA256 `EAA7E82DCC8B7BA63307360402C68A6764AFCA3870E1703C8FAE0EF5BE1266A4`; size 171460195 bytes.
- Release notes: `artifacts/release/RELEASE_NOTES.md` generated with artifact hash, size, verification commands, release/update metadata, Scoop manifest, and external requirements.
- Release/update metadata: `artifacts/release/release-metadata.json`; artifact SHA256 matches the Windows zip.
- Scoop manifest: `artifacts/release/package-managers/scoop/mypowertools.json`; 64-bit hash matches the Windows zip and `bin` exposes `mpt`.
- Release zip hygiene: no `bin/`, `obj/`, or `modules/modules/` entries.
- Portable install dry-run: `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun` succeeded.
- Portable uninstall dry-run: `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force` succeeded.

## Next Highest-Value Work

1. Continue P7 cross-platform capability packs and degraded behavior closure, focusing on remaining hotkey/privilege-helper interface shape, native macOS/Linux runtime validation, and UDS gRPC IPC end-to-end smoke on those hosts.
2. Validate ScreenEase hardware writes against a monitor that supports DDC/CI brightness/color-temperature controls when hardware is available.
3. Validate SmartBird against real Energy Server and FNB-58 hardware when those services are available.
4. Validate real Doubao planner/tool/MCP endpoint contracts when production health APIs are available.
5. Keep production signing and distribution-channel publication classified as external until signing material or a signing service is available.

## External Requirements To Verify Later

- DDC/CI-capable display hardware for ScreenEase write validation, plus SmartBird, FNB-58, Energy Server, and ADB devices.
- Doubao planner/tool/MCP services with documented production health/status endpoint contracts.
- Windows UAC or helper service packaging for running NetworkBroker outside the normal user token.
- Legacy secrets to migrate, if existing module installations already stored sensitive settings elsewhere.
