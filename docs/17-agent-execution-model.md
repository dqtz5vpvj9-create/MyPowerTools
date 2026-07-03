# Agent 执行模型

## 原则

路线按 agent gate 推进。每个 gate 只关心输入、输出、验收和阻塞项，不包含人工时间估算。

## Gate 定义

| Gate | 输入 | 输出 | 阻塞条件 |
|---|---|---|---|
| Contract Gate | schema、proto、ADR | 生成 SDK 和 validator | schema 或 proto 不能生成 |
| Runner Gate | Runner 项目、Host Control proto | Runner 单实例和 IPC server | Shell 无法连接 Runner |
| Shell Gate | UI tokens、Host Control client | Dashboard skeleton | Shell 无法从 Runner 获取 snapshot |
| Module Gate | sample module、module host | InProc 和 gRPC sample | sample 无法执行 command |
| UI Gate | ui tokens、component contracts | 视觉 baseline | token lint 或截图失败 |
| Broker Gate | platform abstraction | PrivilegedBroker 初版 | 高权限动作绕过 broker |
| Package Gate | package schema、installer | `.mptpkg` 可安装回滚 | hash 校验或 rollback 失败 |
| Platform Gate | platform pack | Windows/macOS/Linux preview | capability 降级显示失败 |

## Agent 工作单元

每个 agent 任务使用同一结构：

```text
Context
  相关 docs、schema、proto、example 路径

Task
  需要修改的项目和文件

Constraints
  架构限制、UI 限制、安全限制

Expected output
  新增文件、修改文件、生成文件

Validation
  dotnet test
  mpt validate
  mpt ui check
  proto generation
  schema validation

Failure handling
  失败日志路径
  回滚方式
  后续诊断入口
```

## 并行策略

```text
Protocol 和 schema 可并行
Runner 和 Host Control 可并行
Shell token 与组件可并行
InProcHost 与 GrpcIpcHost 可并行
CLI validator 与 package schema 可并行
平台包在 abstractions 稳定后并行
```

## 交付标准

```text
所有 schema 示例通过
所有 proto 可生成
所有 sample 模块可运行
Runner 可无 Shell 常驻
Shell 可重启恢复
模块崩溃不影响 Runner
UI baseline 通过
Broker audit log 可查看
```
