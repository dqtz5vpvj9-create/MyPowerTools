# 权限 安全 数据

## 安全原则

```text
Host 普通权限常驻
高权限动作走 Broker
secrets 进入 SecretBroker
模块声明权限需求
权限动作留审计日志
模块数据按 module-id 隔离
package 更新要验证 schema、hash 和 trust hook
```

## 权限等级

| 等级 | 说明 | 示例 |
|---|---|---|
| `user` | 普通用户权限 | 读取设置、打开页面、执行普通命令 |
| `elevated` | 单次提权 | 写端口转发、安装服务 |
| `serviceUser` | 用户级服务 | 用户登录后常驻任务 |
| `serviceSystem` | 系统级服务 | 后台硬件控制服务 |
| `sensitive` | 敏感数据访问 | token、cookie、API key |

## Privileged Broker

Broker 提供：

```text
权限请求验证
用户授权
平台提权
动作执行
审计日志
失败回滚
```

请求示例：

```json
{
  "moduleId": "adb-forwarder",
  "actionId": "network.applyPortProxy",
  "capability": "network.portForwarding",
  "reason": "写入本机端口转发规则",
  "parameters": {
    "listenPort": 5556,
    "targetHost": "127.0.0.1",
    "targetPort": 5555
  }
}
```

审计日志：

```json
{
  "auditId": "audit-20260703-0001",
  "moduleId": "adb-forwarder",
  "actionId": "network.applyPortProxy",
  "approved": true,
  "result": "success",
  "timestamp": "2026-07-03T13:56:50+08:00"
}
```

## SecretBroker

SecretBroker 统一管理：

```text
API tokens
server credentials
push tokens
agent secrets
mail credentials
```

模块只能通过引用访问 secret：

```json
{
  "secretRef": "secret://android-tools.notifications/server-token"
}
```

## 数据目录

```text
settings
  global settings
  module settings
  package settings

state
  runtime state
  last health check
  UI state

logs
  module logs
  command logs
  broker audit logs

secrets
  platform secret store only
```

## 数据隔离

| 数据 | 归属 | 路径策略 |
|---|---|---|
| 全局设置 | Host | global settings |
| 模块设置 | Module | `<module-id>/settings.json` |
| 模块状态 | Module | `<module-id>/state.json` |
| package 共享数据 | Package | `<package-id>/shared` |
| secrets | SecretBroker | 系统 secret store |
| 审计日志 | Broker | append-only log |

## 模块权限声明

```json
{
  "permissions": [
    {
      "id": "network-port-forwarding",
      "level": "elevated",
      "capability": "network.portForwarding",
      "reason": "管理 ADB 端口转发规则"
    }
  ]
}
```

## Package Integrity And Trust

Package integrity uses two files under `shared/`:

- `package.hashes.json` lists sha256 values for package content and excludes integrity metadata.
- `package.signature.json` is the local trust hook. It records the hash-manifest SHA256, package id, version, local signer metadata, and future detached-signature algorithm slots.

`mpt package sign-local modules` refreshes both files for local development and release packaging. `mpt package trust modules --strict` requires the signature hook to exist and validates that it points at the current hash manifest. `PackageStore.Install` verifies the source package before copying and writes a fresh local trust hook after install.

## 风险矩阵

| 风险 | 影响 | 处理 |
|---|---|---|
| 高权限动作误执行 | 系统配置损坏 | Broker 审计、确认、回滚 |
| secret 泄露 | 账号或服务暴露 | SecretBroker，不写日志 |
| sidecar 崩溃 | 模块不可用 | Supervisor 自动重启和状态降级 |
| package 破坏 Host | 整体不可用 | sidecar 隔离、schema validation |
| UI Surface 阻塞 | Shell 卡住 | 异步加载、超时、占位页 |
| 命令执行超时 | 用户误判 | progress event、timeout、取消 |
| 平台能力缺失 | 模块不可用 | capability registry 显示 unsupported |

## 日志脱敏

默认脱敏字段：

```text
token
secret
password
cookie
authorization
apiKey
accessKey
refreshToken
```

日志输出规则：

```text
命令参数默认可见
secret 参数永远显示为 ****
Broker 审计只记录 secretRef
异常栈不包含 secret 明文
导出诊断包前再次脱敏
```
