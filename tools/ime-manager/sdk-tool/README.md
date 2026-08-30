# 输入法管理器 — MyPowerTools SDK Tool

这是可由 MyPowerTools 动态发现的 `dotnet-surface` 工具。

- `tool.json` 声明 Tool SDK 清单、路由、权限和 Release 界面程序集。
- `ImeManager.Tool` 仅通过 NuGet 引用 `MyPowerTools.AvaloniaSdk` 与 `MyPowerTools.ToolSdk`。
- `ImeManager.Runtime` 通过 `stdio-jsonrpc` 隔离调用 Windows 输入法 API。
- `ImeManager.Core` 负责标识解析、草稿校验和当前用户输入法列表读写。
- 工具项目没有指向 MyPowerTools Suite 源码项目的引用。

构建与验证：

```powershell
dotnet build .\src\ImeManager.Tool\ImeManager.Tool.csproj -c Release
dotnet build .\src\ImeManager.Runtime\ImeManager.Runtime.csproj -c Release
```
