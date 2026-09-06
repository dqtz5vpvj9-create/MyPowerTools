# macOS 发布

## 发布模型

macOS 版本沿用完整的 Platform Pack 与模块体系：

- Avalonia Shell 继续承担跨平台界面框架。
- Web Surface 在 Windows 使用 WebView2，在 macOS 使用原生 WKWebView。
- MyPowerTools 是菜单栏常驻应用：外层 bundle 与三个 helper bundle 都声明 `LSUIElement`（ServiceManager 用 `LSBackgroundOnly`），不占 Dock 图标，入口是 NSStatusItem。
- 桌面通知使用 UserNotifications，凭据使用 Keychain，常驻菜单使用 NSStatusItem，用户级服务与自启动使用 launchd。NSStatusItem 会显示 Codex 七天额度圆环与重置倒计时，成功后每五分钟刷新，读取失败后一分钟重试。
- 图片剪贴板在 macOS 使用 NSPasteboard，Paste Image 通过系统 `/usr/bin/ssh` 上传。
- `android-tools-suite` 的三个模块及其清单均保留在应用包内。
- `adb-forwarder`、`doubao-agent`、`paste-image`、`screenease`、`smartbird-thermostat` 作为完整模块包一同发布。
- 新建 macOS 用户状态时，Runner 写入 allowlist，只启用 `android-tools.notifications`。用户后续启用或停用其他模块时，该选择会持久化。

## 环境

- macOS 12 或更高版本
- .NET SDK 10
- PowerShell 7
- Node.js 20 或更高版本
- Xcode Command Line Tools

从 Git 仓库首次检出后，先生成仓库内使用的本地 SDK 包：

```powershell
pwsh ./scripts/build-sdk.ps1 -Configuration Release
```

## 生成应用包

Apple Silicon：

```powershell
pwsh ./scripts/publish-macos.ps1 -Architecture arm64 -Configuration Release
```

Intel Mac：

```powershell
pwsh ./scripts/publish-macos.ps1 -Architecture x64 -Configuration Release
```

默认产物路径为：

```text
artifacts/publish/macos-arm64/MyPowerTools.app
artifacts/publish/macos-x64/MyPowerTools.app
```

脚本会发布 App、Shell、Runner、ServiceManager 与 RemoteNotifications.Service，把 Shell、Runner、ServiceManager 装配成嵌套 helper bundle，构建并签名 6 个生产模块包，构建 `libMptMacNative.dylib`，生成 `.icns`，刷新本地包签名，并按「先签 helper bundle、再签外层应用包」的顺序执行签名与校验。`-CodeSignIdentity` 可传入 Developer ID Application 身份；默认 `-` 使用 ad-hoc 签名。

Windows 主机可执行托管交叉发布检查：

```powershell
pwsh ./scripts/publish-macos.ps1 -Architecture arm64 -Configuration Release -SkipNativeBuild -SkipCodeSign
```

该模式用于 arm64/x64 托管包、模块目录和 Service Unit 清单验证；macOS 原生 dylib、`.icns` 与 codesign 结果需要 Mac 主机生成。

## Bundle layout

只有位于 `<bundle>.app/Contents/MacOS/<可执行文件>` 的进程才会被 `NSBundle.mainBundle` 解析成 bundle。拿不到 bundle identifier 时 `UNUserNotificationCenter` 不可用、Dock 与激活身份也是错的，所以 Shell、Runner、ServiceManager 各自打成嵌套 helper bundle：

```text
MyPowerTools.app
└── Contents
    ├── Info.plist                                   com.mypowertools.desktop / LSUIElement / CFBundleURLTypes
    ├── PkgInfo
    ├── Resources/MyPowerTools.icns
    └── MacOS                                        应用根目录
        ├── MyPowerTools                             启动器，外层 bundle 的 CFBundleExecutable
        ├── RemoteNotifications.Service              直接位于 MacOS 下，沿用外层 bundle 身份
        ├── libMptMacNative.dylib
        ├── modules/  schemas/  ServiceUnits/
        ├── Helpers
        │   ├── MyPowerTools Shell.app
        │   │   └── Contents
        │   │       ├── Info.plist  PkgInfo  Resources/MyPowerTools.icns
        │   │       └── MacOS/{MyPowerTools.Shell.Avalonia, libMptMacNative.dylib, 运行时文件}
        │   ├── MyPowerTools Runner.app
        │   │   └── Contents
        │   │       ├── Info.plist  PkgInfo  Resources/MyPowerTools.icns
        │   │       └── MacOS/{MyPowerTools.Runner, libMptMacNative.dylib, 运行时文件}
        │   └── MyPowerTools ServiceManager.app
        │       └── Contents
        │           ├── Info.plist  PkgInfo  Resources/MyPowerTools.icns
        │           └── MacOS/{MyPowerTools.ServiceManager, 运行时文件}
        ├── Shell/MyPowerTools.Shell.Avalonia         → 指向 helper 的相对符号链接
        ├── Runner/MyPowerTools.Runner                → 指向 helper 的相对符号链接
        └── ServiceManager/MyPowerTools.ServiceManager → 指向 helper 的相对符号链接
```

| bundle | identifier | 前台属性 | 可执行文件 |
|---|---|---|---|
| `MyPowerTools.app` | `com.mypowertools.desktop` | `LSUIElement` | `Contents/MacOS/MyPowerTools` |
| `MyPowerTools Shell.app` | `com.mypowertools.shell` | `LSUIElement` | `…/Contents/MacOS/MyPowerTools.Shell.Avalonia` |
| `MyPowerTools Runner.app` | `com.mypowertools.runner` | `LSUIElement` | `…/Contents/MacOS/MyPowerTools.Runner` |
| `MyPowerTools ServiceManager.app` | `com.mypowertools.servicemanager` | `LSBackgroundOnly` | `…/Contents/MacOS/MyPowerTools.ServiceManager` |

契约（OTA、安装脚本与任何改写应用包的流程都依赖这几条）：

- 应用根目录仍然是 `Contents/MacOS`。`modules`、`schemas`、`ServiceUnits`、启动器和 RemoteNotifications.Service 都在这一层，各进程也是从自己的目录向上走到这里。helper 因此嵌套在 `Contents/MacOS/Helpers` 而不是 `Contents/Helpers`。
- helper 可执行文件的相对路径固定为 `Contents/MacOS/Helpers/<Bundle>/Contents/MacOS/<可执行文件>`，bundle 名称带空格。
- `Contents/MacOS/<Host>/<可执行文件>` 是指向 helper 的相对符号链接，给仍按 Windows 扁平布局解析同级进程的调用方兜底。替换文件时要保留符号链接，不要覆盖成普通文件。
- 四个 `Info.plist` 的 `CFBundleShortVersionString` 与 `CFBundleVersion` 由 `publish-macos.ps1` 从 `version.json` 写入，值相同。
- `CFBundleURLTypes`（`mypowertools://`）只在外层 bundle 上声明。通知点击由 Runner 内的原生 delegate 调 `NSWorkspace openURL` 触发，经 Launch Services 回到启动器，再由启动器唤起 Shell。
- `libMptMacNative.dylib` 在 `Contents/MacOS`、Shell helper 和 Runner helper 中各放一份。`DllImport("MptMacNative")` 按加载它的可执行文件所在目录查找，复制一份比配 rpath 更稳：launchd 启动的进程会被 SIP 清掉 `DYLD_*` 环境变量。
- launchd plist 的 `ProgramArguments[0]` 指向 helper 里的真实可执行文件，不走符号链接；launchd label 与 helper 的 bundle identifier 同名。

## 安装

在 Mac 上执行：

```powershell
pwsh ./scripts/install-macos.ps1
```

安装脚本会按 Mac 机器架构选择 arm64 或 x64 产物。默认安装到 `~/Applications/MyPowerTools.app`，数据保存到 `~/Library/Application Support/MyPowerTools`。安装脚本注册 ServiceManager 与 Runner 的用户级 LaunchAgent，两个 plist 的 `ProgramArguments[0]` 指向 helper bundle 内的可执行文件。已有应用包会移动为带时间戳的备份。

覆盖安装前，脚本会先把两个 LaunchAgent 从 GUI domain 中 bootout，再用 `ditto` 复制应用包，避免正在运行的 Runner 覆盖到一半的目录上（`ditto` 也是符号链接与签名能原样保留的原因）。复制完成后对外层 bundle 和三个 helper bundle 分别执行 `lsregister -f`：`mypowertools://` scheme 需要外层记录，Runner 的 `UNUserNotificationCenter` 需要 `com.mypowertools.runner` 的 Launch Services 记录。

## 卸载

```powershell
pwsh ./scripts/uninstall-macos.ps1
```

卸载脚本会 bootout 并删除 `com.mypowertools.runner`、`com.mypowertools.servicemanager` 以及 `com.mypowertools.autostart.*` 的 plist，对三个 helper bundle 和外层 bundle 执行 `lsregister -u` 注销 Launch Services 记录，再删除 `~/Applications/MyPowerTools.app` 和残留的 UDS 目录。用户数据默认保留，`-RemoveData` 会一并删除 `~/Library/Application Support/MyPowerTools` 与 `~/Library/Logs/MyPowerTools`，`-RemoveBackups` 会删除历次安装留下的 `MyPowerTools.backup.*.app`。`-DryRun` 只打印将要执行的操作。

## 首次运行验收

1. 打开 MyPowerTools，允许系统通知权限。
2. 检查菜单栏 MyPowerTools Status Item 显示 Codex 剩余额度圆环与百分比，悬停提示包含七天/五小时额度和重置倒计时，并能打开 Shell 与退出后台进程。
3. 确认首页只显示 Remote Notifications 为启用状态。
4. 打开 Remote Notifications，确认其 Surface 正常渲染。
5. 触发测试通知，点击通知后确认 Shell 打开通知收件箱。
6. 手动启用 Paste Image，确认 NSPasteboard 图片读取、`/usr/bin/ssh` 上传和远端路径回写。
7. 注销并重新登录，确认 Runner 与 ServiceManager 由 launchd 恢复。
8. 从菜单栏选择 Exit MyPowerTools，确认 Shell、Runner、ServiceManager 三个进程都已退出，且 launchd 没有把它们拉起来。

打包与 bundle 身份的校验（在 Mac 上执行，`$APP` 指向安装后的 `~/Applications/MyPowerTools.app`）：

```bash
codesign --verify --deep --strict "$APP"
codesign -dv "$APP/Contents/MacOS/Helpers/MyPowerTools Runner.app" 2>&1 | grep Identifier
mdls -name kMDItemVersion "$APP"
launchctl print "gui/$(id -u)/com.mypowertools.runner" | grep -A2 'arguments'
```

`codesign -dv` 应报出 `com.mypowertools.runner`，`launchctl print` 的第一个参数应是 helper bundle 内的 `MyPowerTools.Runner`，`mdls` 的版本应与 `version.json` 一致。第 5 步的通知点击走的是 `UNUserNotificationCenter`；如果 `~/Library/Logs/MyPowerTools/com.mypowertools.runner.error.log` 里出现回退到 `osascript` 的横幅（点击无反应），说明 Runner 仍没拿到 bundle identifier。

## 当前边界

以下能力在 macOS 上尚未实现，Capability Registry 会把它们报告为不支持，依赖它们的模块会进入 degraded 或 unsupported：

- 全局热键（`hotkey.global`）与快捷键注入（`keyboard.shortcut`）已实现：热键走 Carbon `RegisterEventHotKey`（独立 CFRunLoop 线程），快捷键注入走 CoreGraphics `CGEvent`。注入需要在「系统设置 › 隐私与安全性 › 辅助功能」中授权 MyPowerTools；未授权时命令返回 `permission-required` 并附中文提示，热键注册本身不受影响。手势按字面映射（Ctrl = Control，Alt = Option，Win/Meta = ⌘），尚未在真机验证非主线程的热键投递。
- 显示配置（`display.profile`）已通过 CoreGraphics gamma 表实现（`ScreenEaseMacGammaDisplayService`），screenease 在 macOS 上可用；Apple Silicon 内置屏幕可能被系统限制 gamma 写入，且不含 DDC/CI 硬件亮度。`adb.devices` 通过 PATH 与常见 SDK 路径解析 adb。
- 仍未实现：特权代理（`privilege.elevated`）、系统级服务（`service.system`）、端口转发（`network.portForwarding`）。
- OTA。`scripts/ota-update.ps1` 与 `scripts/invoke-ota-update.ps1` 只覆盖 Windows（`.exe` 路径、计划任务、HKCU Run 键），应用包内也不含更新器。macOS 升级方式是重新下载 zip 并再次执行 `install-macos.ps1`。
- 命令行。`publish-macos.ps1` 不发布 `MyPowerTools.Cli`，应用包内没有 `mpt`。

从 `git` 工作区直接跑（`dotnet run`、`artifacts/build/bin/...`）的 Shell 与 Runner 不在任何 bundle 里，`NSBundle.mainBundle` 没有 identifier，`UNUserNotificationCenter` 不可用，通知会回退到 `osascript` 横幅且不带点击跳转。安装后的应用包不走这条路径：三个 host 都从 helper bundle 启动，通知走 `UNUserNotificationCenter`，点击由原生 delegate 通过 `mypowertools://` 唤起 Shell。

发布产物为 ad-hoc 签名且 `--timestamp=none`，未做 notarization；首次打开需要在「系统设置 → 隐私与安全性」中放行。

## 运行时路径

| 内容 | 路径 |
|---|---|
| 应用 | `~/Applications/MyPowerTools.app` |
| 状态与设置 | `~/Library/Application Support/MyPowerTools` |
| LaunchAgents | `~/Library/LaunchAgents/com.mypowertools.*.plist` |
| 日志 | `~/Library/Logs/MyPowerTools` |
| Remote Notifications UDS | `$TMPDIR/mypowertools/remote-notifications.core.sock` |
