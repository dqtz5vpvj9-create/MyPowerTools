## Windows Dev 版本运行方式

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

需要创建或刷新开始菜单中的“MyPowerTools 开发版”快捷方式时，执行：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/install-windows-dev-shortcut.ps1
```

禁止直接启动仓库 `bin/Debug` 目录中的 Shell 或 Runner。该目录缺少完整
运行布局，可能造成工具、运行时和后台服务缺失。Dev 进程必须从
`%LOCALAPPDATA%\Programs\MyPowerTools` 启动。
