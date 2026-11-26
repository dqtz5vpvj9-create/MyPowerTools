# Dotnet Surface

Reference the local SDK packages only:

```xml
<PackageReference Include="MyPowerTools.AvaloniaSdk" Version="0.3.0" />
<PackageReference Include="MyPowerTools.ToolSdk" Version="0.2.0" />
```

Implement `IMptAvaloniaSurfaceFactory` and return an Avalonia `Control`. `MptAvaloniaSurfaceContext` provides theme, data directory, controlled navigation, command invocation, logging, host events, and the optional `WebSurfaces` capability. The tool project must have no `ProjectReference` to the Suite repository.

```powershell
mypowertools create tool --type dotnet --id example.dotnet --output C:\src\example-dotnet
dotnet build C:\src\example-dotnet
mypowertools validate tool C:\src\example-dotnet
```

Keep device access, network daemons, and crash-prone work in an external runtime. The factory should construct UI quickly and tolerate repeated creation.

Surfaces opened from an operating-system protocol or notification can implement `IMptAvaloniaSurfaceActivationHandler`. The Shell first navigates to the `ToolActivationRequest.ToolId` and `RouteId`, then calls `ActivateAsync` on the loaded control. The activation URI is owned and validated by the tool, which keeps protocol-specific parsing outside the Shell.

Version 0.3.0 adds `IMptAvaloniaSurfaceBackHandler.TryHandleBack()`, so a loaded surface can close its sheet before the host leaves the tool. The context also exposes optional host delegates: `ScanConnectionCodeAsync` opens the native scanner, `OpenFileAsync` launches a platform file viewer, and `ExecuteCommandWithInvocationAsync` preserves a caller's invocation ID through the existing command and permission path. Check whether each delegate is available. These members require a host running SDK 0.3.0; an older SDK does not contain them.

Mobile pages use the scoped `MptMobile*` resources and classes from `MptMobileTheme.axaml`. `MyPowerTools.AvaloniaSdk.Controls.MptQrCode` renders a full connection code through its bindable `Value` property. Only fetch and show a credential-bearing code when the user opens its connection panel; clear it when the panel closes. Do not put the code in logs, automation labels, or diagnostic text. The SDK uses the same managed `ZXing.Net` 0.16.10 dependency as the Android scanner.

When a custom dotnet surface embeds web content, create an `IMptWebSurfaceSession` through `context.WebSurfaces`, place `session.View` in the tool layout, forward `StateChanged` into the tool ViewModel, and dispose the session with the surface. Keep WebToolHost paths, process control, native HWND handling, and overlay visibility inside the host implementation.
