# 交接文档 · 2026-09-03

分支：`codex/ux-platform-hardening-2026-09`（父仓库与 7 个工具子模块使用同一分支名）
基线：上游 `main` 的 `4348264`（0.3.17）
提交：`ea9c330`（主体改动）、`b1fe851`（门禁与架构修正）

这一轮做了两件事：把两次评审发现的缺陷修掉，以及把 macOS 从"能编译"推进到"接近可用"。工作没有全部完成，下面把已完成、未完成和待决策三部分分开写清楚。

---

## 一、拿到代码

父仓库有两个分支，代码树完全相同，只差一个 CI 工作流文件：

| 分支 | 位置 | 说明 |
|---|---|---|
| `codex/ux-platform-hardening-2026-09` | 仅本地 + 交接包里的 bundle | 完整 3 次提交，含 `.github/workflows/ci.yml` 改动 |
| `codex/ux-platform-hardening-2026-09-noci` | **已推送到 GitHub** | 同样的代码，CI 工作流保持上游原样；单次压缩提交 |

推送完整分支被 GitHub 拒绝：当前 OAuth token 缺 `workflow` scope。7 个工具子模块的 `codex/ux-platform-hardening-2026-09` 分支都已正常推送。

```bash
git fetch origin
git checkout codex/ux-platform-hardening-2026-09-noci
git submodule update --init --recursive
git apply /home/chris/repo/mypowertools-handoff-2026-09/ci-workflow.patch   # 恢复 CI 改动
```

要保留完整提交历史，改用交接包里的 bundle：

```bash
git fetch /home/chris/repo/mypowertools-handoff-2026-09/mypowertools-superproject.bundle \
    codex/ux-platform-hardening-2026-09:codex/ux-platform-hardening-2026-09
```

交接包位置：`/home/chris/repo/mypowertools-handoff-2026-09/`（bundle、CI 补丁、说明）。

7 个工具子模块（adb-forwarder、doubao-computer-use、input-monitor、remote-commands、remote-notifications、screenease、smartbird-thermostat）各自在同名分支上有一个提交，父仓库已记录指针。注意 `tools/paste-image`、`tools/local-lag-cleaner`、`tools/ime-manager`、`tools/nssm-manager`、`tools/ddns` **不是**子模块，是父仓库里的普通目录。

Linux 上编译需要 `EnableWindowsTargeting=true`（`MyPowerTools.WebToolHost` 的目标框架带 Windows 限定符）：

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet" EnableWindowsTargeting=true
dotnet build MyPowerTools.slnx -c Debug        # 0 errors
dotnet test src/MyPowerTools.Tests/MyPowerTools.Tests.csproj
```

---

## 二、测试基线：15 个失败是正常的

当前 `748` 个测试中 `15` 个失败。**这 15 个在上游 `4348264` 上同样失败**（我实测对比过），全部是 Linux 环境或 Windows 专属造成的，不是本分支引入的：

| 失败原因 | 涉及测试 |
|---|---|
| 需要 `modules/android-tools-suite/windows/x64` 预先 staged（CI 现已加了这一步） | 4 个 `AndroidTools_*` |
| 需要 `pwsh.exe`（Linux 上是 `pwsh`） | `Release_metadata_script_writes_update_and_scoop_manifests` |
| gRPC 绑定 `localhost:80` 在容器里失败 | `Cli_restarts_runner_grpc_process_pool_over_hostcontrol` 等 |
| Windows 专属 Broker 消息 / 需要 Release 版 Cli | 3 个 `AdbBrokerSecurityTests` |
| Linux 上传输是 Unix socket，不是命名管道 | `ShellFailurePresenterTests.Named_pipe_access_denied...` |
| 其余环境依赖 | `Cli_validate_contracts`、`Cli_runner_autostart_status`、`Runtime_collects_production_module_events`、`Runtime_diagnostics_split`、`LocalLagCleanerProductTests.Mpt_host_routes_health` |

**判断新回归的方法**：任何不在上面这张表里的失败都是回归。测试总数从上游的 658 涨到 748，新增 90 个全部通过。

过程中我修掉了本分支一度引入的 6 个失败（UI 令牌门禁 ×2、瘦视图行数门禁、架构调试文档行数、工具模块禁止 P/Invoke、以及删除死代码树后测试路径失效），提交在 `b1fe851`。

---

## 三、已完成

### Shell 界面响应性
过期页面加载守卫此前只覆盖成功路径，`catch` 里看不到（identity 在 `try` 内部捕获），10 条失败路径都会把错误页盖到用户已经切走的页面上——配合新加的 15 秒 gRPC deadline，这个错误页最晚能在 15 秒后砸下来。守卫上提到 `try` 之前，并补齐了 9 条"操作完再重载"的路径。

日志页此前把最多 2000 行绑在非虚拟化的 `ItemsControl` 上（约 14000 个控件同步测量），搜索框还没有防抖；改用仓库里早已存在但从未被实例化的 `MptLogViewer`，并给它加了 `MaxHeight` 让虚拟化真正生效（页面外层是无限高度的 `ScrollViewer`，不设界的虚拟化面板仍会全量实例化）。

其余：命令面板流式输出去掉二次方扫描并封顶 200 条、工具目录过滤结果缓存、Services 页事件 250ms 合并、绑定属性里的同步 `File.Exists` 移除、导航 180ms 后显示加载占位、InfoBar 补上自动消失与去重、控制器分区文件拆分到门禁要求的行数以内。

### macOS
- 修掉 4 处在 mac 上直接出错的代码：启动器调 `MessageBoxW`（改为 `osascript` 弹窗）、通知中心在无 bundle identifier 时抛 ObjC 异常直接杀死宿主、剪贴板 P/Invoke 无平台守卫、launchd 停止对 `KeepAlive` 服务是空操作（改用 `bootout`）。
- **Bundle 布局重构**：Runner / Shell / ServiceManager 移入 `Contents/MacOS/Helpers/` 下的嵌套 helper `.app`，各自带 bundle identifier 和 `LSUIElement`，通知因此走上 `UNUserNotificationCenter` 正路；旧的 `Contents/MacOS/<Host>/` 路径保留为相对符号链接，兼容 5 处按目录结构向上找根的解析代码。契约写在 `docs/MACOS_RELEASE.md` 的「Bundle layout」一节。
- **全局热键**（Carbon `RegisterEventHotKey` + 独立 CFRunLoop 线程）与**快捷键注入**（CoreGraphics `CGEvent`），含辅助功能授权检测与中文提示；能力矩阵已从"不支持"翻转。
- **ScreenEase 显示器 gamma**（CoreGraphics），gamma 计算与 Windows 驱动共用同一份代码；**adb 发现**按 PATH 与常见 SDK 路径解析。
- 托盘退出会真正 bootout 两个 launchd agent 并关停 ServiceManager 与 Runner；新增 `scripts/uninstall-macos.ps1`；bundle 版本号不再固定为 `0.2.0`。
- 二次启动激活：Unix 管道路径此前零覆盖（测试在非 Windows 上直接 return），现在 20 个测试在 Linux 全通过；ACK 与界面呈现解耦，慢 UI 不再导致客户端误判超时。

### 可靠性
OTA：互斥量此前 `initiallyOwned=$false` 导致完全不互斥（每日任务与手动更新可并发跑 apply），改为真正持有并处理 `AbandonedMutexException`，非管理员环境回退 `Local\` 命名空间；维护模式改为**先落盘再删注册表项**（此前反过来，中途被杀会永久丢失自启动）；新增陈旧状态清扫（`transactions` 与 `downloads` 无限增长）；两个安装器补上 OTA 状态播种（此前 web 安装后首次检查会从 `0.0.0` 误判并下载 500MB 全量包，把核心安装转成完整安装）；签名公钥文件为空时回退内置密钥而不是彻底禁用 OTA。

ServiceManager：`CTRL_BREAK` 对 `CREATE_NO_WINDOW` 子进程根本送不到，此前的实现反而把宽限期从 `StopTimeout` 缩短成 2 秒——已移除该路径并恢复完整宽限期；进程发现不再按文件名全机匹配（此前可能杀掉用户自己的同名进程），要求 rooted 路径 + 同会话 + 同用户 + `MPT_DATA_ROOT` 标记；清单读取失败时保留旧定义而不是把健康运行的单元停掉；重启计数加入健康窗口重置与指数退避；`dependsOn` 真正参与启动排序；reload 发现的新单元会被自动启动；状态文件原子写入；Linux 的 `/proc` 发现路径接上。

Broker：NSSM 主机可执行文件在内容相同时跳过替换（此前任何服务运行中都会因镜像锁失败，导致后续安装全部阻塞）。

### 工具
input-monitor 隐私模式默认开启且隐私模式下不再落盘键码（此前仅清空字符，VK 码仍可还原文本），新增清除数据命令与按钮，`settings.json` 从每秒重写改为设置变更时原子写入、损坏时保留副本而非静默重置为默认值；remote-commands 修掉重跑时命令 id 已失效却选中第 0 条并自动执行的问题；adb-forwarder 修掉保存任一字段就把所有 Wi-Fi 轮询间隔重置为 30 秒、唤醒板配置清空的问题；ime-manager 键盘布局 ID 此前十进制写入、十六进制读取，任何增删改都会让用户输入法在下次登录后消失；nssm-manager 的六个变更类命令此前声明为 `broker.request`，被宿主路由到终止桩，全部失败，`rotate_file` 三个调用点参数顺序错位导致日志按大小轮转从不生效；ddns 每分钟删除同名重复记录改为仅在需要更新时执行且确定性选择保留项。

### 测试
新增 90 个：服务单元监管 28 个、NSSM 提权路径安全 15 个、doubao 密钥处理 7 个（从解决方案外的孤立项目回收）、Unix 激活路径 20 个、其余分散在各工具。remote-notifications 的测试项目此前编译的是已废弃的 `Shell.Avalonia` 副本且不在解决方案里，已重定向到实际发布的 Surface 源码并纳入解决方案（18 个测试）。CI 加了架构门禁（Quick + Process）和 AndroidTools 正向过滤任务。

---

## 四、未完成

### 1. macOS OTA 入口未接线（唯一明确的半成品）

`scripts/ota-apply-macos.ps1`（412 行）已写完并且是完整参数化的：

```
-PackagePath -ExpectedPackageSha256 -ExpectedVersion -AppBundlePath
-DataRoot -StateRoot [-HealthTimeoutSeconds] [-KeepBackupCount] [-KeepBackup] [-NoRelaunch]
```

它做的事：校验包哈希 → 解压到暂存 → `launchctl bootout` 两个 agent → 当前 `.app` 移到带时间戳的备份 → `ditto` 新 bundle 就位 → 重跑 `lsregister` → `bootstrap` + kickstart → 健康检查 → 失败时从备份回滚。

**但是 `scripts/ota-update.ps1` 和 `scripts/invoke-ota-update.ps1` 里 macOS 相关引用为 0**，没有任何代码会调用它。需要做的：

- 在 `ota-update.ps1` 的 `Invoke-FullApply`（约 723 行）加 `$IsMacOS` 分支，委托给 `ota-apply-macos.ps1`，`-AppBundlePath` 从安装根推导。
- 资产与频道选择要能选到 `osx-arm64` / `osx-x64`。托管侧的 `src/MyPowerTools.Packaging/OtaFeedLayout.cs` 已经实现了平台标识与资产名推导，PowerShell 侧还没有对应逻辑。
- delta 路径（`invoke-ota-update.ps1`）用的是 `[IO.File]::Replace` 和偏 Windows 的路径处理，需要审计；如果风险太大，mac 先只支持全量更新，并在 `docs/OTA_UPDATES.md` 写明。
- `scripts/publish-macos.ps1` 需要把 Cli 与 OTA 脚本打进 bundle（目前 19 处 macos 引用，但未确认是否已包含这些）。

托管侧已完成：`OtaUpdaterLocator`（跨平台定位 Cli 与 pwsh）已接入 `src/MyPowerTools.Cli/Program.cs` 与 Shell 的 `ShellWorkspaceController.Ota.cs`。

### 2. Paste Image 粘贴快捷键可能发到错误窗口

上传耗时数秒，期间用户可能已切换窗口，快捷键会发给切换后的窗口。我上一轮加的守卫用了 `user32!GetForegroundWindow` 的裸 P/Invoke，违反"工具模块必须经平台能力访问系统功能"的架构规则（`PasteImageProductTests` 强制），已在 `b1fe851` 撤回。

正确做法：在 `MyPowerTools.Platform.Abstractions` 加一个前台窗口能力（参照 `IClipboardImageService` 的模式），Windows 实现 + 其余平台 Unsupported，在三个平台包注册，模块通过 `TryGetCapability` 取用。注意 `RuntimeAcceptanceTests.PlatformBrokerPackage.Tests.cs` 会断言能力状态，新增能力需要同步更新。

### 3. 需要真机验证的项目

Linux 上只能编译验证，以下必须在真机上跑：

**macOS**
- 热键投递（**风险最高**）：`RegisterEventHotKey` 挂在非主线程的 Carbon 事件队列上，如果 HIToolbox 把热键事件路由到主线程队列，注册会报成功但永不触发。测法：启动 Runner，按 `Control+Option+R`，应看到 `screenease.profile.apply-long-read: ok`。若失败，改用 `CGEventTapCreate`（线程局部、且所需权限与注入路径相同，不增加新的授权提示）。
- `codesign --verify --deep --strict` 是否接受 `Contents/MacOS/<Host>/` 下指向 bundle 内部的符号链接。若不接受，去掉符号链接并改 5 处路径解析代码。
- 通知点击是否打开收件箱（验证 `UNUserNotificationCenter` + `NSWorkspace openURL` + Launch Services 整条链路）。日志里若出现 osascript 回退，说明 Runner 仍无 bundle identifier。
- Avalonia 是否遵守 `LSUIElement`（否则 Shell 仍有 Dock 图标）。
- `SpecialFolder.LocalApplicationData` 在 macOS 上映射到 `Library/Application Support` 还是 `.local/share`——若是后者，launcher 启动的 Shell 与 launchd 启动的 Runner 会读到不同的 token 存储而无法通信。一条命令即可确认。
- gamma 在 Apple Silicon 内置屏可能被系统限制；True Tone / Night Shift 会覆盖 gamma 表。

**Windows**
- NSSM：服务运行中执行 install/migrate 应不再因镜像锁失败。
- 服务单元停止：声明 `stopTimeoutMs: 30000` 的单元不应在 2 秒被杀。
- OTA：非管理员账户下 apply 不应抛 `UnauthorizedAccessException`；并发 apply 第二个应报"已有更新在进行"；在维护模式中途杀掉进程后，`maintenance-mode.json` 应存在且下次运行能恢复自启动；成功全量更新后 `downloads/full-<version>` 应被清理。
- 两个安装器：安装后 `%LOCALAPPDATA%\MyPowerTools\ota-state\` 应有 `installed-release.json` 与 `installed-files.manifest.json`，首次检查应报告已安装版本而不是 `0.0.0`。
- input-monitor 隐私模式：`SELECT key_code, key_bucket, characters FROM events` 应全为 `NULL, <bucket>, NULL`。
- ime-manager：增删输入法后重新登录，输入法列表应保持正确（这是十六进制修复的核心验证）。

### 4. 其余已知但未处理

- `modules/` 下的二进制载荷（DLL/PDB）在本分支里状态不确定，混杂着此前 Windows 构建的产物和 Linux 上的增量编译结果。**发布前必须在 Windows 上重新执行 `scripts/build-all-tools.ps1` 重新生成**。更根本的做法是让 `modules/` 不再跟踪构建产物（评审报告 R1）。
- `modules/android-tools-suite/modules/remote-commands/ui/tool.json` 声明 `dotnet-surface` 且指向 `surface/RemoteCommands.Surface.dll`，但 `ui/` 下没有 `surface/` 目录也未被跟踪；需要构建脚本 stage 该 DLL，或改清单指向实际存在的内容。
- `MptHostRuntime.ListHotkeyBindings` 只从运行时绑定表推导状态，平台层的注册结果（冲突、缺少辅助功能授权）到不了 Shell 的热键概览页——Windows 上同样如此。
- 手势解析器不接受 `Cmd`/`Command` 词元（`Win`/`Meta` 映射为 ⌘），也不支持标点键，F21–F24 在 macOS 无对应键码。
- `src/MyPowerTools.Tests` 里有 507 处针对源码文本的子串断言，其中若干是自我验证（例如断言模块源码含某个常量字面量）；`RemoteNotificationsProduct.Tests.cs` 与 `ScreenEaseProduct.Tests.cs` 有 `if (!Directory.Exists(...)) return;` 形式的静默跳过，在 CI 上恒为真。

---

## 五、待你决策的三项安全边界

这三项都在评审中确认属实，但修法涉及设计取舍，我没有擅自改：

**1. 控制面命名管道对 Everyone 授予 FullControl，客户端不校验服务端身份**
`src/MyPowerTools.Ipc.Shared/MptNamedPipePolicy.cs:154`、`IpcChannelFactory.cs:45`。管道名是机器全局常量，`CurrentUserOnly` 被刻意去掉（推测是为跨提权级别互通）。同机其他账户或低完整性进程可抢先创建同名管道实例，拿到客户端明文发送的 token，进而驱动 ServiceManager 启停任意单元，或通过 HostControl 的 `InstallPackage` 安装任意目录。
建议改法：ACE 改为当前用户 SID + SYSTEM + Administrators（提权不改变用户 SID，跨提权互通仍成立），管道名加上用户 SID 前缀。**需要确认去掉 `CurrentUserOnly` 的原始意图是否就是跨提权互通。**

**2. `input-remap install` 提权路径没有请求校验与内容信任**
`src/MyPowerTools.ElevatedBroker/Program.cs:9`、`src/MyPowerTools.Broker/WindowsInputRemapTaskInstaller.cs:81`。它绕开了 portproxy 与 nssm 那套请求文件 + 摘要 + Broker 自哈希 + 调用方 SID 的机制，仅按文件名判断来源就复制进 Program Files 并注册登录计划任务。NSSM 路径的 `TrustedManagedExecutable` 同样只认文件名，而旁边的 `TrustedServiceImage` 却要求受保护位置。
建议改法：统一走同一套校验，并对这两个可执行文件做构建期哈希白名单或 Authenticode 签名校验。

**3. ServiceManager 工具作用域由客户端自报**
`src/MyPowerTools.ServiceManager.Server/ServiceManagerGrpcService.cs:222`、`ServiceManagerAdminClient.cs:80`。所有工具进程读同一个 token 文件，省略 `x-mpt-caller-tool` 头即获得全量权限；"scoped client 不持有原始 admin token"的注释与实现相反。
建议改法：按工具签发独立 token（服务端由 token 映射工具 id，忽略请求头）。这会改动协议和每个工具的启动方式，成本较高。

---

## 六、给接手 Agent 的提示

- **先跑基线**：修改前先跑一次全量测试，确认失败数是 15 且与第二节的表一致。任何多出来的失败都是自己引入的。
- **UI 门禁很严**：`Views/*.axaml` 与 `UI/Controls/*.axaml` 里禁止 `Margin`/`Padding`/`Spacing`/`FontSize` 的数字字面量和 `#RRGGBB`，必须用 `MptSpacing*` / `MptFontSize*` / `MptBrush*` 资源；需要新值就加到 `src/MyPowerTools.UI/Themes/` 下的令牌文件（令牌文件本身豁免）。视图 code-behind 不得超过 18 行。控制器分区文件不得超过 400 行。
- **工具模块禁止 P/Invoke**：系统功能一律经平台能力（`TryGetCapability<T>`）。
- **中文字符串里的引号**：本轮修了 3 个文件因中文全角引号或字符串内嵌 ASCII 引号导致的编译失败。写中文字符串时注意 `"` 与 `"`。
- **并行改动会互相踩**：本轮多个 Agent 并行工作时反复出现"另一个 Agent 的半成品导致我的构建失败"。若要并行，务必按文件路径划分互斥范围。
- **`tools/` 下哪些是子模块**：改动前先看 `.gitmodules`，`git -C tools/<x>` 对非子模块目录会作用到父仓库。

评审原始记录见 `docs/UX_REVIEW_2026_08.md`（64 项，已全部修复）、`docs/PLUGIN_UPGRADE_PROPOSALS.md`、`docs/PLUGIN_DESIGN_IMPROVEMENTS.md`。本轮两次探索的完整发现（核心平台层 15 项、插件与测试层 15 项）已全部转化为上面的已完成或未完成条目。
