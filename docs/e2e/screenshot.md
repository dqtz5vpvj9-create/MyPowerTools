# Screenshot 流程验证

运行：`npx e2e run tests/e2e/screenshot.e2e.ts --output .e2e/tools/screenshot`。
先构建 `src/MyPowerTools.Tests/MyPowerTools.Tests.csproj`（Debug）；测试桥接使用 no-build，TRX 必须证明所有预期测试实际执行并通过，0 测试或跳过均失败。

## 自动覆盖

两项 e2e 测试调度 26 项 .NET 产品行为回归：

- Windows 标准、WinGet、portable、新旧 Snow Shot 安装发现；显式路径优先；缺失安装提示。
- macOS app bundle 发现、Linux Flameshot gui 参数、help 支持的截图参数、portable 快捷键解析。
- 打开程序的幂等行为、已运行用户进程保留、自有进程清理、PID 复用保护。
- Snow Shot 已运行/首次启动后的截图快捷键、工作线程注入、缺失快捷键、注入失败、启动失败与重试。
- 模块启动延迟、Runner 初始化、设置立即启动、取消待执行启动。
- 修改安装路径后停止旧自有进程并启动所选安装；查询其他安装避免误归属旧进程；无效路径保留旧进程用于清理。

环境接口提供合成安装/进程/快捷键结果，测试真实 resolver、session 和 module 行为。自动用例不会发送系统快捷键、抓取用户桌面或改写系统剪贴板。

## 验收边界

当前 e2e 的 windows tools target 调度行为回归；浏览器/手机引擎无法控制 Avalonia 原生桌面。页面中的刷新、保存、忙碌按钮与 HostControl 连接仍需原生界面验收。Snow Shot/Flameshot 外部工具的框选、标注、保存文件与复制剪贴板，以及真实 Windows/macOS/Linux 安装与快捷键注入，均未包含在本次自动通过声明中。

新增缺陷回归可用 `FullyQualifiedName~ScreenshotWorkflowTests` 独立运行。报告保存在 `.e2e/tools/screenshot/report.json`，各次 TRX 在 `.e2e/dotnet/`。

## 本次执行结果

2026-10-04（Asia/Shanghai）：e2e 0.16.0 的 2 项流程测试通过，合计 26 项 .NET 行为回归通过。修复前的 `.e2e/tools/screenshot-before` 报告记录 2 项新回归失败；修复后全部通过。自动验证范围以上述验收边界为准。
