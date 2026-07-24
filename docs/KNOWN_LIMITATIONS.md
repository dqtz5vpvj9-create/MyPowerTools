# Known Limitations

Run date: 2026-07-11.

P-UI-Foundation UI acceptance is complete on the current Windows host. The limitations below remain external after UI closure; they still require credentials, hardware, services, or host operating systems.

| Limitation | Why It Remains External | Current Local Behavior |
|---|---|---|
| `inproc-dotnet` modules share the Runner's fatal process fault domain. | A collectible `AssemblyLoadContext` cannot contain native access violations, `StackOverflowException`, `Environment.FailFast`, process termination, runaway native threads, memory corruption, or process-wide resource exhaustion. | Awaited contract callbacks have call budgets, cancellation generations, ordered fault accounting, a three-fault circuit breaker, bounded quarantine, and recovery after a verified clean unload. A callback that ignores cancellation is blocked from receiving new work and reports that a Runner restart is required. WebView/native UI is hosted out of process. Diagnostics label this boundary `soft-in-process`. |
| Live Windows portproxy apply/revert requires administrator context or a signed elevated helper. | Normal user processes cannot write system portproxy/firewall state. | Brokered portproxy commands return `permission-required` with audit evidence, expected-change details, and rollback planning. |
| Production package/installer signing requires private signing material or a signing service. | No production certificate or signing service is available in the repository. | Local `package.signature.json` trust hooks, release metadata, and package trust verification pass. |
| ScreenEase hardware writes require a DDC/CI-capable monitor. | Current monitor reports unsupported DDC/CI capabilities. | Display enumeration, profile planning, native writer probing, and safe default no-write behavior are verified. |
| SmartBird manual hardware actions require the installed switch, Energy Server/HID meter, and ADB thermal targets. | Those actions query or change attached devices. | The original dashboard is embedded; read-only status/events/energy probes, task recovery, logs, redaction, fallback and same-origin policy are verified. |
| AndroidTools device notification/command flows require connected ADB devices and notification service state. | End-to-end device behavior depends on local Android devices and services. | powertoold, command import, notification diagnostics, process-monitor watch-list behavior, and local degraded paths are verified. |
| Doubao planner/tool/MCP production endpoint validation requires documented running services. | Local service contracts are external to this repository. | Role-specific health checks and partial outage behavior are verified. |
| macOS/Linux native validation requires macOS and Linux hosts. | Managed macOS arm64/x64 publishing and automated tests ran on Windows. | macOS still needs native dylib, codesign, WKWebView, Codex quota status item, UserNotifications, NSPasteboard, launchd, and UDS smoke; Linux still needs native runtime smoke. |
| Legacy secret migration depends on existing user installations. | No legacy installation data is available in the repository. | New secrets use `secret://module/name` references and platform secret stores; CLI self-test avoids printing secret values. |
