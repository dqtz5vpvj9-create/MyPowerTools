# Open Blockers And Gaps

Run date: 2026-07-04.

This file separates true external blockers from internal phase gaps. External blockers require hardware, credentials, administrator context, signing material, native OS access, or external services. Internal gaps remain normal engineering work for later phases.

## External Blockers

| Blocker | Affected Phase | Evidence | Required External State |
|---|---|---|---|
| Administrator context or elevated helper for live Windows portproxy writes | P3 | `adb-forwarder.portproxy.apply` and CLI `broker portproxy apply` correctly return `permission-required` in normal user context; NetworkBroker tests cover rollback and brokered execution paths; `PrivilegedBroker` recognizes elevated/service/sensitive levels. | Administrator token, UAC/helper service packaging, or signed elevated helper. |
| Private signing material for production package/installer signing | P6 | Local trust hook exists; release uses local signature metadata, release/update metadata, and a Scoop package-manager manifest. | Code-signing/private package signing key or signing service. |
| Hardware validation for display writes | P2, P7 | ScreenEase Windows DDC/CI writer is implemented and wired behind explicit hardware apply; current Generic PnP Monitor returns `GetMonitorCapabilities` unsupported. | Monitor hardware that supports DDC/CI brightness/color-temperature writes. |
| SmartBird hardware/service ecosystem validation | P2 | SmartBird InProc typed facade reads HTTP status/events/config/logs, returns brokered restart details, and reports current degraded Energy Server/FNB-58 state. | Connected SmartBird device, FNB-58 meter, Energy Server, and ADB services. |
| ADB and AndroidTools device/service validation beyond local module contracts | P2 | AdbForwarder diagnostics run; AndroidTools powertoold serves notifications, remote commands, and process monitor locally through `grpc-ipc`. Invalid notification config and empty watch-list degraded paths are covered by acceptance tests. Device-specific notification polling/streaming and command flows depend on local Android devices and services. | Connected ADB devices, notification service state, and expected local command catalog. |
| Doubao planner/tool/MCP endpoint contract validation | P2 | InProc controller checks ports 38102, 38080, and 38189 separately; current local services expose 404 on planner/tool health paths and 200 on MCP. Role-specific partial outage behavior is covered by acceptance tests. | Running Doubao services with documented production health/status APIs for each role. |
| Native macOS/Linux runtime validation | P7 | macOS/Linux projects compile with explicit degraded providers for hotkey, privilege, notification, autostart, service, network, display, tray, and secret surfaces; `ILocalIpc` returns UDS endpoint shapes; tests verify degraded service behavior. Native runtime/smoke validation has not run on those OS hosts. | macOS and Linux validation hosts. |

## Internal Phase Gaps

| Gap | Phase | Current Evidence | Next Work |
|---|---|---|---|
| None for P0-P7 | - | P7 now exposes the planned platform service surfaces with explicit unsupported/broker-required behavior, and the P7 validation matrix passed locally. | Continue P8 final production closure. |

## Skipped Tests

None. Latest test run: 88 passed, 0 failed, 0 skipped.
