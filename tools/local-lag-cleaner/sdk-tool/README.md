# 本机卡顿专清 — MyPowerTools SDK Tool

这是可由 MyPowerTools 动态发现的 `dotnet-surface` 工具。

0.3.1 的分页诊断区分硬缺页读入与已证实的资源压力。Pages Input/sec 包括程序、DLL、
映射文件及分页文件读入。连续至少三个样本、且至少半数样本同时超过分页筛查线并伴随
可用内存/提交或磁盘延迟压力时才告警；孤立峰值、缺少关联计数均保留为信息。
它们无法直接归因到某个进程或证明前台等待。

每条发现均提供对应的处理方案、完成标准和恢复说明。“查看对应方案”进入修复页，
主按钮调用工具内执行流程，技术说明默认折叠。

首页“一键检查并处理”自动执行三轮采样：直接采集每个程序的硬缺页增量并校验进程身份，
对比内核池标签增长，清理有可信证据的旧 MCP 会话；连续两轮繁忙、没有可见窗口、
属于当前用户的受支持应用可降至 BelowNormal，写入恢复记录后调整并读回验证。
受支持列表限定为 WeFlow、OneDrive、Dropbox、Edge、Chrome、Firefox。
可通过“撤销后台调度调整”恢复；原进程已退出或身份变化时跳过。

临时文件执行器只处理当前用户 LocalAppData/Temp 内、创建和修改均超过七天、
扩展名为 .tmp/.temp 且可独占打开的文件。跳过重解析点，单次最多检查 20,000 个条目，
单文件不超过 512 MiB，总量约 2 GiB。删除以文件句柄关闭为边界，保留近期文件与其他类型。

操作完成后重新采样并显示实际变化，区分已处理、无需处理和未解决。当前内核池流程
可以自动对比增长，尚不能从池标签直接确认或修复所有驱动泄漏。分页文件/驱动调整与
重启仍需要独立实现和验收，界面不能将它们标成已修复。

计数器语义参考：[Microsoft 的硬缺页与分页文件说明](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows)。

- `tool.json` 声明 Tool SDK 清单、路由、权限和 Release 界面程序集。
- `LocalLagCleaner.Tool` 仅通过 NuGet 引用 `MyPowerTools.AvaloniaSdk` 与 `MyPowerTools.ToolSdk`。
- `LocalLagCleaner.Runtime` 通过 `stdio-jsonrpc` 隔离执行 PDH、Win32、事件日志、报告与处置。
- `LocalLagCleaner.Core` 负责多阶段诊断、趋势、报告和双确认清理协议。
- 工具项目没有指向 MyPowerTools Suite 源码项目的引用。

构建与验证：

```powershell
dotnet build .\src\LocalLagCleaner.Tool\LocalLagCleaner.Tool.csproj -c Release
dotnet build .\src\LocalLagCleaner.Runtime\LocalLagCleaner.Runtime.csproj -c Release
dotnet ..\..\..\artifacts\build\bin\MyPowerTools.Cli\release\MyPowerTools.Cli.dll validate tool .
```

把本目录加入 `%LOCALAPPDATA%\MyPowerTools\settings\tool-directories.json`，随后在
MyPowerTools 中执行 **Refresh tools**。
