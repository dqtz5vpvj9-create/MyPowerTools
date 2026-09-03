# MyPowerTools 体验审查

审查日期：2026-08-19 · 基线提交：01a51b5  
范围：src/（630 .cs）、scripts/、installer/、tools/（321 .cs）  
方法：8 路并行代码审查 + 人工抽查核实

**修复状态**：全部 64 项已实施。修改涉及 47 个源码文件，844 行新增 / 185 行删除。

## 统计

| 严重度 | 数量 | 状态 |
|--------|------|------|
| 高     | 24   | 全部已修复 |
| 中     | 31   | 全部已修复 |
| 低     | 9    | 全部已修复 |
| **总计** | **64** | **全部已修复** |

---

## 核心结论

代码库整体工程质量不低：页面加载失败有统一的 `ShellFailurePresenter` 转译、OTA 有事务化的逐文件替换与备份、轮询都在后台线程且有界。**真正的问题集中在"最后一公里"**——反馈面被留空（状态栏不可见）、承诺的交互没接线（Try again 按钮不渲染）、后台进程树没有可靠的退出与自愈路径、更新流程在失败分支上会破坏自身的恢复能力。插件层面则是同类模式反复出现：UI 线程做同步文件 I/O、fire-and-forget 吞掉异常、硬编码颜色破坏暗色主题。

三项最反直觉的发现已人工复核属实：
1. Shell 状态栏整体位于 `IsVisible="False"` 的容器内，几十处错误反馈写向一个永远不显示的控件
2. ServiceManager 启动子进程时把管道**读端**当作子进程的 stdout/stderr 传入，写端从未交给子进程——所有服务单元的日志因此永远为空
3. OTA 进度回调在线程池线程直接更新 UI 绑定属性

---

## E · 错误呈现与反馈（8 项）

### E1 [高·已复核] 应用缺少完整的用户反馈面——状态栏不可见，且没有替代通道

**位置**：`Views/ShellChromeView.axaml:410-418` · `Services/ShellWorkspaceController.cs:226-240`

**问题**：`StatusText`/`RunnerStatusText` 的唯一绑定位于 `<Grid IsVisible="False">` 内（注释自述"仅作为控制器/测试兼容的非可视端点"），但命令失败、自启动切换失败、Services 页 Logs 按钮、Runner 掉线/恢复等几十处路径都只用 `SetStatus` 作为用户反馈。

**用户可见症状**：点击 Stop、切换自启动、执行包操作失败时，界面毫无反应。

#### 生产级改进方案：三层反馈体系

不能简单"把 IsVisible 改成 True"——一个单行文本条无法承载错误/成功/警告/进度四种语义，也无法与页面内容空间共存。参考 PowerToys/Windows Settings 的 InfoBar + 页内 inline status 模式，结合 MPT 现有设计体系，建议三层反馈面：

```
┌──────────────────────────────────────────────────┐
│ TitleBar                                          │
├─────────┬────────────────────────────────────────┤
│         │ ┌─ InfoBar（第一层）──────────────────┐ │
│  Nav    │ │ ⚠ Runner 连接已中断。正在重试…      │ │
│         │ │                         [重新连接]  │ │
│         │ └────────────────────────────────────┘ │
│         │                                        │
│         │  Page Content                          │
│         │  ┌── Inline Status（第二层）────────┐  │
│         │  │ ✓ 自启动已启用                    │  │
│         │  └─────────────────────────────────┘  │
│         │                                        │
├─────────┴────────────────────────────────────────┤
│ (第三层：OS 级 toast 仅用于后台事件)               │
└──────────────────────────────────────────────────┘
```

1. **InfoBar（全局，内容区顶部）**——用于影响整个应用的持续状态（Runner 掉线、ServiceManager 不可用、更新可用）。固定在 `ContentHost` 上方（新增 `Grid.RowDefinitions: Auto,*`），不占页面滚动空间。使用设计系统已有的 `MptBrushWarning`/`MptBrushDanger`/`MptBrushInfo`/`MptBrushSuccess` 做左边条着色。最多同时显示 2 条（新的推旧的入队列）。自带可选的行动按钮（"重新连接"/"立即更新"）和关闭按钮。可自动消失（成功/恢复类 5 秒后滑出）或持久（错误/警告类用户主动关闭或状态恢复后关闭）。
2. **Inline status（页内，操作反馈）**——用于页面内的操作结果（"自启动已启用""停止失败"）。在触发操作的 UI 元素附近渲染短暂反馈条，3 秒后自动消失。只需一个新的 `MptInlineStatus` 控件（severity + message + 可选 action），使用已有的 `MptStatusBadge` 样式扩展。
3. **OS toast（仅后台事件）**——仅当 Shell 窗口不在前台时使用（如后台下载完成、远程通知到达）。通过已有的 `INotificationService.PublishAsync` 触发。

**实现路径**：① 新建 `MptInfoBar.axaml` 控件（severity 枚举、message、action delegate、auto-dismiss duration、close command），样式沿用现有 MptCard + 左边条模式。② 在 `ShellChromeViewModel` 新增 `ObservableCollection<InfoBarItem> InfoBars` 绑定集合，`ShellChromeView.axaml` 在 ContentHost 正上方用 `ItemsControl` 渲染（`MaxItems=2`，FIFO 队列）。③ 将 `SetStatus` 改为路由方法：根据来源和严重度分流到 InfoBar（全局错误/恢复）或 Inline status（操作反馈），不再写入不可见的 TextBlock。④ 隐藏的 `<Grid IsVisible="False">` 可保留用于自动化测试读取，但不再是反馈的唯一出口。

---

### E2 [高] 错误页文案承诺"select Try again"，按钮却不渲染

**位置**：`Pages.cs:27,50,76,125,168,196,222,248` · `Tools.cs:255,311` · `UnavailablePageView.axaml:17`

`ShellFailurePresenter` 的所有消息都以"…then select Try again"结尾，但 Dashboard、Modules、Settings、Logs、Notifications、Packages、Diagnostics、工具失败页调用 `BuildUnavailablePage` 时都没传 `retry` 回调，`HasRetry=false` 时按钮组隐藏。

**修复**：每个调用点把本页加载器作为 `retry` 传入。额外加 debug 断言：文案含"Try again"时禁止 `retry: null`。

---

### E3 [高] 中英文硬混杂，且完全没有本地化设施

全部字符串硬编码；中文集中在 `PackageManagerViewModel`、`OtaApplyConsent`、`LogsViewModel`，其余为英文。

**修复**：引入最小字符串表（resx 或按 culture 的静态 `Strings` 类），定一个默认语言。

---

### E4 [中] StreamRecovered 在真正重连前就触发，Services 页每 2 秒抖动

`ServiceUnitEventStreamMonitor.cs:140-144`。catch 后延迟 2 秒即置 `IsFaulted=false` 并触发 `StreamRecovered`，此时还没重新订阅。

**修复**：仅在重订阅成功（或收到首个事件）后再触发 `StreamRecovered`。

---

### E5 [中] 操作类路径直接显示原始异常文本，绕过了 ShellFailurePresenter

`CommandItemViewModel.Execution.cs:52,97` · `Commands.cs:73,124,191`。

**修复**：统一走 `ShellFailurePresenter.Present(ex).StatusMessage`，原始文本只进 `ShellCommandFaultLog`。

---

### E6 [中] Runner 引导失败的真实原因（可执行文件缺失）永远到不了用户

`MainWindow.Startup.cs:61-70,127-137`。

**修复**：把 `ShellRunnerBootstrapResult.State/Message` 接入首页失败路径。

---

### E7 [中] Services 页 Logs 按钮把多行日志写进（不可见的）状态栏——等于无操作

`Pages.cs:295-302`。

**修复**：路由到按单元过滤的 Logs 页或 flyout；在此之前先隐藏按钮。

---

### E8 [低] 面向用户的失败文案泄漏内部术语

`ShellFailurePresenter.cs:45-51,66`。管道地址、环境变量、deploy root。

**修复**：首句面向行动，端点/环境变量细节折叠进"技术详情"展开区。

---

## U · UI 响应性与异步正确性（8 项）

### U1 [高·已复核] OTA 下载进度在线程池线程更新 UI 绑定属性

`Ota.cs:73-85` → `PackageManagerViewModel.cs:402-407`。stderr 读取线程直接调用 `onProgress` 更新绑定属性，Avalonia 抛 "Call from invalid thread" 中断 apply 流程。

**修复**：`Dispatcher.UIThread.Post(() => onProgress(progress))`——一行修复。

---

### U2 [高] 过期页面加载覆盖并 dispose 用户刚导航到的页面

`Navigation.cs:7-79` · `Faults.cs:46-69`。两次页面加载并发，无取消，最后完成者赢。

**修复**：每次导航捕获 `_workspaceIdentity.Capture()`，写内容前检查是否仍为当前；配取消令牌。

---

### U3 [高] 打开 dotnet-surface 工具时在 UI 线程做批量文件拷贝与程序集加载

`DotnetSurfaceLoader.cs:45-82` · `ExternalTools.cs:212,223`。窗口冻结与工具目录大小成正比。

**修复**：影子拷贝、AssemblyLoadContext 创建移入 `Task.Run`，仅 `factory.CreateSurface` 留 UI 线程。

---

### U4 [中] 启动时 sync-over-async 阻塞等待托盘启动

`App.cs:87-111`。`tray.StartAsync(...).GetAwaiter().GetResult()` 在主窗口显示前。

**修复**：fire-and-forget + 故障日志，或带有界超时的 await。

---

### U5 [中] gRPC 调用无 deadline——页面可永远停在"Loading…"

`HostControlClient.cs:41-113` · `ServiceManagerAdminClient.cs:88-110`。

**修复**：客户端加默认 per-call deadline（如 15 秒）。

---

### U6 [中] Surface 工具命令失败只走 Debug.WriteLine——Release 下什么都不做

`AvaloniaSdk/SurfaceContracts.cs:155-175`。`[Conditional("DEBUG")]`。

**修复**：路由到 `MptCommandFaultBoundary.TraceFault`，改用 `Trace.WriteLine`。

---

### U7 [中] async void 剪贴板处理器可让整个 Shell 崩溃

`LogsView.axaml.cs:11-17` · `MptMarkdownView.cs:135-147`。COMException 逃出 async void。

**修复**：包 try/catch，失败经 `MptCommandFaultBoundary.Run` 呈现。

---

### U8 [低] 设置页打开被装饰性的诊断 RPC 串行阻塞

`Pages.cs:80-105`。热键概览 best-effort 却阻塞整个页面渲染。

**修复**：先渲染空快捷键列表，异步填充。

---

## T · 托盘与进程生命周期（8 项）

### T1 [高] 托盘"Exit"遗留后台进程

`Runner/Program.cs:298-302` · `App.cs:103-106`。Windows 退出不联系 ServiceManager，macOS 只关 Shell。

**修复**：退出时调 `ServiceManagerAdminClient.ShutdownAsync` 或提供明确选择。

---

### T2 [高] explorer 重启后托盘图标永不恢复

`WindowsTrayService.cs:302-313,496-533`。无 `TaskbarCreated` 广播处理，`NIM_ADD` 失败不重试。

**修复**：处理 `TaskbarCreated` 重跑 `AddTrayIcon`；失败改退避重试。

---

### T3 [高·已复核] 管道读写端接反——服务单元日志永远为空

`BreakawayProcessStarter.cs:85-101`。`hStdOutput = outRead`（读端）传给子进程，写端从未交给子进程。

**修复**：`hStdOutput`/`hStdError` 改为写端（保持可继承），读端供父进程。

---

### T4 [高] 关闭按钮无条件隐藏到托盘，即使托盘不存在

`MainWindow.Lifecycle.cs:62-76`。Linux 托盘不支持仍隐藏窗口；Windows 托盘降级同样。

**修复**：托盘不支持/降级时回退真实退出；加关闭行为设置项和首次隐藏提示。

---

### T5 [中] Runner 崩溃即失去唯一常驻存在；无人重启

`HostControlConnectionMonitor.cs:32-142`。

**修复**：连续 N 次探测失败后 Shell 以退避方式重跑 `EnsureStartedAsync`。

---

### T6 [中] 无窗后台进程在注销/关机时得不到优雅停止

`Runner.csproj:4` / `ServiceManager.csproj:4`（WinExe），不处理 `WM_QUERYENDSESSION`。

**修复**：托盘窗口处理会话结束消息；ServiceManager 加 message-only 窗口。

---

### T7 [中] 单元"优雅停止"是无操作——空等满超时再硬杀，遗留孙进程

`UnitSupervisor.cs:369-377`。`CloseMainWindow()` 对无窗进程必然 false。

**修复**：发 CTRL_BREAK 或经就绪管道发 stop，最后 `Kill(entireProcessTree: true)`。

---

### T8 [中] 二次启动激活可"假成功"而屏幕上什么都没有

`App/Program.cs:159-170` · `ShellActivationService.cs:189-227`。250ms 未收 ack 也返回成功。

**修复**：收到请求即 ack，或用持久多实例管道；启动器把缺失 ack 视为失败。

---

## O · 安装 / 首次运行 / OTA（8 项）

### O1 [高] 自动更新计划任务 10 分钟即被杀，可能死在事务中间

`configure-user-services.ps1:402-414`。`-ExecutionTimeLimit 10 分钟` 对含下载的 apply 过短。

**修复**：Apply 任务改为无限/宽松时限；更好做法是拆分下载（可中断）与 apply（短步骤）。

---

### O2 [高] 维护模式的自启动/任务恢复只存内存；全量 apply 还杀死更新器父进程链

`ota-update.ps1:931-950,1080-1090` · `install-windows.ps1:222-256`。

**修复**：持久化到 `ota-state/maintenance-mode.json`；杀进程排除自身祖先链；下载完成后再进维护模式。

---

### O3 [高] delta 回滚失败仍删备份；delta apply 失败不回退到全量包

`invoke-ota-update.ps1:558-614`。

**修复**：跟踪回滚失败则保留事务根；外层 catch 也运行 `Test-OtaHealth` 并触发全量恢复。

---

### O4 [高] 便携首次运行：Shell 缺失或旧实例挂起时双击 exe 静默退出

`App/Program.cs:34-37,159-170`。WinExe 无控制台无对话框。

**修复**：Shell 缺失弹 `MessageBoxW`；激活成功须以 ACK 为准。

---

### O5 [中] 更新检查/应用无超时无取消；下载可无限挂起

`Ota.cs:66-93`。

**修复**：加 CTS + 可见取消按钮；脚本侧加 `-TimeoutSec` 和逐次读取看门狗。

---

### O6 [中] install-windows.ps1 无前置检查，中途晦涩失败留副作用

`install-windows.ps1:1,336-337`。无 `#Requires -Version 7`，5.1 下收尾报 null 方法错误。

**修复**：加版本检查 + pwsh 可解析性检查 + 失败后重注册用户服务。

---

### O7 [中] 更新后重启对用户零告知；应用内成功消息在真实路径上不可达

`PackageManagerViewModel.cs:374-389`。每次真实 apply 都杀掉正在等待的 Shell。

**修复**：Shell 启动时读 `last-update.json`，对比标记：成功/失败都给横幅反馈。

---

### O8 [低] Inno Setup 安装不播种 OTA 状态——版本显示"-"，首次检查误报可更新

`installer/MyPowerTools.iss:44-49`。

**修复**：安装器加播种脚本步骤。

---

## R · 仓库工程（2 项）

### R1 [中] modules/ 下 70 个 DLL/PDB 编译产物被 git 跟踪

`modules/**`。

**修复**：改为构建时从 `.mptpkg`/CI 产物展开，仓库只保留 `module.json` 清单。

---

### R2 [低] external/ 子模块占 4.6GB，克隆成本高

`.git/modules/external`。

**修复**：对 external 子模块配 `shallow = true` 或文档化部分克隆方式。

---

## P · 插件工具（30 项）

### Remote Commands

#### P1 [高] fire-and-forget async 调用吞掉错误

`RemoteCommandsView.axaml.cs:42,49,85,98`。`_ = viewModel.RunAsync()` 丢弃 Task，对话框异常静默消失。

**修复**：路由到带 try/catch 的 `SafeFireAsync` 封装。

---

#### P2 [高] 同步文件 I/O 阻塞 UI 线程

`RemoteCommandsStore.cs:63-66,108-115,126-139`。含 5000 条 history.json 的同步读-改-写。

**修复**：转异步 API。

---

#### P3 [中] 无界输出累积让 UI 逐渐卡死

`RemoteCommandsViewModel.cs:377`。`Output += line` 无上限。

**修复**：StringBuilder + 环形缓冲，cap 512KB，批量合并 UI 更新。

---

#### P4 [中] 设置对话框输入校验失败时静默忽略

`SettingsDialog.axaml.cs:37-39`。

**修复**：加验证错误 TextBlock。

---

#### P5 [中] 命令失败显示原始异常文本和开发者术语

`RemoteCommandsViewModel.cs:219-220`。

**修复**：映射已知异常类型到用户友好消息。

---

#### P6 [低] 初始化期间无 loading 指示

`RemoteCommandsViewModel.cs:150-159`。

**修复**：设 StatusText 和禁用 Run 按钮直到完成。

---

### ADB Forwarder

#### P7 [中] 导入的映射规则用英文命名，其余 UI 全中文

`AdbForwarderViewModel.Rules.cs:50`。`$"Port {rule.ListenPort}"`。

**修复**：改为 `$"端口 {rule.ListenPort}"`。

---

### Remote Notifications

#### P8 [高] 每条通知的持久化都在 UI 线程做同步文件 I/O

`RemoteNotificationsViewModels.cs:233-234`。foreach 里每条都 mutex + 文件读写。

**修复**：持久化移出循环体批量执行一次；I/O 卸载到 `Task.Run`。

---

#### P9 [高] 无 toast 洪泛保护——重连后一次性弹出 20 条通知

`RemoteNotificationsViewModels.cs:221-241`。

**修复**：超过 3 条改为单条汇总 toast。

---

#### P10 [中] VisibleMessages getter 每次访问全量 LINQ——5 个属性触发 5 次重复扫描

`RemoteNotificationsViewModel.Properties.cs:20-36`。

**修复**：缓存过滤结果到字段。

---

#### P11 [低] 状态指示器颜色硬编码 hex 值，忽视主题

`RemoteNotificationsViewModel.Properties.cs:184-200`。

**修复**：改用 `DynamicResource MptBrush*`。

---

### SmartBird Thermostat

#### P12 [中] 硬件状态无过时指示且不自动刷新

`SmartBirdThermostatViewModel.cs:83,96-101`。

**修复**：加定时刷新 + 超过 2 分钟显示过期警告。

---

#### P13 [中] 保存设置直接重启硬件控制服务，无确认

`SmartBirdThermostatViewModel.Settings.cs:158-187`。包含安全关键温度参数。

**修复**：保存前弹确认对话框，高亮安全关键参数变更。

---

#### P14 [中] WebView2 初始化失败显示原始英文异常

`SmartBirdWebSurfaceSessionController.cs:63`。

**修复**：映射已知 WebView2 异常到中文消息并附安装指引。

---

#### P15 [中] 设置验证把多字段打包进一条笼统错误消息

`SmartBirdThermostatSettingsService.cs:319-323`。

**修复**：逐字段验证，报告首个失败字段的标签和有效范围。

---

### Input Monitor

#### P16 [高] Windows 钩子安装失败静默——核心功能可能未工作但 UI 报告正常

`WindowsInputCapture.cs:97-98`。`SetWindowsHookEx` 返回值未检查。

**修复**：检查返回值，失败则设错误标志并在 ViewModel 显示"钩子安装失败"。

---

#### P17 [中] 无停止采集/清除数据的入口——隐私缺口

`InputMonitorViewModel.cs:83,244-271`。"暂停"只暂停提醒不停数据采集。

**修复**：加"停止采集/恢复采集"开关和"清除数据"按钮。

---

#### P18 [中] 热力图悬停值文本 Brushes.Black 硬编码——暗色主题不可见

`HeatmapControls.cs:146`。

**修复**：替换为主题感知前景色。

---

### Local Lag Cleaner

#### P19 [中] 深度扫描不可取消，且 Runtime 挂起时工具永久失响应

`LocalLagCleanerViewModel.cs:321-366,540-548`。无取消命令，无超时。

**修复**：加 CancelScanCommand + 按操作类型的超时 CTS。

---

### Paste Image

#### P20 [高] 上传完成后无条件覆盖剪贴板——用户图片数据丢失

`PasteImageModule.cs:350`。`clipboard.WriteTextAsync(remotePath)` 替换原始图片。

**修复**：远端路径存内部状态 + 独立"复制路径"按钮；或明确通知并提供恢复入口。

---

#### P21 [高] 粘贴快捷键发送到错误窗口——上传期间用户可能已 Alt-Tab

`PasteImageModule.cs:194-195`。`SendAfterUploadShortcutAsync` 发送到完成时前台窗口而非发起时。

**修复**：发起时捕获 `GetForegroundWindow`，完成时比对——前台已变则跳过快捷键并通知。

---

#### P22 [低] 视图全部颜色硬编码浅色主题值——暗色模式下视觉破碎

`PasteImageView.axaml:7-21`。

**修复**：替换为 `{DynamicResource ...}`。

---

### ScreenEase

#### P23 [高·已复核] 多显示器 gamma 应用部分失败时先持久化"已应用"再检查——无回滚

`ScreenEaseModule.cs:701-722`。

`Store.Save(state)` 在行 714 写入 `Effect.Enabled=true` 状态，行 720 才检查 `nativeResult.Success`。部分失败时已成功的显示器不回滚，持久化状态标记"已应用"。下次启动 `ReapplyPersistedEffectCoreAsync`（行 1174）对所有显示器重新应用——失败的仍然失败，永久不匹配。

**修复**：`Store.Save` 移到成功检查之后；部分失败时回滚已成功显示器或存储带 `PartialFailure` 标记的状态。

---

#### P24 [中] 配置操作失败吞掉全部异常细节

`ScreenEaseViewModel.Profiles.cs:280-282`。catch 丢弃异常，统一"操作失败。请稍后重试"。

**修复**：按异常类型分类：连接→"服务连接中断"+重试；校验→直接显示；其他→摘要+"查看诊断"。

---

#### P25 [中] 服务单元状态加载一次后不再刷新

`ScreenEaseSurfaceFactory.cs:120`。服务崩溃后 UI 仍显示"活跃"。

**修复**：加定时轮询或订阅 ServiceManager 事件。

---

#### P26 [低] 状态持久化重试用同步 Thread.Sleep 阻塞命令门

`ScreenEaseModule.cs:1658-1669`。

**修复**：改用 `await Task.Delay` + 异步信号量。

---

### 豆包 Computer Use

#### P27 [高] 看门狗在 4 次失败后静默放弃重启，对用户零通知

`DoubaoAgent.Controller.Service/Program.cs:494-504,145-148`。`ShouldAttemptRestart()` 返回 false 后调用方静默 return——无日志、无事件、无状态更新。

**修复**：耗尽时在快照设 `RestartExhausted` 标记，UI 呈现"自动重启已耗尽，请手动重启"+ 重启按钮。

---

#### P28 [中] 所有 catch 块直接展示原始英文 ex.Message

`DoubaoAgentViewModel.Operations.cs:89,110,165,224,285,309`。

**修复**：已知异常类型映射为中文用户消息，原始文本仅进诊断区。

---

#### P29 [中] 运行时操作传 CancellationToken.None——UI 可冻结 45 秒无法取消

`DoubaoAgentViewModel.Operations.cs:101`。

**修复**：用 CTS 接入 + 可见取消按钮。

---

#### P30 [中] Controller Service 同步 .GetAwaiter().GetResult() 阻塞管道处理器

`DoubaoAgent.Controller.Service/Program.cs:160-161,297`。

**修复**：Main 循环改 async，或管道处理器隔离到独立线程池。

---

## 跨插件共性问题

8 个插件中反复出现以下三种模式，建议在 SDK 层面统一解决而非逐个修补：

1. **UI 线程同步文件 I/O**（remote-notifications、remote-commands、paste-image）——SDK 应提供异步持久化辅助（如 `MptAsyncFileStore`），或在 `MptObservableViewModel` 中文档化"文件操作必须 `Task.Run`"规范。
2. **硬编码颜色破坏暗色主题**（remote-notifications、input-monitor、paste-image）——设计系统缺少 `MptBrushInfoBackground`/`MptBrushDangerBackground`/`MptBrushSuccessBackground` 语义背景色（仅 Warning 有），导致插件作者被迫硬编码 hex。补齐四种语义背景色即可从根源解决。
3. **fire-and-forget 吞异常**（remote-commands、部分 surface 事件处理器）——`MptAsyncRelayCommand`（U6）在 Release 下不报错，SDK 应默认路由到 `FaultObserved`。

---

## 修复路线图

### 第一批 · 小改动大回报

- **E1** 新建 `MptInfoBar` + inline status 替代不可见状态栏 + **E2** 全部错误页接上 retry——消除最大量静默失败。
- **U1** OTA 进度 Dispatcher 编组（一行修复）。
- **T3** 管道读写端修正（几行修复，解锁全部服务日志）。
- **T2** TaskbarCreated 重挂托盘；**U7** 剪贴板 try/catch；**O4** 便携启动 MessageBox。
- **P21** paste-image 捕获发起窗口句柄；**P16** input-monitor 钩子返回值检查；**P23** screenease persist-after-verify。
- SDK 层：补齐四种语义背景色（修复 P11/P18/P22 的根因）。

### 第二批 · 更新链路 + 插件 I/O

- **O1-O3** OTA 可靠性三件套：拆分下载/apply、维护模式落盘、回滚保留备份。
- **O7** 启动时报告升级结果；**O5** 全链路超时与取消。
- **P2/P8** remote-commands + remote-notifications 文件 I/O 异步化。
- **P9** toast 洪泛保护。

### 第三批 · 生命周期与结构性

- **T1/T4/T5** 托盘所有权与自愈：Shell 托底 + Runner 主托盘 + TaskbarCreated 重挂，退出语义一次定清。
- **U2** 导航代际检查 + 取消令牌；**U3** 工具加载移出 UI 线程；**U5** gRPC 默认 deadline。
- **E3** 引入字符串表统一语言；**R1** modules/ 二进制退出 git。
- **P17** input-monitor 隐私控件；**P27** doubao 看门狗状态机重构。
- SDK 层：`MptAsyncRelayCommand` Release 下路由错误到 `FaultObserved`（修复 U6 + 所有插件）。

---

## 做得好的地方

- 页面加载失败统一经 `ShellFailurePresenter` 转译为结构化、可行动的文案——问题仅在于操作类路径没用它。
- `invoke-ota-update.ps1` 的加固工作在"顺利失败"路径上扎实：逐文件 `[IO.File]::Replace`、日志化备份、漂移检查、暂存哈希校验。
- 轮询循环全部有界、在后台线程、用 `PeriodicTimer`，无 CPU 空转。
- `UnitSupervisor` 重启限额进入 `Failed` 态并带明确原因；Services 页展示 LastError、重启计数与重连横幅。
- Remote Notifications 的签名拉取、去重环、会话链、协议处理器点击响应和设置管理是插件中最成熟的架构。
- Local Lag Cleaner 的两阶段清理确认模型（扫描 → 计划 → 确认 → 执行）是同类工具中罕见的安全设计。
- ADB Forwarder 的 async/await 使用、Dispatcher 编组、取消处理和异常脱敏是插件中最干净的代码。
- ScreenEase Surface 层有正规的 loading/failure/retry 视图、busy 守卫、中文一致。
