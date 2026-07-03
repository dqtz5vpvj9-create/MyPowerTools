# 模块协议

## 协议目标

模块协议需要支持：

```text
发现
状态
命令
设置
UI Surface
日志
事件
生命周期
权限请求
平台能力申请
取消
进度
错误码
版本协商
```

协议层与传输层分离。协议使用强类型 MPT Protocol，主实现采用 Protobuf/gRPC。传输层由 Runtime 根据 entrypoint 选择。

## 协议服务

主服务为 `ModuleControl`：

```proto
service ModuleControl {
  rpc Initialize(InitializeRequest) returns (InitializeResponse);
  rpc GetStatus(GetStatusRequest) returns (ModuleStatus);
  rpc ListCommands(ListCommandsRequest) returns (ListCommandsResponse);
  rpc ExecuteCommand(ExecuteCommandRequest) returns (CommandExecution);
  rpc CancelCommand(CancelCommandRequest) returns (CancelCommandResponse);
  rpc GetSettingsSchema(GetSettingsSchemaRequest) returns (SettingsSchema);
  rpc GetSettings(GetSettingsRequest) returns (SettingsSnapshot);
  rpc ValidateSettings(ValidateSettingsRequest) returns (ValidationResult);
  rpc ApplySettings(ApplySettingsRequest) returns (SettingsSnapshot);
  rpc ListSurfaces(ListSurfacesRequest) returns (ListSurfacesResponse);
  rpc TailLogs(TailLogsRequest) returns (stream LogEntry);
  rpc SubscribeEvents(SubscribeEventsRequest) returns (stream ModuleEvent);
  rpc Dispose(DisposeRequest) returns (DisposeResponse);
}
```

完整定义见：

```text
proto/mpt_module_v1.proto
```

## 传输映射

| 入口类型 | 协议 | 传输 | 用途 |
|---|---|---|---|
| `inproc-dotnet` | C# interface | 进程内调用 | 可信 .NET 模块 |
| `grpc-ipc` | Protobuf/gRPC | Named Pipes / Unix Domain Socket | sidecar 主路径 |
| `http` | Typed facade | HTTP/WebSocket | 既有服务兼容 |
| `jsonrpc-stdio` | Compatibility adapter | stdin/stdout | fallback |

## C# 进程内接口

```csharp
public interface IMptModule
{
    string Id { get; }
    string PackageId { get; }
    Version Version { get; }

    ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken ct);
    ValueTask<ModuleStatus> GetStatusAsync(CancellationToken ct);
    ValueTask<IReadOnlyList<MptCommand>> ListCommandsAsync(CancellationToken ct);
    ValueTask<CommandExecution> ExecuteCommandAsync(CommandRequest request, CancellationToken ct);
    IAsyncEnumerable<ModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken ct);
    ValueTask<SettingsSchema> GetSettingsSchemaAsync(CancellationToken ct);
    ValueTask<SettingsSnapshot> GetSettingsAsync(CancellationToken ct);
    ValueTask<ValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken ct);
    ValueTask<IReadOnlyList<UiSurface>> ListSurfacesAsync(CancellationToken ct);
    ValueTask DisposeAsync(CancellationToken ct);
}
```

## gRPC Native IPC

```text
Windows  -> Named Pipes
macOS    -> Unix Domain Socket
Linux    -> Unix Domain Socket
```

每个 sidecar 只监听本机 IPC endpoint。默认不暴露 TCP 端口。Host 启动 sidecar 后，先执行 `Initialize`，再订阅事件流。

## 错误模型

所有错误使用结构化错误：

```json
{
  "code": "MPT_CAPABILITY_MISSING",
  "message": "display.profile capability is unavailable on this platform",
  "retryable": false,
  "details": {
    "capability": "display.profile"
  }
}
```

常用错误码：

| 错误码 | 含义 |
|---|---|
| `MPT_VERSION_INCOMPATIBLE` | Host 与模块协议版本不兼容 |
| `MPT_CAPABILITY_MISSING` | 当前平台缺少能力 |
| `MPT_PERMISSION_REQUIRED` | 需要权限或 broker 执行 |
| `MPT_SETTINGS_CONFLICT` | 设置 revision 冲突 |
| `MPT_COMMAND_TIMEOUT` | 命令超时 |
| `MPT_COMMAND_CANCELLED` | 命令被取消 |
| `MPT_RUNTIME_UNAVAILABLE` | sidecar 或服务不可用 |
| `MPT_UNSUPPORTED_TRANSPORT` | 当前平台不支持该入口 |

## 命令执行约束

每次执行必须携带 `invocationId`。模块必须保证同一个 `invocationId` 重复到达时返回同一结果或同一执行记录。

```json
{
  "invocationId": "01JZ7J7Y9M8W6F3C9F7R6D4T3Q",
  "commandId": "adb-forwarder.rules.apply",
  "args": {}
}
```

## 状态同步

状态同步采用：

```text
GetSnapshot
+ SubscribeEvents
+ lastEventSeq resume
+ snapshot fallback
```

模块不通过高频轮询维持状态。事件丢失时，Host 使用最新 snapshot 修复状态。

## 设置同步

SettingsStore 归 Host 管理。模块可以读取、校验、申请修改，最终写入由 Host 完成。

```json
{
  "revision": 42,
  "expectedRevision": 42,
  "patch": {
    "temperature": 4500
  }
}
```

revision 不一致时返回 `MPT_SETTINGS_CONFLICT`。
