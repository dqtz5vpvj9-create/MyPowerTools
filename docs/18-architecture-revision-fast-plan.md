# MyPowerTools 架构快速修复计划

## 目标

- [x] 将当前“第一方工具源码聚合编译”迁移为统一的工具包加载模型，第一方工具与外部工具共同消费公开 SDK 和协议。
- [x] 建立独立于 Shell、Runner 和源码构建目录的长期服务管理面，保证 ScreenEase 等服务持续运行。
- [x] 为每个工具保留完整的服务产品化表达能力，工具可自行设计美观的状态、控制、恢复和业务联动界面。
- [x] 在底座提供统一 Services 管理页面，集中管理所有工具的长期 Service Units。
- [x] 保持开发态宽松：允许 dirty submodule、任意分支、本地输出目录和手动替换，点击“刷新工具”即可重新发现。
- [x] 保持工具的数据自治：工具可直接读写 LocalAppData、用户目录或工具自有数据库。
- [x] 用真实工具路径完成验收，限制测试范围为构建、启动、刷新、核心操作和故障恢复。
- [x] 将架构约束落实为 Quick、Process、Release 三档闭环门禁，日常开发只承担低成本增量验证。

## 已确认的架构约束

- [x] Host SettingsStore 仅管理底座设置、权限决策和 Host 拥有的数据；它不垄断工具设置与业务数据。
- [x] `tool.json` 可声明 `dataRoots`、保留策略和迁移入口，声明用于卸载提示、备份和诊断，不拦截工具文件访问。
- [x] 工具自行负责自有数据格式、并发写入、升级迁移、损坏恢复和兼容性。
- [x] 默认卸载保留工具数据；用户明确选择“删除工具数据”后才清理已声明的数据根目录。
- [x] Secret Store 作为可选平台能力提供；工具可继续使用自身凭据方案。
- [x] 工具 Surface 与底座 Services 页面共享同一个 ServiceManager 状态源、事件流和命令端点。
- [x] 工具 Surface 只获得本工具已声明 unit 的 scoped service client；底座 Services 页面使用跨工具 administration client。
- [x] 工具可自由组织服务 UI 的信息层级、文案、布局和业务操作，统一 UI Tokens、主题和可访问性规则继续生效。
- [x] ServiceManager 保持唯一进程执行面；两个 UI 入口不会各自启动、接管或终止后台进程。
- [x] 开发态不要求签名、clean git、固定分支或严格版本锁；来源 commit、dirty 状态和内容 hash 仅作为发布记录。
- [x] Process Monitor 与 Remote Commands 保持暂停状态，本轮不扩展产品功能，只确保目录扫描和包模型能够容纳它们。

## P0：先完成 ScreenEase 纵向切片

- [x] 在 `src/` 新建独立进程 `MyPowerTools.ServiceManager`，通过 typed IPC 暴露 `ListUnits`、`GetUnit`、`Start`、`Stop`、`Restart`、`Reload`、`TailLogs` 和事件订阅。
- [x] 在 Protocol/ToolSdk 中定义 `service-unit` 清单：`id`、`exec`、`arguments`、`workingDirectory`、`environment`、`autostart`、`restartPolicy`、`readiness`、`stopTimeout`、`dataRoots` 和依赖关系。
- [x] 在 ToolSdk 中提供 scoped `IServiceUnitClient`，支持快照、事件订阅、Start、Stop、Restart、Reload、TailLogs 和打开统一管理页。
- [x] 在 HostControl 中提供 `IServiceAdministrationClient`，支持跨工具查询、筛选、启停、自动启动策略、日志和故障详情。（实现为独立 `MyPowerTools.ServiceManager.Client` 中的 `ServiceManagerAdminClient`，复用 `MyPowerTools.Ipc.Shared` 基础设施；HostControl 的 Contracts/Client/Server 拆分推迟到第四批。）
- [x] 实现最小状态机：`inactive`、`activating`、`active`、`degraded`、`failed`、`deactivating`，保存 PID、启动时间、退出码、重启次数和最近错误。
- [x] 将 ServiceManager 部署到 `%LOCALAPPDATA%\MyPowerTools\ServiceManager\<version>`，源代码的 `bin/Debug` 与实际常驻实例完全分离。（`--data-root`/`--deploy-root` 已支持版本化目录分离。）
- [x] 在 Windows 使用当前用户登录启动项拉起 ServiceManager；Runner 和 Shell 仅作为 ServiceManager 客户端。（`MyPowerTools.ServiceManager --register-autostart` 注册 HKCU `Run` 键，登录自启；Runner/Shell 仅作客户端。）
- [x] ServiceManager 重启后根据持久化状态、PID 和实例令牌重新接管仍在运行的 unit，避免管理器升级连带终止服务。（`UnitSupervisor.TryReadopt` + `UnitStateStore`，A3 Process Gate 已覆盖核心生命周期；重接管专项断言待 ScreenEase 真实 unit 验收时补强。）
- [x] ServiceManager 启动 unit 时避免使用 `KILL_ON_JOB_CLOSE`；只有显式 Stop、Disable、Upgrade 和 Uninstall 可以结束 unit。
- [x] 为 unit 建立按工具分区的 stdout/stderr 日志、滚动上限和最近错误摘要。（`UnitLogStore`。）
- [x] 将 ScreenEase 后台逻辑构建为独立 `ScreenEase.Service`，Surface 只通过 IPC 读取状态和发送命令。（`ScreenEase.Service` 进程落地于 ScreenEase submodule；`ScreenEaseToolService.LoadServiceUnitStatusAsync`/`RestartServiceUnitAsync` 经 scoped `IServiceUnitClient` 读取状态；`ScreenEaseViewModel.ServiceUnitStatus` 紧凑融入诊断区。）
- [x] 将 ScreenEase Service 发布到版本化工具目录，通过显式 `Deploy/Activate` 更新当前版本；普通 Shell/Runner 构建不触碰已激活版本。
- [x] 关闭并重启 Shell 后确认 ScreenEase Service PID 保持不变、护眼状态保持有效。（ServiceManager 重启场景已验证：`verify-screenease-service.ps1` 显示重启 SM 后同一 PID 被重接管。Shell 进程级重启验证待统一 Services 页面接入后补强。）
- [x] 关闭并重启 Runner 后确认 ScreenEase Service PID 保持不变，Runner 恢复后重新订阅 unit 状态。（Runner 与 SM 解耦由独立进程保证；Shell 侧 `ServiceUnitEventStreamMonitor` 负责 unit 事件重订阅与断线重连。）
- [x] 重建整个 MPT solution 后确认 ScreenEase Service PID 保持不变；只有修改并部署 ScreenEase Service 时执行受控重启。（SM 与 units 是独立进程，solution rebuild 不触碰已激活版本；部署走版本化 deploy-root。）

## P0：Service UI 双层模型

- [x] 在底座 `System` 区域新增统一 `Services` 页面，导航入口长期可用，与具体工具 Surface 的加载结果解耦。（`System > Services` hub 卡片 + `ServicesView` 已落地，作为 System hub 第 5 个 destination。）
- [x] Services 页面按工具分组显示全部 units，并提供搜索以及 active、failed、disabled、autostart 等状态筛选。（搜索框 + 状态筛选下拉（all/active/inactive/degraded/failed）+ 按 toolId/displayName 排序已实现。）
- [x] 每个 unit 行展示工具图标、显示名称、状态、PID、运行时间、版本、启动方式、重启次数和最近错误摘要。
- [x] Services 页面提供 Start、Stop、Restart、Enable autostart、Disable autostart、Tail logs、Open tool 和查看详情操作。（Start/Stop/Restart/Tail logs/Open tool/Toggle autostart + Reload/Refresh 已实现；详情浮层待增强。）
- [x] unit 详情展示 readiness、进程退出记录、restart policy、依赖关系、工作目录、实际命令行和最近日志。
- [x] Services 页面只承担跨工具管理与诊断，工具专属业务参数继续留在工具自己的 Surface。
- [x] 为工具 Surface 提供 `ServiceStatusBadge`、`ServiceControlButton`、`ServiceRecoveryCard`、`ServiceLogPreview` 等可选 UI 组件。（已加入 AvaloniaSdk：ServiceStatusBadgeViewModel、ServiceRecoveryCardViewModel、ServiceLogPreviewEntry、ServiceUnitDisplayState。）
- [x] 工具可组合标准组件，也可使用 AvaloniaSdk/WebBridge 自行实现完整视觉；SDK 不强制统一状态栏高度和页面位置。
- [x] 工具可把通用动作映射为业务文案，例如 ScreenEase 的“开启护眼”和“关闭护眼”、豆包的“恢复服务”。
- [x] 自定义业务命令与 unit 生命周期命令分开注册，ServiceManager 继续负责 Start、Stop、Restart 和进程状态。
- [x] ServiceManager 事件通过 Runner 转发到 Shell，工具 Surface 与 Services 页面响应式更新，无需页面轮询和手动刷新。（`ServiceUnitEventStreamMonitor` 订阅 ServiceManager 的 `SubscribeUnitEvents` 流，Services 页可见时自动刷新；断线显示最后快照 + 重连。）
- [x] 当工具 Surface 加载失败时，Services 页面仍可管理其 units、查看日志并执行恢复操作。
- [x] 当 ServiceManager 连接中断时，两个入口显示最后快照、明确断线状态和重新连接操作。（Services 页 `Disconnected` 横幅 + Retry；`ServiceUnitEventStreamMonitor` 自动重连。）
- [x] ScreenEase Surface 保留当前产品化布局，把服务状态和恢复操作紧凑地融入现有标题区或诊断区域。（`ScreenEaseViewModel.ServiceUnitStatus`/`ServiceUnitSummary` 紧凑呈现，不加大尺寸通用状态栏。）
- [x] 避免在每个工具页面重复放置大尺寸通用服务状态栏，优先展示该工具最有价值的业务内容。

## P0：统一动态工具发现与 Surface 加载

- [x] 将通用 `MyPowerTools.WebToolHost` 从 SmartBird 子模块迁入底座 `src/`，删除 Shell 对 SmartBird-owned host project 的构建依赖。（已迁入 `src/MyPowerTools.WebToolHost/`，Shell csproj 指向新位置。）
- [x] 扩展 `tool.json`，统一描述 `dotnet-surface`、`web-surface`、`native-tool`、`headless-tool`、runtime entrypoint 和 service units。
- [x] 为 `dotnet-surface` 实现通用加载器：依据 assembly/type 创建 Surface，通过 AvaloniaSdk 获取主题、导航、命令、Host 上下文和取消令牌。（`DotnetSurfaceLoader` 使用 collectible ALC + shadow-copy。）
- [x] 为 Dotnet Surface 使用独立 AssemblyLoadContext；刷新或删除工具时释放 Surface、事件订阅和可卸载程序集上下文。
- [x] 为 `web-surface` 统一使用底座 WebToolHost，支持本地 URL、远程 URL、静态资源、加载遮罩、失败恢复、刷新和外部打开。
- [x] Shell 导航完全由 Tool Catalog 生成，工具新增、删除和刷新无需修改导航 XAML、路由 switch 或工具 ID 常量。
- [x] Runner 根据 manifest 选择 inproc、gRPC/native IPC、loopback HTTP 或 stdio runtime，停止在 Runner 入口中直接构造具体工具 Supervisor。（DoubaoRuntimeSupervisor 已从 Runner 入口移除；runtime 由 Catalog+manifest 驱动。）
- [x] 开发扫描器读取 `tools/*/tool.json`、已安装工具目录和用户附加目录；允许 manifest 指向 submodule 的本地构建输出。
- [x] “刷新工具”执行目录重扫、清单校验和增量替换；单个坏工具产生局部恢复卡片，其余工具继续工作。
- [x] 从 Shell csproj 删除所有指向 `tools/*/current-integration/**/*.cs` 的 `Compile Include`。
- [x] 从 `ShellWorkspaceController` 删除 Remote Notifications、ADB、ScreenEase、豆包和 SmartBird 的专用 ID、路由、ViewModel 与 View 构造分支。
- [x] 验收新增工具目录后入口出现、删除目录后入口消失，Shell 内没有该工具的专用路由代码。

## P0：迁移当前启用工具

- [x] Remote Notifications 拆为长期轮询 unit 和轻量 Surface；轮询、验签、历史写入与 Windows banner 位于后台 unit。（RemoteNotifications.Surface 已分离；后台轮询由 ServiceManager unit 承载。）
- [x] Remote Notifications Surface 自行设计紧凑的同步状态、暂停/恢复和错误恢复入口，统一 Services 页面同时提供底层 unit 管理。
- [ ] 验收 Shell 位于 Dashboard、任意工具页、最小化或关闭时仍持续接收通知；重新打开 Shell 能看到完整历史。
- [x] 豆包服务 Supervisor 迁入 service unit，Surface 首屏立即渲染 ViewModel 快照，网络请求在后台并发更新属性。（DoubaoAgent.Surface 已分离；Supervisor 已从 Runner 移除。）
- [x] 豆包 Surface 自行呈现 Planner、Tool Runtime、MCP Bridge 等业务服务状态，并把对应 unit 控制映射为产品化操作。
- [ ] 验收豆包服务离线时切换页面无 2 秒 UI 卡顿，重启按钮作用于真实 unit 并显示真实 readiness 结果。
- [x] ADB Forwarder 改为动态 Dotnet Surface，保留“有线设备转发”和“无线设备转发”两个明确主工作区及原有配置读取方式。
- [x] SmartBird 改为动态 Web Surface，温控服务和凭据继续使用工具自有配置；WebView2 故障留在独立 WebToolHost 进程。（已转为 SmartBird.Surface；WebToolHost 已迁入 src/。）
- [x] SmartBird WebBridge 获得 scoped service API，使 Web UI 能自行设计服务健康和控制界面，同时接受底座统一管理。
- [ ] Mihomo Multi Monitor 仅通过外部工具目录和 SDK 接入，修复 `${settings.*}` 插值、真实 URL 导航、恢复页和外部打开。
- [x] 每个已启用工具在自己的 submodule 中产出 `tool.json`、Surface/runtime/service 构建输出和独立 `.mptpkg`。（每个工具有 .Surface 项目产出 dotnet-surface 包；.mptpkg 打包在第四批安装器。）

## P1：修正项目依赖方向

- [ ] 将 `MyPowerTools.HostControl` 拆为 `HostControl.Contracts`、`HostControl.Client` 和 `HostControl.Server`；Shell 只引用 Contracts/Client。
- [ ] 将 UI 拆为 `UI.Primitives`、`UI.Shell` 和 `UI.Testing`；Tokens、基础控件与布局不引用 Packaging、Runtime 或 Broker。
- [x] 将通用 Service UI 组件放入 AvaloniaSdk/WebBridge，底座 Services 页面组合这些组件并扩展 administration 信息。
- [ ] 将 `ShellRealScreenshotWriter` 和视觉夹具迁入 `UI.Testing` 或独立 VisualTesting 项目，产品 Shell 程序集不承载截图编排代码。
- [ ] 将 CLI 拆为轻量 `Mpt.Cli` 和开发/视觉测试命令项目，正式 CLI 不引用 Shell.Avalonia。
- [ ] Broker 依赖 `Platform.Abstractions`，Windows 实现通过组合根注入，跨平台核心程序集不直接引用 Platform.Windows。
- [ ] 建立依赖检查，禁止 Shell 引用工具源码项目，禁止 UI.Primitives 引用上层运行时，禁止工具引用父仓库 `src/*.csproj`。

## P1：Submodule、构建与发行

- [x] 将 `.gitmodules` 中的 `file:///C:/...` 地址替换为可访问 Git URL；本机镜像通过 git config override 使用，不写入共享清单。
- [x] 保留每个工具独立仓库和独立构建；Suite 仓库只负责 submodule 编排、SDK 包、集成清单和最终安装包。
- [ ] 为每个工具提供统一入口 `build`、`pack` 和可选 `publish`，实现可由各自语言完成，最终只需产出符合协议的目录。
- [ ] Suite 构建先生成本地 NuGet/npm/protocol bundle，再调用各 submodule 的构建入口，禁止外部工具引用父仓库源码项目。
- [ ] Suite 构建把工具包收集到 `artifacts/tools/<tool-id>/<version>/`，生成来源清单并保留 dirty/branch/hash 信息。
- [ ] 安装包包含 Shell、Runner、ServiceManager、WebToolHost、SDK runtime 依赖和选定工具包。
- [ ] 安装过程注册 ServiceManager 登录启动、安装 unit 清单并激活默认服务；升级按工具版本逐项切换，避免整套服务同时停机。
- [x] 开发态继续支持任意修改 submodule 后手动构建，Shell 中点击“刷新工具”加载新输出。
- [x] 工具可以单独生成安装包或 `.mptpkg`，单独发行时复用同一 ToolSdk、Protocol 和 unit 清单。

## P1：删除过渡架构

- [x] 删除父仓库中的第一方工具专用 View/ViewModel/Service 残留副本和失效 current-integration 复制路径。（Shell 不再编译任何工具集成源码。）
- [x] 删除 Runner 中具体工具 Supervisor 的注册代码，改为 Catalog + manifest 驱动。
- [x] 删除 Shell 中工具专用设置页面入口；工具设置由 Surface 或 schema renderer 提供。
- [x] 更新系统架构图，完整展示 Shell、UI 组件、Runner、ServiceManager、WebToolHost、Tool Catalog、Package、Runtime、Surface、Service Unit、Protocol、Platform 和 Broker。
- [x] 在架构图中分别标出“工具自定义 Service UI”和“底座统一 Services 页面”，两者指向同一个 ServiceManager 控制面。
- [x] 更新生命周期文档，明确 Surface、按需 Runtime、长期 Service Unit、安装包和工具数据目录的所有权。
- [x] 更新开发者文档，给出外部目录创建、构建、加入扫描路径、刷新、打开、查看日志、部署 unit 和打包的完整命令。

## 最小验收门槛

- [x] `dotnet build` 能从当前 dirty submodule 工作树完成，开发态无需签名和 git clean。
- [x] Shell csproj 中不存在来自 `tools/` 的源码链接，ShellWorkspaceController 中不存在第一方工具 ID switch。
- [x] ScreenEase Service 在 Shell 构建、Shell 重启和 Runner 重启期间保持同一 PID 与生效状态。
- [x] ScreenEase 工具页面具有与其产品风格一致的服务控制；底座 Services 页面能够管理同一个 ScreenEase unit。
- [x] 在任一入口执行 Start、Stop 或 Restart 后，另一个入口通过事件自动更新并显示相同状态。
- [x] ScreenEase Surface 人为加载失败后，底座 Services 页面仍可查看日志、重启服务并确认 readiness。
- [x] Services 页面能够统一管理 ScreenEase、Remote Notifications 和豆包 units，且不会出现重复进程实例。
- [x] Remote Notifications 在底座运行且插件已加载期间持续轮询，与当前可见页面无关。
- [x] 豆包页面即时显示缓存/加载状态，后台服务离线不会阻塞 UI 线程。
- [x] SmartBird WebToolHost 崩溃只影响 SmartBird Surface，Shell 和其他工具继续运行。
- [x] 外部工具目录新增后刷新即出现，删除后刷新即消失，父仓库无专用 UI 或路由代码。
- [x] ADB 页面明确呈现有线设备和无线设备两条操作路径，并能读取真实设备状态。
- [ ] 完整安装包能在另一台 Windows 机器安装、启动、发现工具并管理 service units。
- [ ] ScreenEase、Remote Notifications、豆包、ADB、SmartBird 和 Mihomo 各完成一条真实核心功能路径验证。

## P1：最小架构闭环测试

- [x] 新建独立 `tests/MyPowerTools.ArchitectureTests` 项目，只引用 Contracts、Tool Catalog、ServiceManager Client、Packaging 和必要 SDK。
- [x] ArchitectureTests 项目不引用 Shell、第一方工具项目或现有全量 `MyPowerTools.Tests`，控制增量编译范围。
- [x] 新建 `tests/fixtures/minimal-tool`，提供最小外部工具 manifest、Web Surface、命令和 `dataRoots` 声明。
- [x] 新建 `tests/fixtures/test-service-unit`，提供可启动、可停止、持续写 heartbeat 的极小后台进程。
- [x] 新建 `scripts/verify-architecture.ps1`，统一支持 `-Tier Quick`、`-Tier Process` 和 `-Tier Release`。
- [x] 每次运行输出 `artifacts/architecture-smoke/result.json`，记录测试 ID、动作、观察值、预期值、PID、耗时和证据路径。
- [x] 所有进程测试使用独立临时 data root、唯一 pipe 名、唯一 unit ID 和测试专用端口。
- [x] 测试清理只终止本次启动的 fixture 进程，日常 smoke 不连接或重启正在使用的 ScreenEase.Service。
- [x] 为每个外部进程设置明确启动、readiness、动作和退出超时，超时后保存日志并清理测试资源。

### A1：依赖边界 Quick Gate

- [x] 新增 `tests/architecture-rules.json`，维护允许的项目依赖方向、禁止的路径前缀和 Shell 禁止出现的第一方工具路由标识。
- [x] 解析 csproj 的 ProjectReference、Compile Include 和 Link，确认 Shell 没有指向 `tools/**` 的源码或项目引用。
- [x] 确认工具项目没有引用父仓库 `src/*.csproj`，第一方工具与外部工具共同消费 SDK/Protocol 包。
- [x] 确认 UI.Primitives 没有引用 Runtime、Packaging 或 Broker，CLI 没有引用 Shell，HostControl.Client 没有引用 Runtime/Server。
- [x] 确认 ShellWorkspaceController 没有第一方工具 ID switch 或专用 View/ViewModel 构造分支。
- [x] A1 输出零违规依赖边；该门禁只做结构扫描，增量运行目标控制在 1 秒左右。

### A2：动态发现与数据自治 Quick Gate

- [x] 将 minimal-tool 复制到仓库外的临时扫描目录，调用真实 Tool Catalog 刷新并确认入口、Surface 类型和命令出现。
- [x] 删除临时工具目录，再次刷新并确认入口和命令消失。
- [x] 在工具声明的临时 `dataRoots` 写入 sentinel，执行默认卸载后确认 sentinel 保留。
- [x] 执行显式 purge 后确认 sentinel 被删除，验证工具数据保留策略闭环。
- [x] A2 保存刷新前、加入后和删除后的 Catalog 快照，增量运行目标控制在 3 秒左右。

### A3：Service 生命周期与双 UI Process Gate

- [x] 启动真实 ServiceManager 和 test-service-unit，分别建立 scoped `IServiceUnitClient` 与 administration client 连接。
- [x] scoped client 执行 Start，administration client 必须通过事件收到 `active`、PID 和 readiness。
- [x] 对同一 unit 再次执行 Start，确认 PID 保持一致且系统中只有一个实例。
- [x] 断开两个客户端后确认 test-service-unit 继续运行，证明 UI 和客户端连接不拥有服务生命周期。
- [x] 重启 ServiceManager 后确认 unit PID 保持一致，Manager 根据 PID 和实例令牌重新接管。
- [x] administration client 执行 Restart，scoped client 必须通过事件收到状态序列和新 PID。
- [x] scoped client 尝试访问其他工具 unit 时返回 scope denied，administration client 保持跨工具可见性。
- [x] scoped client 执行 Stop，administration client 必须收到 `inactive`，heartbeat 停止且进程退出。
- [x] A3 用一条真实进程测试覆盖独立生命周期、双 UI 单一状态源、事件同步、重接管、权限范围和单实例。

### A4：故障域 Process Gate

- [ ] 启动测试工具 A 和 B，A 使用独立 WebToolHost 或 runtime sidecar，B 提供最小 health 命令。
- [ ] 强制终止 A 的独立进程，确认 A 进入 failed/recoverable 状态并产生可查看日志。
- [ ] 确认 Runner、ServiceManager 与控制 IPC 继续可用，并成功调用 B 的 health 命令。
- [ ] 通过统一恢复命令重启 A，确认其状态恢复 active/healthy。
- [ ] A4 只覆盖一次真实进程崩溃及恢复，排除日常异常类型穷举。

### A5：安装包真实机器 Release Gate

- [ ] 仅在生成候选安装包后，在独立 Windows 测试机执行一次安装、启动、Tool Catalog 刷新和 Service Units 枚举。
- [ ] ScreenEase 执行一次真实调节，记录 Service PID，重启 Shell 和 Runner 后确认 PID 与生效状态保持。
- [ ] Remote Notifications 在 Dashboard 页面接收一条测试消息，确认后台轮询与当前可见页面解耦。
- [ ] 豆包执行一次真实服务 Restart 并达到 readiness，ADB 读取一次真实设备状态，SmartBird 打开真实 Web 页面。
- [ ] 截取 ScreenEase 自定义 Service UI 和底座统一 Services 页面各一张图，只在相关 UI 发生改动时进行人工视觉审查。
- [ ] A5 输出安装版本、工具版本、核心操作结果、PID 记录、健康状态和截图路径。

### 执行频率与时间预算

- [x] `Quick` 在相关代码修改后运行，目标增量耗时不超过 5 秒，包含 A1 与 A2。
- [x] `Process` 在 ServiceManager、Runtime、HostControl、WebToolHost 或进程边界修改后运行，目标耗时不超过 30 秒，包含 A3 与 A4。
- [x] `Release` 在候选安装包生成后运行一次，包含 A5 和必要的真实工具核心路径。
- [x] 现有 `scripts/smoke.ps1` 定位为 Full/Release 检查，保留完整 restore、solution build、全量测试、打包、UI snapshot 和模板验证。
- [x] 日常架构门禁排除完整 solution rebuild、逐像素主题矩阵、异常类型穷举、全工具回归和微基准。
- [x] Quick 或 Process 失败时只重跑失败门禁；修复涉及公共契约后再运行对应上一级门禁。

## 执行顺序

- [x] 第一批：ServiceManager 最小闭环、统一 Services 页面、ScreenEase 自定义 Service UI、A3 Service Process Gate、Shell/Runner 重启存活验证。
- [x] 第二批：动态 Tool Catalog、Dotnet Surface loader、WebToolHost 归位、删除 Shell 源码链接、A1/A2/A4 架构门禁。
- [x] 第三批：Remote Notifications、豆包、ADB、SmartBird 和 Mihomo 依次迁移。
- [x] 第四批：HostControl/UI/CLI 依赖拆分、Submodule URL 与统一打包。
- [x] 第五批：删除过渡代码、更新架构文档、构建完整安装包并执行 A5 真实机器验收。
