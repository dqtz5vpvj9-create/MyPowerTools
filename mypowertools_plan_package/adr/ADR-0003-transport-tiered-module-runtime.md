# ADR 0003 Transport-tiered Module Runtime

## 决策

MyPowerTools 采用分级传输运行时：

```text
T0 Static Index
T1 InProc .NET SDK
T2 gRPC over Native IPC
T3 HTTP / WebSocket localhost
T4 JSON-RPC stdio fallback
```

## 背景

模块来自同一作者，源码可控。底座目标包含性能、同步完整性、扩展性和跨平台。单纯使用 `stdin/stdout` 虽然兼容性强，但会把主架构限制在最低公共分母上。

## 结果

可信 .NET 模块优先 in-process 调用。跨语言 sidecar 使用 gRPC over platform-native IPC。已有 HTTP 服务通过 typed facade 接入。stdio 只保留为兼容层。

## 影响

```text
性能关键路径走 InProc 或 Native IPC
强类型协议减少 schema 演进风险
事件流、日志流、取消、超时由 gRPC 支持
sidecar 崩溃隔离仍然保留
每个平台使用合适 IPC
stdio 不再承载正式底座主路径
```

## 平台映射

```text
Windows  -> Named Pipes
macOS    -> Unix Domain Sockets
Linux    -> Unix Domain Sockets
```

## 取舍

```text
实现复杂度上升
需要维护 proto 和 SDK 生成流程
换来性能、完整性、跨语言和长期扩展能力
```
