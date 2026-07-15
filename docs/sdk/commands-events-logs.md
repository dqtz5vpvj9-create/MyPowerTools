# Commands, events, and logs

Commands have stable IDs, titles, descriptions, and an optional HTTP method/path. The Shell command palette and tool page use the same descriptor. Invocation results include a request ID, state, output, and structured error.

A command may keep its runtime execution contract while declaring a separate command-palette activation. Put a nested `execution.activation` object with `type: "navigation"`, `toolId`, `routeId`, and optional `routeArgs` in `commands.index.json`. The palette navigates generically; invocation from the tool Surface still uses the outer runtime execution such as `broker.request`. This removes tool IDs and command IDs from Shell source.

Events include `eventId`, monotonic `sequence`, `toolId`, `topic`, time, and JSON payload. Runner owns subscriptions for the full process lifetime, so tools keep producing events while the user visits another page.

Logs use time, level, category, message, and optional JSON properties. HTTP tools expose the declared `logsPath`; stdio and gRPC tools stream logs through their protocol. Put protocol frames on stdout and free-form diagnostics on stderr.

Recommended command IDs are `<tool>.health`, `<tool>.refresh`, `<tool>.open-external`, and `<tool>.tail-logs`.
