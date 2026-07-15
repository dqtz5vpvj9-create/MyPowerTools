# MyPowerTools 架构闭环执行计划（外部 Agent 版）

更新时间：2026-07-15（Asia/Shanghai）

执行角色：外部实现 Agent 负责机械编码、构建、部署和证据采集

审核角色：主 Codex 负责审查 diff、复跑门禁、修正实现和最终勾选

状态：等待用户审查；审核通过后从 Batch 0 开始

## 1. 执行纪律

- [ ] 外部 Agent 每轮只领取一个 Batch，先阅读根目录 `AGENTS.md`、`HANDOFF.md` 和本计划。
- [ ] 外部 Agent 保留当前 dirty worktree、所有 submodule 改动和未跟踪文件；严禁 reset、checkout、clean、批量覆盖、批量删除。
- [ ] 工具源码继续留在 `tools/*` submodule；父仓库负责 SDK、协议、宿主、构建编排、安装和门禁。
- [ ] 工具仅通过已生成的 SDK/协议包接入；Shell 与 Runner 禁止引用 `tools/*` 源码项目。
- [ ] 工具可直接操作 LocalAppData、用户目录和工具自有数据库；Host 只管理底座设置、权限决策和 Host 自有数据。
- [ ] Service Unit 的生命周期归独立 ServiceManager 管理；Shell、Runner、Surface 仅通过 scoped/admin client 访问。
- [ ] 外部 Agent 完成编码后提交执行报告，保持 Batch 验收框为未勾选状态。
- [ ] 主 Codex 审查改动与证据，复跑该 Batch 对应门禁；通过后由主 Codex勾选验收框。
- [ ] 编译成功仅证明可构建；功能完成必须包含真实进程、真实 IPC、真实工具入口或真实远程安装证据。
- [ ] 环境缺失、凭据缺失和硬件缺失统一记录为 `blocked-by-environment`；该核心路径保持未完成。

## 2. 当前已验证基线

以下结果已有本地证据，外部 Agent 直接复用，禁止重新实现：

- Quick/A1：11 项通过，覆盖依赖方向与源码边界。
- Quick/A2：5 项通过，覆盖动态发现、增删刷新和数据自治。
- Process/A3：13 项通过，证据位于 `artifacts/architecture-smoke/a3/result.json`。
- Process/A4：6 项通过，证据位于 `artifacts/architecture-smoke/a4/result.json`。
- Release/A5 本地阶段：7 项通过，不可变证据位于 `artifacts/architecture-smoke/a5/result-18065254.json`。
- Release/A5 远程阶段已有一次通过记录 `result-65aef22b.json`，随后一次并发复跑记录 `result-ff61fff8.json` 失败；远程闭环等待主 Codex 复核与稳定复跑。
- 五个工具已有动态 Surface DLL 与独立 `.mptpkg`：ADB Forwarder、Doubao Computer Use、Remote Notifications、ScreenEase、SmartBird Thermostat。
- Shell 产品项目已移除工具源码链接；视觉测试写入独立 `Mpt.Cli.VisualTesting` 项目。
- 产品 CLI 已包含 `service list/status/start/stop/restart/reload/shutdown`。
- Runner 已支持隔离实例参数 `--endpoint-address` 与 `--instance-name`。
- 当前候选包：`artifacts/install/MyPowerTools-0.2.0-win-x64.zip`。
- 候选包会被 A5 执行者重建；提交证据时从最终静止文件重新计算 SHA-256，禁止引用并发构建期间的瞬时哈希。
- 当前候选包大小：521,998,368 bytes（497.8 MiB）。
- 当前只有 ScreenEase 拥有真实独立 Service Unit。Remote Notifications 的轮询仍在 Surface 内，Doubao 的进程控制仍在 Surface 内。
- 远程 `veeam` 上已有用户日常 MPT：Shell PID `596364`、Runner PID `639168`。远程验收必须保留这两个进程及其安装目录。

## 3. 外部 Agent 交付格式

每个 Batch 的执行报告必须包含：

1. `Scope`：本轮领取的单个 Batch。
2. `Changed files`：父仓库与每个 submodule 分开列出。
3. `Commands`：完整命令、退出码、开始和结束时间。
4. `Evidence`：JSON、日志、PID、端点、截图或包路径。
5. `Residuals`：仍未完成的条目与原因。
6. `Risks`：影响现有常驻服务、设置、凭据或数据的风险。

主 Codex 审核动作固定为：

- 审查 diff 是否越过 Batch 范围。
- 检查工具是否继续消费公开 SDK/协议包。
- 复跑最小相关门禁。
- 复核远程旧进程与用户数据是否保持完整。
- 修正代码与门禁后更新本计划勾选状态。

## 4. Batch 0：闭合远程 A5 驱动

目标：让现有候选安装流程在 `veeam` 的隔离目录与隔离 Runner 端点完成远程验收，同时保留远程日常 MPT。

### 外部 Agent 编码与执行

- [ ] 重新运行 `scripts/build-installer.ps1`，把最新 installer stdout 收敛修复写入候选包。
- [ ] 确认 `candidate-manifest.json` 的五个 `toolId` 与 `version` 均为标量字符串。
- [ ] 运行 Release 本地阶段，确认 `phase=local`、`passed=true`、7 条记录全部通过。
- [ ] 使用 `scripts/verify-release-candidate.ps1 -RemoteHost veeam` 执行隔离远程安装。
- [ ] 远程测试使用独立 TEMP 安装根、独立 Runner endpoint、独立 instance name 和独立日志目录。
- [ ] 远程测试前后读取日常 Shell PID `596364` 与 Runner PID `639168` 的路径、启动时间和存活状态。
- [ ] 远程测试启动候选 ServiceManager、候选 Runner、ScreenEase 测试 unit，并通过候选 CLI 枚举服务。
- [ ] 远程测试运行候选 Shell HostControl smoke，确认五个工具均由候选 Runner 动态发现。
- [ ] 清理范围仅覆盖本轮 runId 对应 TEMP 根、上传 zip、上传脚本、测试 ServiceManager 自启项和测试进程。
- [ ] 生成 `artifacts/architecture-smoke/a5/result-<runId>.json`，其中 `phase=remote`、`passed=true`、`remote` 含安装与进程证据。

### 主 Codex 验收

- [ ] 复查远程安装脚本的删除目标均位于测试根。
- [ ] 复查日常 Shell/Runner PID、路径和启动时间前后保持一致。
- [ ] 复跑 `-Tier Release -RemoteHost veeam` 并读取 JSON，禁止依据控制台文字直接判定。
- [ ] Batch 0 通过。

### 停止条件

- 远程检测到测试目录之外的进程冲突。
- 隔离端点仍指向日常 Runner 或日常 ServiceManager。
- 清理目标无法通过规范化绝对路径限定在测试根。

## 5. Batch 1：Remote Notifications 独立 Service Unit

目标：底座运行且插件已加载时持续轮询；Shell 停留在任意页面、窗口关闭到托盘、Shell 重启期间均能收取并持久化消息。

### 外部 Agent 编码

- [ ] 在 `tools/remote-notifications` submodule 新建独立 worker/service 项目，产出可单独启动的进程。
- [ ] 将轮询、签名验证、历史写入、去重、主题索引和 Windows banner 发送迁入该进程。
- [ ] 为 worker 定义 typed runtime contract；Surface 只读取快照、订阅事件和发送用户命令。
- [ ] 提供 `unit-manifest.json`，声明 exec、arguments、workingDirectory、autostart、restartPolicy、readiness、日志和 dataRoots。
- [ ] 将 endpoint、轮询间隔、保留上限、banner 开关映射到工具设置；Secret 通过 Secret Store 引用，日志禁止输出 Secret。
- [ ] 移除 Surface 生命周期中的持续轮询与后台定时器。
- [ ] 保留现有消息渲染、主题筛选、详情与历史交互。
- [ ] 更新工具构建与打包，使 `.mptpkg` 同时携带 Surface、Service Unit、清单和设置 schema。
- [ ] 增加最小进程测试：worker 启动、ready、写入唯一测试消息、Surface 客户端读取、worker 重启后历史仍存在。

### 主 Codex 验收

- [ ] Shell 位于 Dashboard 时触发唯一测试消息，历史数量增加且主题出现。
- [ ] 关闭 Shell 窗口后触发唯一测试消息，再打开 Shell，消息已经存在。
- [ ] 重启 Runner 后再次触发消息，worker PID 保持或被 ServiceManager 按策略恢复。
- [ ] `service status`、统一 Services 页面和工具 Surface 显示同一 unit 状态。
- [ ] Process/A3、A4 与 Remote Notifications 专项测试通过。
- [ ] Batch 1 通过。

## 6. Batch 2：Doubao Computer Use 独立监督 Service Unit

目标：Planner、Tool Runtime、MCP Bridge 等进程由 ServiceManager 托管；切换页面仅绑定缓存状态，UI 线程零网络等待。

### 外部 Agent 编码

- [ ] 在 `tools/doubao-computer-use` submodule 新建独立 controller/worker 进程。
- [ ] 将子进程启动、停止、重启、健康探测、退避、日志聚合和 readiness 迁入该进程。
- [ ] 为四类初始化状态提供缓存快照与事件流；Surface 创建后立即显示缓存值，再异步接收更新。
- [ ] 移除 Surface 构造、导航和 ViewModel 激活路径中的同步等待与聚合 HTTP 等待。
- [ ] 为 controller 提供 `unit-manifest.json`、设置 schema、dataRoots、日志与依赖声明。
- [ ] `Restart` 命令必须经过 ServiceManager，等待 readiness 结果并返回结构化失败原因。
- [ ] 更新 `.mptpkg`，携带 Surface、controller、unit manifest 和设置 schema。
- [ ] 增加最小进程测试：离线启动、部分服务异常、重启恢复、超时错误、日志尾读。

### 主 Codex 验收

- [ ] 切换到 Doubao 页面首帧在 100 ms 内完成，离线四个 HTTP 请求继续在后台更新状态。
- [ ] 点击重启后观察真实子进程 PID 或 readiness 发生预期变化。
- [ ] controller 崩溃后 ServiceManager 按策略恢复，Shell 继续响应。
- [ ] Shell 与 Runner 重启期间 controller 生命周期符合 unit 策略。
- [ ] Process/A3、A4 与 Doubao 专项测试通过。
- [ ] Batch 2 通过。

## 7. Batch 3：安装器按工具清单收集 Service Units

目标：安装器从工具产物动态收集任意数量的 Service Units，消除 ScreenEase 专用部署分支。

### 外部 Agent 编码

- [ ] 扩展工具产物清单，记录 Surface、runtime、Service Units、设置 schema、dataRoots、来源 commit、dirty 状态和内容 hash。
- [ ] 修改 `scripts/build-installer.ps1`，遍历选择工具的 Service Unit 清单并部署版本化 payload。
- [ ] 移除 installer 内 ScreenEase 专用路径、专用文件名和专用激活逻辑。
- [ ] 安装流程完成 ServiceManager reload，再按 autostart 与 enabled 状态启动 units。
- [ ] 升级流程保留用户数据、设置、Secret 引用和上一个可回滚版本。
- [ ] 卸载默认保留工具数据；显式删除数据时仅处理已声明且通过边界校验的 dataRoots。
- [ ] 为动态收集、版本升级、回滚和卸载清理增加最小脚本测试。
- [ ] 记录 497.8 MiB 体积来源，消除多个工具重复携带的 .NET runtime；选定方案后验证另一台机器可启动。
- [ ] 重建 zip、SHA-256、candidate manifest 与 source manifest。

### 主 Codex 验收

- [ ] 安装器源码中无第一方工具 ID 专用分支。
- [ ] 新增一个 fixture Service Unit 后无需改 installer 源码即可进入候选包。
- [ ] 删除 fixture 后重新构建，候选包入口与 unit 同步消失。
- [ ] 全新安装、覆盖升级、回滚和卸载保留数据路径均有证据。
- [ ] Batch 3 通过。

## 8. Batch 4：删除遗留实现并收紧公开契约

### 外部 Agent 编码

- [ ] 从 `src/MyPowerTools.Cli/Program.cs` 删除 `MPT_LEGACY_VISUAL_HARNESS` 死代码块。
- [ ] 检查 Shell、Runner、CLI、SDK 项目，清除剩余 `tools/*/*.csproj` 引用和工具 ID 路由分支。
- [ ] 为 CLI `service list/status/start/stop/restart/reload/shutdown` 增加最小契约测试。
- [ ] 为正式 NuGet 包添加 package readme，消除打包 advisory。
- [ ] 检查公开包 API 与协议 bundle，记录兼容版本并更新 docs/sdk。
- [ ] 运行 solution Release build 与受影响项目测试，保存 0 warning/0 error 日志。

### 主 Codex 验收

- [ ] Quick/A1、A2 通过。
- [ ] Process/A3、A4 通过。
- [ ] `rg` 检查未发现产品项目引用工具源码或 legacy visual harness。
- [ ] Batch 4 通过。

## 9. Batch 5：真实产品核心路径验收

目标：候选包在独立 Windows 主机上完成安装与真实操作；环境能力不足时给出精确阻塞项。

### 外部 Agent 执行

- [ ] 远程安装候选包，启动候选 ServiceManager、Runner 和 Shell。
- [ ] Remote Notifications 使用已授权机制触发唯一测试消息，验证任意 Shell 页面、关闭窗口和重启后的收取与历史。
- [ ] Doubao 执行真实状态读取、服务重启和 readiness 验证，记录进程 PID 与响应。
- [ ] ADB 页面分别显示有线设备转发与无线设备转发；读取真实 adb/netsh 状态并执行一次安全刷新。
- [ ] ScreenEase 验证 Service PID 跨 Shell/Runner 重启保持稳定，工具页与统一 Services 页面状态一致。
- [ ] SmartBird 使用已配置 URL 加载真实管理页，验证 loading、成功、失败恢复、刷新和外部打开。
- [ ] 采集核心页面截图、命令日志、服务快照和候选版本信息。
- [ ] 将每条能力标记为 `passed`、`failed` 或 `blocked-by-environment`，附具体证据。

### 主 Codex 验收

- [ ] Release/A5 远程 JSON 通过。
- [ ] 五个工具均有真实核心路径证据；阻塞项保持未勾选并进入后续工作包。
- [ ] 日常 `veeam` MPT 与用户数据保持完整。
- [ ] Batch 5 通过。

## 10. Batch 6：文档与最终交付

### 外部 Agent 编码

- [ ] 更新 `docs/sdk/`、安装说明、Service Unit 开发说明和故障排查。
- [ ] 更新 `HANDOFF.md` 中的实际证据、剩余项、候选包路径和远程限制。
- [ ] 输出父仓库与各 submodule 的修改文件清单。
- [ ] 输出最终 SDK 包、协议 bundle、五个 `.mptpkg`、安装候选和校验文件路径。
- [ ] 输出 clean checkout 构建命令与另一台机器安装命令。

### 主 Codex 验收

- [ ] 文档中的每项完成声明均能映射到 JSON、日志、包或真实操作记录。
- [ ] Quick、Process、Release 三档最终复跑通过。
- [ ] 所有计划勾选状态与实际完成情况一致。
- [ ] Batch 6 通过，架构闭环完成。

## 11. 固定命令

在 `C:\Users\lixinrui\repo\MyPowerTools` 执行：

```powershell
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Quick
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Process
pwsh -NoProfile -File .\scripts\build-installer.ps1 -Version 0.2.0 -RuntimeIdentifier win-x64
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0
pwsh -NoProfile -File .\scripts\verify-architecture.ps1 -Tier Release -CandidateRoot .\artifacts\install\0.2.0 -RemoteHost veeam
```

## 12. 绝对禁止重复的坑

- 禁止把 Surface 内的定时器、轮询器或子进程监督器称为 Service Unit。
- 禁止把本地 A5 的 `passed=true` 描述成远程安装通过。
- 禁止依据空白 WebView、静态壳页、诊断页或命令存在宣称工具完成。
- 禁止修改或终止 `veeam` 上日常 MPT 进程。
- 禁止在远程清理中使用未经规范化边界校验的递归删除。
- 禁止将工具源码复制进 Shell 来绕过包或 SDK 边界。
- 禁止把编译成功、进程曾启动或按钮可点击当作核心业务路径证据。
- 禁止外部 Agent 自行勾选 Batch 通过项；最终状态归主 Codex 审核。
