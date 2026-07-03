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
- P2 is the next active phase.

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
