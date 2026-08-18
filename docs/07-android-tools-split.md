# AndroidTools 拆分方案

## 最终结论

AndroidTools 不作为一个单独工具迁入。`powertool` 作为一个 package 迁入，导出三个用户可见 module：

```text
android-tools-suite
├─ android-tools.notifications
├─ android-tools.remote-commands
└─ android-tools.process-monitor
```

三者共用 `AndroidTools.Runtime` gRPC Native IPC runtime、历史数据库、服务器客户端、secret store、日志目录、通用资源。

## 拆分理由

| 页面 | 独立业务语义 | 新模块 |
|---|---|---|
| Notifications | 远程消息、SSE、轮询、标签、桌面通知、手机推送 | `android-tools.notifications` |
| Remote Commands | 命令模板、Host、参数输入、执行输出、历史记录 | `android-tools.remote-commands` |
| Process Monitor | 进程状态、告警、诊断、后续自动化 | `android-tools.process-monitor` |

## 新 package 结构

```text
android-tools-suite
├─ package.json
├─ shared
│  ├─ AndroidTools.Runtime
│  ├─ assets
│  ├─ commands.index.json
│  ├─ dashboard.index.json
│  ├─ history.db
│  └─ package.hashes.json
└─ modules
   ├─ notifications
   │  ├─ module.json
   │  └─ settings.schema.json
   ├─ remote-commands
   │  ├─ module.json
   │  └─ settings.schema.json
   └─ process-monitor
      ├─ module.json
      └─ settings.schema.json
```

## 传输设计

```text
T0: package 静态索引提供首屏命令和卡片 skeleton
T1: AndroidTools.MyPowerTools InProc facade 承载当前生产桥接层
T2: AndroidTools.Runtime 通过 gRPC Native IPC 提供主能力
T4: 兼容期保留 jsonrpc-stdio module_server.py
```

当前实现状态：

```text
AndroidTools.MyPowerTools.dll
  -> android-tools.notifications: 导入通知端点配置，检查服务器可达性，提供 inbox summary
  -> android-tools.remote-commands: 导入 package-shared commands.yaml，生成动态 MPT commands，执行已迁移文本工具
  -> android-tools.process-monitor: 持久化共享 processes.json，扫描当前进程实例
```

`AndroidTools.Runtime` 继续作为长连接、轮询、SSE、共享历史数据库和跨语言服务化的 T2 目标。

Windows：

```text
MPTAndroidTools.Runtime.exe
  -> Named Pipe: mypowertools.android-tools-suite.module-host
```

macOS / Linux：

```text
MPTAndroidTools.Runtime
  -> Unix Domain Socket: $RUNTIME_DIR/mypowertools/android-tools-suite/module-host.sock
```

## Module 服务

`AndroidTools.Runtime` 内部提供多个服务：

```text
android_tools.notifications.v1.NotificationsModule
android_tools.remote_commands.v1.RemoteCommandsModule
android_tools.process_monitor.v1.ProcessMonitorModule
```

这些服务统一实现 MPT Protocol 的 `ModuleControl` 语义。

## Notifications 模块

Dashboard：

```text
未读数量
监听状态
服务状态
最新消息
```

DetailPage：

```text
消息列表
标签筛选
SSE / polling 状态
历史消息
错误诊断
```

Commands：

```text
android-tools.notifications.open
android-tools.notifications.search
android-tools.notifications.mute-tag
android-tools.notifications.toggle-persistent
android-tools.notifications.check-server
```

## Remote Commands 模块

Dashboard：

```text
默认 Host
最近命令
最近执行状态
快速运行
```

DetailPage：

```text
命令选择
Host 选择
参数表单
输出面板
执行历史
产物入口
```

Commands：

```text
android-tools.remote-commands.catalog.summary
android-tools.remote-commands.history.summary
android-tools.remote-commands.run.<powertool-command-id>
```

## Process Monitor 模块

Dashboard：

```text
监控进程数量
异常数量
最近事件
启用状态
```

DetailPage：

```text
进程列表
规则列表
告警记录
日志入口
```

Commands：

```text
android-tools.process-monitor.status.summary
android-tools.process-monitor.watch.list
android-tools.process-monitor.watch.save
```

## PyQt UI 的位置

PyQt UI 作为迁移参考和临时 fallback。正式入口迁入 Avalonia Surface。

```text
PyQt UI             迁移参考
powertool 业务逻辑   服务化为 AndroidTools.Runtime
Avalonia Surface    正式 MyPowerTools 入口
```
