# MyPowerTools 架构闭环执行计划（主 Codex 审计版）

更新时间：2026-07-16（Asia/Shanghai）

状态：程序化架构闭环与远程发布门禁已通过；真实硬件、实际凭据和交互式 GUI 路径保留为产品验收残项。

## 1. 目标架构约束

- [x] 工具源码保留在 `tools/*` submodule，可独立构建与发行。
- [x] Shell、Runner 只消费公开 SDK、协议和工具产物，产品项目无 `tools/*/*.csproj` 引用。
- [x] 工具目录可动态增加、删除和刷新，Shell 源码无第一方工具路由表。
- [x] Dotnet Surface 负责工具自有 UI；长期任务进入独立 Service Unit。
- [x] `MyPowerTools.ServiceManager.exe` 统一管理 Service Unit 的发现、启动、停止、重启、readiness、日志、事件和重领养。
- [x] Shell 与 Runner 重启期间，未变更的 Service Unit 进程保持运行。
- [x] 工具 Surface 通过 scoped `IServiceUnitClient` 管理自有 unit；ServiceManager 在服务端拒绝跨工具访问。
- [x] ServiceManager 将有效 `readiness.kind/address/timeout` 返回 scoped client，工具 typed client 连接实例专属业务管道。
- [x] 业务命名管道使用 `PipeOptions.CurrentUserOnly`，日志省略 token、Secret 和凭据内容。
- [x] 工具可以直接使用 LocalAppData、用户目录与工具自有数据库；默认卸载保留数据，显式 purge 仅删除声明且通过边界校验的 dataRoots。

## 2. Batch 0：远程发布门禁驱动

- [x] 远程执行主体拆分为 `verify-release-candidate.remote.ps1`，包含超时、SSH keepalive、阶段日志、结构化结果和 `try/finally` 清理。
- [x] 安装重试可承受首次瞬时 `Unauthenticated`，原生命令退出码由调用边界显式处理。
- [x] GUI Shell smoke 使用 `Start-Process`、输出重定向和显式等待。
- [x] 清理器按测试根内的可执行文件路径识别派生进程，包括 `powertoold.exe`。
- [x] veeam 验收使用独立 TEMP、ServiceManager endpoint、Runner endpoint、instance name 和数据根。
- [x] 日常 Shell PID `596364`、Runner PID `639168` 的路径、启动时间和 PID 前后保持一致。
- [x] 远程 R1–R7 全部通过，残留测试进程为 0，`cleanupErrors=[]`。
- [x] Batch 0 通过。

证据：`artifacts/architecture-smoke/a5/result-619dff7d.json`。

## 3. Batch 1：Remote Notifications Service Unit

- [x] `RemoteNotifications.Service.exe` 拥有签名拉取、去重、历史持久化、主题索引和 Windows banner 路径。
- [x] worker 使用现有工具设置存储，循环读取 endpoint、轮询间隔和 banner 开关。
- [x] Surface 通过 scoped lifecycle client 获取有效业务管道，再读取状态和持久化历史。
- [x] Shell 生命周期中无 Remote Notifications 后台 worker，Shell 控制器无通知工具 ID 特判。
- [x] `.mptpkg` 和 Suite 均携带 Surface、Service Unit 与 unit manifest。
- [x] 无 Shell 进程时注入唯一消息，历史成功写入；ServiceManager 重启后 worker PID 保持；worker 崩溃后恢复且历史保留。
- [x] 专项进程门禁 13/13 通过。
- [x] Batch 1 架构切片通过。
- [ ] `blocked-by-environment`：使用真实签名端点完成 Windows banner、Dashboard、关窗重开三种 GUI 观测并截图。

证据：`artifacts/remote-notifications-verify-ce9c9281.json`。

## 4. Batch 2：Doubao Computer Use Controller Service Unit

- [x] `DoubaoAgent.Controller.Service.exe` 拥有子进程启动、停止、重启、身份校验、健康探测、退避和崩溃看门狗。
- [x] controller 定时更新缓存快照；Surface 构造直接绑定 `CurrentSnapshot`，页面首帧不等待四个 HTTP 请求。
- [x] Surface 的生命周期与业务调用使用 scoped unit 快照中的实例专属管道。
- [x] controller readiness、状态、inspect、ServiceManager 重领养、单实例和崩溃恢复门禁通过。
- [x] 专项进程门禁 10/10 通过。
- [x] Batch 2 架构切片通过。
- [ ] `blocked-by-environment`：在真实 Doubao Python runtime 上点击重启，记录受管子进程 PID 变化和 readiness 恢复。
- [ ] `blocked-by-environment`：在交互式 Shell 中测量页面首帧时间并保留截图/时间线证据。

证据：`artifacts/doubao-controller-verify-4a3410de.json`。

## 5. Batch 3：动态 Service Unit 安装与生命周期

- [x] `build-all-tools.ps1` 从工具注册项发布任意数量的 `service-units/<unit-id>/`。
- [x] 工具 runtime、独立 `.mptpkg` 和 Suite Service Unit 使用独立 staging，消除同一 worker 在模块目录内的嵌套复制。
- [x] `build-installer.ps1` 动态收集 units，安装器源码无 ScreenEase 专用部署分支。
- [x] 安装时改写 exec、working directory、pipe、heartbeat、instance token 和 dataRoot；随后 reload 并启动 autostart units。
- [x] 升级时删除上一版由 Suite 管理且本版已移除的 manifest，用户自行维护的 manifest 保持原状。
- [x] ServiceManager reload 会停止并替换清单已变更的进程，也会停止和移除清单已删除的进程。
- [x] 重领养校验 PID、启动时间、可执行路径和 instance token。
- [x] A2 验证默认卸载保留数据与显式 purge 删除声明数据；A3 验证清单升级和删除。
- [x] 候选包在 veeam 无全局构建依赖的隔离根启动三个 self-contained Service Units。
- [ ] 后续优化：六类 self-contained 进程仍分别携带 .NET runtime；共享 runtime 部署作为独立体积工作包。
- [ ] 后续增强：完整 Suite 覆盖升级与版本回滚再增加一条远程安装证据。
- [x] Batch 3 架构切片通过。

## 6. Batch 4：公开契约与动态 Shell

- [x] 删除产品 CLI 中的 `MPT_LEGACY_VISUAL_HARNESS` 死代码，视觉测试保留在 `Mpt.Cli.VisualTesting`。
- [x] Shell command palette 支持通用 `execution.activation.navigation`，ADB 审批工作流由命令索引声明。
- [x] A1 扫描全部 10 个 `ShellWorkspaceController*.cs`，确认无第一方工具 ID。
- [x] ServiceManager proto 增加兼容字段 `UnitReadiness.kind/address/timeout`，server/client 完成往返映射。
- [x] A3 增加实例专属 readiness 地址协议往返门禁，以无响应管道验证 `Degraded`，并验证控制面先于 startup readiness 开放。
- [x] protocol bundle 包含 module、HostControl、ServiceManager 三份 proto、JSON Schema 和跨语言向量。
- [x] 正式 NuGet 包均携带 README，SDK 构建无 package readme advisory。
- [x] solution Release build：0 warning / 0 error。
- [x] 架构相关产品测试：92/92 通过。
- [x] Batch 4 通过。

## 7. Batch 5：真实产品路径验收

- [x] 候选在 veeam 隔离目录完成安装。
- [x] 三个 Service Unit 远程状态均为 Active。
- [x] 候选 Runner 动态发现五个已交付工具，Shell HostControl smoke 得到 7 modules / 7 dashboard cards / 102 commands。
- [x] 候选 Runner 关闭后，三个 Service Unit 仍由独立 ServiceManager 管理。
- [x] 日常 veeam MPT 进程与安装目录保持完整。
- [ ] `blocked-by-environment`：Remote Notifications 真实签名消息与 GUI banner。
- [ ] `blocked-by-environment`：Doubao 真实 runtime restart。
- [ ] `blocked-by-environment`：ADB 有线设备与无线设备的真实转发。
- [ ] `blocked-by-environment`：ScreenEase 在非 RDP 显示会话中的硬件 gamma 调节。
- [ ] `blocked-by-environment`：SmartBird 使用实际 panel URL 的加载、失败恢复、刷新与外部打开。
- [ ] `blocked-by-environment`：五个工具的最终核心路径截图组。
- [x] Release/A5 远程架构门禁通过。
- [ ] Batch 5 产品级 GUI/硬件签收。

## 8. Batch 6：文档与交付

- [x] 更新 Service Unit 开发、生命周期、命令激活、版本兼容和协议 bundle 文档。
- [x] 更新 `HANDOFF.md` 的实际证据、残项、候选路径与远程保护边界。
- [x] 生成 13 个 NuGet SDK 包、WebBridge npm 包、协议 bundle、五个 `.mptpkg` 和 Suite 候选包。
- [x] 记录四个已修改 submodule 的独立提交。
- [x] Quick、Process、Release local、Release remote 全部复跑通过。
- [x] 程序化架构闭环完成。

## 9. 最终证据

| 门禁 | 结果 | 证据 |
| --- | --- | --- |
| Quick A1 | 12/12 | 2026-07-16 最终复跑控制台；A1.8 扫描 10 个 controller partials |
| Quick A2 | 5/5 | `artifacts/architecture-smoke/a2/catalog-after-add-5fd16605.json` 等 |
| Process A3 | 18/18 | `artifacts/architecture-smoke/a3/result-8505aca1.json` |
| Process A4 | 6/6 | `artifacts/architecture-smoke/a4/result-780b2439.json` |
| RN Service | 13/13 | `artifacts/remote-notifications-verify-ce9c9281.json` |
| Doubao Service | 10/10 | `artifacts/doubao-controller-verify-4a3410de.json` |
| ScreenEase Service | 11/11 | `artifacts/screenease-service-verify-6653ab26.json` |
| Release A5 local | 全部通过 | `artifacts/architecture-smoke/a5/result-b0e8d3a6.json` |
| Release A5 remote | R1–R7 全部通过 | `artifacts/architecture-smoke/a5/result-619dff7d.json` |

完整历史测试程序集审计结果为 405/447。42 项失败主要来自迁移后仍引用旧 Shell/工具源码路径的静态断言、共享 Avalonia Headless 全局状态冲突及既有产品债务；本计划以 Quick、Process、三个 Service Unit 专项门禁、92 项架构相关产品测试和 Release A5 为闭环门禁。

## 10. 最终产物

- Candidate：`artifacts/install/MyPowerTools-0.2.0-win-x64.zip`
- SHA-256：`796527064abcc30d21291be695150be82645cb99a2e0101a3b1fd9bd639f68ae`
- 大小：712,388,900 bytes（679.4 MiB）
- Candidate root：`artifacts/install/0.2.0`
- NuGet：`artifacts/sdk/nuget`
- npm：`artifacts/sdk/npm/mypowertools-web-bridge-0.2.0.tgz`
- Protocol：`artifacts/sdk/protocol/mypowertools-protocol-0.2.0.zip`
- 独立工具包：`artifacts/install/0.2.0/payload/packages`
- Suite units：`artifacts/install/0.2.0/payload/service-units`

Submodule commits：

- `tools/adb-forwarder`：`521dffd`（声明式 command-palette activation）
- `tools/remote-notifications`：`81e62cd`（独立通知 worker）
- `tools/doubao-computer-use`：`bdc1113`（独立 runtime controller）
- `tools/screenease`：`4a046e9`（真实 ScreenEase runtime 迁入 Service Unit）

## 11. 固定复验命令

```powershell
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Quick
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Process
pwsh -NoProfile -File .\scripts\verify-remote-notifications-service.ps1
pwsh -NoProfile -File .\scripts\verify-doubao-controller-service.ps1
pwsh -NoProfile -File .\scripts\verify-screenease-service.ps1
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.2.0 -RuntimeIdentifier win-x64
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0 -RemoteHost veeam
```

## 12. 严禁重复的坑

- [x] Surface 定时器、轮询器和子进程监督器不再冒充 Service Unit。
- [x] 本地 A5 与远程 A5 使用不同证据，完成声明读取 JSON。
- [x] 远程脚本使用独立文件，避开超长内联命令。
- [x] 原生命令重试显式检查退出码，瞬时 stderr 不终止重试循环。
- [x] 远程清理先校验规范化绝对路径，再按测试根内可执行文件清理派生进程。
- [x] 安装器从 unit manifest 动态收集，工具专用部署路径已清除。
- [x] 工具业务协议与 ServiceManager 生命周期协议保持分层。
- [x] 编译、进程启动、静态壳页和按钮存在均不单独构成产品完成证据。
