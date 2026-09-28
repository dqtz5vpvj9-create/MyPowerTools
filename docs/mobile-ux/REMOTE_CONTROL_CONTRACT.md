# 手机访问电脑工具：实现合同

依据 M8 审查及主代理源码核验：Runner 的 HostControl 仅绑定本机 NamedPipe/UnixSocket，
手机不能直接连接。采用 Tailnet 内的独立 gateway 模块；电脑侧用现有 HostControlClient
执行，Android 侧继续以模块命令和 Surface 提供功能。保留共享执行、取消、权限与提权底座。
SSH 常用命令保留为已有独立工具，不作为所有用户连接电脑的前提。

## 文件归属

- G1：`tools/remote-tool-gateway/`，但不写 `android-integration/`。
  包含桌面 IMptModule、Tailnet listener、授权/调用状态、桌面设置 Surface、打包与测试。
- G2：`tools/remote-tool-gateway/android-integration/` 与
  `src/MyPowerTools.MobileToolControl/`（含自有 tests）；手机 HTTP 调用模块、凭据存储和 UI。
- 共同 wire 合同以本文为准。避免 G1/G2 同改共享源文件；端点采用 JSON 的兼容边界。
- 根方案/注册表/Android 构建嵌入由主代理在 M7 交付后统一接线。
- M2 的页面不由 G1/G2 修改。G2 交付 public Surface/activation 合同后，主代理接入设备详情。

## 连接与权限

桌面模块默认关闭网络监听；用户在“远程工具访问”设置中启用。只能绑定实际 Tailnet IP，
不得绑定 wildcard、公网或普通 LAN；客户端仅接受 literal Tailnet IP，不跟随 HTTP 重定向。
传输依赖 Tailscale 的网络加密，与现有文件直传一致。测试可注入 loopback transport，
不可提供绕过生产边界的设置开关。

桌面为每台手机创建独立 grant，使用平台 secret store 保存随机凭据。授权中保存设备名、
固定 grantId、明确的 commandId 集合及 allowElevated 标志；默认 commandId 集合为空。
只读 catalog 不等于允许执行任意“看起来像查询”的命令。文件配对 token 永不被网关接受。
新安装的命令不自动加入旧授权，网关自身的授权/管理命令不能通过网络执行。

连接码为 `mpt://control/<base64url-json>`，JSON：
`{"version":1,"endpoint":"http://100.64.0.2:49541","grantId":"...","deviceName":"工作电脑","token":"..."}`。
端口为独立配置默认值，可更改；不能复用/占用文件传输 listener。导入在用户确认后保存，
token 只进 secret store，不进入 preferences、日志、历史、错误摘要或导出验收数据。
授权由服务端 token 定位，忽略客户端自行声明的权限；撤销后立刻拒绝新请求并取消该 grant 活动调用。

## Wire v1

所有端点以 `/mpt-control/v1` 为前缀，均要求 `Authorization: Bearer <grant token>`。
响应为 `application/json`；属性 camelCase。错误返回适当 4xx/5xx 和
`{"error":{"code":"...","message":"..."}}`，不得把权限/执行错误包装成成功状态。

### GET /catalog

返回 `{"device":{"name":"...","platform":"windows"},"tools":[...],"commands":[...]}`。

- tool：`toolId, moduleId, title, description, category, state, availability`。
- command：`commandId, moduleId, title, subtitle, dangerLevel, requiresElevation,
  supportsProgress, supportsCancellation, parameters, allowed`。
- parameter：`id, label, type, required, defaultValue`。
- 来源必须是当前 HostControl 的实际工具/命令目录，不包含路径、凭据或私有配置。
- 手机可以看见未授权命令及原因，但不得提交执行；服务端必须再次核验 commandId 是否授权且仍存在。

### POST /invocations

请求：`{"invocationId":"客户端生成的唯一ID","commandId":"...","args":{...}}`。
响应为 invocation JSON：`invocationId, commandId, state, message, terminal, result`。
`result` 是现有 HostControl `CommandExecutionResponse` 的兼容 JSON：
`invocationId,state,summary,logCursor,errorCode,errorMessage,retryable,errorDetails`。
提交响应允许 202（已接收但未结束）；只有 terminal 和实际 result 能决定成功/失败。

### GET /invocations/{id}

读取同一个 grant 发起的调用。跨 grant 返回不可访问，不得泄露调用是否存在。
仅在页面可见且调用进行中时按需更新；完成或页面关闭后停止。

### POST /invocations/{id}/cancel

只可取消同 grant 调用；复用现有 HostControl CancelCommand，返回真实 accepted/state。
重试使用新的调用 ID；重复同 ID 不得重复执行，优先复用运行时已有 invocation 机制。

## 提权、确认与审计

网关不能越过既有 RuntimeOperationPolicy、模块确认 token、Broker 或 UAC。
`requiresElevation`/明确 elevated execution constraint 的命令在 allowElevated=false 时必须
在调用 HostControl 前拒绝。允许提权仍不等于同意某次操作。

需要电脑确认的操作进入桌面待确认列表，显示发起设备、命令和参数摘要，由电脑用户操作
网关 Surface 的确认按钮，再通过既有 MptAvaloniaSurfaceContext.ExecuteCommandAsync
触发原有确认/提权链。不能自动 approve、静默 RunAs 或用 SYSTEM 身份绕过 UAC。
未经真实排队不能返回“等待电脑确认”；未支持的确认类型返回明确错误并保留可重试路径。

本地审计包含 grantId/设备、commandId、invocationId、状态与结果，不记录 token 或秘密参数。
敏感参数遵守现有 MptLogRedactor；不向另一个 grant 暴露历史与审计。

## 手机 UI

沿用批准原型的设备卡、工具列表、详情、底部面板、进度与结果。
用实际目录组织电脑工具；优先为 Input Monitor、ScreenEase 和 Paste Image 做易懂的动作与状态呈现，
其他工具由实际 command 参数构造可触控表单，保留描述、权限、取消和确认。
不得为未知响应编造统计、图片或运行状态；不把完整原始 JSON 当默认产品页面。
设置允许按需查看技术详情。禁止新增可任意读取电脑文件的通用 HTTP 接口。

## 最小验收

未授权、文件 token、错误/撤销 token、非 Tailnet、未知命令、越权 elevated、跨 grant
result/cancel 全部拒绝；明确授权的 fake HostControl 命令正常执行并保留真实失败；重复 ID
不重复执行；默认不开监听，关闭工具后停止 listener，空闲没有定时扫描。
另验证真实本机 HostControl 目录和一条只读命令；任何管理服务/提权操作使用测试替身，
Windows 实际用户确认流程由主代理安排最终验收。
