# 本机卡顿专清 — MyPowerTools SDK Tool

这是可由 MyPowerTools 动态发现的 `dotnet-surface` 工具。

- `tool.json` 声明 Tool SDK 清单、路由、权限和 Release 界面程序集。
- `LocalLagCleaner.Tool` 仅通过 NuGet 引用 `MyPowerTools.AvaloniaSdk` 与 `MyPowerTools.ToolSdk`。
- `LocalLagCleaner.Runtime` 通过 `stdio-jsonrpc` 隔离执行 PDH、Win32、事件日志、报告与处置。
- `LocalLagCleaner.Core` 负责多阶段诊断、趋势、报告和双确认清理协议。
- 工具项目没有指向 MyPowerTools Suite 源码项目的引用。

构建与验证：

```powershell
dotnet build .\src\LocalLagCleaner.Tool\LocalLagCleaner.Tool.csproj -c Release
dotnet build .\src\LocalLagCleaner.Runtime\LocalLagCleaner.Runtime.csproj -c Release
& ..\..\..\src\MyPowerTools.Cli\bin\Release\net10.0\MyPowerTools.Cli.exe validate tool .
```

把本目录加入 `%LOCALAPPDATA%\MyPowerTools\settings\tool-directories.json`，随后在
MyPowerTools 中执行 **Refresh tools**。
