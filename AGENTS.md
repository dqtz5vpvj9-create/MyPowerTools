## Windows Dev 版本运行方式

本地开发**默认只使用开发版**。不要用未叠加当前仓库代码的安装版做验证，包括
开始菜单里的正式 “MyPowerTools”、Inno/OTA 安装结果，以及未跑 Dev overlay 的
`%LOCALAPPDATA%\Programs\MyPowerTools`。日常工作流是：改代码 → 立即更新开发版
→ 启动开发版。

MyPowerTools Dev 是日常开发使用的快速运行方式，其结构为：

`完整安装布局 + 仓库最新编译组件`

当 `%LOCALAPPDATA%\Programs\MyPowerTools` 尚未建立完整安装布局时，例如
新电脑首次配置、安装目录已被清理或目录内容残缺，
`scripts/install-windows.ps1`会建立包含 modules、Runtimes、
service-units、ServiceManager、Broker 等运行组件的完整安装目录。该步骤无需手动执行。

日常开发时直接执行
`scripts/Start-MyPowerTools-Dev.ps1`；该脚本会自动检查 Launcher、Shell、
Runner、modules、Runtimes、service-units 和 ServiceManager。脚本报告
`The complete MyPowerTools installation is required` 时，执行一次
`scripts/install-windows.ps1`，然后重新运行 Dev 命令。

仓库负责提供源代码和最新编译产物。Dev 更新脚本只覆盖发生变化的
Shell、Runner 或工具包，然后从完整安装目录启动进程。
`-Scope Tools` 还会覆盖该工具已安装的 service-units（例如 Remote Notifications
的 toast 后台进程）；此前 Dev overlay 不会替换 service-units，所以改了通知服务
后必须重新跑 Tools 更新才会进桌面开发版。

**改完一个插件后必须立刻更新并启动开发版**，不要只停在编译成功，也不要把启动
留给用户。默认不要带 `-NoOpenShell`。脚本失败（例如缺少 `Runtimes`）时先补完整
安装布局，再重跑 Dev 命令，直到开发版真正启动。

修改 Shell、Runner、平台代码或公共依赖后，执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1
```

修改单个工具后，执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId <tool-id>
```

例如更新 Paste Image：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId paste-image
```

例如更新输入法管理器：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId ime-manager
```

例如更新 Remote Notifications（含桌面 toast 服务进程）：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId remote-notifications
```

需要创建或刷新开始菜单中的“MyPowerTools 开发版”快捷方式时，执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/install-windows-dev-shortcut.ps1
```

禁止直接启动仓库构建输出目录（`artifacts/build/bin/...`）中的 Shell 或 Runner。
该目录缺少完整运行布局，可能造成工具、运行时和后台服务缺失。Dev 进程必须从
`%LOCALAPPDATA%\Programs\MyPowerTools` 启动。

## Remote Notifications 双端同步

Remote Notifications 相关改动必须同时更新两端，不能只改桌面或只改手机：

- 桌面：`tools/remote-notifications`（MyPowerTools 插件、Surface UI、Service）
- 手机：`external/NotifyApp`（GitHub 仓库 `dqtz5vpvj9-create/NotifyApp`）

消息格式、引用块处理、横幅正文、列表预览等行为必须在桌面插件和 Notify APP
保持一致。改完桌面插件后按上一节立刻更新并启动开发版；Notify APP 的改动按
下一节升版本并上架 GitHub。

## GitHub 闭环

所有更新以上架 GitHub 为完成标准，不要把只改了本地、未推送的状态当成完成。
工具子模块（例如 `tools/remote-notifications`）与父仓库的 submodule 指针都要
推到对应远程。推送失败时必须明确说明闭环未完成。

手机 Notify APP 每次有实质更新时必须：

1. 同时提升 `external/NotifyApp/app/build.gradle.kts` 里的 `versionName` 和
   `versionCode`，以及 `external/NotifyApp/notifyapp_version.json` 的 `version`。
2. 把 Notify APP 提交并推送到 `https://github.com/dqtz5vpvj9-create/NotifyApp`，
   按 `versionName` 打 tag 并发布 GitHub Release（上架 APK）。
3. 更新父仓库中 `external/NotifyApp` 的 submodule 指针并推送。

未 bump 版本、未推送 NotifyApp 仓库或未发布 Release，都不算 Notify APP 更新完成。

## artifacts 目录治理

`src/` 下所有项目的编译输出统一落在 `artifacts/build/bin/<项目名>/<小写配置名>`，
而不是各自的 `bin`/`obj`。需要引用某个 `src/` 项目的产物时，从这个布局推导路径，
不要硬编码 `bin/Debug`。

`artifacts/` 下的每一条路径都必须在 `scripts/artifacts-policy.json` 中声明所属类别
和保留规则。新增任何往 `artifacts/` 写入的产物时，同时补上对应条目，否则 CI 的
`Artifacts Governance Gate` 会失败。

查看占用与可回收内容：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/prune-artifacts.ps1 -Report
```

按保留规则回收（先用 `-WhatIf` 预览）：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/prune-artifacts.ps1
```

完整约定见 `docs/ARTIFACTS_GOVERNANCE.md`。
