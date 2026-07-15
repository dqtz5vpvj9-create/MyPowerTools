# Dotnet Surface

Reference the local SDK packages only:

```xml
<PackageReference Include="MyPowerTools.AvaloniaSdk" Version="0.2.0" />
<PackageReference Include="MyPowerTools.ToolSdk" Version="0.2.0" />
```

Implement `IMptAvaloniaSurfaceFactory` and return an Avalonia `Control`. `MptAvaloniaSurfaceContext` provides theme, data directory, controlled navigation, command invocation, and logging. The tool project must have no `ProjectReference` to the Suite repository.

```powershell
mypowertools create tool --type dotnet --id example.dotnet --output C:\src\example-dotnet
dotnet build C:\src\example-dotnet
mypowertools validate tool C:\src\example-dotnet
```

Keep device access, network daemons, and crash-prone work in an external runtime. The factory should construct UI quickly and tolerate repeated creation.
