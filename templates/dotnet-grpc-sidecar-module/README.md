# .NET gRPC Sidecar Module Template

Build:

```powershell
dotnet build .\Sample.DotNetGrpcSidecar.MyPowerTools.csproj -c Release
```

Validate from the repository root:

```powershell
dotnet run --project src\MyPowerTools.Cli -- validate templates\dotnet-grpc-sidecar-module
dotnet run --project src\MyPowerTools.Cli -- ui check templates\dotnet-grpc-sidecar-module
```
