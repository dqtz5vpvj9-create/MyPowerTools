# MyPowerTools macOS 安装器

「MyPowerTools Installer」是一个可以直接双击打开的小程序。它会从 GitHub 下载最新版
MyPowerTools，校验后安装到你的 Mac 上；以后有新版本时，再打开它就能一键更新。
整个过程不需要打开「终端」，也不需要安装 .NET 或 PowerShell。

## 下载哪一个

打开 [最新发布页](https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/latest)，
在 Assets 里按你的 Mac 选择：

| 你的 Mac | 下载 |
|---|---|
| Apple 芯片（M1、M2、M3、M4 等） | `MyPowerTools-Installer-macos-arm64.zip` |
| Intel 处理器 | `MyPowerTools-Installer-macos-x64.zip` |

不确定是哪种：点屏幕左上角苹果菜单  ›「关于本机」，「芯片」一行写着 Apple M… 就是
Apple 芯片；写着「处理器 … Intel」就是 Intel。

## 安装步骤

1. 下载后双击 zip 文件，得到「MyPowerTools Installer」（Safari 通常会自动解压）。
2. 双击「MyPowerTools Installer」。第一次打开时 macOS 会拦截，按下一节放行一次即可。
3. 安装器打开后会自动检查版本，点击 **安装 MyPowerTools**。
4. 等待进度条走完。看到「完成，正在打开 MyPowerTools…」后，安装器会自动关闭，
   MyPowerTools 会出现在屏幕顶部的菜单栏里（它没有 Dock 图标）。

MyPowerTools 安装在你自己的「应用程序」文件夹：`~/Applications/MyPowerTools.app`
（Finder › 前往 › 个人 › 应用程序）。安装不需要管理员密码。

## 第一次打开时被 macOS 拦住怎么办

安装器目前使用 ad-hoc 签名，没有经过 Apple 公证，所以第一次打开时 macOS 会提示
「无法验证开发者」或「Apple 无法检查其是否包含恶意软件」。这是预期行为，放行一次后
以后都能直接打开。

### macOS 15 Sequoia 及更新版本

1. 双击「MyPowerTools Installer」，在弹出的提示里点 **完成**（不要点「移到废纸篓」）。
2. 打开 **系统设置 › 隐私与安全性**，向下滚动到「安全性」部分。
3. 你会看到「已阻止“MyPowerTools Installer”以保护你的 Mac」，点旁边的 **仍要打开**。
4. 再次确认 **仍要打开**，按提示输入登录密码或使用触控 ID。

macOS 15 起，右键 ›「打开」不再能跳过这个拦截，必须走系统设置。

### macOS 14 Sonoma 及更早版本

任选一种：

- 在 Finder 里 **按住 Control 键点按**（或右键点按）「MyPowerTools Installer」，选择
  **打开**，在弹出的对话框中再点 **打开**。
- 或者先双击一次，看到提示后点 **好**；然后打开 **系统设置 › 隐私与安全性**，在
  「安全性」部分点 **仍要打开**，再在对话框里点 **打开**。

### 仍然打不开

- 提示「已损坏，无法打开」：通常是下载不完整。删除后重新下载 zip 再试。
- 依然不行时，可以请熟悉电脑的朋友在「终端」执行一次下面的命令（路径按实际位置修改），
  它只移除这个文件的“来自互联网”标记：

  ```bash
  xattr -dr com.apple.quarantine ~/Downloads/"MyPowerTools Installer.app"
  ```

## 更新

再次打开「MyPowerTools Installer」：

- 有新版本时按钮显示 **更新到 x.y.z**，点击即可。更新时会先暂停 MyPowerTools 的后台
  服务，替换完成后自动重新打开；更新失败会自动恢复到原来的版本。
- 已经是最新版本时显示「已是最新版本」，按钮 **打开 MyPowerTools** 直接启动应用。
- 网络不通或下载失败时会显示原因和 **重试** 按钮，原来的安装不会受影响。

装好以后，日常更新直接在 MyPowerTools 里点「检查更新」。应用内更新由随包的 `mpt ota`
调用同一个原生更新引擎（`MacNativeInstaller`）完成，不需要 PowerShell，也不需要再下载安装器。

## 常见问题

**需要管理员权限吗？** 不需要。应用装在当前用户的 `~/Applications`，数据保存在
`~/Library/Application Support/MyPowerTools`。

**会删除我的设置吗？** 不会。安装和更新只替换应用本身，设置和数据保留。

**安装器可以删掉吗？** 可以。安装完成后可以把下载的安装器移到废纸篓，以后需要时再下载；
应用包内还带有一份。

---

## 开发者说明

### 工作方式

安装器只是一个 Avalonia 小窗口，所有安装逻辑都在 `MacNativeInstaller`
（`src/MyPowerTools.Packaging/MacNativeInstaller.cs`，命名空间
`MyPowerTools.Packaging.Ota`）里，与 CLI 和应用内更新共用：

1. 打开窗口后调用 `CheckAsync()`：读取已安装版本，下载并验证签名的 OTA feed
   （`channel-<channel>-osx-<arch>.json`），返回 `installed`、`currentVersion`、
   `latestVersion`、`available`、`reason`。
2. 点击安装/更新后调用 `ApplyAsync()`，进度阶段依次为
   `check → download → verify → stage → stop → swap → services → health → done`，
   失败时进入 `rollback`。下载阶段显示确定进度，其余阶段显示不确定进度。
3. 成功后引擎按 `Relaunch = true` 重新打开 MyPowerTools，安装器约 2 秒后自行退出。

整个流程不依赖 pwsh；旧的 `install-macos.ps1` / `ota-update-macos.ps1` 仍保留给开发者。

窗口状态：

| 检查结果 | 状态文字 | 主按钮 |
|---|---|---|
| 未安装 | 这台 Mac 还没有安装 MyPowerTools | 安装 MyPowerTools |
| 有更新（含 `forced`） | 当前版本 …，有新版本 … | 更新到 {latest} |
| 最新 / `downgrade-blocked` | 已是最新版本 {v} | 打开 MyPowerTools（`/usr/bin/open`） |
| 检查失败 | 错误原因 | 重试 |
| 非 macOS | 此安装器仅适用于 macOS | 无 |

安装过程中窗口不可关闭，避免在替换应用包的途中退出。

### 命令行参数

| 参数 | 作用 |
|---|---|
| `--update` | 检查后如有更新立即开始安装，供应用内更新调用 |
| `--channel <name>` | OTA 渠道，默认 `stable` |
| `--app <path>` | 目标应用包；省略时，如果安装器位于某个 `X.app/Contents/Resources/Installer/` 下，则更新这个 `X.app`，否则使用 `~/Applications/MyPowerTools.app` |
| `--feed <url>` | 覆盖 feed 地址（测试用） |

应用内更新建议先把 `MyPowerTools Installer.app` 用 `ditto` 复制到临时目录再以
`open -n "<copy>" --args --update --app "<当前 MyPowerTools.app>"` 启动，这样替换应用包时
不会移动安装器自身所在的目录。

### 构建与发布

项目：`src/MyPowerTools.Installer.Mac`（net10.0，Avalonia 12，引用
`MyPowerTools.Packaging`），编译输出在
`artifacts/build/bin/MyPowerTools.Installer.Mac/<配置>`。

在 Mac 上生成应用包与 zip：

```powershell
pwsh ./scripts/publish-macos-installer.ps1 -Architecture arm64
pwsh ./scripts/publish-macos-installer.ps1 -Architecture x64
```

产物（已在 `scripts/artifacts-policy.json` 中声明为 `publish/macos-installer-*`）：

```text
artifacts/publish/macos-installer-<arch>/MyPowerTools Installer.app
artifacts/publish/macos-installer-<arch>/MyPowerTools-Installer-macos-<arch>.zip
artifacts/publish/macos-installer-<arch>/MyPowerTools-Installer-macos-<arch>.zip.sha256
```

- 发布方式：`dotnet publish` 自包含单文件（`PublishSingleFile`、
  `IncludeNativeLibrariesForSelfExtract`、`EnableCompressionInSingleFile`），用户无需安装
  .NET。Avalonia、Skia 的原生库首次运行时解压到 `~/.net`。未开启裁剪：引擎使用反射式
  JSON。
- 应用包：`Contents/MacOS/MyPowerToolsInstaller`、`Contents/Info.plist`
  （`packaging/macos/Installer.Info.plist`，`com.mypowertools.installer`，版本由脚本写入）、
  `Contents/PkgInfo`、`Contents/Resources/MyPowerTools.icns`（与主应用相同的
  svg → iconset → icns 流程）。
- 签名：默认 `-CodeSignIdentity '-'`（ad-hoc，`--timestamp=none`）。传入 Developer ID
  身份时加 `--options runtime` 与 `packaging/macos/MyPowerTools.entitlements`
  （.NET 需要 JIT 与关闭库校验）。`-SkipCodeSign` 用于非 Mac 主机的托管交叉发布检查。
- 压缩：`ditto -c -k --keepParent`，zip 顶层就是 `MyPowerTools Installer.app`。
- 安装器不嵌入主应用包：应用内更新走 `Contents/MacOS/Cli` 里的 `mpt ota`，与安装器共用
  `MyPowerTools.Packaging` 中的 `MacNativeInstaller`，避免每次 OTA 多下载一份自包含运行时。

### 发布资产与 CI

- `.github/workflows/macos-ota-release.yml`：两个架构在 `publish-macos.ps1` 之后各运行一次
  `publish-macos-installer.ps1`，把 `MyPowerTools-Installer-macos-<arch>.zip` 及其
  `.sha256` 上传到同一个 GitHub Release。
- `.github/workflows/macos-ota-validation.yml`：以 ad-hoc 签名构建 arm64 安装器，检查
  `.app` 布局、Mach-O 架构、bundle id、`codesign --verify` 和 zip 顶层结构。

### 公证（可选）

同时设置以下环境变量（CI 中为同名 secrets），并用 Developer ID Application 身份签名时，
脚本会执行 `xcrun notarytool submit --wait` 与 `xcrun stapler staple`：

| 变量 | 内容 |
|---|---|
| `MPT_NOTARY_APPLE_ID` | 开发者账号 Apple ID |
| `MPT_NOTARY_TEAM_ID` | Team ID |
| `MPT_NOTARY_PASSWORD` | App 专用密码 |

ad-hoc 签名无法公证，此时即便设置了变量也会跳过并给出警告。当前 CI 没有导入
Developer ID 证书，所以发布的安装器是 ad-hoc 签名，用户需要按上文放行一次。要启用公证，
需要在 CI 中把证书导入钥匙串，并给 `publish-macos-installer.ps1` 传入
`-CodeSignIdentity "Developer ID Application: …"`。
