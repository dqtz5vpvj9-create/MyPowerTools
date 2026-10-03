# MPT workflow e2e

使用 [tester-army/e2e](https://github.com/tester-army/e2e)，固定版本 0.16.0。Windows 需要 Node 24、global.json 指定的 .NET SDK、Python 3.12、PowerShell 7，以及完整子模块检出。

```powershell
npm ci --ignore-scripts
npm run test:e2e:prepare
npm run test:e2e
```

单工具示例：`npx e2e run tests/e2e/input-monitor.e2e.ts`。配置为单 worker、零重试、关闭缓存，报告输出至忽略目录 `.e2e/`，包括 JSON、JUnit、Markdown 和实际 .NET TRX。测试要求实际执行且通过，拒绝零测试或跳过的 .NET 结果。GitHub Actions 自动构建四个测试项目、运行套件并保存证据。

2026-10-04 合并最新主分支后的独立检出完整轮次：18 个文件，17 个通过、1 个跳过；82 项流程测试，81 个通过、1 个跳过。报告 run id：`01a102e6-05d3-7100-a25f-fdb9004a5e64`，耗时 341.21 秒。证据保存在 `.e2e/settings-final/`，提交的摘要见 [verification-2026-10-04.json](e2e/verification-2026-10-04.json)。Architecture Quick Gate 通过。独立检出重新生成 SDK feed；第三方 NuGet 包复用缓存，网络恢复使用 Microsoft dotnet-public 镜像。

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

开发启动额外修复：内部模块状态及恢复保存设置的取消按降级处理（5 项隔离回归通过），调用方取消继续传播；启动检查保留主分支的 Ping/目录分阶段设计，严格遵守剩余预算并记录失败阶段；运行时启动避免等待驻留子进程持有的输出管道 EOF；脚本服务和 SmartBird Python 修复随工具覆盖更新。截图安装解压失败时清理唯一 staging 目录。所有修改工具均已覆盖并启动开发版，Core 与合并后的 Screenshot 覆盖通过安装目录 Shell-to-Runner 连接验收。SmartBird Python 文件及 Input Monitor 原生 SQLite 文件均与修复源码/升级包哈希一致。

上游提供 browser/mobile engine。本次采用 tools-only target 调用真实运行时、ViewModel、SQLite、HTTP loopback 与独立测试项目；合成设备和临时目录用于隔离系统状态。Windows 桌面鼠标点击、UAC、真实全局输入 hook、物理设备、真实 DNS/SSH/路由器账号操作保留人工验收，详细范围见各工具文档。此轮未修改手机通知行为，NotifyApp 未升版本。

云端首轮发现 Windows 8.3 短路径及混合路径分隔符影响比较，已将隔离目录统一为真实路径，并修复服务宿主路径识别。报告上传显式启用隐藏目录。修复后的本机完整轮次仍为 81 项通过、1 项跳过；GitHub 流程持续验证同一套件。
