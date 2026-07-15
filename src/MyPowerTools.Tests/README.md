# MyPowerTools Tests

`RuntimeAcceptanceTests.cs` keeps shared fixtures, helper types, and cross-test utilities for the acceptance suite.

Acceptance test methods are split by domain:

- `RuntimeAcceptanceTests.SchemaPolicy.Tests.cs` - schemas, runtime policy, transport selection, contract validation.
- `RuntimeAcceptanceTests.CliRelease.Tests.cs` - CLI flows and release metadata generation.
- `RuntimeAcceptanceTests.ShellUi.Tests.cs` - Avalonia shell view models, AXAML wiring, snapshots, and UI lint checks.
- `RuntimeAcceptanceTests.RuntimeCore.Tests.cs` - runtime indexing, command execution, events, and core behavior.
- `RuntimeAcceptanceTests.Settings.Tests.cs` - settings persistence, validation, apply, rollback, and HostControl/Shell settings paths.
- `RuntimeAcceptanceTests.PlatformBrokerPackage.Tests.cs` - brokers, package store/trust, platform packs, hotkeys, tray, and logging.
- `RuntimeAcceptanceTests.InProcContracts.Tests.cs` - in-process soft isolation, circuit breaking, collectible unload, SDK boundaries, and module contract checks.
- `RuntimeAcceptanceTests.SidecarInterop.Tests.cs` - HTTP facade, gRPC IPC sidecar, restart policy, shared runtime, and powertoold flows.
- `RuntimeAcceptanceTests.ProductionModules.Tests.cs` - production module behavior for AdbForwarder, AndroidTools, ScreenEase, DoubaoAgent, and SmartBird.
- `RuntimeAcceptanceTests.HostControl.Tests.cs` - HostControl service, event stream, runner control, and process diagnostics.
- `Runtime.PFoundation6.Tests.cs` - P-Foundation-6 lifecycle, event pump, hotkey persistence, sidecar readiness, typed args, and Shell hotkey patch coverage.
- `Runtime.PFoundation7.Tests.cs` - P-Foundation-7 hotkey re-registration, notification refresh, cancellation evidence, stream crash handling, bounded invocation cache, lifecycle diagnostics, and component-layer UI lint coverage.
