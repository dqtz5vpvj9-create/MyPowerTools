# Project Status

Run date: 2026-07-04.

## Snapshot

| Field | Value |
|---|---|
| Project | MyPowerTools |
| Current phase | P2 Module runtime and existing tools production closure |
| Last completed phase | P1 Foundation and architecture conformance |
| Next phase | P2 |
| SDK | 10.0.301 from `global.json` and `dotnet --version` |
| Target frameworks | `net10.0` projects across the solution |
| Production packages | 5: `adb-forwarder`, `android-tools-suite`, `doubao-agent`, `screenease`, `smartbird-thermostat` |
| Production modules | 7 |
| Templates | 6 |
| Tests | 68 passed, 0 failed, 0 skipped |
| Release artifact | `artifacts/release/MyPowerTools-win-x64.zip` |
| Release SHA256 | `AEF78FD0AC90441B336F5816A944919FF9297D0413EECA3B268F1C511DB5CCFA` |
| Production closure | false |

## Latest Validation Results

| Command | Result |
|---|---|
| `dotnet --version` | `10.0.301` |
| `dotnet restore MyPowerTools.slnx` | Succeeded; all projects were up-to-date. |
| `dotnet build MyPowerTools.slnx --no-restore` | Succeeded with 0 warnings and 0 errors. |
| `dotnet test MyPowerTools.slnx --no-build` | Passed 68, failed 0, skipped 0. |
| `dotnet run --project src\MyPowerTools.Cli -- validate modules` | 5 production packages valid. |
| `dotnet run --project src\MyPowerTools.Cli -- validate contracts` | 5 packages and 7 modules passed contract validation; SmartBird now reports 12 commands and runtime settings schema. |
| `dotnet run --project src\MyPowerTools.Cli -- ui check modules` | UI gate passed. |
| `dotnet run --project src\MyPowerTools.Runner -- --once` | 7 modules indexed; current expected degraded states reported for AndroidTools Process Monitor, Doubao partial services, ScreenEase hardware writes, and SmartBird Energy Server/FNB-58 dependencies. |
| `pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1` | Passed; Shell HostControl smoke connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 79 commands, and requested graceful Runner shutdown. |

## Current Module State

| Module | State From Latest Runner/Contract Evidence | Notes |
|---|---|---|
| `adb-forwarder` | running | ADB diagnostics and Windows portproxy diagnostics are available. |
| `android-tools.notifications` | running | Endpoint imported from package-shared config. |
| `android-tools.remote-commands` | running | 11 command(s) imported from package-shared `commands.yaml`. |
| `android-tools.process-monitor` | degraded | Needs a saved watch list through `android-tools.process-monitor.watch.save`. |
| `doubao-agent` | degraded | InProc controller checks planner/tool/MCP separately; current local runtime reports 1/3 services reachable. |
| `screenease` | degraded | Native display writer is absent; profile/status paths work. |
| `smartbird-thermostat` | degraded | InProc typed facade reads HTTP status/events/config/logs, returns brokered restart details, and reports missing Energy Server/FNB-58 dependency state. |

## Validation Note

Local builds copy module assemblies into `modules/`. When assemblies change, run `dotnet run --no-build --project src\MyPowerTools.Cli\MyPowerTools.Cli.csproj -- package sign-local modules` before trust-sensitive tests or strict package verification. The latest P0 verification was repeated after the signatures matched the build outputs.

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
