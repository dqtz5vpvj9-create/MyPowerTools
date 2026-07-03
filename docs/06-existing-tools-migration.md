# 现有工具迁移方案

## 迁移总表

| 工具 | 目标形态 | 首轮目标 | 深度目标 |
|---|---|---|---|
| AndroidTools / powertool | `android-tools-suite` package | 拆成三个 module，共享 `powertoold` gRPC IPC runtime | Avalonia 原生页面 + 命令和通知完全平台化 |
| ScreenEase | `screenease` module | InProc Avalonia module 接入 status、profile、settings | 显示能力进入 Platform Pack |
| 豆包 Agent | `doubao-agent` module | gRPC runtime controller 接入 health、启动停止、日志 | WPF 壳迁移为 MyPowerTools 页面 |
| 散热器管理服务 | `smartbird-thermostat` module | gRPC facade 包装现有 HTTP status、事件、重启 | 服务托管和策略配置平台化 |
| AdbForwarder | `adb-forwarder` module | InProc UI + Broker 接入 ADB 状态、portproxy 诊断 | Network Broker + Privileged Broker 完整接管 |

## AndroidTools / powertool

最终形态：

```text
android-tools-suite.mptpkg
├─ shared runtime: powertoold
├─ android-tools.notifications
├─ android-tools.remote-commands
└─ android-tools.process-monitor
```

传输策略：

```text
T0 commands.index/dashboard.index
T2 gRPC IPC shared powertoold
T4 jsonrpc-stdio compat fallback
```

迁移任务：

1. 从 PyQt UI 中提取 UI 无关逻辑。
2. 建立 `powertoold`，使用 gRPC Native IPC 暴露模块服务。
3. `commands.yaml` 继续作为内部命令来源，模块转换为标准 `MptCommand`。
4. Notifications、Remote Commands、Process Monitor 通过 Shell 通用 Modules / Settings / Logs / Notifications 页面暴露；模块专用 Avalonia 表单继续推进。
5. 历史数据库、server client、secret 进入 shared runtime。

## ScreenEase

推荐路径：

```text
T1 InProc .NET module
+ T2 CoreService gRPC IPC 或现有 Named Pipe 兼容
+ display Platform Pack
```

当前实现状态：

```text
ScreenEase.MyPowerTools.dll
  -> screenease.status.summary
  -> screenease.displays.list
  -> screenease.profile.list / plan / apply / save
  -> screenease.rules.status
```

Windows `IDisplayService` 已可枚举 monitor；macOS/Linux provider 编译通过并显式降级。亮度、色温硬件写入当前返回 `native-host-required`，等待 ScreenEase native display writer 接入。

迁移任务：

1. 把 WPF Desktop 页面迁入 Avalonia Surface。
2. 保留 CoreService 作为独立服务边界。
3. 将 profile、亮度、色温、规则暴露为 Commands 和 Settings。
4. 显示器控制进入 `MyPowerTools.Platform.*.DisplayService`。

## 豆包 Agent

推荐路径：

```text
T2 gRPC runtime controller
+ existing HTTP services retained
+ SecretBroker
```

迁移任务：

1. 增加 `doubao-agent-controller`。
2. 统一检查 `38102 / 38080 / 38189`。
3. 暴露启动、停止、重启、自检、日志入口。
4. secrets 迁入 SecretBroker。
5. WPF 壳迁入 Avalonia DetailPage。

## 散热器管理服务

推荐路径：

```text
T2 gRPC facade
+ T3 existing HTTP service compatibility
+ ServiceBroker
```

迁移任务：

1. 保留现有 `19002/api/status`。
2. 增加 typed facade，对外提供 `GetStatus / ListEvents / Restart / GetLogs`。
3. 计划任务托管逐步迁到 ServiceBroker。
4. 策略配置进入 SettingsStore，邮件/通知进入 NotificationCenter。

## AdbForwarder

推荐路径：

```text
T1 InProc .NET module for UI/rules
+ PrivilegedBroker for netsh / firewall / portproxy
+ NetworkBroker
```

迁移任务：

1. 规则编辑、设备状态、诊断页面进 Avalonia。
2. ADB 设备发现作为普通 capability。
3. `netsh portproxy`、防火墙、服务安装全部走 PrivilegedBroker。
4. 命令执行必须使用 invocationId，避免重复写规则。
