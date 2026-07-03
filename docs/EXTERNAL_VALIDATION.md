# External Validation

Run date: 2026-07-04.

The following checks require machine state outside the repository. They remain separate from automated local validation so internal completion is not confused with unavailable hardware, privileges, credentials, or operating systems.

## Required External Checks

| Check | Phase | Current Automated Evidence | External Requirement | Expected Result |
|---|---|---|---|---|
| Elevated portproxy apply/revert | P3 | NetworkBroker plans and audits portproxy changes; normal user apply returns `permission-required`. | Administrator token or signed elevated helper. | Apply/revert succeeds through Broker and audit log records each elevated step. |
| Production signing | P6 | Local `package.signature.json` trust hook and strict package trust verification pass. | Production signing key/certificate or signing service. | Release package and installer carry production signature metadata. |
| ScreenEase hardware write path | P2, P7 | Display enumeration and profile planning work; hardware writes return `native-host-required`. | Native display writer plus monitor hardware supporting brightness/color-temperature writes. | Apply command changes display state or returns actionable hardware-level diagnostics. |
| SmartBird full hardware flow | P2 | InProc typed facade reads HTTP status/events/config/logs, returns ServiceBroker restart details, bounds event output, redacts local paths, and reports degraded Energy Server/FNB-58 diagnostics. | SmartBird device, FNB-58, Energy Server, and ADB environment. | Status, events, config, restart, Energy Server, FNB-58, and ADB commands reflect real hardware/service state. |
| Android device flows | P2 | AndroidTools modules validate and AdbForwarder diagnostics run. | One or more connected ADB devices and expected local command catalog. | Device discovery, notification polling/streaming, and remote commands operate end-to-end. |
| Doubao role endpoint validation | P2 | `doubao-agent.status.summary` checks planner, tool runtime, and MCP bridge separately and reports degraded when only part of the runtime is reachable. | Local Doubao planner/tool/MCP services with documented health/status APIs. | Planner, tool runtime, and MCP bridge all report reachable status or a role-specific degraded reason. |
| macOS native validation | P7 | macOS platform project compiles with degraded providers. | macOS host with .NET SDK 10. | Build, module validation, Runner once, Shell smoke, UDS transport, and degraded providers behave as documented. |
| Linux native validation | P7 | Linux platform project compiles with degraded providers. | Linux host with .NET SDK 10. | Build, module validation, Runner once, Shell smoke, UDS transport, and degraded providers behave as documented. |

## Latest Local Release Evidence

| Artifact | Evidence |
|---|---|
| Windows portable zip | `artifacts/release/MyPowerTools-win-x64.zip` |
| SHA256 | `AEF78FD0AC90441B336F5816A944919FF9297D0413EECA3B268F1C511DB5CCFA` |
| Release notes | `artifacts/release/RELEASE_NOTES.md` |
| Install dry-run | Previously verified by `scripts/install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun`. |
| Uninstall dry-run | Previously verified by `scripts/uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force`. |
