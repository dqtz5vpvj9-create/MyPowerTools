# External Validation

Run date: 2026-07-06.

The checks below require machine state outside the repository. They remain separate from automated local validation so internal completion is not confused with unavailable hardware, privileges, credentials, or operating systems.

## Required External Checks

| Check | Current Automated Evidence | External Requirement | Expected Result |
|---|---|---|---|
| Elevated portproxy apply/revert | NetworkBroker plans and audits portproxy changes; normal user `adb-forwarder.portproxy.apply` returns `permission-required`; planned permission levels and rollback are covered by tests. | Administrator token or signed elevated helper. | Apply/revert succeeds through Broker and audit log records each elevated step. |
| Production signing | Local `package.signature.json` trust hook, strict package trust verification, release/update metadata, and Scoop manifest generation pass. | Production signing key/certificate or signing service. | Release package, installer, or package-manager channel carries production signature metadata. |
| ScreenEase hardware write path | Display enumeration, profile planning, native writer status/configure, and explicit hardware apply path exist; current monitor returns unsupported capabilities. | Monitor hardware supporting DDC/CI brightness/color-temperature writes. | Apply command changes display state or returns hardware-level diagnostics. |
| SmartBird attached-hardware controls | The fixed loopback dashboard, read-only status/events/energy probes, original task log, bounded output, restart request and redaction are verified. | SmartBird switch, Energy Server/HID meter, and ADB thermal targets. | Read Meter and manual switch actions in the embedded original dashboard reflect the attached devices and append source events. |
| Android device and notification flows | AndroidTools modules validate through the package-shared powertoold gRPC IPC sidecar; AdbForwarder diagnostics run; invalid notification config and empty watch-list paths are tested. | Connected ADB devices, notification service state, and expected command catalog. | Device discovery, notification polling/streaming, and remote commands operate end-to-end through powertoold. |
| Doubao role endpoint validation | Doubao Agent checks planner, tool runtime, and MCP bridge separately and reports role-specific degraded status. | Local Doubao planner/tool/MCP services with documented health/status APIs. | Planner, tool runtime, and MCP bridge all report reachable status or a role-specific degraded reason. |
| macOS native validation | Managed arm64/x64 application bundles cross-publish with all six production packages and only Remote Notifications enabled by default. Native WKWebView, UserNotifications, NSPasteboard, Keychain, launchd, and NSStatusItem providers are wired; the status item includes the Codex quota Retina renderer and refresh monitor. | macOS host with .NET SDK 10 and Xcode Command Line Tools. | Build the native dylib, verify codesign, run Shell/Runner/UDS smoke, confirm live Codex quota percentage/reset tooltip and color thresholds, render WKWebView, exercise notification activation and NSPasteboard Paste Image, and validate launchd restoration. |
| Linux native validation | Linux platform project compiles with degraded hotkey, privilege, notification, autostart, service, network, display, tray, and secret providers. | Linux host with .NET SDK 10. | Build, module validation, Runner once, Shell smoke, UDS transport, and degraded providers behave as documented. |

## Latest Local Release Evidence

| Artifact | Evidence |
|---|---|
| Windows portable zip | `artifacts/release/MyPowerTools-win-x64.zip` |
| SHA256 | `B4F8CFED2E13C0370068B0D4DEBB0F66BCF4A18E74620FD7FEBAAEB93CC84BE5` |
| Size | 223125040 bytes |
| Release notes | `artifacts/release/RELEASE_NOTES.md` |
| Release/update metadata | `artifacts/release/release-metadata.json`; artifact hash matches the zip hash and artifact URL is relative: `MyPowerTools-win-x64.zip`. |
| Scoop manifest | `artifacts/release/package-managers/scoop/mypowertools.json`; 64-bit hash matches the zip hash, URL is relative, and `bin` exposes `mpt`. |
| Release package trust | `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe package trust artifacts\release\win-x64\modules --strict` passed. |
| Release Runner once | `artifacts\release\win-x64\Runner\MyPowerTools.Runner.exe --once --data-root artifacts\review-evidence\release-root-once-data` indexed 7 modules. |
| Release Shell smoke | Release Shell connected to Runner 0.2.0, reported 7 modules, 7 dashboard cards, 81 commands, requested Runner shutdown, and Runner exited with code 0. |
| Release autostart dry-run | `artifacts\release\win-x64\Cli\MyPowerTools.Cli.exe runner autostart enable --dry-run` resolved the release Runner command. |
| Install dry-run | Verified by `scripts/install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun`. |
| Uninstall dry-run | Verified by `scripts/uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force`. |
| Review evidence | `artifacts/review/MyPowerTools-final-evidence.zip`, SHA256 `ABA4ED23AC71D4727A1FB3619610CD57B996DBCA3A32465CB6A2CD6DCB8A4AF1`. |


