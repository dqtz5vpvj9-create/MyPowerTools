# MyPowerTools

MyPowerTools is a PowerToys-style personal tools platform built on .NET SDK 10, an Avalonia Shell, a long-running Runner control plane, typed gRPC protocols, and package-based modules.

The project is designed for local, long-term use: modules register through manifests, commands and UI surfaces are indexed by the Runner, privileged actions go through brokers, and the Shell talks to the Runner through HostControl IPC.

## Current Capabilities

- Runner / Shell split with HostControl gRPC over local IPC.
- Runner tray integration on Windows with Open Shell and Quit Runner actions, plus explicit degraded tray services on macOS/Linux.
- Shell smoke mode validates HostControl IPC without opening the interactive Avalonia window.
- Shell subscribes to the HostControl event stream, resumes by event sequence after stream faults, and refreshes affected pages from Runner snapshots.
- Typed module protocol and host-control protocol from `proto/`.
- Transport tiers: static manifests, trusted InProc .NET modules, gRPC IPC sidecars, HTTP facades, and stdio compatibility.
- Package registry, command index, settings store with revision protection, event bus, notification center, log router, broker audit, package hash manifests, and local package trust hooks.
- Avalonia Shell pages for Dashboard, Modules, Settings, Logs, Notifications, Packages, Diagnostics, command palette, broker permission prompt, and broker audit.
- CLI commands for validate, inspect, run, diagnostics, module list/enable/disable, package hash, package sign-local, package trust, install, uninstall, update, rollback, repair, UI gate, UI snapshots, broker audit, broker portproxy, broker secret self-test, and doctor.
- Runner autostart status, enable, and disable flow through `AutostartBroker`, the Windows HKCU Run provider, and broker audit.
- Module capability requirements and declared permissions are exposed through typed HostControl IPC, visible on Shell module pages, and inspectable through `mpt inspect modules`.
- Package trust state, local policy, signature hook path, trust issue counts, and package lifecycle operations are exposed through HostControl and the Shell Packages page.
- SecretBroker stores sensitive values through platform secret stores. Windows uses Credential Manager, tests use an in-memory provider, and macOS/Linux expose compile-ready degraded providers.
- Runtime diagnostics report active gRPC IPC process pools with pool key, PID, endpoint, start count, restart limit, last start time, and module membership.
- Runtime process controls can restart, pause, and resume gRPC IPC pools through Shell Diagnostics or `mpt runner process <restart|pause|resume>`.
- Restart-policy decisions persist under the runtime state root and appear in Runtime Diagnostics with source-aware policy history and optional maintenance-window expiry.
- UI snapshots write paired contract JSON and deterministic PNG pixel artifacts with SHA256 and nonblank image metrics.
- Module contract validation checks every production module for schema validity, dashboard/settings/log surfaces, indexed commands, typed health state, runtime settings schema, and log provider readiness.
- Six validated module templates for .NET InProc, .NET gRPC sidecar, Python gRPC sidecar, HTTP facade, WebView, and stdio compatibility modules.
- Windows self-contained portable publish, install/uninstall scripts, and release notes generation.

## Production Modules

| Package | Modules | State |
|---|---|---|
| `android-tools-suite` | `android-tools.notifications`, `android-tools.remote-commands`, `android-tools.process-monitor` | Shared InProc facade, command import, notification diagnostics, process watch scanning. |
| `adb-forwarder` | `adb-forwarder` | ADB diagnostics, Windows portproxy inspection, brokered apply/revert plan with rollback. |
| `screenease` | `screenease` | Display enumeration, profile list/plan/apply/save, rules status, and Windows DDC/CI native writer probing for brightness/color-temperature hardware changes. |
| `doubao-agent` | `doubao-agent` | InProc controller with planner/tool/MCP health separation, self-test, settings schema, and logs summary. |
| `smartbird-thermostat` | `smartbird-thermostat` | InProc typed facade for HTTP status, events, config, logs, brokered restart, and degraded hardware diagnostics. |

## Requirements

- .NET SDK `10.0.301`, locked by `global.json`.
- PowerShell 7 (`pwsh.exe`) for scripts.
- Windows for the current production publish path.

## Build And Test

```powershell
dotnet restore MyPowerTools.slnx
dotnet build MyPowerTools.slnx --no-restore
dotnet test MyPowerTools.slnx --no-build
dotnet run --project src\MyPowerTools.Cli -- validate modules
dotnet run --project src\MyPowerTools.Cli -- validate contracts
dotnet run --project src\MyPowerTools.Cli -- package sign-local modules
dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict
dotnet run --project src\MyPowerTools.Cli -- ui check modules
dotnet run --project src\MyPowerTools.Cli -- ui snapshot --surface dashboard-card --theme light --size 1366x768 --density normal --out artifacts\ui-snapshots
dotnet run --project src\MyPowerTools.Cli -- runner autostart status
dotnet run --project src\MyPowerTools.Cli -- broker secret self-test
dotnet run --project src\MyPowerTools.Runner -- --once
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\smoke.ps1
```

## Run

Start the Runner once to validate module indexing:

```powershell
dotnet run --project src\MyPowerTools.Runner -- --once
```

Start the Shell:

```powershell
dotnet run --project src\MyPowerTools.Shell.Avalonia
```

Validate Shell-to-Runner IPC after Runner is running:

```powershell
dotnet run --project src\MyPowerTools.Shell.Avalonia -- --smoke --timeout-ms 30000
```

When the smoke process owns the Runner lifecycle, add `--quit-runner` to request graceful shutdown through HostControl after validation.

When the Runner is started normally on Windows, it registers a tray icon with actions to open the Shell or quit the Runner. Use `--no-tray` for headless runs:

```powershell
dotnet run --project src\MyPowerTools.Runner -- --no-tray
```

Run a module command through the CLI:

```powershell
dotnet run --project src\MyPowerTools.Cli -- run screenease.status.summary
```

Manage module enable state through the Runner state store:

```powershell
dotnet run --project src\MyPowerTools.Cli -- module list --include-disabled
dotnet run --project src\MyPowerTools.Cli -- module disable doubao-agent
dotnet run --project src\MyPowerTools.Cli -- module enable doubao-agent
```

Manage current-user Runner autostart through the brokered Windows provider:

```powershell
dotnet run --project src\MyPowerTools.Cli -- runner autostart status
dotnet run --project src\MyPowerTools.Cli -- runner autostart enable
dotnet run --project src\MyPowerTools.Cli -- runner autostart disable
```

## Publish

Create the Windows portable package and release notes:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\publish-windows.ps1
```

Outputs:

- `artifacts/release/win-x64/`
- `artifacts/release/MyPowerTools-win-x64.zip`
- `artifacts/release/RELEASE_NOTES.md`
- `artifacts/release/win-x64/templates/`

The zip root includes `install-windows.ps1` and `uninstall-windows.ps1`. After extracting the zip, install the portable app for the current user:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\install-windows.ps1 -EnableAutostart -StartRunner
```

The default install directory is `%LOCALAPPDATA%\Programs\MyPowerTools`; runtime data stays under `%LOCALAPPDATA%\MyPowerTools`. Uninstall the app:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\uninstall-windows.ps1
```

Validate install/uninstall plans from the repo without changing the machine:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1 -PackageRoot artifacts\release\win-x64 -InstallDir artifacts\install-dryrun -DryRun
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\uninstall-windows.ps1 -InstallDir artifacts\install-dryrun -DryRun -Force
```

## Module Package Lifecycle

Package install, uninstall, rollback, and repair use the CLI package store. The Shell Packages page uses the same HostControl package operations against the Runner package root:

```powershell
dotnet run --project src\MyPowerTools.Cli -- install tests\fixtures\modules\sample-dotnet
dotnet run --project src\MyPowerTools.Cli -- uninstall sample-dotnet
dotnet run --project src\MyPowerTools.Cli -- rollback sample-dotnet
dotnet run --project src\MyPowerTools.Cli -- repair sample-dotnet
```

Package integrity and local trust hooks are explicit CLI steps:

```powershell
dotnet run --project src\MyPowerTools.Cli -- package hash modules
dotnet run --project src\MyPowerTools.Cli -- package sign-local modules
dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict
```

`package sign-local` writes `shared/package.signature.json` with the hash-manifest SHA256 and future algorithm slots. `install` verifies the package hash manifest and trust policy before copying, then writes a local signature hook into the package store.

Production modules are loaded from `modules/` during local development and from the release package in portable builds.

Module enable state is persisted under the runtime data root in `state/modules.enabled.json`. Disabled modules remain visible on the Shell Modules page and `mpt module list --include-disabled`, while Dashboard cards and command palette entries only include enabled modules.

## Add A Module

1. Create a module directory with `module.json` or a multi-module `package.json`.
2. Add UI surface files under `ui/`.
3. Add static commands in `commands.index.json` when the module should appear before runtime startup.
4. Choose a transport entrypoint:
   - `inproc-dotnet`
   - `grpc-ipc`
   - `http`
   - `jsonrpc-stdio`
5. Run:

```powershell
dotnet run --project src\MyPowerTools.Cli -- validate modules
dotnet run --project src\MyPowerTools.Cli -- validate contracts
dotnet run --project src\MyPowerTools.Cli -- ui check modules
dotnet run --project src\MyPowerTools.Cli -- package sign-local modules
dotnet run --project src\MyPowerTools.Cli -- package trust modules --strict
dotnet run --project src\MyPowerTools.Runner -- --once
```

Templates live under `templates/`:

- `dotnet-inproc-module`
- `dotnet-grpc-sidecar-module`
- `python-grpc-sidecar-module`
- `http-facade-module`
- `webview-module`
- `stdio-compat-module`

Validate all templates:

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\validate-templates.ps1
```

## Architecture

```text
Shell.Avalonia -> HostControl IPC -> Runner
Runner -> PackageRegistry -> ModuleRegistry -> TransportSelector
Runner -> SettingsStore / EventBus / NotificationCenter / LogRouter
Runner -> Broker -> Platform Packs
Runner -> ModuleHost -> InProc .NET / gRPC IPC / HTTP / stdio
```

Key rules:

- The Shell reads module state through HostControl IPC.
- Modules integrate through manifests, commands, capabilities, and UI surfaces.
- Privileged actions flow through Broker abstractions and audit logs.
- Settings writes are owned by the Runner and guarded by revisions.
- UI surfaces use design tokens and MPT component contracts.

## Troubleshooting

Check SDK:

```powershell
dotnet --version
```

Check module manifests:

```powershell
dotnet run --project src\MyPowerTools.Cli -- validate modules
```

Check runtime module contracts:

```powershell
dotnet run --project src\MyPowerTools.Cli -- validate contracts
```

Check UI contracts:

```powershell
dotnet run --project src\MyPowerTools.Cli -- ui check modules
```

Inspect indexed packages and modules:

```powershell
dotnet run --project src\MyPowerTools.Cli -- inspect modules
dotnet run --project src\MyPowerTools.Cli -- diagnostics
dotnet run --project src\MyPowerTools.Cli -- doctor
```

`mpt inspect modules` prints each module's declared capabilities, required/optional platform capabilities, and broker permissions.

When a gRPC sidecar runtime is active, `mpt diagnostics` and the Shell Diagnostics page show its transport process pool, PID, endpoint, restart budget, restart policy, policy reason, policy expiry, attached modules, and recent process-policy history.

Restart an active gRPC IPC pool through the running Runner:

```powershell
dotnet run --project src\MyPowerTools.Cli -- runner process restart grpc-ipc module:sample.grpc
```

Pause and resume automatic restart for a gRPC IPC pool:

```powershell
dotnet run --project src\MyPowerTools.Cli -- runner process pause grpc-ipc module:sample.grpc --reason "maintenance"
dotnet run --project src\MyPowerTools.Cli -- runner process pause grpc-ipc module:sample.grpc --reason "maintenance" --duration-minutes 60
dotnet run --project src\MyPowerTools.Cli -- runner process pause grpc-ipc module:sample.grpc --until "<iso-8601-time>"
dotnet run --project src\MyPowerTools.Cli -- runner process resume grpc-ipc module:sample.grpc
```

The Shell Diagnostics page exposes Pause, Pause 1h, Resume, and Restart controls for active gRPC IPC pools.

Inspect broker audit:

```powershell
dotnet run --project src\MyPowerTools.Cli -- broker audit
```

Verify the local OS secret store without printing a secret value:

```powershell
dotnet run --project src\MyPowerTools.Cli -- broker secret self-test
```

Expected degraded states:

- ScreenEase hardware writes use the Windows DDC/CI native writer when explicitly enabled; monitors without DDC/CI brightness/color-temperature support return actionable hardware diagnostics.
- AndroidTools Process Monitor reports degraded until a watch list is saved.
- NetworkBroker portproxy apply requires administrator rights or an elevated helper.
- macOS/Linux secret providers compile and report unsupported until Keychain/Secret Service implementations are added.
