# Transport-tiered Module Runtime

## 定案

MyPowerTools 使用分级传输模型：

```text
Protocol-first
Transport-tiered
Platform-native IPC
InProc for trusted modules
gRPC IPC for sidecars
HTTP facade for existing services
stdio compatibility only
```

## 控制面传输

Shell 与 Runner 使用 typed Host Control Protocol。

```text
Windows     gRPC over Named Pipes
macOS       gRPC over Unix Domain Sockets
Linux       gRPC over Unix Domain Sockets
```

Shell 不直接访问模块进程、sidecar、broker、settings store。所有控制请求先进入 Runner。

## 模块传输分级

| 等级 | 通道 | 对象 | 说明 |
|---:|---|---|---|
| T0 | manifest / commands.index | 静态索引 | 启动时只读文件，不启动 runtime |
| T1 | InProc .NET | 可信模块 | 最高性能，适合自有 Avalonia/.NET 工具 |
| T2 | gRPC Native IPC | sidecar | 跨语言、强隔离、强类型、流式事件 |
| T3 | HTTP/WebSocket facade | 既有服务 | 包装已有本地或远程服务 |
| T4 | JSON-RPC stdio | fallback | 临时脚本和开发兼容层 |

## 平台映射

```text
Windows
  InProc .NET
  gRPC over Named Pipes
  HTTP/WebSocket facade
  stdio compatibility

macOS
  InProc .NET
  gRPC over Unix Domain Sockets
  HTTP/WebSocket facade
  stdio compatibility

Linux
  InProc .NET
  gRPC over Unix Domain Sockets
  HTTP/WebSocket facade
  stdio compatibility
```

## EntryPoint 选择

`module.json` 支持多个 entrypoint：

```json
{
  "entrypoints": [
    {
      "kind": "inproc-dotnet",
      "priority": 100,
      "assembly": "ScreenEase.Module.dll",
      "type": "ScreenEase.Mpt.ScreenEaseModule",
      "platforms": ["windows", "macos", "linux"]
    },
    {
      "kind": "grpc-ipc",
      "priority": 80,
      "command": "AndroidTools.Runtime",
      "windows": {
        "transport": "named-pipe",
        "name": "mypowertools.android-tools"
      },
      "macos": {
        "transport": "unix-domain-socket",
        "path": "$RUNTIME_DIR/mypowertools/android-tools.sock"
      },
      "linux": {
        "transport": "unix-domain-socket",
        "path": "$XDG_RUNTIME_DIR/mypowertools/android-tools.sock"
      }
    },
    {
      "kind": "jsonrpc-stdio",
      "priority": 10,
      "compat": true,
      "command": "python",
      "args": ["module_server.py"]
    }
  ]
}
```

选择规则：

```text
OS / CPU 架构匹配
SDK 版本兼容
capability 满足
permission 满足
entrypoint health check 通过
priority 最高
fallback 可用
```

## PackageRuntimePool

一个 package 可以导出多个 module，底层共享一个 runtime。

```text
android-tools-suite
  ├─ notifications module
  ├─ remote-commands module
  ├─ process-monitor module
  └─ shared AndroidTools.Runtime
```

Runner 对 package runtime 进行池化：

```text
按需启动
引用计数
健康检查
崩溃重启
空闲回收
事件复用
日志复用
```

## stdio compatibility

stdio 只用于：

```text
开发样例
轻量脚本
临时迁移
无法使用 gRPC 的工具
```

限制：

```text
不承载高频事件
不承载大 payload
不承载高权限动作
不承载长期服务主路径
```
