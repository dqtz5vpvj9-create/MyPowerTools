# Service Unit Development

A **Service Unit** is a long-running worker process supervised by `MyPowerTools.ServiceManager`. Its life is independent of the Shell and the Runner: closing the Shell window, recycling the Runner, or even restarting the ServiceManager never ends a unit process — the next ServiceManager instance re-adopts the still-running process via its `instanceToken`.

Use a Service Unit when a tool must keep doing work while no UI is open:

- **Remote Notifications** polls a signed endpoint, dedups, persists history and raises Windows toasts whether or not the Shell is visible.
- **ScreenEase** owns the eye-care loop so it survives Shell restarts.
- **Doubao Computer Use** supervises the Planner / Tool Runtime / MCP Bridge subprocesses with a crash-watchdog and a cached health snapshot so the Surface renders with zero UI-thread network waits.

## Anatomy

A Service Unit is a self-contained console executable plus a `unit-manifest.json`:

```
tools/<toolid>/current-integration/src/<Tool>.Service/
  <Tool>.Service.csproj   # OutputType Exe, net10.0, self-contained
  Program.cs              # Readiness pipe + the long-running loop
  unit-manifest.json      # id, toolId, exec, arguments, readiness, restartPolicy, dataRoots
```

The executable:

1. Opens a **current-user-only named pipe** (default `<toolid>.core`) speaking the Chromium native-messaging wire format (4-byte little-endian length header + UTF-8 JSON body). It answers `ping` with `{"ok":true,"data":{"pong":true}}` — this is the readiness probe and the tool-owned business channel.
2. Runs its real work loop on a timer (poll, supervise, health-probe), persisting state to files/registry under the tool's `dataRoot`.
3. Writes a heartbeat to stdout (captured by `UnitLogStore`) and an optional heartbeat file.
4. Handles `Ctrl+C` / `ProcessExit` to exit cleanly.

## unit-manifest.json

```json
{
  "id": "screenease.service",
  "toolId": "screenease",
  "displayName": "ScreenEase Service",
  "exec": "ScreenEase.Service.exe",
  "arguments": ["--pipe", "screenease.core", "--heartbeat-file", "%LOCALAPPDATA%/MyPowerTools/state/screenease.service.heartbeat", "--instance-token", "screenease-service-v1"],
  "workingDirectory": "",
  "autostart": true,
  "restartPolicy": { "maxRestarts": 5, "backoffMs": 2000 },
  "readiness": { "kind": "pipe", "address": "screenease.core", "timeoutMs": 8000 },
  "stopTimeoutMs": 5000,
  "dataRoots": ["%LOCALAPPDATA%/MyPowerTools/screenease"],
  "dependsOn": [],
  "instanceToken": "screenease-service-v1"
}
```

Key fields:

- `exec` — bare filename; the installer rewrites it to the versioned absolute path at deploy time.
- `arguments` — `--pipe`, `--heartbeat-file`, `--instance-token` are rewritten by the installer for isolated test instances (the pipe name and token get a `.<instance>` suffix so concurrent test/daily runs never collide).
- `readiness.kind: "pipe"` — ServiceManager probes this endpoint and returns its effective `kind`, instance-specific `address`, and `timeout` to the owning tool's scoped client.
- `restartPolicy` — maximum automatic restart attempts and delay between attempts before ServiceManager marks the unit failed.
- `instanceToken` — lets a restarting ServiceManager re-adopt the still-running process (PID-stable across manager restarts).
- `dataRoots` — tool-owned data directories; preserved on default uninstall, purged only on explicit data deletion through declared + boundary-validated roots.

## Registering a unit for the installer

Declare the unit in the tool's registry entry in `scripts/build-all-tools.ps1`:

```powershell
[pscustomobject]@{
    Id = 'your-tool'
    ...
    ServiceUnits = @(
        [pscustomobject]@{
            Project = 'tools\your-tool\current-integration\src\YourTool.Service\YourTool.Service.csproj'
            Manifest = 'tools\your-tool\current-integration\src\YourTool.Service\unit-manifest.json'
            UnitId   = 'your-tool.service'
        }
    )
}
```

`build-all-tools.ps1` publishes each declared unit (self-contained, win-x64) into `artifacts/tools/<id>/<ver>/service-units/<unit-id>/`. The installer (`build-installer.ps1`) then discovers **all** `service-units/*/` directories dynamically — no tool id is hard-coded in the installer. Adding or removing a unit only requires editing the registry entry; the installer and the deploy script adapt automatically.

## How the Surface reads state and invokes business operations

The Surface uses two deliberately separate contracts:

1. The scoped `IServiceUnitClient` (from `MyPowerTools.Abstractions.ServiceUnitContracts`), injected by the host into `MptAvaloniaSurfaceContext.ServiceUnits`, owns lifecycle and discovery:

- `ListAsync` / `GetSnapshotAsync` — point-in-time snapshots (`State`, `Pid`, `Uptime`, `RestartCount`).
- `SubscribeEventsAsync(cursor)` — streams state-change events.
- `RestartAsync(unitId)` — lifecycle control scoped to the owning tool.

2. The tool-owned typed client sends business commands over the effective readiness endpoint from `ServiceUnitSnapshot.Readiness.Address`. This address may carry an installer/run suffix, so clients must prefer it over a compiled default. Named-pipe clients and servers use `PipeOptions.CurrentUserOnly`. Tokens, secrets, and full credential-bearing endpoints stay out of stdout and ServiceManager logs.

ServiceManager never interprets tool business messages. The split keeps lifecycle policy generic while each tool can evolve its native protocol independently.

See `tools/screenease/current-integration/src/ScreenEase.Surface/Services/ScreenEaseToolService.cs` for the canonical Surface-side consumer.

## Verifying a unit

Each first-party unit ships a liveness gate under `scripts/verify-<toolid>-service.ps1` that proves:

1. The worker publishes and starts.
2. The readiness pipe answers `ping`.
3. A real command (inject/inspect/state) works over the framed protocol.
4. A ServiceManager restart re-adopts the unit with an **unchanged PID** (the process survived).
5. Exactly one instance exists (no duplicate spawn).
6. The pipe still answers after the restart.

Run them in isolation — they use unique pipe names and isolated TEMP roots and never touch the user's daily units.
