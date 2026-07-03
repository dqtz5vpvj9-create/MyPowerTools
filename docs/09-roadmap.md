# 实施路线

## 阶段总览

本路线按 agent gate 组织。阶段之间只保留依赖、产物和验收条件，不包含人工时间估算。

| 阶段 | 名称 | 目标 | 产物 |
|---|---|---|---|
| P0 | 协议与传输定稿 | 定 typed protocol、entrypoints、transport selector、host control | proto + schemas + ADR |
| P1 | Runner / Shell 骨架 | Runner 常驻，Shell 独立，Host Control 可用 | 可运行双进程原型 |
| P2 | Windows MVP | Avalonia Shell + Runtime + InProc 模块 | 可运行模块原型 |
| P3 | gRPC Native IPC | Windows Named Pipes sidecar host | GrpcIpcModuleHost |
| P4 | UI 系统准入 | token、组件、视觉回归、设置 schema | UI baseline |
| P5 | Multi-module Package | AndroidTools 拆成 3 个模块 | android-tools-suite |
| P6 | Platform Packs | Windows 能力包和 Broker | service/network/secret/display provider |
| P7 | 现有工具迁移 | 接入 5 个工具包 | Dashboard 全量可用 |
| P8 | Linux/macOS | UDS sidecar + 平台能力补齐 | 跨平台预览版 |
| P9 | 模块生态 | CLI、模板、验证器、打包 | 新工具标准化接入 |

## P0 协议与传输定稿

输入：

```text
docs/00-final-decision.md
docs/13-transport-runtime.md
docs/14-consistency-integrity-scalability.md
docs/15-powertoys-code-comparison.md
```

任务：

```text
定义 module/package schema
定义 ui-surface schema
定义 entrypoints schema
定义 mpt_module_v1.proto
定义 mpt_host_control_v1.proto
定义 error code
定义 event seq / settings revision
定义 transport selector 规则
定义 package runtime pool
```

验收：

```text
mpt validate package 可校验示例包
proto 可生成 C# 和 Python stub
示例 module.json 通过 schema
ui surface 示例通过 schema
ADR-0003 明确 stdio fallback 定位
ADR-0006 明确 Runner/Shell 分进程
```

## P1 Runner / Shell 骨架

任务：

```text
MyPowerTools.Runner
MyPowerTools.Shell.Avalonia
Host Control IPC Server
Host Control IPC Client
Runner single instance
Runner tray placeholder
Shell startup handshake
Dashboard skeleton from Runner snapshot
```

验收：

```text
Runner 可无 Shell 常驻
Shell 可连接 Runner
Shell 退出不影响 Runner
Shell 重启后可恢复 Dashboard snapshot
Runner 可响应 OpenShell、ShowDashboard、Quit
```

## P2 Windows MVP

任务：

```text
Avalonia Shell
Dashboard skeleton
Settings Center skeleton
Command Palette skeleton
PackageRegistry
ModuleRegistry
InProcDotNetHost
ScreenEase Sample Module
```

验收：

```text
启动不依赖 sidecar
命令面板可读取 commands.index
ScreenEase sample 通过 InProc 调用 GetStatus/ListCommands
Shell 全程通过 Runner 获取数据
```

## P3 gRPC Native IPC

任务：

```text
GrpcIpcModuleHost
Windows Named Pipe transport
ModuleSupervisor
SubscribeEvents stream
TailLogs stream
Python gRPC sidecar template
```

验收：

```text
Python sample sidecar 可被 Runner 启动
事件流可订阅
sidecar 崩溃 Runner 不崩溃
Runner 可重启 sidecar
Shell 可查看 sidecar 状态和日志
```

## P4 UI 系统准入

任务：

```text
Mpt design tokens
Mpt shell layout
Mpt component library
Mpt settings renderer
Mpt status and error components
Visual regression matrix
UI lint rules
```

验收：

```text
Dashboard、Detail、Settings、Command Palette 都使用 token
模块自定义 surface 不允许注入全局样式
Light/Dark/Compact 三组截图通过视觉回归
空状态、错误状态、加载状态、权限状态都有统一组件
```

## P5 AndroidTools 拆分

任务：

```text
powertoold runtime
notifications module
remote-commands module
process-monitor module
shared history store
Avalonia pages
```

验收：

```text
Dashboard 展示三个独立卡片
Command Palette 展示 remote commands
Notifications 接入 NotificationCenter
底层只启动一个 powertoold
AndroidTools 页面通过 UI gate
```

## P6 Platform Packs 与 Broker

任务：

```text
Windows Platform Pack
PrivilegedBroker
SecretBroker
ServiceBroker
NetworkBroker
AutostartBroker
```

验收：

```text
AdbForwarder 高权限动作走 Broker
SmartBird 服务控制走 ServiceBroker
Secrets 不进入模块明文配置
Broker 审计日志可在 Logs Viewer 查看
```

## P7 现有工具迁移

任务：

```text
ScreenEase 完整迁移
Doubao Agent controller
SmartBird typed facade
AdbForwarder InProc UI + Broker
AndroidTools 三模块完善
```

验收：

```text
五个工具包全部在 Dashboard 可用
所有用户可见模块提供 status、commands、settings、logs
Host 不包含工具专属业务逻辑
模块页面通过 UI gate
```

## P8 Linux/macOS

任务：

```text
Unix Domain Socket transport
macOS Platform Pack 初版
Linux Platform Pack 初版
路径、通知、自启动、service provider
```

验收：

```text
Sample module 在三平台运行
TransportSelector 按平台选择 entrypoint
缺失 capability 显示降级状态
UI baseline 在三平台截图通过
```

## P9 模块生态

任务：

```text
mpt create module
mpt validate module
mpt package module
mpt install module
mpt update module
mpt inspect runtime
mpt ui check
mpt ui snapshot
```

验收：

```text
新工具可以从模板创建
package 可以校验
module 可以 inspect
package 可以打包
安装失败可以 rollback
UI surface 可以自动检查
```
