# 仓库结构

## 推荐结构

```text
MyPowerTools
├─ src
│  ├─ MyPowerTools.Runner
│  ├─ MyPowerTools.Shell.Avalonia
│  ├─ MyPowerTools.HostControl
│  ├─ MyPowerTools.Runtime
│  ├─ MyPowerTools.Protocol
│  ├─ MyPowerTools.ModuleHost
│  │  ├─ InProcDotNet
│  │  ├─ GrpcIpc
│  │  ├─ Http
│  │  └─ StdioCompat
│  ├─ MyPowerTools.Platform.Abstractions
│  ├─ MyPowerTools.Platform.Windows
│  ├─ MyPowerTools.Platform.Mac
│  ├─ MyPowerTools.Platform.Linux
│  ├─ MyPowerTools.Broker
│  ├─ MyPowerTools.Packaging
│  ├─ MyPowerTools.UI
│  │  ├─ Tokens
│  │  ├─ Controls
│  │  ├─ Layouts
│  │  ├─ Icons
│  │  └─ Testing
│  ├─ MyPowerTools.Cli
│  └─ MyPowerTools.Tests
│
├─ proto
│  ├─ mpt_module_v1.proto
│  └─ mpt_host_control_v1.proto
│
├─ sdk
│  ├─ dotnet
│  ├─ python
│  ├─ node
│  └─ rust
│
├─ modules
│  ├─ android-tools-suite
│  ├─ screenease
│  ├─ doubao-agent
│  ├─ smartbird-thermostat
│  └─ adb-forwarder
│
├─ templates
│  ├─ dotnet-inproc-module
│  ├─ dotnet-grpc-sidecar-module
│  ├─ python-grpc-sidecar-module
│  ├─ http-facade-module
│  ├─ stdio-compat-module
│  └─ webview-module
│
├─ schemas
├─ ui
├─ docs
├─ adr
└─ tests
```

## 关键项目说明

| 项目 | 说明 |
|---|---|
| `MyPowerTools.Runner` | 常驻控制面，管理 tray、hotkey、runtime、module host、settings、broker |
| `MyPowerTools.Shell.Avalonia` | 独立 UI 进程，承载 Dashboard、Settings、Detail、Logs、Command Palette |
| `MyPowerTools.HostControl` | Shell 与 Runner 的 typed IPC client/server |
| `MyPowerTools.Protocol` | Protobuf 生成类型、错误模型、协议 helpers |
| `MyPowerTools.ModuleHost.InProcDotNet` | 加载可信 .NET module |
| `MyPowerTools.ModuleHost.GrpcIpc` | 管理 gRPC Native IPC sidecar |
| `MyPowerTools.ModuleHost.Http` | 包装已有 HTTP/WebSocket 服务 |
| `MyPowerTools.ModuleHost.StdioCompat` | JSON-RPC stdio fallback |
| `MyPowerTools.Runtime` | registry、supervisor、event bus、settings store |
| `MyPowerTools.Broker` | 权限、secret、service、network、自启动 |
| `MyPowerTools.Platform.*` | 平台能力实现 |
| `MyPowerTools.UI` | design tokens、统一控件、布局、图标、视觉回归工具 |
