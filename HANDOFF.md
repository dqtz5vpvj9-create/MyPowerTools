# MyPowerTools 架构闭环交接

更新时间：2026-07-16（Asia/Shanghai）

## 我们在做什么

MyPowerTools 正在收敛为 PowerToys 式工具底座：工具源码位于独立 submodule，可单独发行；父仓库提供公开 SDK、协议、动态发现、Shell、Runner、ServiceManager、安装器和发布门禁。

本轮核心任务是建立具有独立生命周期、由 MPT 统一管理的 Service Unit 模型。长期轮询、硬件效果和子进程监督进入独立进程，Surface 保留工具自有 UI 与控制体验。

权威计划：[`docs/18-architecture-revision-fast-plan.md`](docs/18-architecture-revision-fast-plan.md)

## 已经完成什么

### 1. 独立 ServiceManager

- `MyPowerTools.ServiceManager.exe` 拥有独立 named-pipe gRPC 控制面、bearer token、单实例保护、清单目录、日志和运行状态。
- Shell 使用 admin client 展示统一 `System > Services` 页面，支持 start、stop、restart、reload、日志和打开所属工具。
- 工具 Surface 接收 scoped `IServiceUnitClient`，服务端按 `toolId` 做授权。
- ServiceManager restart 会重领养仍存活的 worker，并验证 PID 启动时间、可执行路径和 instance token。
- 清单变更会停止旧进程并启动新版；清单删除会停止进程并移除 supervisor。
- `UnitReadiness.kind/address/timeout` 已进入 proto、server 和 scoped client。ServiceManager 控制面先于 startup reconcile 开放，并用标准 framed `ping` 区分 `Active` 与 `Degraded`。

### 2. 三个真实 Service Units

- ScreenEase：真实 `ScreenEaseModule`、设置、逻辑效果、护眼循环和恢复状态位于 `ScreenEase.Service.exe`；Runner 模块改为代理。
- Remote Notifications：签名拉取、去重、历史持久化、主题索引和 Windows banner 位于 `RemoteNotifications.Service.exe`。
- Doubao Computer Use：受管子进程控制、身份验证、健康探测、退避、崩溃看门狗和缓存快照位于 `DoubaoAgent.Controller.Service.exe`。
- 三个业务服务端与客户端均使用 current-user-only named pipe；日志省略 token 与 Secret。

### 3. 动态 Shell 和工具包

- Shell、Runner 产品项目无 `tools/*/*.csproj` 引用。
- A1 扫描 10 个 `ShellWorkspaceController` partial，确认无第一方工具 ID。
- ADB command palette 行为由 `commands.index.json` 的 `execution.activation.navigation` 声明；外层 `broker.request` 继续供工具 Surface 执行真实审批。
- Remote Notifications Surface 自行读取 Service Unit 与持久化历史；Shell 无通知工具特判。
- 五个工具都产出动态 Dotnet Surface 与独立 `.mptpkg`。

### 4. 安装和发布

- `build-all-tools.ps1` 发布每个工具声明的 Service Units。
- `.mptpkg`、module runtime 和 Suite unit 使用独立 staging，worker payload 无嵌套三份复制。
- `build-installer.ps1` 动态收集任意 unit，安装脚本改写实例资源并按 autostart 启动。
- 远程 verifier 使用上传脚本、SSH keepalive、阶段日志、原生命令真实重试、GUI 显式等待和 `try/finally` 清理。
- 清理器按测试根内可执行文件路径处理派生进程，已覆盖 `powertoold.exe`。

### 5. SDK 与文档

- 13 个 NuGet 包均携带 README，最终 `build-sdk.ps1` 无 package-readme advisory。
- protocol bundle 包含 `mpt_module_v1.proto`、`mpt_host_control_v1.proto`、`mpt_service_manager_v1.proto`、全部 schema 与测试向量。
- `docs/sdk/service-unit-development.md` 明确区分 scoped lifecycle client 和工具 typed business pipe。
- 版本兼容、生命周期、命令 activation 文档已同步。

## 最终验证结果

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| Quick A1 | 12/12 | 最终复跑通过；controller partials 扫描 10 个文件 |
| Quick A2 | 5/5 | `artifacts/architecture-smoke/a2/catalog-after-add-5fd16605.json` 等 |
| Process A3 | 18/18 | `artifacts/architecture-smoke/a3/result-8505aca1.json` |
| Process A4 | 6/6 | `artifacts/architecture-smoke/a4/result-780b2439.json` |
| RN Service | 13/13 | `artifacts/remote-notifications-verify-ce9c9281.json` |
| Doubao Service | 10/10 | `artifacts/doubao-controller-verify-4a3410de.json` |
| ScreenEase Service | 11/11 | `artifacts/screenease-service-verify-6653ab26.json` |
| 架构相关产品测试 | 92/92 | `ToolProductFoundation + ServiceUnit + RN + Doubao + ScreenEase` filter；2026-07-16 最终复跑 |
| Solution Release | 0 warning / 0 error | `dotnet build MyPowerTools.slnx -c Release` |
| A5 local | 全部通过 | `artifacts/architecture-smoke/a5/result-b0e8d3a6.json` |
| A5 remote | R1–R7 全部通过 | `artifacts/architecture-smoke/a5/result-619dff7d.json` |

补充审计：完整历史测试程序集当前为 405/447，通过架构相关筛选的 92 项全部为绿。其余 42 项包含迁移后仍引用旧 Shell/工具源码路径的静态断言、共享 Avalonia Headless 全局状态冲突及既有产品债务；本轮未将“全仓历史测试全绿”写入完成声明。

远程 A5 的关键事实：

- veeam 隔离安装根中三个 units 均为 Active。
- Runner 发现五个已交付工具；Shell smoke 得到 7 modules、7 dashboard cards、102 commands。
- 日常 Shell PID `596364`、Runner PID `639168` 的路径与启动时间前后完全一致。
- 测试派生 `powertoold.exe` 已停止；`residualTestProcesses=[]`、`cleanupErrors=[]`、`executionError=null`。

## 当前产物

- Candidate zip：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\MyPowerTools-0.2.0-win-x64.zip`
- SHA-256：`796527064abcc30d21291be695150be82645cb99a2e0101a3b1fd9bd639f68ae`
- 大小：712,388,900 bytes（679.4 MiB）
- Candidate root：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\0.2.0`
- NuGet：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\sdk\nuget`
- npm：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\sdk\npm\mypowertools-web-bridge-0.2.0.tgz`
- Protocol：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\sdk\protocol\mypowertools-protocol-0.2.0.zip`
- 五个 `.mptpkg`：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\0.2.0\payload\packages`
- 三个 Suite units：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\install\0.2.0\payload\service-units`

Submodule commits：

- `tools/adb-forwarder`：`521dffd`
- `tools/remote-notifications`：`81e62cd`
- `tools/doubao-computer-use`：`bdc1113`
- `tools/screenease`：`4a046e9`

## 当前卡点与下一步

程序化架构工作已闭环。剩余工作集中在产品级 GUI、硬件和真实凭据环境：

- Remote Notifications：真实签名消息、Windows banner、Dashboard 与关窗重开观测。
- Doubao：真实 Python runtime restart，记录受管子进程 PID 与 readiness 恢复；交互式首帧计时。
- ADB：真实有线设备和无线设备转发。
- ScreenEase：非 RDP 显示会话中的硬件 gamma 写入。
- SmartBird：实际 panel URL 的 loading、成功、失败恢复、刷新和外部打开。
- 五工具最终截图组。

体积优化也留作独立工作包。当前 679.4 MiB 主要来自多个 self-contained .NET 进程。共享 runtime 部署可以继续降低体积。

## 新会话直接执行什么

若目标是继续产品验收，直接从上述六项残余路径开始，沿用当前候选，完成一项便把 `docs/18-architecture-revision-fast-plan.md` 中对应 `blocked-by-environment` 改为 `[x]` 并附证据。

若修改了协议、ServiceManager、Runner、Shell、工具 Surface 或 unit worker，依次执行：

```powershell
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Quick
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Process
pwsh -NoProfile -File .\scripts\verify-remote-notifications-service.ps1
pwsh -NoProfile -File .\scripts\verify-doubao-controller-service.ps1
pwsh -NoProfile -File .\scripts\verify-screenease-service.ps1
dotnet build MyPowerTools.slnx -c Release
pwsh -NoProfile -File .\scripts\build-installer.ps1
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0 -RemoteHost veeam
```

## 踩过的坑，严禁重复

- Surface 内的 timer、poller 和 subprocess supervisor 缺少独立生命周期，不能登记为 Service Unit 完成证据。
- ServiceManager proto 曾丢失 readiness address，隔离安装会回退到默认管道并在 10 秒后超时。A3.2b 已覆盖该缺陷。
- ServiceManager 曾把进程存活直接视为 ready。当前启动与重领养都会执行标准 framed `ping`；A3.2c 以无响应管道验证 `Degraded` 状态。
- ServiceManager 曾在开放控制管道前等待全部 startup readiness。当前控制面先启动；A3.0 验证 5000 ms 故障探测期间控制面在 274 ms 可用。
- 延迟崩溃重启曾可能与 stop、manifest 替换或删除竞争。当前 generation cancellation 会让过期重启任务失效。
- ServiceManager reload 曾保留陈旧 supervisor。A3.14 与 A3.15 已覆盖升级和删除。
- installer 曾在 module runtime、`.mptpkg`、Suite payload 三处重复嵌套 worker。
- PowerShell `$ErrorActionPreference='Stop'` 会把瞬时原生命令 stderr 升级为终止错误；重试循环需要显式处理退出码。
- Windows PowerShell 5.1 启动 GUI 子系统程序会提前返回；使用 `Start-Process` 和 `WaitForExit()`。
- 远程内联脚本过长且异常路径容易跳过清理；上传独立 `.ps1`。
- 固定进程名清理会遗漏派生 executable；按已规范化测试根中的 executable path 枚举。
- candidate 测试必须保护 Program Files 下的日常 MPT，并比对 PID、路径和启动时间。
- SDK protocol bundle 曾漏掉 ServiceManager proto；当前脚本复制 `proto\*.proto`。
- 命令面板的工具专用特判会破坏动态发现；使用声明式 `execution.activation.navigation`。
- RN 专项测试早期污染过用户 HKCU 历史；清理前注册表备份位于 `artifacts/audit/remote-notifications-registry-before-test-cleanup.reg`。测试必须使用隔离数据根。

## 工作树和提交保护

- 四个工具 submodule 已各自提交，父仓库只记录 gitlink。
- `sdk/web-bridge/node_modules` 当前包含 npm/换行噪声，父仓库提交应排除这些路径。
- 禁止 reset、checkout、clean、stash 和历史重写。
- 父仓库最终提交应包含计划、HANDOFF、协议、ServiceManager、Shell/Runner、脚本、测试、文档和四个 submodule gitlink。
