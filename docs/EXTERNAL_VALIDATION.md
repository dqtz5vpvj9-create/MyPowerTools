# External Validation

Run date: 2026-07-04.

The following checks require machine state outside the repository. They remain separate from automated local validation so internal completion is not confused with unavailable hardware, privileges, credentials, or operating systems.

## Required External Checks

| Check | Phase | Current Automated Evidence | External Requirement | Expected Result |
|---|---|---|---|---|
| Elevated portproxy apply/revert | P3 | NetworkBroker plans and audits portproxy changes; normal user `adb-forwarder.portproxy.apply` and CLI `broker portproxy apply` return `permission-required`; planned permission levels and `serviceUser` audit are covered by tests. | Administrator token or signed elevated helper. | Apply/revert succeeds through Broker and audit log records each elevated step. |
| Production signing | P6 | Local `package.signature.json` trust hook, strict package trust verification, release/update metadata, and Scoop manifest generation pass. | Production signing key/certificate or signing service. | Release package, installer, or package-manager channel carries production signature metadata. |
| ScreenEase hardware write path | P2, P7 | Display enumeration, profile planning, native writer status/configure, and explicit hardware apply path exist; current Generic PnP Monitor returns `GetMonitorCapabilities` unsupported. | Monitor hardware supporting DDC/CI brightness/color-temperature writes. | Apply command changes display state or returns actionable hardware-level diagnostics. |
| SmartBird full hardware flow | P2 | InProc typed facade reads HTTP status/events/config/logs, returns ServiceBroker restart details, bounds event output, redacts local paths, and reports degraded Energy Server/FNB-58 diagnostics. | SmartBird device, FNB-58, Energy Server, and ADB environment. | Status, events, config, restart, Energy Server, FNB-58, and ADB commands reflect real hardware/service state. |
| Android device and notification flows | P2 | AndroidTools modules validate through the package-shared `powertoold` gRPC IPC sidecar; AdbForwarder diagnostics run; invalid notification config and empty watch-list degraded paths are covered by acceptance tests. | One or more connected ADB devices, notification service state, and expected local command catalog. | Device discovery, notification polling/streaming, and remote commands operate end-to-end through powertoold. |
| Doubao role endpoint validation | P2 | `doubao-agent.status.summary` checks planner, tool runtime, and MCP bridge separately and reports degraded when only part of the runtime is reachable; role-specific partial outage behavior is covered by acceptance tests. | Local Doubao planner/tool/MCP services with documented health/status APIs. | Planner, tool runtime, and MCP bridge all report reachable status or a role-specific degraded reason. |
| macOS native validation | P7 | macOS platform project compiles with degraded providers. | macOS host with .NET SDK 10. | Build, module validation, Runner once, Shell smoke, UDS transport, and degraded providers behave as documented. |
| Linux native validation | P7 | Linux platform project compiles with degraded providers. | Linux host with .NET SDK 10. | Build, module validation, Runner once, Shell smoke, UDS transport, and degraded providers behave as documented. |

## Latest Local Release Evidence

| Artifact | Evidence |
|---|---|
| Windows portable zip | `artifacts/release/MyPowerTools-win-x64.zip` |
| SHA256 | `EAA7E82DCC8B7BA63307360402C68A6764AFCA3870E1703C8FAE0EF5BE1266A4` |
| Size | 171460195 bytes |
| Release notes | `artifacts/release/RELEASE_NOTES.md` |
| Release/update metadata | `artifacts/release/release-metadata.json`; artifact hash matches the zip hash. |
| Scoop manifest | `artifacts/release/package-managers/scoop/mypowertools.json`; 64-bit hash matches the zip hash and `bin` exposes `mpt`. |
| Release package trust | `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` passed. |
| Release Runner once | `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\release-root-once-data-p6` indexed 7 modules. |
| Release Shell smoke | Release Shell connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and Runner exited with code 0. |
| Release autostart dry-run | `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` resolved the release Runner command. |
| Install dry-run | Verified by `scripts/install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun`. |
| Uninstall dry-run | Verified by `scripts/uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force`. |
