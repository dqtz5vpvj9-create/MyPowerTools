# Lifecycle and fault model

Discovery parses and validates the manifest without starting the runtime. Opening or invoking a tool transitions it through `discovered`, `starting`, `ready`, `degraded`, `failed`, and `stopped` states.

Runtime boundaries:

- `web-surface`: Web content runs in `MyPowerTools.WebToolHost.exe`. Navigation failure or WebView failure produces a recovery page while Shell stays alive.
- `dotnet-surface`: the UI factory loads in the Shell process. Factory construction and surface creation are caught at the route boundary. Long-running and failure-prone logic belongs in an external runtime.
- `native-tool`: Shell launches the declared native entry point; process exit does not close Shell.
- `headless-tool`: Runner supervises the declared transport and publishes state, logs, commands, and events.

HTTP runtimes use timeouts and explicit health endpoints. stdio runtimes keep stdout reserved for protocol messages and stderr for diagnostics. gRPC runtimes use named pipes on Windows and Unix domain sockets elsewhere.

Removing a discovered directory and clicking **Refresh tools** removes its catalog entry. Active out-of-process runtimes are stopped during catalog replacement.
