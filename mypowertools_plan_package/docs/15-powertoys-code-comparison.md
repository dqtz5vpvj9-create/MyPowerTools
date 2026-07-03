# PowerToys 源码对照

## 对照范围

对照目标是 `microsoft/PowerToys` 当前主线架构，重点关注：

```text
Runner
module interface
settings IPC
Command Palette provider
hotkey routing
process split
module lifecycle
```

参考路径：

```text
doc/devdocs/core/runner.md
src/runner/main.cpp
src/modules/interface/powertoy_module_interface.h
src/runner/settings_window.cpp
src/modules/cmdpal/README.md
src/modules/cmdpal/extensionsdk/Microsoft.CommandPalette.Extensions/Microsoft.CommandPalette.Extensions.idl
src/modules/cmdpal/Microsoft.CmdPal.UI.ViewModels/TopLevelCommandManager.cs
```

## PowerToys 的关键事实

| 观察点 | 源码事实 | MyPowerTools 采用方式 |
|---|---|---|
| Runner 常驻 | `PowerToys.exe` 加载并管理模块、tray、hotkey、settings | `MyPowerTools.Runner` 常驻 |
| 模块清单 | `main.cpp` 有 knownModules DLL 清单 | 改为 package registry 和 module manifest |
| 模块接口 | `PowertoyModuleIface` 暴露 `get_config`、`set_config`、`enable`、`disable`、hotkeys | 改为 typed `IMptModule` / `ModuleControl` |
| Settings UI | Settings UI 独立进程，经 Named Pipes + JSON 和 Runner 通信 | `Shell.Avalonia` 独立进程，经 typed Host Control IPC 通信 |
| 复杂模块 | PowerToys Run / CmdPal 由接口 DLL 启动独立应用 | sidecar 与 package runtime pool 承担复杂工具 runtime |
| Command Palette | `ICommandProvider` 聚合顶层命令、fallback 命令、dock bands | `CommandProvider` + `CommandIndex` 聚合静态和动态命令 |
| provider 加载 | 内置和外部 provider 进入同一 top level manager，慢加载可后台补齐 | 静态 commands.index 首屏可用，动态 provider 后台补充 |
| Windows 细节 | DLL、Named Pipes、WinRT AppExtension、Win32 message loop | 通过 Platform Pack 和 TransportSelector 抽象 |

## 架构修正结论

原计划里 `MyPowerTools.Runner + Shell + Runtime` 容易做成单进程桌面应用。对照 PowerToys 后，控制面调整为：

```text
MyPowerTools.Runner
  长期常驻，最小 UI，负责 tray、hotkey、module lifecycle、settings、event bus、broker。

MyPowerTools.Shell.Avalonia
  独立 UI 进程，负责 Dashboard、Settings、Detail、Logs、Command Palette。

Host Control IPC
  Shell 与 Runner 的 typed gRPC native IPC。
```

这个修正解决三类问题：

| 风险 | 修正 |
|---|---|
| Shell 崩溃导致模块 runtime 退出 | Runner 与 Shell 分进程 |
| UI 复杂度污染 runtime 控制面 | Shell 只通过 Host Control 访问 Runner |
| 未来 Command Palette 需要轻量启动 | CommandIndex 常驻 Runner，Shell 只负责展示 |

## 保留 PowerToys 的结构思想

```text
常驻 Runner
统一模块契约
设置中心和 Runner 分离
复杂模块独立进程
中心化 hotkey 处理
命令 provider 聚合
模块启停由 settings 驱动
```

## 不复制 PowerToys 的 Windows 实现细节

| PowerToys 细节 | MyPowerTools 取代方案 |
|---|---|
| `LoadLibrary + powertoy_create` | InProc .NET SDK + gRPC sidecar + HTTP facade |
| `PowertoyModuleIface` C++ ABI | typed protocol + generated SDK |
| Named Pipes + raw JSON | gRPC over Named Pipes / UDS + Protobuf |
| WinRT AppExtension | module package + provider manifest |
| Win32 message loop | Platform Pack |
| Settings UI 专属 Windows 管道 | Host Control Protocol 跨平台化 |

## 通过标准

架构实现需要满足以下对照门槛：

```text
Runner 可无 Shell 常驻
Shell 退出不影响模块
模块可独立启停
设置变更经 Runner 分发
Command Palette 命令可从 provider 聚合
慢 provider 不阻塞首屏
平台 API 不进入模块业务代码
```
