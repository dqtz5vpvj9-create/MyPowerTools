# MyPowerTools

MyPowerTools 是一个集中管理日常小工具的桌面应用。

它把设备管理、系统诊断、显示器调节、图片传输和智能硬件控制放进同一个界面。打开应用即可查看所有工具、运行常用操作并了解当前状态，无需分别寻找和启动多个程序。

## Windows 开发版

首次使用需要通过 `scripts/install-windows.ps1` 建立完整安装布局。后续代码更新使用：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1
```

该入口以 Debug 配置快速覆盖 Shell 和 Runner，并从完整安装目录启动。模块、运行时、服务单元、Broker 与 ServiceManager 会继续保留。安装或刷新“ MyPowerTools 开发版”开始菜单快捷方式：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows-dev-shortcut.ps1
```

更新单个工具时传入工具 ID，例如：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId paste-image
```

## 截图

### 仪表盘

![MyPowerTools 仪表盘](docs/images/dashboard.png)

### 全部工具

![MyPowerTools 全部工具](docs/images/tools.png)

## 功能

- **本机卡顿专清**：检查 CPU、内存、磁盘、GPU、系统进程和可靠性事件，帮助定位电脑卡顿原因，并提供可确认的清理与恢复方案。
- **ADB Forwarder**：查看 ADB 连接和端口转发状态，集中处理 Android 设备的网络转发。
- **Android Tools**：接收远程通知、执行远程命令、监控指定进程，方便管理 Android 设备。
- **ScreenEase**：查看显示器信息，保存和应用亮度、色温等显示配置。
- **Doubao Agent**：统一查看豆包电脑操作服务的运行状态、配置和日志。
- **Paste Image**：读取剪贴板图片并上传到远程设备，上传完成后自动复制远程路径，并向前台窗口发送可配置的粘贴快捷键（默认 `Ctrl+Shift+V`）。
- **SmartBird Thermostat**：查看和控制 SmartBird 温控设备，浏览状态、事件、配置和日志。
- **统一工具入口**：在一个列表中搜索、收藏和打开工具，快速查看每个工具的可用状态。
- **后台常驻**：通过系统托盘保持服务运行，需要时快速打开主界面。
- **状态与提醒**：集中展示工具健康状态、运行日志和通知，出现问题时给出清晰提示。
