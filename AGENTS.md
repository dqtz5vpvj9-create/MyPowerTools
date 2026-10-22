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

## 构建与测试不得弹出命令行窗口

`dotnet build`、`dotnet test`、`scripts/**`、`tools/**`、`tests/**` 里的构建和测试
都在开发者当前的桌面会话里执行，任何新开的控制台窗口都会抢焦点、打断用户正在做的事。
能画出这种窗口的只有三种写法，构建和测试代码都不许碰到：

- `CreateProcess` 带 `CREATE_NEW_CONSOLE`（仅 SCM 的 session-0 服务宿主允许，见
  `NssmConsole.ShouldAllocateConsole` 与 `NativeChildProcess.ConsoleCreationFlags`）。
- 交互式宿主里调用 `AllocConsole()`（唯一例外是用户显式请求控制台的 `--console`）。
- 对控制台程序使用 ShellExecute，也就是 PowerShell 的 `Start-Process` 不带
  `-WindowStyle Hidden` / `-NoNewWindow`。

具体要求：

- PowerShell／批处理启动控制台程序时必须 `-WindowStyle Hidden` / `-NoNewWindow`，
  或显式 `UseShellExecute = $false` 且 `CreateNoWindow = $true`；Inno `[Run]` 里启动
  `powershell.exe` / `cmd.exe` / `pwsh` 必须带 `runhidden`。
- .NET 测试代码启动进程必须 `UseShellExecute = false` 且 `CreateNoWindow = true`。
- 启动 GUI（WinExe）产品入口（`MyPowerTools.exe`、Shell）时不要隐藏窗口，
  `Start-Process` 不要加 `-WindowStyle Hidden`，否则会把产品界面一起藏起来。

该规则由 Architecture Gate 的 `A1.16-no-console-window-in-build-and-test` 强制：
scripts/tools/tests 的脚本、测试 C# 代码、控制台分配调用和 Inno `[Run]` 条目都会被
扫描，新增未抑制的启动会让 `scripts/verify-architecture.ps1 -Tier Quick` 直接失败。
确需豁免时在 `tests/architecture-gate/Program.cs` 的 `ConsoleWindowExemptions` 表里
登记路径、标记和原因，不要用注释或改写语句绕过。

怀疑还有窗口在弹时，用窗口监视器抓现行（记录每个新窗口的类名、所属进程、可见性、
大小、存活时间，以及每个新进程的父进程与命令行）：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/diagnose-console-windows.ps1 -Seconds 300
```

脚本结束时打印 `visibleConsoleWindows=` 计数和每个可见控制台窗口的创建者；复现问题
期间让它跑着即可。`hidden` 的 `ConsoleWindowClass` 与 0x0 的 `PseudoConsoleWindow`
是无窗口控制台（`CreateNoWindow` / ConPTY），不算打扰。

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

## macOS 低功耗发布门槛（默认要求）

macOS 版优先服务低内存、电池设备。每次 macOS 发布默认执行
`docs/MACOS_POWER_ACCEPTANCE.md`，不是可选优化。

- 必须在真实 macOS 完整安装版上测量，关闭窗口后保留用户明确启用的后台任务；未使用的工具不得因安装而启动服务、轮询或动画。
- 使用 `scripts/measure-macos-idle.py`，预热后连续测量至少 120 秒；全部 MPT
  进程及其子进程平均 CPU 合计应不超过单核的 1%。0% 是无工作时的方向，不能
  通过停用用户正在使用的服务、丢消息或延迟正常消息接收制造结果。
- 区分禁用与闲置：用户禁用的服务不启动；已启动但闲置的服务允许驻留，线程应阻塞
  等待事件，不能用自动停止服务代替消除空转。驻留进程同样计入 CPU 预算。
- 用户明确禁用的工具必须同时停止模块观察和独立服务；不能只隐藏入口。没有活动任务
  的工具应等待用户操作或系统事件，不得为了刷新状态持续读文件、探测硬件或服务。
- 同时验证通知接收/历史/手动刷新、窗口恢复、内存稳定性，并记录后台唤醒。
- macOS 默认关闭装饰动画，忙碌状态用静态指示；窗口隐藏或最小化时保留页面、
  绑定与命令状态，不得以整体卸载可视树代替停止空转。只暂停不可见时无意义的动画、
  绘制和展示刷新，恢复时补充刷新；后台业务继续运行。整体卸载必须另有同条件对照
  测量收益和完整恢复验收，不作为默认策略。
- 超预算时必须用函数调用栈定位热点；Instruments 可用时使用 CPU/Time Profiler
  与系统/能耗跟踪。没有 Instruments 时可使用 sample、dotnet-trace，但必须说明
  归因限制，不能把等待栈占比当成 CPU 占比，也不能把 CPU 百分比换算成续航。
- 未完成或未通过验收时，构建只能作为明确标注的待验收开发包；不得称作省电达标。
  发布说明必须附测量条件、结果与未通过项目。详细门槛见上述文档。
