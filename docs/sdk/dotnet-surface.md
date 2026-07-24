# Dotnet Surface

Reference the local SDK packages only:

```xml
<PackageReference Include="MyPowerTools.AvaloniaSdk" Version="0.2.0" />
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

When a custom dotnet surface embeds web content, create an `IMptWebSurfaceSession` through `context.WebSurfaces`, place `session.View` in the tool layout, forward `StateChanged` into the tool ViewModel, and dispose the session with the surface. Keep WebToolHost paths, process control, native HWND handling, and overlay visibility inside the host implementation.
