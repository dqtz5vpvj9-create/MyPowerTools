# Package 与 Module 模型

## 核心定义

```text
Package = 安装、更新、共享依赖、共享 runtime、共享资源的单位
Module  = 用户看到的工具单位
Runtime = 一个 package 内多个 module 可共享的后端进程或进程内服务
```

一个 package 可以导出多个 module。这个能力是 MyPowerTools 支持大量工具的基础。

## 包结构

```text
<package-id>.mptpkg
├─ package.json
├─ shared
│  ├─ assets
│  ├─ runtimes
│  ├─ commands.index.json
│  └─ package.hashes.json
├─ modules
│  ├─ module-a
│  │  ├─ module.json
│  │  ├─ settings.schema.json
│  │  └─ commands.index.json
│  └─ module-b
│     ├─ module.json
│     └─ settings.schema.json
├─ windows
├─ macos
└─ linux
```

## Runtime 共享模型

AndroidTools 这类包应使用一个共享 runtime：

```text
android-tools-suite package
  ├─ shared runtime: powertoold
  ├─ module: android-tools.notifications
  ├─ module: android-tools.remote-commands
  └─ module: android-tools.process-monitor
```

Host 看到三个工具，底层只启动一个 `powertoold`，并通过 gRPC Native IPC 暴露多个 module service。

## package.json 示例

```json
{
  "schemaVersion": "1.0",
  "id": "android-tools-suite",
  "displayName": "Android Tools Suite",
  "version": "0.2.0",
  "modules": [
    "modules/notifications/module.json",
    "modules/remote-commands/module.json",
    "modules/process-monitor/module.json"
  ],
  "shared": {
    "runtimes": [
      {
        "id": "powertoold",
        "entrypoints": [
          {
            "kind": "grpc-ipc",
            "priority": 90,
            "command": "windows/x64/powertoold.exe",
            "platforms": ["windows-x64"],
            "windows": {
              "transport": "named-pipe",
              "name": "mypowertools.android-tools-suite.powertoold"
            }
          }
        ]
      }
    ]
  }
}
```

## module.json 示例

```json
{
  "schemaVersion": "1.0",
  "id": "android-tools.remote-commands",
  "packageId": "android-tools-suite",
  "displayName": "Remote Commands",
  "version": "0.2.0",
  "moduleSdk": "1.0",
  "entrypoints": [
    {
      "kind": "package-runtime",
      "priority": 90,
      "runtimeId": "powertoold",
      "service": "android_tools.remote_commands.v1.RemoteCommandsModule"
    },
    {
      "kind": "jsonrpc-stdio",
      "priority": 10,
      "compat": true,
      "command": "python",
      "args": ["compat/module_server.py"]
    }
  ],
  "capabilities": ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]
}
```

## 静态索引

每个 package 应提供静态索引，避免 Host 启动时拉起所有模块。

```text
commands.index.json     命令面板首屏使用
dashboard.index.json    Dashboard skeleton 使用
settings.index.json     设置中心目录使用
```

动态命令和实时状态在模块激活后补充。


## UI 与 Package

一个 package 可以导出多个 module。每个 module 必须声明自己的 `ui.surfaces`，但 package 可以共享图标、资源、页面组件和 screenshot baseline。

```text
package shared assets
  -> module icon
  -> screenshot baselines
  -> custom panel resources
  -> shared style resources that only reference Shell tokens
```

共享资源不得覆盖 Shell 全局主题。
