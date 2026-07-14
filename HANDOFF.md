# MyPowerTools 架构重构交接

更新时间：2026-07-15（Asia/Shanghai）

## 新会话先读

- 主仓库：`C:\Users\lixinrui\repo\MyPowerTools`
- 当前分支：`codex/tool-product-rebuild`
- 当前 HEAD：`4c31165`
- 权威执行计划：`docs/18-architecture-revision-fast-plan.md`
- 计划现有 160 个未完成 checkbox，包含目标架构、Service UI、迁移、发行和 A1-A5 最小测试门禁。
- 工作树极脏，包含大量用户改动、submodule 改动、生成模块、SDK、安装器和 UI 工作。严禁 reset、checkout、clean、批量删除或覆盖。
- `docs/PROJECT_STATUS.md`、`docs/AGENT_HANDOFF.md` 等文件记录了 2026-07-06 及更早的历史验收。当前工作树已经继续变化，这些结果只能当历史证据。
- 本轮只完成了架构审查、决策收敛、Revision Plan 和本交接文档。目标架构的主体实现尚未开始。

## 我们正在做什么

MyPowerTools 最初希望成为类似 PowerToys 的工具底座：统一 Shell、动态工具发现、可独立发布的工具包、多语言 Runtime、统一设置/命令/日志/事件，以及受控的长期后台服务。

当前真实形态仍是过渡架构：

```text
第一方工具 submodule 源码
        │ Compile Include / ProjectReference
        ▼
Avalonia Shell ── HostControl IPC ── Runner ── Runtime hosts
        │                               │
        │                               └─ 豆包专用 Supervisor
        ├─ 通知页面 DispatcherTimer
        └─ SmartBird-owned WebToolHost
```

目标形态：

```text
Shell（可重启 UI） ── HostControl ── Runner（Catalog/命令/事件）
   │                                      │
   ├─ Dotnet Surface                      ├─ Runtime Host
   └─ 独立 WebToolHost                    └─ ServiceManager Client
                                                   │
ServiceManager（独立安装、独立常驻） ────────────────┘
   └─ ScreenEase / Remote Notifications / Doubao 等 Service Units
```

重构目标是让第一方工具与外部工具共同走公开 SDK、manifest 和构建产物，移除 Shell 对工具源码与工具 ID 的认知；长期服务拥有独立生命周期，MPT 的日常开发和 Shell/Runner 重启不会终止这些服务。

## 已确认的产品与架构决策

### 工具与源码

- 每个工具继续以 Git submodule 维护独立源码仓库。
- 每个工具可单独构建、打包和发行。
- Suite 仓库负责 SDK、协议、submodule 编排、集成目录和最终安装包。
- 开发态允许 dirty submodule、任意分支、本地构建目录和手动替换。
- 用户点击“刷新工具”后，新增目录出现、删除目录消失。
- 发布记录 commit、dirty 状态和 hash，用于追踪来源，不形成严格锁定。
- 第一方工具必须消费公开 SDK/Protocol 包，禁止引用父仓库 `src/*.csproj`。

### 支持的工具形态

- `dotnet-surface`：可信 Avalonia UI，使用 AvaloniaSdk。
- `web-surface`：Web UI，使用独立 WebToolHost 与 WebBridge。
- `native-tool`：原生工具，可包含复杂原生 UI 与业务逻辑。
- `headless-tool`：无 UI Runtime 或 Service Unit。
- Runtime 可以使用 .NET、Python、Rust、Web/HTTP 或其他语言，通过 gRPC/native IPC、loopback HTTP、stdio 等协议接入。

### 数据与配置

- 工具可以直接读写 LocalAppData、用户目录和自有数据库。
- Host SettingsStore 管理底座设置、权限决策和 Host 自有数据。
- `tool.json.dataRoots` 用于卸载提示、备份、诊断和显式 purge。
- 工具负责自己的数据迁移、并发控制、兼容性和损坏恢复。
- 默认卸载保留工具数据，用户显式选择后执行 purge。
- Secret Store 是可选能力，工具可以继续使用自身凭据方案。
- `0.0.0.0` 是允许的监听配置。禁止重新加入自定义的阻断逻辑。

### Service 生命周期

- 新增独立进程 `MyPowerTools.ServiceManager`，角色类似用户级 systemd manager。
- ServiceManager 部署到版本化安装目录，与源码 `bin/Debug` 分离。
- Shell 与 Runner 都是客户端，Service Unit 生命周期归 ServiceManager。
- ServiceManager 重启后依据 PID 与实例令牌重新接管存活 unit。
- Service Unit 只有显式 Stop、Disable、Upgrade、Uninstall 或自身故障会结束。
- ScreenEase、Remote Notifications、豆包后台都需要 Service Unit 模型。
- Process Monitor 与 Remote Commands 暂停开发，本轮只保留兼容能力。

### Service UI

- 每个工具可以自行设计美观、产品化的服务状态、控制、恢复和业务联动界面。
- 底座在 `System > Services` 提供统一管理页面。
- 工具 Surface 使用 scoped `IServiceUnitClient`，只能控制自己声明的 units。
- 统一 Services 页面使用 administration client，可以跨工具查看和管理。
- 两个入口共享 ServiceManager 的状态、事件和执行命令。
- ServiceManager 是唯一进程执行面，两个 UI 入口不会产生重复服务实例。
- Services 页面展示状态、PID、运行时间、版本、启动方式、重启次数、最近错误、readiness、依赖和日志。
- 工具页面优先呈现业务信息，避免复制大型低价值状态栏。

### UI 与用户工作流

- “Open” 必须进入真实工具工作流。
- 模块详情、诊断、日志只能作为维护入口，不能冒充工具产品页。
- ScreenEase 当前页面视觉方向得到用户认可，可作为其他复杂工具的布局参考。
- ADB Forwarder 必须明确分为“有线设备转发”和“无线设备转发”两条工作流。
- Remote Notifications 只要底座运行且插件已加载，就应持续接收消息，与当前页面无关。
- 豆包页面先展示 ViewModel 快照，网络请求后台更新；页面切换不能等待 HTTP 初始化。
- SmartBird 使用 Web Surface，WebView 进程故障只影响该工具。

## 已经完成的基础

以下内容已经存在于当前工作树，但仍需在重构后重新验证：

- Runner、Avalonia Shell、HostControl IPC、Runtime、Packaging、Protocol、Broker 和 Platform 项目已存在。
- `MyPowerTools.ToolSdk`、`MyPowerTools.Protocol`、`MyPowerTools.AvaloniaSdk` 已配置为 0.2.0 可打包项目。
- 已生成本地 SDK 包：
  - `artifacts/sdk/nuget/MyPowerTools.ToolSdk.0.2.0.nupkg`
  - `artifacts/sdk/nuget/MyPowerTools.Protocol.0.2.0.nupkg`
  - `artifacts/sdk/nuget/MyPowerTools.AvaloniaSdk.0.2.0.nupkg`
  - `artifacts/sdk/npm/mypowertools-web-bridge-0.2.0.tgz`
  - `artifacts/sdk/protocol/mypowertools-protocol-0.2.0.zip`
- `sdk/web-bridge` 已有 TypeScript 源码、声明文件和构建输出。
- CLI 已有以下真实入口：
  - `mypowertools create tool --type web|dotnet|native|headless --id <id> --output <dir>`
  - `mypowertools validate tool <dir>`
  - `mypowertools pack tool <dir>`
- Runtime 已有 `ToolRegistry` 与附加目录配置基础。
- Shell 已有外部 SDK 工具页面、Tool Catalog 和刷新入口基础。
- 七个工具目录已经登记为 submodule：ADB、Remote Notifications、Remote Commands、Process Monitor、ScreenEase、SmartBird、Doubao。
- 五个活跃工具 submodule 有 `build.ps1`：ADB、Remote Notifications、ScreenEase、SmartBird、Doubao。
- `modules/**/ui/tool.json` 已存在，当前用于 staging/integration。
- `scripts/build-sdk.ps1`、`scripts/build-tool-packages.ps1`、安装/发布脚本和 installer 目录已经存在。
- 旧的父仓库第一方模块源码目录正处于删除状态，源码已经迁入 submodule；Shell 目前仍通过 `current-integration` 链接这些源码。
- WebView 原生 HWND 崩溃之后已经采用独立 WebToolHost 方向，但 WebToolHost 项目仍归 SmartBird submodule 所有。
- `docs/18-architecture-revision-fast-plan.md` 已完整记录 160 项执行计划和最小架构测试设计。

## 当前真正卡住的位置

### 1. ServiceManager 引擎已落地，待接真实工具

- `src/MyPowerTools.ServiceManager`（独立进程，named pipe `mypewertools.servicemanager.v1`，独立 token）已实现。
- `src/MyPowerTools.ServiceManager.Server`：`UnitSupervisor`（状态机 inactive/activating/active/degraded/failed/deactivating、PID/StartedAt/ExitCode/RestartCount、无 KILL_ON_JOB_CLOSE、重接管 `TryReadopt`）、`UnitLogStore`（按 unit 分区 stdout/stderr + 滚动上限 + 最近错误）、`UnitEventBus`（单调 seq）、`ServiceUnitCatalog`（deploy-root manifest 加载）、`ServiceManagerGrpcService`。
- `src/MyPowerTools.ServiceManager.Client`：`ServiceManagerAdminClient`（跨工具 administration）+ `ScopedServiceUnitClient`（scoped `IServiceUnitClient`，经 `x-mpt-caller-tool` header 服务端强校验 scope）+ `ServiceUnitClientFactory`。
- `src/MyPowerTools.Ipc.Shared`：从 HostControl 抽出的通用 channel factory + bearer-token interceptors + `AuthTokenStore`；HostControl 已改为引用它并保持原公开 API。
- `proto/mpt_service_manager_v1.proto`：ServiceManager 线协议（ListUnits/GetUnit/Start/Stop/Restart/Reload/TailLogs(stream)/SubscribeUnitEvents(stream)）。
- `MyPowerTools.Abstractions`：`ServiceUnitManifest`/`Snapshot`/`Event`/`State`/`IServiceUnitClient`/`IServiceUnitClientFactory`/`NullServiceUnitClient`。
- A3 Process Gate 已绿：`scripts/verify-architecture.ps1 -Tier Process` 驱动 `tests/architecture-gate`，6 项断言全过（list/active/idempotent-PID/scope-isolation/stop/admin-confirm），全 solution build 0 错误。
- **尚未完成**：登录启动项注册（第四批安装器）、Shell 统一 Services 页面、ScreenEase 真实 unit 切换、重接管在真实 unit 上的二次验收。

### 2. 第一方工具仍静态嵌入 Shell

`src/MyPowerTools.Shell.Avalonia/MyPowerTools.Shell.Avalonia.csproj` 仍然：

- 从 `tools/adb-forwarder/current-integration` 链接 Services/ViewModels/Views。
- 从 `tools/remote-notifications/current-integration` 链接 Services/ViewModels/Views。
- 从 `tools/screenease/current-integration` 链接 Services/ViewModels/Views。
- 从 SmartBird 与 Doubao integration 链接对应源码。
- 直接引用 SmartBird-owned WebToolHost 项目。

`ShellWorkspaceController.Tools.cs` 仍含第一方工具 ID、专用路由以及具体 View/ViewModel 构造。

### 3. 后台任务仍依赖错误生命周期

- Remote Notifications 的轮询使用 `ShellPageDataService` 内的 Avalonia `DispatcherTimer`。
- 豆包使用 Runner 入口直接构造的 `DoubaoRuntimeSupervisor`。
- ScreenEase 的 MPT 页面与外部 `ScreenEase.CoreService` 已能通信，但该服务尚未纳入 MPT Service Unit 模型。

### 4. WebToolHost 所有权仍倒置

- 底座 `src/MyPowerTools.WebToolHost` 不存在。
- 当前项目位于 `tools/smartbird-thermostat/current-integration/src/MyPowerTools.WebToolHost`。
- 缺少 SmartBird submodule 时，Shell 构建会缺少通用 Web Surface 基础设施。

### 5. Submodule 与独立工具包尚未闭环

- `.gitmodules` 仍使用本机 `file:///C:/Users/lixinrui/repo/MyPowerTools.ToolRepos/...` URL，另一台机器无法直接 clone。
- 七个 submodule 根目录都没有统一 `tool.json`。
- 现有 tool manifests 位于 `modules/**/ui/tool.json` staging 目录。
- 第一方工具独立 `.mptpkg`、Suite 收集与安装后的动态加载尚未完成。

### 6. 最小架构测试尚未实现

- `tests/MyPowerTools.ArchitectureTests` 不存在。
- `scripts/verify-architecture.ps1` 不存在。
- A1-A5 目前只有计划说明。
- 现有 `src/MyPowerTools.Tests` 引用了 Shell 与多个第一方工具，构建耦合较大。
- 现有 `scripts/smoke.ps1` 会执行 restore、全 solution build、全量测试、打包、截图和模板验证，适合 Full/Release。

### 7. 当前树缺少新验证

- `docs/PROJECT_STATUS.md` 中的 191 tests passed 等数据来自 2026-07-06。
- 当前分支拥有大量后续修改和未跟踪文件。
- 本轮没有执行 restore、build、test、安装或 GUI 验收。
- 新会话完成首个代码批次后，应运行对应 Quick/Process 门禁，再根据影响范围决定完整构建。

## 下一步执行顺序

以 `docs/18-architecture-revision-fast-plan.md` 为唯一 checklist，快速完成以下批次。

### 第一批：ServiceManager + ScreenEase 纵向切片

1. 新建 Service Unit contracts、状态机与 scoped/admin clients。
2. 新建独立 `MyPowerTools.ServiceManager` 进程和 typed IPC。
3. 使用版本化安装目录、PID/实例令牌、日志与重启策略。
4. 新建 `test-service-unit` 和 A3 Process Gate。
5. 将 ScreenEase 后台能力迁为 `ScreenEase.Service` unit。
6. 在 ScreenEase 自定义页面接入 scoped client。
7. 在 `System > Services` 增加统一 Services 页面。
8. 验收 Shell 重启、Runner 重启和 solution rebuild 期间 ScreenEase Service PID 保持。

第一批完成定义：ScreenEase 工具页与统一 Services 页面可以管理同一个 unit；任一入口操作后另一个入口自动更新；Surface 故障后统一页面仍可恢复服务。

### 第二批：动态 Tool Catalog 与通用 Surface Host

1. 将 WebToolHost 迁入底座 `src/`。
2. 扩展统一 `tool.json`，覆盖 Dotnet/Web/Native/Headless、Runtime 和 Service Units。
3. 实现通用 Dotnet Surface loader 与可卸载 AssemblyLoadContext。
4. Shell 导航由 Tool Catalog 生成。
5. 删除 Shell csproj 的 `tools/**` Compile Include 与专用 ProjectReference。
6. 删除 `ShellWorkspaceController` 的第一方 ID、route switch 和具体 View/ViewModel 构造。
7. 完成 A1、A2、A4 门禁。

### 第三批：迁移活跃工具

1. Remote Notifications：后台轮询、验签、历史和 banner 迁入 Service Unit。
2. Doubao：Supervisor 迁入 Service Unit，页面保持 MVVM 异步响应。
3. ADB：动态 Dotnet Surface，保留有线/无线两条清晰工作流。
4. SmartBird：动态 Web Surface，使用底座 WebToolHost 与 scoped service API。
5. Mihomo Multi Monitor：外部 SDK 工具，修复 settings 插值、真实 URL、恢复页和外部打开。

### 第四批：依赖拆分与发行

1. HostControl 拆分 Contracts/Client/Server。
2. UI 拆分 Primitives/Shell/Testing。
3. CLI 与 Shell/视觉测试解耦。
4. 修复 `.gitmodules` 为可访问 Git URL，本机镜像通过 git config override 使用。
5. 每个 submodule 独立 build/pack，Suite 收集到 `artifacts/tools/<id>/<version>`。
6. 安装包包含 Shell、Runner、ServiceManager、WebToolHost 和所选工具包。

### 第五批：删除过渡代码与真实机器验收

1. 删除旧 source-link、专用 route、工具源码副本和失效 integration 路径。
2. 更新架构文档、生命周期文档和 SDK 文档。
3. 生成完整安装包。
4. 在独立 Windows 测试机执行 A5：ScreenEase、通知、豆包、ADB、SmartBird 各一条真实核心路径。

## 最小测试策略

- Quick：A1 依赖边界 + A2 动态发现/数据自治，目标增量耗时不超过 5 秒。
- Process：A3 Service 生命周期/双 UI + A4 故障域，修改进程边界后运行，目标不超过 30 秒。
- Release：A5 安装包真实机器闭环，仅候选安装包生成后执行一次。
- Full：保留 `scripts/smoke.ps1`，用于发布或公共契约大改。
- 日常测试使用专用 TestUnit、独立 data root、唯一 pipe 和临时端口。
- 日常测试禁止连接、停止或重启用户正在使用的 ScreenEase.Service。
- 视觉验收只在相关页面改动时保留 ScreenEase 自定义 UI 与统一 Services 页面两张截图。

## 当前运行现场（2026-07-14 读取快照）

此节中的 PID 会变化，新会话执行动作前必须重新查询。

- `ScreenEase.CoreService.exe` 当时 PID 为 `28744`，运行自：
  `C:\Users\lixinrui\Documents\Codex\2026-07-01\careueyes-ida-pro-core-service\outputs\ScreenEase\src\ScreenEase.CoreService\bin\Release\net8.0\ScreenEase.CoreService.exe --pipe-only`
- Debug `MyPowerTools.Runner.exe` 当时 PID 为 `126036`，使用仓库 `modules` 和 `%LOCALAPPDATA%\MyPowerTools`。
- `powertoold.exe` 当时从仓库 staging module 目录运行。
- 豆包 planner/tool/MCP 服务存在真实运行进程，另有许多 stdio MCP 进程。它们可能属于其他 Codex/Computer Use 会话。
- 禁止使用 `Get-Process dotnet/python | Stop-Process`、按名称批量 kill 或清理所有子进程。

## 绝对不要再踩的坑

### 架构与生命周期

- 禁止把页面定时器当成后台服务。Remote Notifications 已因此只在特定 UI 生命周期内工作。
- 禁止在 Runner 入口直接构造长期工具专用 Supervisor。
- 禁止让普通 Shell/Runner build 覆盖已激活 Service Unit 文件。
- 禁止用当前平台 `IServiceManager` 冒充新 ServiceManager。前者是 OS service capability provider。
- 禁止让 ServiceManager 进程退出时连带杀死已运行 unit；需要 PID/实例令牌重接管。
- 禁止用 Tool Surface 直接启动后台进程；所有生命周期动作进入 ServiceManager。

### 工具接入

- 禁止继续向 Shell 添加工具 ID、工具 route switch、专用 ViewModel 构造和工具源码链接。
- 禁止只复制源码后声称完成 submodule/插件迁移。验收必须看到独立 build、manifest、动态刷新和独立 package。
- 禁止把通用 WebToolHost 放在单个工具仓库中。
- 禁止要求工具只能使用 .NET。Surface 契约统一，Runtime 语言保持开放。
- 禁止引入严格 git/submodule 版本锁。开发便利优先。

### 用户工作流与 UI

- 禁止让 `Open` 打开模块详情或诊断页。
- 禁止用模块诊断页冒充工具产品页。
- 禁止实现与原工具工作流无关的空壳 UI。先读 submodule 中原工具源码和原界面。
- 禁止使用巨大低价值状态栏、重复标题、过量空白和多层工具栏挤压核心信息。
- ADB 页面必须直接表达有线设备与无线设备两条路径，避免抽象成用户无法理解的 mappings 工作流。
- ScreenEase 的视觉层级与紧凑布局可作为复杂工具参考。
- 中文字体需要统一 FontFamily 与 fallback，避免同一页面出现两套中文字形。

### 异步、WebView 与故障

- 禁止在导航方法中等待多个初始化 HTTP 请求。View 先显示，ViewModel 后台加载并响应式更新。
- WebView 初始加载必须有不透明 loading surface，避免短暂透出后方窗口。
- WebView 原生控件必须留在独立 WebToolHost 故障域。
- URL 中的 `${settings.*}` 必须在导航与外部打开前完成插值和 URI 校验。
- 工具失败要产生局部恢复页面，Shell 与其他工具继续运行。

### 验收与安全策略

- 禁止只凭 build、静态页面或 mock health 报告成功。
- 核心功能必须启动真实应用、调用真实服务并记录外部可观察结果。
- 禁止重新阻止 `0.0.0.0`；用户已经明确允许。
- 禁止强制所有工具数据经 Host SettingsStore。工具数据自治已定案。
- 禁止把完整测试、全主题截图和异常穷举放进日常开发门禁。
- GUI 调试默认使用 headless/Process smoke；用户明确要求查看时再启动真实窗口。

### 工作树与环境

- 禁止清理当前 dirty worktree、回退用户文件、覆盖 submodule 改动或重新生成整棵目录。
- 文件编辑使用小范围补丁，先检查 overlapping changes。
- 新建进程测试必须使用独立临时目录和唯一资源名。
- 对正在运行的 ScreenEase、豆包、SmartBird、powertoold 进程先确认所有权，再执行停止或重启。
- dorm 部署场景不需要 ADB 与 SmartBird；不要把它们作为该机器的必装服务。
- veeam 可承担 Windows GUI/安装验证，执行前先确认连接与测试范围。

## 重要路径

- 主计划：`C:\Users\lixinrui\repo\MyPowerTools\docs\18-architecture-revision-fast-plan.md`
- 当前 Shell 项目：`C:\Users\lixinrui\repo\MyPowerTools\src\MyPowerTools.Shell.Avalonia`
- 当前 Runtime：`C:\Users\lixinrui\repo\MyPowerTools\src\MyPowerTools.Runtime`
- 当前工具 submodules：`C:\Users\lixinrui\repo\MyPowerTools\tools`
- 当前 staging packages：`C:\Users\lixinrui\repo\MyPowerTools\modules`
- SDK artifacts：`C:\Users\lixinrui\repo\MyPowerTools\artifacts\sdk`
- ADB 原始源码：`C:\Users\lixinrui\source\repos\AdbForwarder`
- Mihomo Multi Monitor 源码：`C:\Users\lixinrui\Documents\Codex\2026-06-19\ubuntu-home-lixinrui-repo-mihomo-l2blug6i7scw\work\status-monitor`

## 安全开始方式

1. 读取本文件与 `docs/18-architecture-revision-fast-plan.md`。
2. 运行 `git status --short` 与 `git submodule status`，确认当前现场。
3. 查询 ScreenEase、Runner、powertoold、WebToolHost 和豆包相关进程，仅记录，不执行批量停止。
4. 从第一批 ServiceManager contracts 与测试 fixture 开始，小范围修改。
5. 每完成一个可运行闭环，更新 plan checkbox 与本交接的“已完成/当前卡点”。
6. 用户要求查看 GUI 时，先完成 headless 构建与 Process Gate，再启动真实窗口。
7. 报告成功时给出实际命令、进程/PID、状态事件和核心功能结果。

## 最终完成判据

- Shell csproj 中没有来自 `tools/` 的 Compile Include 或工具专用 ProjectReference。
- ShellWorkspaceController 中没有第一方工具 ID 与专用页面构造。
- 第一方工具根目录具有独立 manifest、构建入口和 package。
- 新增/删除工具目录后刷新即可出现/消失。
- ServiceManager、统一 Services 页面和 scoped tool service API 可用。
- ScreenEase Service 在 Shell build、Shell 重启和 Runner 重启期间保持运行。
- Remote Notifications 在任意页面和 Shell 关闭期间持续接收并保存消息。
- 豆包页面切换即时，后台状态异步更新。
- SmartBird WebHost 崩溃只影响 SmartBird。
- ADB 页面提供清晰的有线/无线真实操作路径。
- 完整安装包可在另一台 Windows 机器安装并运行选定工具。
- A1-A4 快速架构门禁通过，A5 真实机器核心路径通过。
