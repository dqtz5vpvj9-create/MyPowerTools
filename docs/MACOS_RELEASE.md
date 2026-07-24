# macOS 发布

## 发布模型

macOS 版本沿用完整的 Platform Pack 与模块体系：

- Avalonia Shell 继续承担跨平台界面框架。
- Web Surface 在 Windows 使用 WebView2，在 macOS 使用原生 WKWebView。
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

脚本会发布 App、Shell、Runner、ServiceManager 与 RemoteNotifications.Service，构建并签名 6 个生产模块包，构建 `libMptMacNative.dylib`，生成 `.icns`，刷新本地包签名，并对嵌套 Mach-O 与外层应用包执行签名校验。`-CodeSignIdentity` 可传入 Developer ID Application 身份；默认 `-` 使用 ad-hoc 签名。

Windows 主机可执行托管交叉发布检查：

```powershell
pwsh ./scripts/publish-macos.ps1 -Architecture arm64 -Configuration Release -SkipNativeBuild -SkipCodeSign
```

该模式用于 arm64/x64 托管包、模块目录和 Service Unit 清单验证；macOS 原生 dylib、`.icns` 与 codesign 结果需要 Mac 主机生成。

## 安装

在 Mac 上执行：

```powershell
pwsh ./scripts/install-macos.ps1
```

安装脚本会按 Mac 机器架构选择 arm64 或 x64 产物。默认安装到 `~/Applications/MyPowerTools.app`，数据保存到 `~/Library/Application Support/MyPowerTools`。安装脚本注册 ServiceManager 与 Runner 的用户级 LaunchAgent。已有应用包会移动为带时间戳的备份。

## 首次运行验收

1. 打开 MyPowerTools，允许系统通知权限。
2. 检查菜单栏 MyPowerTools Status Item 显示 Codex 剩余额度圆环与百分比，悬停提示包含七天/五小时额度和重置倒计时，并能打开 Shell 与退出后台进程。
3. 确认首页只显示 Remote Notifications 为启用状态。
4. 打开 Remote Notifications，确认其 Surface 正常渲染。
5. 触发测试通知，点击通知后确认 Shell 打开通知收件箱。
6. 手动启用 Paste Image，确认 NSPasteboard 图片读取、`/usr/bin/ssh` 上传和远端路径回写。
7. 注销并重新登录，确认 Runner 与 ServiceManager 由 launchd 恢复。

macOS 全局热键 provider 仍在后续范围内；当前可从 Shell 或命令面板执行 Paste Image。

## 运行时路径

| 内容 | 路径 |
|---|---|
| 应用 | `~/Applications/MyPowerTools.app` |
| 状态与设置 | `~/Library/Application Support/MyPowerTools` |
| LaunchAgents | `~/Library/LaunchAgents/com.mypowertools.*.plist` |
| 日志 | `~/Library/Logs/MyPowerTools` |
| Remote Notifications UDS | `$TMPDIR/mypowertools/remote-notifications.core.sock` |
