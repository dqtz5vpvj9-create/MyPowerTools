# 同步 完整性 可扩展性

## 同步原则

通信通道不承担同步语义。同步由协议和 Runtime 保证。

```text
设置：Host 单一写入方 + revision
状态：snapshot + event stream
命令：invocationId 幂等
日志：stream + cursor
事件：seq + resume
```

## SettingsStore

Host 是设置单一写入方。

```json
{
  "moduleId": "screenease",
  "revision": 42,
  "updatedAt": "2026-07-03T14:30:00+08:00",
  "values": {
    "profile": "night",
    "temperature": 4200
  }
}
```

修改请求必须带 `expectedRevision`：

```json
{
  "expectedRevision": 42,
  "patch": {
    "temperature": 4500
  }
}
```

revision 不一致返回 `MPT_SETTINGS_CONFLICT`。

## 状态同步

状态同步采用：

```text
GetStatus snapshot
SubscribeEvents stream
lastEventSeq resume
snapshot fallback
```

事件示例：

```json
{
  "moduleId": "android-tools.notifications",
  "seq": 1087,
  "type": "message.received",
  "time": "2026-07-03T14:30:00+08:00",
  "payload": {}
}
```

## 命令幂等

每个命令执行必须有 `invocationId`。

```json
{
  "invocationId": "01JZ7J7Y9M8W6F3C9F7R6D4T3Q",
  "commandId": "adb-forwarder.rules.apply",
  "args": {}
}
```

模块收到重复 `invocationId` 时，返回已有执行记录，避免重复写端口规则、重复通知、重复操作硬件。

## 消息完整性

主通道使用 Protobuf/gRPC。额外约束：

```text
每条请求有 requestId
每次命令有 invocationId
每个事件有 seq
每个设置快照有 revision
每个模块有 protocolVersion
每个响应有 typed error
```

## 包完整性

每个包生成文件哈希：

```json
{
  "packageId": "android-tools-suite",
  "version": "0.2.0",
  "files": [
    {
      "path": "modules/remote-commands/module.json",
      "sha256": "..."
    }
  ]
}
```

演进路径：

```text
开发期：hash 校验
自用稳定期：本地 trust store
发布期：签名包
```

## 高权限完整性

高权限动作必须经过 Broker：

```text
模块提交 PrivilegedActionRequest
Host 展示权限和影响
Broker 执行
Broker 写审计日志
模块拿结果
```

模块不能自己弹 UAC、运行 `netsh` 或直接安装服务。

## 可扩展性机制

### 静态命令索引

启动 MyPowerTools 时不启动所有模块：

```text
读取 manifest
读取 commands.index
渲染 Dashboard skeleton
后台加载需要实时状态的模块
```

### 延迟加载

模块只在以下场景启动：

```text
用户打开模块详情
用户执行动态命令
模块声明 alwaysOn
健康监控需要实时状态
另一个模块依赖它的 runtime
```

### 多模块共享 runtime

```text
PackageRuntime
  -> one process
  -> many module services
  -> one IPC endpoint
```

### Capability Registry

模块声明能力，平台包执行能力：

```json
{
  "requires": [
    { "capability": "display.profile", "required": true },
    { "capability": "network.portForwarding", "required": true }
  ]
}
```

Host 根据当前平台决定：

```text
可运行
可降级运行
缺少能力
缺少权限
需要安装 provider
```
