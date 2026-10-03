# MPT workflow e2e

使用 [tester-army/e2e](https://github.com/tester-army/e2e)，固定版本 0.16.0。Windows 需要 Node 24、global.json 指定的 .NET SDK、Python 3.12、PowerShell 7，以及完整子模块检出。

```powershell
npm ci --ignore-scripts
npm run test:e2e:prepare
npm run test:e2e
```

单工具示例：`npx e2e run tests/e2e/input-monitor.e2e.ts`。配置为单 worker、零重试、关闭缓存，报告输出至忽略目录 `.e2e/`，包括 JSON、JUnit、Markdown 和实际 .NET TRX。测试要求实际执行且通过，拒绝零测试或跳过的 .NET 结果。GitHub Actions 自动构建四个测试项目、运行套件并保存证据。

2026-10-04 独立检出完整轮次：18 个文件，17 个通过、1 个跳过；81 项流程测试，80 个通过、1 个跳过。报告 run id：`01a102b4-e411-7790-9858-e5da2c3bdef4`，耗时 340.32 秒。证据保存在 `.e2e/clean-final/`。Architecture Quick Gate 通过。独立检出使用自建 SDK feed；第三方 NuGet 包复用缓存，网络恢复使用 Microsoft dotnet-public 镜像。

每个工具已分配子代理。各流程、复现、修复与验收限制见下列文档：

| 工具 | 覆盖文档 |
|---|---|
| ADB Forwarder | [adb-forwarder](e2e/adb-forwarder.md) |
| DDNS | [ddns](e2e/ddns.md) |
| Doubao Computer Use | [doubao-computer-use](e2e/doubao-computer-use.md) |
| IME Manager | [ime-manager](e2e/ime-manager.md) |
| Input Monitor | [input-monitor](e2e/input-monitor.md) |
| Local Lag Cleaner | [local-lag-cleaner](e2e/local-lag-cleaner.md) |
| NSSM Manager | [nssm-manager](e2e/nssm-manager.md) |
| Paste Image | [paste-image](e2e/paste-image.md) |
| Process Monitor | [process-monitor](e2e/process-monitor.md) |
| Remote Commands | [remote-commands](e2e/remote-commands.md) |
| Remote Notifications | [remote-notifications](e2e/remote-notifications.md) |
| ScreenEase | [screenease](e2e/screenease.md) |
| Screenshot | [screenshot](e2e/screenshot.md) |
| SmartBird Thermostat | [smartbird-thermostat](e2e/smartbird-thermostat.md) |
| XBRD | [xbrd](e2e/xbrd.md) |
| File Transfer | [file-transfer](e2e/file-transfer.md) |

套件还覆盖 Shell 导航、连接恢复、驻留生命周期、三个真实 Avalonia 页面渲染、模块内部取消的隔离及开发覆盖脚本的服务发现、进程选择和部分移动回滚。文件互传正式目录缺失，明确跳过；目录出现后测试强制要求补齐正式流程。

开发启动额外修复：内部模块状态取消按降级处理，调用方取消继续传播；完整目录启动检查单轮请求预算受总超时约束，记录失败阶段；运行时启动避免等待驻留子进程持有的输出管道 EOF；脚本服务和 SmartBird Python 修复随工具覆盖更新。所有修改工具均已覆盖并启动开发版，最后执行 Core 覆盖并通过 Shell-to-Runner 连接验收。SmartBird 安装目录的 Python 文件哈希与修复源码一致。

上游提供 browser/mobile engine。本次采用 tools-only target 调用真实运行时、ViewModel、SQLite、HTTP loopback 与独立测试项目；合成设备和临时目录用于隔离系统状态。Windows 桌面鼠标点击、UAC、真实全局输入 hook、物理设备、真实 DNS/SSH/路由器账号操作保留人工验收，详细范围见各工具文档。此轮未修改手机通知行为，NotifyApp 未升版本。
