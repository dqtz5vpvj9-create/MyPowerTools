# Open Blockers And Gaps

Run date: 2026-07-03.

This file separates true external blockers from internal phase gaps. External blockers require hardware, credentials, administrator context, signing material, native OS access, or external services. Internal gaps remain normal engineering work for later phases.

## External Blockers

| Blocker | Affected Phase | Evidence | Required External State |
|---|---|---|---|
| Administrator context or elevated helper for live Windows portproxy writes | P3 | `adb-forwarder.portproxy.apply` correctly returns `permission-required` in normal user context; NetworkBroker tests cover rollback and brokered execution paths. | Administrator token, UAC/helper service packaging, or signed elevated helper. |
| Private signing material for production package/installer signing | P6 | Local trust hook exists; release uses local signature metadata. | Code-signing/private package signing key or signing service. |
| Hardware validation for display writes | P2, P7 | ScreenEase reports `native-host-required` for brightness/color-temperature writes while status/profile paths work. | Native display writer and monitor hardware validation environment. |
| SmartBird hardware/service ecosystem validation | P2 | SmartBird HTTP facade health path exists; deeper hardware paths depend on local SmartBird, FNB-58, Energy Server, and ADB setup. | Connected devices and local services. |
| ADB device validation beyond diagnostics | P2 | AdbForwarder diagnostics and AndroidTools facade paths run; device-specific flows depend on local Android devices. | Connected ADB devices and expected local commands. |
| Native macOS/Linux runtime validation | P7 | macOS/Linux projects compile with degraded providers; native runtime/smoke validation has not run on those OS hosts. | macOS and Linux validation hosts. |

## Internal Phase Gaps

| Gap | Phase | Current Evidence | Next Work |
|---|---|---|---|
| AndroidTools long-running `powertoold` T2 parity | P2 | Current shared InProc facade imports commands, checks notifications, and scans process watch lists. | Add or finish shared gRPC sidecar streaming/polling parity where needed. |
| Doubao Agent deeper controller model | P2 | Current module has HTTP health facade and surfaces. | Split planner/tool/MCP status, logs, settings, and self-test coverage more deeply. |
| SmartBird deeper typed facade coverage | P2 | Current module has HTTP health facade and surfaces. | Add config/events/restart and degraded hardware diagnostics. |
| Shell keyboard and interactive visual diff matrix | P4 | UI gate, module snapshots, and Shell snapshot matrix pass. | Add interactive screenshot diff matrix and keyboard navigation audit. |
| Module-specific deep editors | P4 | Generic Shell pages cover module status, commands, settings, logs, permissions, packages, and diagnostics. | Add focused editors for AndroidTools, AdbForwarder, ScreenEase, Doubao Agent, and SmartBird. |
| Broader ModuleSupervisor health policy automation | P5 | Crash recovery, restart throttling, process policy, and diagnostics tests exist. | Expand long-running health policy automation and operational reporting. |
| MSI/MSIX or package-manager distribution | P6 | Windows portable zip and install/uninstall scripts exist. | Add signed installer or package-manager metadata. |

## Skipped Tests

None. Latest test run: 65 passed, 0 failed, 0 skipped.
