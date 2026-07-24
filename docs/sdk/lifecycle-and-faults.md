# Lifecycle and fault model

Discovery parses and validates the manifest without starting the runtime. Opening or invoking a tool transitions it through `discovered`, `starting`, `ready`, `degraded`, `failed`, and `stopped` states.

Runtime boundaries:

- `web-surface`: Web content runs in `MyPowerTools.WebToolHost.exe` through the Shell's SDK host-capability implementation. Navigation failure or WebView failure produces a recovery page while Shell stays alive. Optional command failures keep a ready surface visible.
- `dotnet-surface`: the UI factory loads in the Shell process. Factory construction and surface creation are caught at the route boundary. Long-running and failure-prone logic belongs in an external runtime.
- `native-tool`: Shell launches the declared native entry point; process exit does not close Shell.
- `headless-tool`: Runner supervises the declared transport and publishes state, logs, commands, and events.

Long-running Service Units use a separate boundary. `MyPowerTools.ServiceManager.exe` owns activation, restart policy, readiness, logs, scoped access, and re-adoption. Shell and Runner restarts leave an active unit process alive. A restarted ServiceManager validates the recorded executable path, PID start time, and instance token before re-adopting it. Manifest reload stops and replaces changed units, and stops removed units.

ServiceManager opens its authenticated control pipe before startup reconciliation. A worker that consumes its full readiness timeout remains diagnosable through list/status/log RPCs. Responsive framed `ping` probes enter `Active`; timed-out probes enter `Degraded` while the process remains available for inspection or an explicit restart.

Tool Surfaces receive an `IServiceUnitClient` scoped by `toolId`. The server rejects access to foreign units. The lifecycle snapshot exposes the effective readiness address so the tool's typed client can reach an instance-suffixed current-user-only pipe without hard-coded deployment assumptions.

HTTP runtimes use timeouts and explicit health endpoints. stdio runtimes keep stdout reserved for protocol messages and stderr for diagnostics. gRPC runtimes use named pipes on Windows and Unix domain sockets elsewhere.

Removing a discovered directory and clicking **Refresh tools** removes its catalog entry. Active out-of-process runtimes are stopped during catalog replacement.
