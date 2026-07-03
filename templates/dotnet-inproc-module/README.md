# .NET InProc Module Template

Build:

```powershell
dotnet build .\Sample.DotNetInProc.MyPowerTools.csproj -c Release
```

Validate from the repository root:

```powershell
dotnet run --project src\MyPowerTools.Cli -- validate templates\dotnet-inproc-module
dotnet run --project src\MyPowerTools.Cli -- ui check templates\dotnet-inproc-module
```
