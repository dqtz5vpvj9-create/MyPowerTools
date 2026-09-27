# MyPowerTools on Android

Android 端复用桌面版 MyPowerTools 的 Shell、Runtime、模块宿主、模块目录、命令、设置与
权限模型，在共享代码与契约之上新增 Android 平台能力包、手机外壳和原生工具模块。

- Android 解决方案：`MyPowerTools.Android.slnx`
- 应用与平台包：`src/MyPowerTools.Android`、`src/MyPowerTools.Platform.Android`
- 构建脚本：`scripts/build-android.ps1`
- 手动 CI：`.github/workflows/android-preview.yml`（`workflow_dispatch`）
- 手机布局测试：`tests/MobileLayout.Tests`

## 1. 支持范围

- 保留共享架构与既有底座（模块、命令、权限、SDK、日志、设置），不引入第二套框架。
- 12 个桌面工具 Surface 全部做了窄屏响应式适配。工具能否运行取决于所需的平台能力。
- Android 上有 **3 个原生工具模块**（基于模块/命令契约实现）：
  `file-transfer`（文件互传）、`remote-notifications-android`（远程通知）、
  `remote-commands-android`（远程命令/SSH）。
- 依赖桌面专属能力的工具（Windows 服务、输入法/输入钩子、桌面剪贴板、硬件设备、
  adb/桌面自动化等）在手机上**如实标记为不支持**，不做假实现、不伪造可用状态。
- 其余桌面工具保留在工具目录中，手机功能移植尚未覆盖全部桌面工具。

## 2. 架构：共享底座 + Android 平台包

启动时装配既有运行时（`src/MyPowerTools.Android/AndroidHost.cs`）：应用私有数据根 →
解包 APK 内 `assets/modules/**` → 注入 `AndroidPlatformPack` → `MptHostRuntime` +
`InProcDotNetModuleHost` 加载模块并启动事件泵。模块仍以 collectible 上下文进程内加载，
与桌面一致。HostControl 走进程内实现，不监听命名管道/网络端口。

平台能力（`AndroidPlatformPack`）：

| 能力 | 状态 | 实现 |
| --- | --- | --- |
| `secret.store` | 支持 | 应用私有凭据存储（Keystore），模块密钥/口令只进这里 |
| `background.activity` | 支持 | 用户显式开启的前台服务 + 常驻通知 |
| `notification.desktop` | 支持 | 通知渠道 + API 33+ 运行时权限 |
| `files.downloads` | 支持 | MediaStore 发布到系统“下载/MPT” |
| `clipboard.image` | 支持 | 前台剪贴板读取 |
| `network.ssh` / `service.user` | 不支持 | 明确声明为 unsupported，不做占位实现 |

模块目录与入口由清单驱动：`AndroidHost` 读取各工具 `ui/tool.json` 的
`shareMimeTypes` / `activationUriPrefixes` 决定系统分享与 `mpt://` 链接的接收者；
新增工具不需要改 Activity。`AndroidManifest.xml` 只申请网络、网络状态、前台服务与通知权限。

**Windows/macOS 桌面目录不受影响**：Windows 目录仍是 8 个包（原有 7 个 + `file-transfer`）；
Android-only 模块（`remote-commands-android`）不镜像进仓库 `modules/`，避免在 Windows 上
出现重复的 Android 卡片。

## 3. 手机 UI

- `MobileShellView`：手机外壳（底部导航、页面栈），返回键优先交给 Shell 返回栈。
- `MptAdaptiveLayout`（`src/MyPowerTools.AvaloniaSdk`）：跨端附加属性
  `StackBelow`（窄屏多列改单列）、`ColumnLabels`（表格行变带标签卡片）、
  `HideBelow`（隐藏非关键控件），恢复宽度后原样还原。
- 12 个桌面 Surface 均已按窄屏规则适配；`tests/MobileLayout.Tests` 在
  320/360/390/768 逻辑像素宽度下回归这些布局。
- 文件互传页面在 Android 上隐藏桌面专属入口（打开文件夹、本机 OpenList），改为明确提示。

## 4. 构建与产物

前置：`global.json` 指定的 SDK、Android workload、Android SDK（platforms + build-tools）、JDK 17。

```bash
dotnet workload install android --skip-manifest-update
pwsh scripts/build-android.ps1 -AndroidSdkDirectory "$ANDROID_HOME" -JavaSdkDirectory "$JAVA_HOME"
```

`scripts/build-android.ps1` 的步骤（任一步失败非 0 退出）：

1. 把 Android solution 以**包**形式消费的 SDK 契约包打进仓库内 feed
   `artifacts/sdk/nuget`，并清掉对应包缓存（本地开发包复用版本号）。
   仓库级权威打包清单是 `scripts/build-sdk.ps1`。
2. 按 `$moduleStages` 逐个构建 Android 工具模块包。当前三条**必需**条目：
   `file-transfer`、`remote-notifications-android`、`remote-commands-android`。
   不传 `-SkipSurface`/backend-only；Android-only 的 Remote Commands 传 `-NoMirror`
   （元数据直接来自 APK asset 用的 stage，仓库不生成镜像）。
   每条都带 `RequireFiles`（模块适配器、SSH 依赖、**工具 Surface**）——缺文件即失败。
   宿主契约程序集只按精确 SDK 程序集名剔除，**不能用 `MyPowerTools.*.dll` 通配**：
   Remote Commands 的 Surface 本身就叫 `MyPowerTools.MobileRemoteCommands.dll`，
   宿主 `AndroidAsset` 的 `Exclude` 也必须逐项点名。
3. 构建 `MyPowerTools.Android.slnx`（默认 universal `android-arm64;android-x64`，
   实验性 CoreCLR 运行时；`UseMonoRuntime=false`），然后断言
   `artifacts/build/bin/MyPowerTools.Android/<配置名>/com.mypowertools.android-Signed.apk` 存在。

产物位于 `artifacts/build/bin/MyPowerTools.Android/**`（artifacts 治理中属 `build` 类别）。
CI 只能手动触发，可选 Debug/Release 与单 ABI；工作流会安装 workload、构建、用 `unzip`
校验 ABI/CoreCLR/模块资产并上传 artifact。原 `MyPowerTools CI` 不安装 Android workload。

## 5. 手机端使用要点

- **文件互传**：Tailscale 直传只接受 Tailscale 私有地址/回环，配对码里带接收密钥，
  接收密钥进 Keystore；网盘中转在手机侧不运行 OpenList，使用电脑端 OpenList 的 WebDAV
  账号（连接码导入或手填）。手机收到/下载的文件发布到系统“下载/MPT”。
- **远程通知**：签名私钥从工具页一次性导入并存入 Keystore（不落文件）；通知权限在
  API 33+ 首次使用时申请；后台续传依赖用户显式开启的前台服务。
- **远程命令**：命令目录与主机信任走模块命令，页面不直接开 socket、不执行外部 ssh/scp；
  SSH 客户端由模块内的托管实现提供。
- 系统分享与 `mpt://` 链接按各工具清单声明的类型路由；多个候选项时弹出选择框。

## 6. 当前状态

已交付共享架构、手机 Shell、12 个桌面 Surface 的窄屏适配、三个 Android 工具模块，
以及独立构建脚本与手动 CI。完整开发包已通过专用模拟器安装、启动、导航、冷/热分享、
返回键、通知页面和 SSH 设置持久化验证。

各套件结果、文件传输验证和实际网络条件见 [ANDROID_PORT_ACCEPTANCE.md](ANDROID_PORT_ACCEPTANCE.md)。
APK 使用开发签名；真实网盘凭据、跨设备 Tailscale、SSH 和通知服务仍需对应环境验证。
本次没有发布 Windows/macOS 安装包，也没有进行 macOS 实机低功耗测量。

## 7. 维护约定（新增 Android 工具模块）

1. `MyPowerTools.Android.slnx`：加手机 Surface 项目、`tools/<tool>/android-integration`
   的模块项目与其测试；主 solution 也加测试项目（让原 CI 跑到）。
2. `scripts/build-android.ps1` 的 `$moduleStages`：加 build script、stage、manifest、
   `RequireFiles`（必须包含该模块的 Surface）。已完成的模块一律 `Optional = $false`；
   只有源码尚未落盘时才允许 `Optional = $true`。
3. 同时服务桌面的工具才镜像到 `modules/<id>`；Android-only 的传 `-NoMirror`。
4. 应用项目的 `ProjectReference` 与 `AndroidAsset` 由 host 侧维护，`Exclude` 不得整类
   排除 `MyPowerTools.*`。
