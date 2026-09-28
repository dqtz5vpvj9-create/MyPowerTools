# MPT 手机体验实现计划

用户已确认 `.lavish/mpt-mobile/index.html` 的交互与视觉方向，并授权 DSH 并发实现。
设计基线为父仓库 `2a7620c`；上一轮 Windows 恢复基线为 `67e678b`，Input Monitor
子模块为 `e603ea9`。网页是设计依据，其中示例设备、数据和模拟操作不能进入正式应用。

## 目标与完成标准

在现有 MPT 的模块、命令、设置、权限、事件和 Surface 装载机制上实现完整手机体验。
Android 使用原生 Avalonia 页面；复用同一运行时，不嵌入网页原型作为应用，不另建工具平台。

1. 四个一级入口：常用、工具、设备、动态；头像进入设置。工具详情有明确返回路径。
2. 首页用高频动作、真实收藏、最近记录和必要设备状态组织内容，移除桌面诊断面板式首页。
3. 工具库覆盖交付清单中的 12 个工具，支持搜索、用途分组、手机/电脑筛选和收藏。
   ScreenEase 是显示舒适/护眼工具，不能误写成远程桌面；Paste Image 包含图片上传与路径复用。
4. 文件助手以“发给自己”的持久会话和“发给设备”的自动发现为主流程，系统分享直接进入待发送区。
   按用户最新要求执行 [文件助手合同](FILE_ASSISTANT_CONTRACT.md)，替代原来的文件发送向导。
   直传、中转、重试由模块处理，只有真实接收回执才能显示对方已收到。
5. 通知、SSH 和设置采用同一视觉语言；使用真实历史、连接与执行结果。
6. 电脑专属能力显示实际执行设备和权限。手机遥控必须连接真实受控端后启用；
   不以静态状态或点击后 toast 代替实现，也不因 UI 重做删除桌面功能。
7. Android 安装包完成冷启动、分享、返回、重启保存、主要流程和截图验收；桌面完成相应回归。
8. 改动提交并推送各子模块及父仓库，发布有版本号、验收边界清楚的 Android 开发包。

## 视觉与交互合同

设计依据为 `.lavish/mpt-mobile/app.css`、`app.js`，不是旧版 Android 的布局。
实现需对照真实截图，允许适配系统安全区和字体度量，不自行改变信息结构或配色方向。

| 项目 | 浅色 | 深色/规则 |
| --- | --- | --- |
| 页面底色 | `#F6F7F9` | `#171B22` |
| 卡片 | `#FFFFFF` | `#222731` |
| 主要文字 | `#1C2025` | `#EBEDF2` |
| 次要文字 | `#69737F` | `#A0AABA` |
| 强调色 | `#0965EE` | `#6BA5FF` |
| 标题/正文/说明 | 28 / 14 / 12 dp | 跟随字体缩放 |
| 页边距 | 手机 22–24 dp | 320 dp 可缩到 18 dp |
| 卡片/按钮圆角 | 20–24 / 14–16 dp | 不叠加装饰阴影 |
| 操作目标 | 至少 44 dp；主要按钮 48 dp | 图标按钮同样可触达 |
| 底部导航 | 四等分，图标与中文标签 | 尊重 Android 安全区 |
| 弹层 | 单个底部面板、内容可滚动 | 返回键先关闭弹层，焦点恢复 |

通用主题放在既有 AvaloniaSdk，采用新增且隔离的 `MptMobile*` 资源/样式；
不得重写桌面默认样式。所有移动页面根容器添加 `MptMobileRoot` class。
首轮无需依赖新的公共 C# 类型：页面可先用标准 Avalonia 控件和约定 class 并行实现。

整合阶段的返回键合同为 `IMptAvaloniaSurfaceBackHandler.TryHandleBack()`：公开 Surface
可以关闭自己的面板或返回上一步；Shell 在离开工具之前调用，避免引用模块内部视图类型。

统一资源键：`MptMobileBackgroundBrush`、`MptMobileCardBrush`、`MptMobileTextBrush`、
`MptMobileSecondaryTextBrush`、`MptMobileAccentBrush`、`MptMobileDividerBrush`。
统一样式 class：`MptMobilePageTitle`、`MptMobileSectionTitle`、`MptMobileBody`、
`MptMobileCaption`、`MptMobileCard`、`MptMobilePrimary`、`MptMobileSecondary`、
`MptMobileIconButton`、`MptMobileListRow`、`MptMobileSearch`。

现有 SDK 版本和外部依赖由整合阶段统一决定；各任务不要独立批量 bump 或清空包缓存。
首轮添加资源和 selector，不引入消费方编译所必需的新 API，避免等待组件线完成。

## 并行任务及文件归属

| ID | 交付内容 | 独占写入范围 | 依赖 |
| --- | --- | --- | --- |
| M1 | 手机设计资源、组件样式、深浅色、触控与字体缩放 | `src/MyPowerTools.AvaloniaSdk/Themes/*Mobile*`、主题入口的 include、该 SDK 项目必要资源登记 | 无 |
| M2 | 手机 Shell，常用/工具/设备/动态/设置，收藏与真实目录，通用弹层和返回 | `src/MyPowerTools.Shell.Avalonia/Views/Mobile*`、新增 `Views/Mobile/`、`ViewModels/Mobile/`、`Services/Mobile/`（不含 M4 文件）、`Styles/Mobile.axaml`，必要的 `ShellWorkspaceController*` 手机适配点及 `tests/MobileLayout.Tests` | M1 样式合同；M4 数据合同 |
| M3 | 文件助手会话、自动发现设备面板、系统分享与接收确认、移动和桌面交互 | `tools/file-transfer/src/FileTransfer.Surface/`、`tests/FileTransfer.Surface.Tests/` | 文件助手命令合同；M4 后端 |
| M4 | 文件助手模块、接收授权、持久队列调度、自动路由、中转回执、Shell 服务 | `tools/file-transfer/src/FileTransfer.Core/`（除 F2/F3 新目录）、`FileTransfer.MyPowerTools/`、原 Core tests、`package/`；`MobileDeviceService.cs` 及同名模型文件 | F2 发现和 F3 会话存储，按合同整合 |
| F2 | 有界设备发现，Tailnet 候选与身份探测 | Core/Discovery/ 与 Core.Tests/Discovery/ | M4 的 v3 身份应答 |
| F3 | 持久会话与待发附件、OpenList 会话和逐设备回执 | Core/Assistant/ 与 Core.Tests/Assistant/ | 复用 OpenListClient 的 partial 扩展 |
| M5 | 手机通知列表、未读/详情/搜索、后台接收与按需设置 | `src/MyPowerTools.MobileNotifications/`，仅手机 UI 的新增测试文件 | M1 样式；保留现有消息语义 |
| M6 | 手机 SSH 常用命令、结果、连接设置与真实执行反馈 | `src/MyPowerTools.MobileRemoteCommands/` 及其现有 tests | M1 样式；保留已修复的 wire 数值处理 |
| M7 | Android 安全区/键盘/系统返回/冷暖分享/启动外观，配对扫码接入可行性与原生入口 | `src/MyPowerTools.Android/`、`src/MyPowerTools.Platform.Android/`、必要 `MobileServices.cs`、`scripts/build-android.ps1`；新接口在报告中明确告知整合方 | 保持 MobileShellView 已有公共方法兼容；不得重写 Shell |
| M8 | 架构和桌面回归审查、远程电脑工具的真实接入方案、安全权限边界、后续实现任务合同 | 只读分析；向主代理交付可直接派发的合同和风险证据 | 无 |

主代理独占：计划与验收文档、根解决方案登记、产物策略、Git 提交/推送、版本与发布、
Windows 部署调度及最终 Android 构建/设备验收调度。子代理不得提交、推送或部署。
第一轮预计 8 个独立 DSH 任务同时开展；不把尚未确定的跨设备权限协议交给多个代理各自实现。

## 设备与 Shell 数据合同

M4 为 M2 提供 `MyPowerTools.Shell.Avalonia.Services.Mobile` 命名空间下的服务：

- `MobileDeviceService : IMobileDeviceService` 提供无参生产构造器和可注入测试构造器，使用既有 HostControlClient/command execution 读取和调用 file-transfer 模块。
  不直接引用 FileTransfer.Core、模块私有程序集或磁盘中的模块配置。
- `GetSnapshotAsync(CancellationToken)`：本机、已配对设备、连接信息、中转配置摘要和最近传输。
- `ImportPairingAsync(string code, CancellationToken)`、`RemovePeerAsync(string deviceId, CancellationToken)`、
  `GetPairingCodeAsync(CancellationToken)`：沿用现有 secret store、安全连接码与授权边界。
- `CheckPeerAsync(string deviceId, CancellationToken)`：显式动作或页面打开时检查；不得建立常驻轮询。
- `MobileDeviceModels.cs` 已固定接口与模型，M2/M4 按此并行实现。
  M4 可向记录添加末尾可选属性，不能自行改动既有签名；M2 通过接口注入，不复制后端实现。
- 仅存有设备地址时显示“已配对/尚未检查”，不能显示“在线”。接收端真实应答才改变状态。
- 无设备、无网络、无权限、网盘未配置均有正常空态与下一步，不显示开发诊断栈。

六位短码不是现有安全连接码。优先把现有连接码的导入、分享和二维码变得便利；
若实现短码，必须有可信发现、时效及双端确认，不得把授权 token 截短为六位数字。

## 接入电脑工具的第二阶段

M8 先核查现有 HostControl、远程命令、文件配对、权限/审计和受控端入口，再给出最小复用方案。
接受其证据后，主代理立即派发实现，保持原型中的电脑工具入口在最终目标内：

1. 设备上真实工具/能力目录与只读状态；只能展示目标设备真正提供的命令。
2. 经用户分别授权的远程调用、取消、结果、审计；文件配对不自动授权任意电脑操作。
3. Input Monitor / ScreenEase / Paste Image 的手机控制与信息页。
4. 输入法、NSSM、卡顿清理、ADB、SmartBird、豆包工具的手机详情与命令页。
5. Windows 提权、敏感操作确认及已有禁用策略继续由原有机制处理。

如果某项受 OS 或现有模块限制，报告具体缺口并继续完成可实施部分；不能把“入口存在”
写成“功能移植完成”。不把原型的模拟成功流程复制为运行时代码。

## 编译、提交与设备使用规则

- 不新建 worktree，不切换现有分支，不 reset/clean/revert 用户改动。
- 保留 `external/haidian` 删除、现有 Input Monitor/Paste Image 的 DLL/PDB 改动及生成目录。
- 所有模块构建使用 `-p:StageRepositoryModule=false` 或该脚本对应的禁用镜像参数；
  不覆盖 `modules/` 与 `current-integration/modules/` 中已有开发产物。
- 临时文件使用 `/mnt/cache/data-cache`。子代理沙箱不可写该目录时，构建使用已经登记的
  `artifacts/.tmp-android-verify/`，并说明此受限执行条件，不使用 `/tmp`。
- 同仓库编译/打包共享公共输出。使用 `flock artifacts/.tmp-android-verify/mobile-build.lock`
  串行执行会写公共 build/cache 的命令；源码实现本身保持并行。
- 不运行全套测试来反复验证局部样式。各任务执行与交互/生命周期改变相符的定向检查；
  最后由主代理统一编译与回归，防止 8 个代理重复完整构建。
- Windows 只有最终指定的一个部署任务能修改远端安装目录。先保留当前工作源码，检查子模块
  来源，再使用官方 Dev 脚本，必须真正启动 Shell。禁止从仓库 bin 启动。
- 只使用本项目专用 Android AVD（之前为 r743 的 console 5682 / adb 5683，使用前重新核对），
  不触碰研究用设备。APK 更新保留应用数据，首次安装路径另用隔离数据验证。
- 本次不改 NotifyApp 消息语义；如必须修改共享格式，先提交具体变更范围供主代理调度双端，
  继续遵守 NotifyApp 版本、推送与 Release 要求。

## 集成顺序与验收证据

1. **并行实现**：M1–M8 各自交付代码/证据；每个任务注册一次原生完成回调，主代理不轮询。
2. **合同收口**：主代理先接受 M1/M4，再对齐 M2/M3；接入 M5/M6/M7，检查完整原型覆盖。
3. **跨设备能力**：按 M8 的具体方案完成上述第二阶段；对应 UI 以真实状态接线。
4. **自动化**：320/360/390/768 宽度、字体放大、深浅色、导航/返回/弹层、搜索/收藏保存、
   文件取消与重试、事件刷新、后台恢复；继续保留 HostControl 慢模块、打包路径和权限回归。
5. **Android 实测**：构建 APK、安装专用 AVD；逐屏截图并与原型对照；冷暖分享、真实文件收发、
   连接失败/离线/空态、网盘上传与领取状态、配置重启保存。禁止只凭编译成功宣布完成。
6. **桌面回归**：Input Monitor 深色休息窗与 DPI/周期逻辑、NSSM 服务操作、Paste Image 历史、
   Local Lag 一键操作；完成官方 Dev overlay、真实工具调用和重启后持久性检查。
7. **GitHub 闭环**：子模块先推送，父仓库更新指针；发布 APK、验收记录、未完成事项及证据路径。
   没有实机 macOS 120 秒测量时不发布或声称低功耗达标。

每个任务交付：具体改动文件、已完成行为、测试命令与结果、接口变化、需整合的问题。
主代理的接受结果记入本目录的运行记录，未接受的交付不计入完成数。
