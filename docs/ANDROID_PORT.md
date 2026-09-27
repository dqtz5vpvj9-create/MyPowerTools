# MyPowerTools Android 移植（手机端）

本文说明 Android 端不是“另做一个手机文件工具”，而是把整个 MyPowerTools 移植到
Android：Shell、Runtime、模块宿主、SDK、命令、权限/能力底座全部复用，工具目录
（`modules/`）与桌面端同源；文件互传只是其中第一个在手机上完整可用的工具。

- Android 解决方案：`MyPowerTools.Android.slnx`
- Android 应用：`src/MyPowerTools.Android`
- Android 平台能力包：`src/MyPowerTools.Platform.Android`
- 手机构建脚本：`scripts/build-android.ps1`
- 手动 CI：`.github/workflows/android-preview.yml`（`workflow_dispatch`）
- 手机布局测试：`tests/MobileLayout.Tests`
- 文件互传工具：`tools/file-transfer`（桌面 + Android 共用同一份 Core / 模块 / Surface）

## 1. 为什么是独立 Android solution

`MyPowerTools.slnx`（桌面 Windows/macOS 构建、原 CI）**不包含任何 Android 项目**，
因此普通桌面构建和安装包流程不需要 Android workload。Android 目标（`net10.0-android`）
只出现在 `MyPowerTools.Android.slnx`：

| 项目 | 目标框架 | 作用 |
| --- | --- | --- |
| `src/MyPowerTools.Android` | `net10.0-android` | 应用入口（Activity、资产、打包） |
| `src/MyPowerTools.Platform.Android` | `net10.0-android` | Android 平台能力包 |
| `tools/file-transfer/src/FileTransfer.Core` | `net10.0` | 传输/网盘核心逻辑（跨端） |
| `tools/file-transfer/src/FileTransfer.MyPowerTools` | `net10.0` | 文件互传 MPT 模块 |
| `tools/file-transfer/src/FileTransfer.Surface` | `net10.0` | 文件互传 UI 表面 |

桌面侧通过 `MyPowerTools.slnx` 引用同样的三个 FileTransfer 项目，所以两端共享
同一份业务代码，不存在“手机专用实现”。

## 2. 复用的底座

应用启动时不创建新的框架，而是装配既有运行时（`src/MyPowerTools.Android/AndroidHost.cs`）：

1. `RuntimePaths.CreateDefault()` 建立应用私有数据/日志根。
2. 把 APK 内的 `assets/modules/**` 解包到 `bundled-modules/`，作为模块目录。
3. `PlatformPackFactory.EmbeddedPlatformFactory` 注入 `AndroidPlatformPack`。
4. `new MptHostRuntime(...)` + `InProcDotNetModuleHost` 加载模块目录，
   再 `StartModuleEventPump()`；模块加载上下文仍是 collectible（可卸载），
   与桌面端一致。
5. `HostControlClient.EmbeddedInvoker = new EmbeddedHostControlInvoker(...)`：
   手机端走进程内 HostControl，不监听命名管道/网络端口。
   `src/MyPowerTools.Ipc.AspNetCore/MptNamedPipeTransport.cs` 只负责 Windows 管道
   安全策略，在非 Windows 上不注册 `PipeSecurity`。
6. `AndroidBackgroundActivityService.StopRequested` 与模块启停联动。

因此手机上仍然是“模块 + 命令 + 权限”的模型：模块清单、`commands.index.json`、
`ui/tool.json`、设置 schema、日志、权限提示等全部来自同一套契约。

打包进 APK 的目录本身由模块与工具声明驱动，而不是在应用里硬编码工具名：

```xml
<AndroidAsset Include="../../modules/**/module.json;...;../../modules/**/ui/*.json" ... />
<AndroidAsset Include="../../tools/file-transfer/artifacts/package/**/*" ... />
```

`AndroidHost.ShareTargets` / `ActivationTargets` 读取每个工具的
`ui/tool.json`（`shareMimeTypes`、`activationUriPrefixes`）来决定谁可以接收
系统分享或 `mpt://` 链接，新增工具不需要改 Activity。

## 3. Android 平台能力

`AndroidPlatformPack`（`src/MyPowerTools.Platform.Android`）实现 `IPlatformPack`：

| 能力 | 状态 | 实现 |
| --- | --- | --- |
| `secret.store` | 支持 | `AndroidSecretStore`（应用私有凭据存储，供模块密钥引用） |
| `background.activity` | 支持 | `AndroidBackgroundActivityService`（用户显式开启的前台服务 + 常驻通知） |
| `files.downloads` | 支持 | `AndroidDownloadsService`（MediaStore 发布到 `下载/MPT`） |
| `notification.desktop` | 支持 | Android 通知渠道 + 运行时通知权限（API 33+ 弹窗申请） |
| `clipboard.image` | 支持 | 前台剪贴板读取 |
| `network.ssh` / `service.user` | 不支持 | 明确声明为 unsupported，不做假实现 |

`AndroidManifest.xml` 只申请必要权限：网络、网络状态、前台服务（含
`dataSync`/`connectedDevice`）、通知。没有新增存储读写、悬浮窗等宽权限。

## 4. 手机 UI

- `src/MyPowerTools.Shell.Avalonia/Views/MobileShellView.cs`：手机外壳（底部导航、
  页面栈、返回键处理）。Android 的 `OnBackPressed` 优先交给 Shell 返回栈，只有
  栈底才 `MoveTaskToBack`，符合 Android 习惯。
- `src/MyPowerTools.AvaloniaSdk/MptAdaptiveLayout.cs`：跨端自适应附加属性，
  桌面端同样可用：
  - `MptAdaptiveLayout.SetStackBelow(control, width)`：窄于阈值时把多列 `Grid`
    改成单列纵向堆叠，恢复宽度后还原列/行/跨列。
  - `MptAdaptiveLayout.SetColumnLabels(grid, "设备|状态|操作")`：窄屏时把表格式
    多列行变成“带标签的卡片”，恢复后去掉重复标签。
  - `MptAdaptiveLayout.SetHideBelow(control, width)`：窄屏隐藏非关键控件。
- 各设置页/模块页/工具页在 320/360/390/768 逻辑像素宽度下都按上述规则排版；
  文件互传页面在 Android 上隐藏“打开文件夹”“本机 OpenList”等桌面专属入口，
  改为明确提示（文件进入系统“下载”目录 / 由电脑端 OpenList 中转）。
- `tests/MobileLayout.Tests` 用 Avalonia Headless 在 320~768 宽度下验证布局与还原，
  已接入 `MyPowerTools.slnx`，随原 CI 一起执行。

## 5. 文件互传（Tailscale 直传 + OpenList 中转）

同一个模块在桌面与手机上都提供 `FileTransferModule`（`file-transfer`），
清单声明 `secret.store` 必需能力、命令与设置 schema。

### 5.1 Tailscale 直传

`tools/file-transfer/src/FileTransfer.Core/DirectTransfer.cs`：

- 自研最小协议 v1：4 字节大端 JSON 长度 + UTF-8 JSON 握手 + 精确文件字节流；
  接收端先回“准入”，落盘并发布后再回“完成”，避免发送端重发已送达文件。
- 只接受 Tailscale 私有地址（`TransferFiles.IsTailAddress`）与本机回环，其他地址
  直接拒绝，防止误把文件发到公网。
- 接收密钥至少 24 字符，用固定时间比较校验；握手/传输都有 15 秒与 2 分钟的空闲
  超时，断连会删除半成品文件，接收端不会因为一次坏连接而退出。
- 文件名经过清洗，拒绝路径穿越；落盘使用临时文件 + 原子改名；重复文件名自动
  变成 `名字 (1).txt`，不覆盖已有文件。
- 配对码 `Pairing` 只携带设备名、Tailscale 地址与接收密钥，导入后密钥进
  secret store，不写入设置 JSON。

### 5.2 OpenList / 网盘中转

- 桌面端可以“一键安装并启用 OpenList”：下载官方 OpenList 二进制到应用数据目录，
  初始化管理员口令（进 secret store），只监听 Tailscale 地址或回环地址，并在
  用户的网盘挂载目录上建立专用 WebDAV 中转账号（`OpenListSetup`）。
  传输统一走 WebDAV（`OpenListClient`），不引入额外哈希或自建加密层。
- 手机端**不运行 OpenList 服务**（Android 应用数据目录不允许随意执行外部二进制）。
  手机使用电脑端 OpenList 的 WebDAV 账号：电脑“复制本机网盘连接码”→ 手机粘贴
  导入（`file-transfer.cloud.import`），或手动填写 WebDAV 地址与账号。
  连接码里带密码，导入后只存 secret store；日志经过 `MptLogRedactor` 脱敏。
- 云端收发同样是“先写临时目录，完成后才出现在收件列表”，避免半截文件被下载。
- 手机收到/下载完成的文件通过 `files.downloads` 发布到系统“下载/MPT”，
  收件目录默认在模块私有数据目录内。

## 6. 构建与产物

```bash
# 前置：global.json 指定的 SDK、Android workload、Android SDK(platforms+build-tools)、JDK 17
dotnet workload install android --skip-manifest-update
pwsh scripts/build-android.ps1 \
  -AndroidSdkDirectory "$ANDROID_HOME" -JavaSdkDirectory "$JAVA_HOME"
```

`scripts/build-android.ps1` 的顺序（每一步失败都以非 0 退出）：

1. 把 `MyPowerTools.Platform.Abstractions`、`MyPowerTools.Abstractions`(ToolSdk)、
   `MyPowerTools.AvaloniaSdk` 打进仓库内 feed `artifacts/sdk/nuget`
   （fresh checkout 时该 feed 为空，必须先 pack，不能依赖旧包）。
   仓库级权威打包清单是 `scripts/build-sdk.ps1`（其中也包含
   `MyPowerTools.Ipc.AspNetCore` 0.2.0 等包）；`build-android.ps1` 只 pack 独立
   Android solution 真正以**包**形式消费的那几个，全部保持 0.2.1，不做额外升版。
   本地包复用版本号，所以脚本随后清掉
   `artifacts/sdk/global-packages` 里对应包 id 的缓存，让新包一定被重新解包。
2. 按 `$moduleStages` 列表逐个构建 Android 工具模块包并断言 manifest 与
   `RequireFiles`（当前三条**必需**条目：`file-transfer`、`remote-notifications-android`、
   `remote-commands-android`）。
   每条都必须包含自己的工具 Surface（`ui/surface/*Surface.dll` /
   `ui/surface/MyPowerTools.Mobile*.dll`）；宿主契约程序集只按**精确 SDK 程序集名**剔除，
   不能用 `MyPowerTools.*.dll` 通配。
   镜像策略：同时属于桌面目录的 `file-transfer` 同步到 `modules/file-transfer`；
   Android-only 的 Remote Commands 以 `-NoMirror` 构建（元数据就在 APK asset 用的
   stage 里，与通知模块一致），**不生成** `modules/remote-commands-android`，
   以免 Windows 上出现重复的 Android 卡片。
   新增 Android 工具模块时只需在 `$moduleStages` 追加一条；标记
   `Optional = $true` 的条目在源码尚未落盘时会被跳过并告警，不会让预览构建失败。
3. 构建 `MyPowerTools.Android.slnx`（默认 universal：`android-arm64;android-x64`，
   CoreCLR 运行时，`UseMonoRuntime=false`）。
4. 断言 `artifacts/build/bin/MyPowerTools.Android/<配置名>/com.mypowertools.android-Signed.apk`
   存在并打印大小。

产物落在 `artifacts/build/bin/MyPowerTools.Android/**`，属于
`scripts/artifacts-policy.json` 已声明的 `build` 类别；`artifacts/sdk/nuget`、
`artifacts/sdk/global-packages` 属于 `cache` 类别。

CI：`.github/workflows/android-preview.yml` 只能手动触发（`workflow_dispatch`），
可选 Debug/Release 与 single-ABI；工作流安装 Android workload、构建 APK、
用 `unzip` 校验 ABI/CoreCLR/模块资产并上传 artifact。原 `MyPowerTools CI`
不安装 Android workload。

测试归属：

- `tools/file-transfer/tests/FileTransfer.Core.Tests`（纯逻辑）与
  `tools/file-transfer/tests/FileTransfer.Surface.Tests`（Avalonia Headless）在
  `MyPowerTools.slnx`，随原 CI 跑；
- `src/MyPowerTools.Platform.Android/tests/MyPowerTools.Platform.Android.Tests`
  是 `net10.0` 纯逻辑测试（链接 Android provider 中与平台无关的规则文件），
  放在 `MyPowerTools.Android.slnx`，不需要真机；
- `tools/remote-notifications/android-integration/tests/RemoteNotifications.Android.Tests`
  同时进两个 solution（Android solution 跑模块套件，主 solution 让原 CI 也跑到）。

## 7. 集成状态

已经落盘并接入构建：

- 独立 Android solution + `build-android.ps1` + 手动 CI；
- 文件互传（Tailscale 直传 + OpenList 中转）模块；
- **Remote Notifications（Android）模块 `remote-notifications-android`**：
  - `scripts/build-android.ps1` 的 `$moduleStages` 里有**必需**条目
    （`Optional = $false`），stage 为
    `tools/remote-notifications/android-integration/artifacts/package/remote-notifications-android`，
    缺失即构建失败；
  - `MyPowerTools.Android.slnx` 加入 `src/MyPowerTools.MobileNotifications`（手机 Surface）、
    `tools/remote-notifications/android-integration/src/RemoteNotifications.Android`（模块适配器）
    与其测试项目；主 solution 也加入该测试项目；
  - 模块包由 `android-integration/build.ps1` 产出并自校验
    `manifest/android-package-manifest.json`：`RemoteNotifications.Android.dll`、
    `BouncyCastle.Cryptography.dll`、`ui/surface/MyPowerTools.MobileNotifications.dll`
    与 `ui/*.json` 描述符；host 契约程序集（`MyPowerTools.*`）与 pdb 被有意剔除，
    由 Android host 的加载上下文提供。
- **Remote Commands（Android）模块 `remote-commands-android`**（集成依据：
  `tools/remote-commands/android-integration/README.md` §5）：
  - 必需 stage 为
    `tools/remote-commands/android-integration/artifacts/package/remote-commands-android`，
    由 `android-integration/build.ps1` 产出；产品构建**不传** `-SkipSurface`，
    并传 `-NoMirror`（该模块只服务 Android，元数据直接来自 APK asset 用的 stage，
    与通知模块一致，仓库根不再生成 `modules/remote-commands-android`，避免 Windows 上重复卡片）；
  - 包内含 `RemoteCommands.Android.dll`、`Renci.SshNet.dll`（SSH.NET 2025.1.0）、
    `BouncyCastle.Cryptography 2.6.2`、`Microsoft.Extensions.*.Abstractions.dll`
    与 `ui/surface/MyPowerTools.MobileRemoteCommands.dll`；
  - solution 加入 `src/MyPowerTools.MobileRemoteCommands`（手机 Surface）、
    `tools/remote-commands/android-integration/src/RemoteCommands.Android`（模块适配器）
    与其测试项目（测试同时进主 solution）。
- 三个模块共同的主机侧守卫：`$moduleStages` 每条都有 `RequireFiles`，其中必含
  `ui/surface/…Surface.dll` 或 `ui/surface/MyPowerTools.Mobile*.dll`。剔除宿主契约程序集时
  **不能**用 `MyPowerTools.*` 整类通配（Remote Commands 的 Surface 本身就叫
  `MyPowerTools.MobileRemoteCommands.dll`）；root 已把模块侧 `$hostProvided` 改成
  **精确的三个 SDK 程序集名**，宿主 `AndroidAsset` 的 `Exclude` 同样逐项点名。
- 加密栈单一：SSH.NET 的 `BouncyCastle.Cryptography` 与通知模块同版本 **2.6.2**，
  APK 不会带两份 crypto。
- `scripts/build-all-tools.ps1` 内的 `file-transfer` 注册（Windows 模块暂存用）；
- `$moduleStages`（`build-android.ps1`）与 `$toolRegistry` 的 `Optional` 支持，
  用于后续 Android 工具模块。

**已闭合（root 完成）**：host 侧 `ProjectReference` + `AndroidAsset` 已按精确 stage 接线；
三个模块包在 canonical 环境下均已构建并通过 staging 校验；root 另把 `build-android.ps1`
的 pwsh 解析改为 `Get-Command … | Select-Object -First 1`，避免 PATH 上多个 pwsh 时选错。

**唯一未闭合项**：三模块 + 宿主接线后的**最终 APK 尚未验证**（最近的两模块 APK 是 07:42
的历史产物，不是验收证据）。因此现在的状态是“源码与模块 stage 就绪”，
不是“Android 应用已可用”。

集成方式（后续模块）：

1. 在 `MyPowerTools.Android.slnx` 加入移动端 Surface 项目与
   `tools/<tool>/android-integration` 模块项目（只加已存在的文件）；
2. 在 `scripts/build-android.ps1` 的 `$moduleStages` 追加该模块的 build script、
   stage 路径、manifest 与 `RequireFiles`；已完成的模块写 `Optional = $false`（必需），
   只有还没落盘的才允许 `Optional = $true`；
3. 需要进 Windows 发布的模块再在 `scripts/build-all-tools.ps1` 的 `$toolRegistry`
   追加对应条目；
4. Android 应用项目里相应的 `ProjectReference` / `AndroidAsset` 由 host 负责人补。

## 8. 当前验证状态（详见本轮验收说明）

**权威测试数字**（canonical 环境，root 复验；其它章节不得再引用旧快照）：

- `tools/file-transfer/tests/FileTransfer.Core.Tests`：**55 passed**
- `src/MyPowerTools.Platform.Android/tests/MyPowerTools.Platform.Android.Tests`：**16 passed**
- `tools/file-transfer/tests/FileTransfer.Surface.Tests`：**20 passed**
- `tools/remote-notifications/android-integration/tests/RemoteNotifications.Android.Tests`：**58 passed**
- `tools/remote-commands/android-integration/tests/RemoteCommands.Android.Tests`（SSH）：**59 passed**
- IPC 契约（root 报告口径）：**8 passed**
- `tests/MobileLayout.Tests`：曾达 **17 passed**，随后发现真实“默认导航”缺陷，正在修，
  配套新用例稍后补；**当前不得写成通过**

构建/结构验证（静态核对，非 dotnet 构建）：

- solution 结构：`MyPowerTools.slnx` **78** 个项目、`MyPowerTools.Android.slnx` **12** 个项目，
  两者项目路径唯一且文件都存在；桌面 solution 中没有任何 `net10.0-android` 项目。
- 三个模块包（file-transfer / remote-notifications-android / remote-commands-android）
  在 canonical 环境下均已构建并通过 staging 校验；宿主 `ProjectReference` + `AndroidAsset`
  已按精确 stage 接线。
- `scripts/build-android.ps1` 完整成功过一次（Debug、universal，文件互传单模块时期），产出
  `artifacts/build/bin/MyPowerTools.Android/debug/com.mypowertools.android-Signed.apk`
  （131,324,527 字节）；`unzip` 核验：`arm64-v8a` + `x86_64`、两边都有
  `libcoreclr.so`、8 个模块目录（含 `file-transfer`）。这是**历史证据**：
  三模块 + 宿主接线后的**最终 APK 尚未验证**，07:42 的两模块 APK 也不是验收证据。
- 发布验收脚本（`verify-release-candidate(.remote).ps1`）的期望集合已加
  `file-transfer`：`$expectedTools` 12、A5.3 加载面 9、A5.7 Runner 发现 10、
  A5.R4 远端目录 7，判定仍是“缺一个就失败”。

尚未验收（不得当作已通过）：

- **最终 APK 未构建验证**：因此现在只能说“源码与模块 stage 就绪”，
  不能说“Android 应用已可用”。
- 没有 Android 真机/模拟器安装与操作测试（未验证启动、分享入口、返回键、
  通知权限、MediaStore 落盘、前台服务在真机上的行为）。
- 没有在真机 Tailscale 网络下做端到端直传/中转测试；SSH/Remote Commands 真机链路未验证。
- 未跑官方 OpenList 二进制的集成用例（用例默认跳过）。
- Release 配置 APK 未构建；签名是本地 Android debug key，不是发布签名。
- `MobileLayout.Tests` 的默认导航缺陷正在修，结果未定；A5 发布验收需在 Windows 远端
  重跑一次取证。
- macOS 低功耗 120 秒门槛未测量（无 Mac 实机），macOS 包只能算待验收开发包。
