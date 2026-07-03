# Open Blockers And Gaps

Run date: 2026-07-04.

This file separates true external blockers from internal phase gaps. External blockers require hardware, credentials, administrator context, signing material, native OS access, or external services. Internal gaps remain normal engineering work for later phases.

## External Blockers

| Blocker | Affected Phase | Evidence | Required External State |
|---|---|---|---|
| Administrator context or elevated helper for live Windows portproxy writes | P3 | `adb-forwarder.portproxy.apply` correctly returns `permission-required` in normal user context; NetworkBroker tests cover rollback and brokered execution paths. | Administrator token, UAC/helper service packaging, or signed elevated helper. |
| Private signing material for production package/installer signing | P6 | Local trust hook exists; release uses local signature metadata. | Code-signing/private package signing key or signing service. |
| Hardware validation for display writes | P2, P7 | ScreenEase Windows DDC/CI writer is implemented and wired behind explicit hardware apply; current Generic PnP Monitor returns `GetMonitorCapabilities` unsupported. | Monitor hardware that supports DDC/CI brightness/color-temperature writes. |
| SmartBird hardware/service ecosystem validation | P2 | SmartBird InProc typed facade reads HTTP status/events/config/logs, returns brokered restart details, and reports current degraded Energy Server/FNB-58 state. | Connected SmartBird device, FNB-58 meter, Energy Server, and ADB services. |
| ADB and AndroidTools device/service validation beyond local module contracts | P2 | AdbForwarder diagnostics run; AndroidTools powertoold serves notifications, remote commands, and process monitor locally through `grpc-ipc`. Invalid notification config and empty watch-list degraded paths are covered by acceptance tests. Device-specific notification polling/streaming and command flows depend on local Android devices and services. | Connected ADB devices, notification service state, and expected local command catalog. |
| Doubao planner/tool/MCP endpoint contract validation | P2 | InProc controller checks ports 38102, 38080, and 38189 separately; current local services expose 404 on planner/tool health paths and 200 on MCP. Role-specific partial outage behavior is covered by acceptance tests. | Running Doubao services with documented production health/status APIs for each role. |
| Native macOS/Linux runtime validation | P7 | macOS/Linux projects compile with degraded providers; native runtime/smoke validation has not run on those OS hosts. | macOS and Linux validation hosts. |

## Internal Phase Gaps

| Gap | Phase | Current Evidence | Next Work |
|---|---|---|---|
| Shell keyboard and interactive visual diff matrix | P4 | UI gate, module snapshots, and Shell snapshot matrix pass. | Add interactive screenshot diff matrix and keyboard navigation audit. |
| Module-specific deep editors | P4 | Generic Shell pages cover module status, commands, settings, logs, permissions, packages, and diagnostics. | Add focused editors for AndroidTools, AdbForwarder, ScreenEase, Doubao Agent, and SmartBird. |
| Broader ModuleSupervisor health policy automation | P5 | Crash recovery, restart throttling, process policy, and diagnostics tests exist. | Expand long-running health policy automation and operational reporting. |
| MSI/MSIX or package-manager distribution | P6 | Windows portable zip and install/uninstall scripts exist. | Add signed installer or package-manager metadata. |

## Skipped Tests

None. Latest test run: 74 passed, 0 failed, 0 skipped.
