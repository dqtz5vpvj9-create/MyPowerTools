# Known Limitations

Run date: 2026-07-04.

Only external limitations are listed here. The P8 production closure audit found no remaining internal limitation after final validation.

| Limitation | Why It Remains External | Current Local Behavior |
|---|---|---|
| Live Windows portproxy apply/revert requires administrator context or a signed elevated helper. | Normal user processes cannot write system portproxy/firewall state. | Brokered portproxy commands return `permission-required` with audit evidence instead of reporting success. |
| Production package/installer signing requires private signing material or a signing service. | No production certificate or signing service is available in the repo. | Local `package.signature.json` trust hooks and release metadata are generated and verified. |
| ScreenEase hardware writes require a DDC/CI-capable monitor. | Current monitor reports unsupported DDC/CI capabilities. | Display enumeration, profile planning, native writer probing, and safe default no-write behavior are verified. |
| SmartBird full hardware validation requires device, Energy Server, FNB-58, and ADB environment. | Required hardware/services are outside the repository. | Module status, events, config, logs, restart request, redaction, and degraded diagnostics are verified. |
| AndroidTools device notification/command flows require connected ADB devices and notification service state. | End-to-end device behavior depends on local Android devices and services. | powertoold, command import, notification diagnostics, and process-monitor degraded paths are verified. |
| Doubao planner/tool/MCP production endpoint validation requires documented running services. | Local service contracts are external to this repo. | Role-specific health checks and partial outage behavior are verified. |
| macOS/Linux native validation requires macOS and Linux hosts. | Current validation ran on Windows. | Platform packs compile with explicit degraded providers and UDS endpoint selection tests. |
| Legacy secret migration depends on existing user installations. | No legacy installation data is available in the repo. | New secrets use `secret://module/name` references and platform secret stores; CLI self-test avoids printing secret values. |
