# Phase History

## 2026-07-03

### P0 State Audit And Project Ledger

Status: done.

Actions:
- Read the current repository structure with `rg --files`.
- Read the plan package structure and core plan files under `mypowertools_plan_package`.
- Read `docs/PRODUCTION_READINESS.md`, `docs/AGENT_HANDOFF.md`, `README.md`, `CHANGELOG.md`, `docs/10-testing-release.md`, and `artifacts/release/RELEASE_NOTES.md`.
- Confirmed `.codex/project-state.json` was absent and created it.
- Created `docs/PHASES.md`, `docs/PROJECT_STATUS.md`, `docs/OPEN_BLOCKERS.md`, and `docs/EXTERNAL_VALIDATION.md`.
- Ran P0 validation commands and recorded results.

Validation:
- SDK: `10.0.301`.
- Restore succeeded.
- Build succeeded with 0 warnings and 0 errors.
- Tests passed: 65 passed, 0 failed, 0 skipped.
- Module validation passed for 5 production packages.
- Contract validation passed for 5 production packages and 7 modules.
- UI gate passed.
- Runner `--once` indexed 7 modules.
- Smoke passed with Runner/Shell HostControl IPC and graceful Runner shutdown.

Decision:
- P0 is complete.
- P1 is already verified complete by current architecture evidence and validation.
- P2 was selected as the next active phase at that point.

### P1 Foundation And Architecture Conformance

Status: done by current evidence.

Evidence:
- Runner and Shell are separate projects and processes.
- HostControl typed IPC covers the Shell surfaces listed in `docs/PRODUCTION_READINESS.md`.
- Runtime has package registry, module registry, command index, settings store, event bus, diagnostics, transport selection, health refresh, and broker routing.
- Transport hosts exist for T1 InProc, T2 gRPC IPC, T3 HTTP, and T4 stdio compatibility.
- Event stream resume and Shell reconnect behavior are tested.
- A foundation search for production tool IDs returned no matches in foundation projects.

Next:
- Continue with P2 module production closure.

## 2026-07-04

### P2 Module Runtime And Existing Tools Production Closure

Status: partial, progressed.

Actions:
- Added `src/DoubaoAgent.MyPowerTools`, a production InProc controller module for `doubao-agent`.
- Updated `modules/doubao-agent/module.json` with an `inproc-dotnet` entrypoint.
- Replaced the single HTTP health command with module-backed commands for status summary, all-service health, planner health, tool runtime health, MCP bridge health, self-test, and log summary.
- Added release packaging support for the Doubao module assembly.
- Added acceptance coverage for planner/tool/MCP separation, command discovery, self-test redaction, and log summary.

Validation:
- `dotnet restore MyPowerTools.slnx` succeeded after adding the project.
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test MyPowerTools.slnx --no-build` passed 66 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- validate modules` passed for 5 packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- validate contracts` passed for 5 packages and 7 modules; `doubao-agent` now reports 12 commands and runtime settings schema.
- `dotnet run --no-build --project src\MyPowerTools.Runner\MyPowerTools.Runner.csproj -- --once` indexed 7 modules and reported `doubao-agent [degraded] 1/3 Doubao runtime service(s) are reachable.`
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed and Shell smoke reported 73 commands.

Remaining P2 work:
- SmartBird config/events/restart typed facade and degraded hardware diagnostics.
- AndroidTools long-running `powertoold` T2 parity or documented runtime boundary with tests.
- ScreenEase native display writer validation remains external/native-host work.
- Real Doubao planner/tool/MCP endpoint contracts need validation against production local services.

### P2 Module Runtime And Existing Tools Production Closure

Status: partial, progressed.

Actions:
- Added `src/SmartBirdThermostat.MyPowerTools`, a production InProc typed facade for `smartbird-thermostat`.
- Updated `modules/smartbird-thermostat/module.json` with an `inproc-dotnet` entrypoint.
- Replaced the basic SmartBird command index with module-backed status, events, logs, config get/save, hardware diagnostics, self-test, and brokered restart commands.
- Added local path redaction for SmartBird HTTP response bodies and bounded event output with `eventLimit`.
- Added release packaging support for the SmartBird module assembly.
- Added acceptance coverage for status/events/config/log facade behavior, config persistence, ServiceBroker restart request details, redaction, and degraded Energy Server/FNB-58 diagnostics.
- Hardened `LogRouter` for concurrent CLI/runtime log writes after parallel command probes exposed `runner.jsonl` contention.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test MyPowerTools.slnx --no-build` passed 68 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- validate modules` passed for 5 packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- validate contracts` passed for 5 packages and 7 modules; `smartbird-thermostat` now reports 12 commands and runtime settings schema.
- `dotnet run --no-build --project src\MyPowerTools.Runner\MyPowerTools.Runner.csproj -- --once` indexed 7 modules and reported `smartbird-thermostat [degraded] Energy Server: Timed out while checking http://127.0.0.1:19003.; FNB-58 power meter: FNB-58 serial port is not configured.`
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- run smartbird-thermostat.status.summary` succeeded with SmartBird HTTP status reachable, ADB identifiers redacted, and Energy Server/FNB-58 degraded diagnostics.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- run smartbird-thermostat.events.list` succeeded with bounded event output: latest 25 of 200 events and `truncated=true`.
- `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- run smartbird-thermostat.service.restart` returned expected `permission-required` ServiceBroker output.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed and Shell smoke reported 79 commands.

Remaining P2 work:
- AndroidTools long-running `powertoold` T2 parity or documented runtime boundary with tests.
- ScreenEase native display writer validation remains external/native-host work.
- SmartBird full Energy Server/FNB-58 hardware validation remains external.
- Real Doubao planner/tool/MCP endpoint contracts need validation against production local services.

### P2 Module Runtime And Existing Tools Production Closure

Status: partial, progressed.

Actions:
- Added `src/AndroidTools.Powertoold`, a production gRPC IPC sidecar for the package-shared AndroidTools runtime.
- Wired `android-tools.notifications`, `android-tools.remote-commands`, and `android-tools.process-monitor` to prefer `package-runtime:100` while retaining the existing InProc fallback.
- Updated `GrpcIpcModuleHost` to forward `CommandRequest.Args` through the module protocol `args` map so T2 commands receive text input, booleans, and JSON array payloads.
- Added AndroidTools powertoold acceptance coverage for imported command execution, C++ comment removal, shared gRPC process diagnostics, and process watch-list persistence.
- Updated `scripts/publish-windows.ps1` so release packages include `modules/android-tools-suite/windows/x64/powertoold.exe` and its runtime dependencies.
- Rebuilt the Windows portable release zip.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test MyPowerTools.slnx --no-build` passed 68 tests, 0 failed, 0 skipped.
- `dotnet run --project src\MyPowerTools.Cli -- validate modules` passed for 5 packages.
- `dotnet run --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules; AndroidTools notifications and remote commands are running through powertoold, and process monitor reports its watch-list degraded state.
- `dotnet run --project src\MyPowerTools.Cli -- diagnostics` reported `grpc-ipc` process pool `package:android-tools-suite:runtime:powertoold` with all three AndroidTools modules.
- `dotnet run --project src\MyPowerTools.Cli -- inspect modules` showed AndroidTools modules with `package-runtime:100`.
- `dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --project src\MyPowerTools.Runner -- --once` indexed 7 modules and reported AndroidTools powertoold-backed states.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts/release/MyPowerTools-win-x64.zip` with SHA256 `FC79EEC5976F26ED7CA3F509AD2C070F1981057261BA2B199F33175B99C5D802`.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p2-powertoold` indexed 7 modules from the release root and started AndroidTools powertoold.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 79 commands, and requested Runner shutdown.
- Install and uninstall dry-runs passed for `artifacts\release\win-x64`.

Remaining P2 work:
- ScreenEase native display writer validation remains external/native-host work.
- SmartBird full Energy Server/FNB-58 hardware validation remains external.
- Real Doubao planner/tool/MCP endpoint contracts need validation against production local services.
- Android device notification and command end-to-end flows need connected devices/services for external validation.

### P2 Module Runtime And Existing Tools Production Closure

Status: partial, progressed.

Actions:
- Added `DisplayWriterStatus` to `IDisplayService`.
- Implemented Windows ScreenEase display writer support through Dxva2 DDC/CI capability probing, brightness range mapping, and supported color-temperature mapping.
- Kept macOS/Linux display providers explicit degraded implementations.
- Added `screenease.native-writer.status` and `screenease.native-writer.configure` dynamic/static commands.
- Kept `screenease.profile.apply` safe by default; hardware writes run only when `hardwareWrite=true` is passed or the native writer is configured on.
- Added ScreenEase acceptance coverage for default no-hardware-write behavior, explicit writer invocation, and persisted writer enable flow.
- Rebuilt the Windows portable release zip.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test MyPowerTools.slnx --no-build` passed 71 tests, 0 failed, 0 skipped after the release module copy was refreshed.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules; ScreenEase reports 14 commands.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- run screenease.status.summary` returned 1 detected Windows display and current DDC/CI writer status.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- run screenease.native-writer.status` returned an actionable unsupported-monitor `GetMonitorCapabilities` diagnostic for the current Generic PnP Monitor.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- run screenease.profile.apply` returned the expected safe default `native-host-required` status because hardware writes are disabled unless explicitly requested or configured.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` reported 81 commands.
- `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` indexed 7 modules and reported ScreenEase degraded due current DDC/CI hardware support.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts/release/MyPowerTools-win-x64.zip` with SHA256 `594634C7733FC4CC7A138CD67DF90218F3249C754DE952AF50A795F031292EFA`.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-screenease-writer` indexed 7 modules from the release root.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, and requested Runner shutdown.
- Install and uninstall dry-runs passed for `artifacts\release\win-x64`.

Remaining P2 work:
- ScreenEase hardware write validation on a DDC/CI-capable monitor remains external.
- SmartBird full Energy Server/FNB-58 hardware validation remains external.
- Real Doubao planner/tool/MCP endpoint contracts need validation against production local services.
- Android device notification and command end-to-end flows need connected devices/services for external validation.

### P2 Module Runtime And Existing Tools Production Closure

Status: done.

Actions:
- Added AndroidTools acceptance coverage for invalid notification endpoint config, including degraded status and actionable server-check output.
- Added AndroidTools Process Monitor acceptance coverage for empty watch-list configuration, degraded state, zero configured processes, and validation failure for empty saves.
- Added Doubao Agent acceptance coverage for role-specific partial outages when planner is reachable but tool runtime and MCP bridge are unavailable.
- Refreshed debug production module binaries plus local package hash/signature metadata after the build copied updated assemblies into `modules/`.
- Reclassified remaining P2 items as external validation because hardware, connected devices, or documented production service APIs are required.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- `dotnet test MyPowerTools.slnx --no-build` passed 74 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- inspect modules` listed capabilities, requirements, and broker permissions for all production modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- module list --include-disabled` listed all 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` indexed 7 modules and reported expected degraded states for AndroidTools Process Monitor, Doubao Agent, ScreenEase, and SmartBird.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` reported 5 packages, 7 modules, 81 commands, and the AndroidTools `grpc-ipc` package runtime pool.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` passed.
- P2 command probes passed for `adb-forwarder.diagnostics.summary`, `adb-forwarder.portproxy.plan`, `screenease.status.summary`, `doubao-agent.health.check`, and `smartbird-thermostat.status.summary`.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed, including build, sign-local, 74 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke with 7 modules, 7 dashboard cards, and 81 commands.

Remaining P2 work:
- None internally. External validation remains tracked in `docs/OPEN_BLOCKERS.md` and `docs/EXTERNAL_VALIDATION.md`.

### P3 Broker, Privilege, Security And Secrets

Status: done.

Actions:
- Expanded `schemas/module.schema.json` to accept the planned permission levels: `serviceUser`, `serviceSystem`, and `sensitive`, while retaining `service` and `broker` compatibility.
- Updated `PrivilegedBroker` so `elevated`, `service`, `serviceUser`, `serviceSystem`, `sensitive`, and `broker` require broker handling, while ordinary `user` actions remain non-brokered.
- Updated `ServiceBroker.RestartAsync` audit entries to use `serviceUser` for user-level service restarts.
- Added acceptance coverage for planned permission-level schema validation, `PrivilegedBroker` broker-required decisions, and `ServiceBroker` service-user audit/redaction behavior.
- Rebuilt and refreshed local package hash/signature metadata after module build outputs were copied into `modules/`.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet test MyPowerTools.slnx --no-build` passed 77 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- inspect modules` printed capabilities, requirements, and broker permissions.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- broker audit` listed broker audit entries.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- broker portproxy list` listed 6 local Windows portproxy rules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- broker portproxy apply --listen-address 127.0.0.1 --listen-port 45678 --connect-address 127.0.0.1 --connect-port 45679` returned `permission-required` in normal user context.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- run adb-forwarder.portproxy.apply` returned the expected `permission-required` exit path.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- broker secret self-test --module cli.secret-self-test --name self-test-p3-codex` passed without printing the secret value.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed, including build, sign-local, 77 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke with 7 modules, 7 dashboard cards, and 81 commands.

Remaining P3 work:
- None internally. Live elevated helper/service execution requires administrator context or signed helper packaging and remains tracked as external validation.

### P4 Shell UI, Design System And Visual Quality

Status: done.

Actions:
- Added `ShellKeyboardShortcut` and MainWindow keyboard handling for command palette focus, clear/focus return, page refresh, and primary page navigation.
- Added HostControl `GetSettingsSchema` plus Runtime transport schema retrieval for InProc, gRPC IPC, and stdio modules.
- Replaced raw-only Shell Settings editing with schema-rendered controls for booleans, enums, scalar fields, and JSON object/array fields while preserving HostControl revision writes.
- Added `MptTheme` and replaced direct color literals in Shell/MainWindow and MPT controls with centralized theme tokens.
- Expanded Shell snapshot metadata with keyboard shortcuts, focus states, Settings conflict, Command Palette permission-required, and Logs streaming state coverage.
- Made `scripts/smoke.ps1` fail on native command exit codes instead of reporting false success after failed `dotnet` commands.
- Hardened `PackageStore` install/uninstall/rollback directory move/delete operations with retry for transient Windows file-system access failures.
- Rebuilt the Windows portable release zip.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- `dotnet test MyPowerTools.slnx --no-build` passed 80 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` passed.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots` wrote 7 module snapshots and 7 PNG pixel snapshots.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui shell-snapshot --theme light --size 1366x768 --density normal --out artifacts\shell-ui-snapshots` wrote 10 Shell snapshots and 10 PNG pixel snapshots with 12 shortcut entries and 7 focus states.
- `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` indexed 7 modules with expected degraded external-dependency states.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed with strict native exit-code handling.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts\release\MyPowerTools-win-x64.zip` with SHA256 `2418C0BEF33FE955DB81C677B1C74FE3E0DEC5CCA4D2D14BD1CB5D46B02DFDB0` and size 171430107 bytes.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p4` indexed 7 modules from the release root.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited.

Remaining P4 work:
- None internally. Richer module-specific editors beyond schema-rendered Settings and HostControl-backed Detail pages are future feature work.

### P5 Reliability, Observability And Runtime Policy

Status: done.

Actions:
- Added `ModuleSupervisor` to record per-module health observations, consecutive failure counts, supervisor state, last observation time, and actionable next steps.
- Wired supervisor observation into runtime load, health refresh, dynamic command refresh, and module enable/disable flows.
- Added Dashboard host alerts for modules that reach `intervention-needed` after repeated unhealthy samples.
- Extended `RuntimeModuleDiagnostics` and `mpt_host_control_v1.proto` with summary, observation count, consecutive failure count, supervisor state/action, and last observed time.
- Updated HostControl mapping, CLI `mpt diagnostics`, and Shell Diagnostics rendering for the new supervisor fields.
- Made `mpt diagnostics` refresh module health before reporting diagnostics.
- Added `mpt runner process pause . --duration-minutes 1` shorthand to resolve `.` to the first active RuntimeDiagnostics process pool.
- Updated Shell event handling so `module.health.changed` refreshes Dashboard, Modules, and Diagnostics views.
- Added acceptance tests for repeated HTTP facade failure escalation, recovery reset, HostControl supervisor fields, CLI diagnostics supervisor output, and CLI process policy shorthand.
- Rebuilt the Windows portable release zip.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- `dotnet test MyPowerTools.slnx --no-build` passed 82 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` reported ModuleSupervisor state/action/failure counts for all 7 modules plus the AndroidTools shared `grpc-ipc` process pool.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- runner process pause . --duration-minutes 1` passed against a temporary Runner, paused the AndroidTools shared gRPC process pool, printed expiry/modules, and resume restored automatic policy.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` indexed 7 modules with expected degraded external-dependency states.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed, including build, sign-local, 82 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke with graceful Runner shutdown.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts\release\MyPowerTools-win-x64.zip` with SHA256 `3210E8F4607F484C82AD95452BFE9E76ECC51DACEB1C04099719B57AA40ECA9B` and size 171459935 bytes.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p5` indexed 7 modules from the release root.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited.

Remaining P5 work:
- None internally. Hardware, external services, administrator context, signing material, and native macOS/Linux validation remain tracked in external validation rows or later phases.

### P6 Packaging, Templates, CLI, Install And Release

Status: done.

Actions:
- Added `scripts/release-metadata.ps1` to generate `artifacts/release/release-metadata.json` and `artifacts/release/package-managers/scoop/mypowertools.json` from the Windows portable zip.
- Wired `scripts/publish-windows.ps1` to generate release metadata before release notes.
- Updated `scripts/release-notes.ps1` so release notes list release/update metadata and the Scoop package-manager manifest.
- Added acceptance coverage for release metadata, local artifact URL generation, SHA256 parity, and Scoop `mpt` shim shape.
- Refreshed local package hash/signature metadata after build outputs changed under `modules/`.

Validation:
- `dotnet restore MyPowerTools.slnx` succeeded.
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- `dotnet test MyPowerTools.slnx --no-build` passed 83 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- inspect modules` printed capabilities, requirements, and broker permissions for all production modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` passed for all 6 templates.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed, including build, sign-local, 83 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts\release\MyPowerTools-win-x64.zip`, `RELEASE_NOTES.md`, `release-metadata.json`, and the Scoop manifest.
- `Get-FileHash artifacts\release\MyPowerTools-win-x64.zip -Algorithm SHA256` returned `EAA7E82DCC8B7BA63307360402C68A6764AFCA3870E1703C8FAE0EF5BE1266A4`.
- Release metadata and Scoop manifest hashes matched the Windows zip hash; Scoop `bin` exposes `mpt`.
- Release zip hygiene check found no `bin/`, `obj/`, or `modules/modules/` entries.
- `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` passed.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p6` indexed 7 modules from the release root.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and the release Runner exited with code 0.
- `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` resolved the release Runner path without registry writes.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun` printed the install plan.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force` printed the uninstall plan.

Remaining P6 work:
- None internally. Production signing and channel publication require private signing material or a signing service and remain tracked as external validation.

### P7 Cross-Platform Capability Packs And Degraded Behavior

Status: done.

Actions:
- Added `ILocalIpc` and `LocalIpcService` to choose Named Pipe endpoints on Windows and Unix Domain Socket paths on macOS/Linux.
- Added unsupported provider implementations for notification, autostart, service, network, and process service surfaces in platform abstractions.
- Added `ManagedProcessService` so macOS/Linux can expose truthful basic process inspection through the managed runtime.
- Updated Windows, macOS, and Linux platform packs to expose `LocalIpc`.
- Updated macOS/Linux platform packs to expose notification, autostart, service, network, process, display, tray, and secret services with explicit unsupported states where native implementation is pending.
- Added acceptance coverage for missing required capability -> `unsupported`, missing optional capability -> `degraded`, platform-native IPC endpoint shape, and Mac/Linux degraded provider behavior.
- Added `IHotkeyService`, `IPrivilegeBroker`, `UnsupportedHotkeyService`, `UnsupportedPrivilegeBroker`, and `BrokerRequiredPrivilegeBroker` to platform abstractions.
- Updated Windows, macOS, and Linux platform packs to expose hotkey and privilege provider surfaces. Windows hotkey registration now uses Win32 `RegisterHotKey`, Windows privilege evaluation returns broker-required, and macOS/Linux return explicit unsupported states.
- Updated `PrivilegedBroker` to implement the platform privilege contract.
- Added acceptance coverage for cross-platform hotkey/privilege provider behavior and `PrivilegedBroker` async contract/audit behavior.

Validation:
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- Targeted P7 tests passed: `Platform_capability_registry_marks_missing_required_capability_unsupported`, `Local_ipc_service_selects_platform_native_endpoint_shape`, `Mac_and_linux_platform_packs_expose_truthful_degraded_services`, `Platform_packs_expose_hotkey_and_privilege_surfaces`, and `Privileged_broker_implements_platform_privilege_contract`.
- `dotnet test MyPowerTools.slnx --no-build` passed 88 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- inspect modules` printed module requirements and broker permissions.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` reported Windows `grpc-ipc` runtime process state and ModuleSupervisor summaries.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed with build, sign-local, 88 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke.

Remaining P7 work:
- Native macOS/Linux Runner/Shell/UDS smoke requires those OS hosts.
- None internally. Native macOS/Linux validation remains external.

### P8 Final Production Closure

Status: done.

Actions:
- Ran the final P8 audit scans for TODO/FIXME/placeholder/stub/fake/coming soon, unsupported states, sample modules in production roots, hardcoded user paths, release URLs, and high-confidence secret patterns.
- Fixed the local package trust algorithm label by renaming `sha256-manifest-placeholder` to `sha256-manifest-local` and refreshing all production package signatures.
- Renamed `UiSurfaceGate.WriteSnapshotPlaceholder` to `WriteDefaultSnapshotSet` so the code name matches the real contract/PNG snapshot output.
- Regenerated the Windows portable release after release metadata URL handling was changed to relative `MyPowerTools-win-x64.zip`.
- Verified release metadata and Scoop manifest hash parity, relative URLs, release package trust, release Runner once, release Shell smoke, autostart dry-run, install/uninstall dry-run, and zip hygiene.
- Updated P8 audit docs, phase ledger, project status, production readiness evidence, external validation, known limitations, handoff notes, changelog, and project state.

Validation:
- `dotnet --version` returned `10.0.301`.
- `dotnet restore MyPowerTools.slnx` succeeded.
- `dotnet build MyPowerTools.slnx --no-restore` succeeded with 0 warnings and 0 errors.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package sign-local modules` refreshed hash manifests and local trust hooks for all 5 production packages.
- `dotnet test MyPowerTools.slnx --no-build` passed 88 tests, 0 failed, 0 skipped.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate modules` passed for 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- validate contracts` passed for 5 packages and 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- package trust modules --strict` reported `signature-hook` for all 5 production packages.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui check modules` passed.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots` wrote the module dashboard snapshot manifest.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- diagnostics` reported 5 packages, 7 modules, 81 commands, ModuleSupervisor state, and the AndroidTools `grpc-ipc` pool.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- inspect modules` printed capabilities, requirements, and broker permissions.
- `dotnet run --no-build --project src\MyPowerTools.Cli -- module list --include-disabled` listed all 7 modules.
- `dotnet run --no-build --project src\MyPowerTools.Runner -- --once` indexed 7 modules with truthful running/degraded states.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1` passed for 6 templates.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` passed with build, sign-local, 88 tests, module validation, contract validation, package trust, UI snapshots, template validation, Runner once, and Shell HostControl smoke.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1` rebuilt `artifacts\release\MyPowerTools-win-x64.zip`.
- `Get-FileHash artifacts\release\MyPowerTools-win-x64.zip -Algorithm SHA256` returned `29BECF13374D92F100E58BA60F9187FD166C136919427B826C0A8979EEA3C670`; size is 171498490 bytes.
- Release metadata and Scoop manifest hashes match the Windows zip hash and both use relative URL `MyPowerTools-win-x64.zip`.
- Release zip hygiene found no `bin/`, `obj/`, or `modules/modules/` entries.
- `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` passed for all 5 production packages.
- `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once` indexed 7 modules from the release root.
- Release Shell smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and Runner exited with code 0.
- `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` resolved the release Runner executable without registry writes.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun` printed the install plan.
- `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force` printed the uninstall plan.

Remaining P8 work:
- None internally. External validation remains for administrator/signed-helper execution, production signing, display and device hardware, external services, and native macOS/Linux hosts.
