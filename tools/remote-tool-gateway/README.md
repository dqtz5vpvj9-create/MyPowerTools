# remote-tool-gateway（电脑端 / G1）

手机远程使用电脑工具的电脑侧实现，合同见 `docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md`。
本目录由 G1 独占；`android-integration/`（若存在）属于 G2，本工具不写、不清理该目录。

## 交付内容

| 路径 | 说明 |
| --- | --- |
| `src/RemoteToolGateway.Core/` | wire v1、Tailnet 绑定规则、连接码、grant/secret、调用与审计、确认策略、HostControl 桥、有界 HTTP listener、网关服务 |
| `src/RemoteToolGateway.MyPowerTools/` | `IMptModule`（`remote-tool-gateway`）：命令、设置、状态、事件、生命周期 |
| `src/RemoteToolGateway.Surface/` | 桌面 Surface：监听开关、设备授权与命令白名单编辑、连接码、**待电脑确认列表**（已受理的调用继续显示为"执行中"，不再提供确认/拒绝按钮）、最近调用与审计；跟随 `SubscribeEvents` 事件刷新（突发合并、detach 释放、无闲置轮询） |
| `package/` | `module.json`、`commands.index.json`、`ui/tool.json`、`ui/control-page.json` |
| `build.ps1` | 编译并打包到 `artifacts/package`；默认**不**写 `modules/`（`-StageRepositoryModule` 才镜像） |
| `tool-release.json` / `source-map.json` | 第一方工具发布契约（适配器项目、打包输出、源码分类、最低 SDK 要求），供根 `build-all-tools`/源码包脚本消费 |
| `tests/RemoteToolGateway.Core.Tests/` | 95 个边界/HTTP/确认/生命周期/竞态/决策边界/模块/打包测试，含真实 HostControl 嵌入链路与"打包模块在真实 host 中装载"验证 |
| `tests/RemoteToolGateway.Surface.Tests/` | 16 个 Surface 测试：保留手机调用 ID 的真实 context 适配与取消同 ID、无该入口时明确拒绝、事件刷新/合并/detach、选择不被刷新重置 |

## 安全模型（实现要点）

- **默认不开监听**：`listenerEnabled` 默认 `false`，只有用户在 Surface/设置里开启才绑定；关闭工具（`DisposeAsync`）即停止 listener。空闲时没有定时器、没有轮询，listener 阻塞等待连接。
- **只绑定真实 Tailnet IP**：`TailnetBinding` 只接受 `100.64.0.0/10` 与 `fd7a:115c:a1e0::/48` 的字面量地址，且必须是本机当前地址；通配符、公网、普通 LAN、主机名一律在创建 socket 前拒绝。listener 还会再次校验对端源地址属于 Tailnet。loopback 只能由测试构造参数（`RemoteToolGatewayOptions.AllowLoopbackTransport` / 模块 internal 属性）注入，没有任何设置项、manifest 字段或 HTTP 参数能打开它。
- **独立 grant 与 secret store**：每台手机一份 grant（设备名、固定 `grantId`、明确的 `commandId` 集合、`allowElevated`，默认命令集合为空）。token 为 32 字节随机值的 base64url，仅存平台 secret store；`grants.json` 只保存 `secret://` 引用。查找 token 用 SHA-256 常量时间比较，文件配对 token/其他实例的 token 一律找不到 grant。
- **精确白名单 + 二次核验**：`/catalog` 的 `allowed` 按 grant 计算；执行时再次核验命令存在、未被撤销、在白名单内。网关自身 `remote-tool-gateway.*` 命令永远不能被网络执行（`allowed=false`）。新安装的命令不会自动进入旧授权。
- **提权**：`requiresElevation` / `requiresElevatedWrites` 约束 / `execution.type=broker.request` 在 `allowElevated=false` 时在调用 HostControl **之前**拒绝（`elevation-not-allowed`）。
- **桌面确认**：危险/提权/需要 Broker 的命令进入真实待确认队列（`awaiting-confirmation`，`terminal=false`），Surface 逐条显示发起设备、命令与参数摘要。确认链路：`confirmation.claim` **原子领取** → Surface 用 `MptAvaloniaSurfaceContext.ExecuteCommandWithInvocationAsync(**手机原 invocationId**, …)` 走原有确认/提权链 → `confirmation.resolve` 只回报**真实结果**。
  - **决策边界**：慢的目录读取在接纳锁之外；读取完成后的"当前 grant + 当前白名单 + 当前 allowElevated + `TryClaim`"在**同一个 `_admission` 边界内**完成。`grant.update` 的授权发布与 `revoke` 的 `MarkRevoked` 也在同一把锁内发布，因此"目录读取期间收紧授权/关闭提权/撤销"都不可能再用旧快照领取（`ClaimDecisionBoundaryTests`）。文件的落盘、secret 删除与 runtime cancellation 都在边界之外，单个设备的慢取消不会阻塞其它设备提交。
  - 领取时授权不再满足 → 该调用被终结为失败（`command-not-authorized` / `elevation-not-allowed` / `unauthorized`），零执行、不会继续排队。第二个 Surface 拿不到第二份可执行参数。未领取的调用只能被拒绝；已领取的调用不能再被 `resolve accepted=false` 反转。没有自动同意、没有静默 RunAs、没有伪造 pending：`execution.approval` 为不支持的取值时返回 `unsupported-confirmation` 明确失败。
  - **终态一次性**：终态写入在记录锁内做一次性保护，第一个终态结果胜出，后到的 `resolve`/运行时事件只会被忽略（审计记 `confirmation-resolve-ignored`），并发 resolve 不会互相覆盖。`cancelling` 不是终态，因此"取消请求发出后运行时仍报成功"会被如实记录为成功。
  若 Shell 没有 `ExecuteCommandWithInvocationAsync`（旧 host），页面**明确拒绝**该请求（`rejected` + 原因），绝不改用一个随机 ID 偷偷执行。
- **取消语义**：只有 pending 且未被领取的调用可以本地取消（`cancelled`）。已领取/运行中的调用一律把请求交给运行时（`CancelCommand`，同一 invocationId）；`accepted=true` 只是"已请求取消"（`cancelling`，非终态），取消被拒绝时保留真实状态。**任何情况下都不会把"已请求取消但没拿到终态"写成 `cancelled`**：流结束/超时/网关停止一律报告 `failed` + `cancel-unconfirmed` 或 `host-stream-ended`（结果未知、可重试）。
  边界说明（诚实而非近似成功）：已领取但运行时**尚未真正启动**那次执行时，运行时的 `CancelCommand` 会返回 `accepted=false`（它还不知道这个 invocation）。本实现如实把 `accepted=false` 与当时的 `claimed` 状态回给手机，不伪造取消成功、也不新增补偿事务；此时执行结果仍以随后 `resolve` 回来的真实结果为准。
- **撤销**：先同步把 grant 移出实时授权集合（此后任何新请求立即 401，即使取消调用还在等待或抛错），再完成 secret 删除/持久化，最后取消该 grant 的活动调用（未领取的待确认本地取消，其余交给运行时并保留其真实状态）。接纳与撤销共用同一把接纳锁：`/invocations` 在目录读取后、真正接纳/执行前会用实时 grant 复查，绝不用撤销前的旧快照放行。
- **有界**：请求体 64 KiB、请求头 16 KiB、连接预算 8、请求超时 20 s；catalog（500 工具 / 2000 命令）、调用记录（200）、审计（500）均有上限；结果 summary/message/errorDetails 在服务层再次截断。
- **审计**：`audit.jsonl` 记录 grantId、设备名、commandId、invocationId、状态与结果；参数只记录脱敏摘要（凭据类键 `****`），token 永不进入 preferences、审计、inspect、日志或错误摘要。

## Wire v1（与 G2 的冻结接口）

前缀 `/mpt-control/v1`，全部要求 `Authorization: Bearer <grant token>`，响应 `application/json`，
失败返回 4xx/5xx + `{"error":{"code":"...","message":"..."}}`（绝不把失败包装成 2xx 成功）。

- `GET /catalog` → `{device:{name,platform},tools:[{toolId,moduleId,title,description,category,state,availability}],commands:[{commandId,moduleId,title,subtitle,dangerLevel,requiresElevation,supportsProgress,supportsCancellation,parameters:[{id,label,type,required,defaultValue}],allowed,notAllowedReason}]}`
  （`notAllowedReason` 是可选增补字段，手机用它显示"为什么这条命令不能执行"；未知它的客户端可忽略。）
- `POST /invocations` ← `{invocationId,commandId,args}` → invocation JSON；`invocationId` 为 1–64 位
  `[A-Za-z0-9._-]`（与手机端 `Uri.EscapeDataString` 兼容；listener 拒绝含 `%` 的路径）；
  接收但未结束返回 `202`，已结束返回 `200`；重复 `invocationId` 不重复执行（同 grant 回放当前状态，
  跨 grant 用同一 ID 返回 `409 invocation-exists`，不泄露对方的调用内容）。
- `GET /invocations/{id}` → invocation JSON；跨 grant 与不存在都返回 `404 not-found`，不泄露存在性。
- `POST /invocations/{id}/cancel` → **invocation JSON + `accepted` + `cancelAccepted`（+ 可选 `cancelState`）**：
  同一份 body 同时满足合同里"返回真实 accepted/state"与手机端按 invocation 文档解析两种读法。`state`
  始终是该 invocation 自己的状态（运行时的取消应答不会覆盖它，只出现在 `cancelState`）；`accepted`/
  `cancelAccepted` 来自真实 HostControl `CancelCommand`。未领取的待确认调用本地取消（`cancelled`）；已领取/
  运行中仅当运行时接受时进入 `cancelling`（非终态）。
- invocation JSON：`{invocationId,commandId,state,message,terminal,result}`，
  `result` 为既有 `CommandExecutionResponse` 兼容 JSON：
  `{invocationId,state,summary,logCursor,errorCode,errorMessage,retryable,errorDetails}`。
- 状态：`accepted` / `awaiting-confirmation` / `running` / `succeeded` / `failed` / `cancelled` /
  `rejected` / `permission-required`。只有 `terminal=true` 与实际 `result` 能判定成功/失败。
- 错误码：`bad-request`、`unauthorized`、`forbidden`、`forbidden-peer`、`not-found`、`method-not-allowed`、
  `command-not-authorized`、`command-not-found`、`elevation-not-allowed`、`unsupported-confirmation`、
  `invocation-exists`、`payload-too-large`、`busy`、`host-unavailable`、`internal-error`。

连接码：`mpt://control/<base64url-json>`，JSON 为
`{"version":1,"endpoint":"http://100.64.0.2:49541","grantId":"g-...","deviceName":"工作电脑","token":"..."}`。
`ControlConnectionCode.TryDecode` 拒绝 `mpt://pair/`、`mpt://cloud/`、https、非 Tailnet 主机、域名与版本不符的码。

## 桌面模块命令

`inspect`、`listener.start`、`listener.stop`、`grant.create`、`grant.update`、`grant.revoke`、`grant.code`、
`confirmation.claim`、`confirmation.resolve`（全部本地命令，网络不可执行）。
`confirmation.claim` 是唯一返回原始参数的路径，供桌面 Surface 交给既有执行入口；`inspect` 只返回脱敏摘要。

## 构建与测试

```powershell
# 打包（默认不动 modules/）
pwsh -NoLogo -NoProfile -File tools/remote-tool-gateway/build.ps1
# 需要镜像到 modules/ 时
pwsh -NoLogo -NoProfile -File tools/remote-tool-gateway/build.ps1 -StageRepositoryModule

dotnet test tools/remote-tool-gateway/tests/RemoteToolGateway.Core.Tests/RemoteToolGateway.Core.Tests.csproj -c Release
dotnet test tools/remote-tool-gateway/tests/RemoteToolGateway.Surface.Tests/RemoteToolGateway.Surface.Tests.csproj -c Release
```

- 模块包自带 HostControl 客户端、protobuf、gRPC 依赖（`CopyLocalLockFileAssemblies`），
  `AssemblyDependencyResolver` 从模块目录即可解析；Surface 与既有 dotnet Surface 一样保持精简，
  由 Shell 提供 Avalonia 与共享 SDK 契约。
- 本仓库编译共享公共输出/NuGet 缓存时，调用方需与其它任务串行：
  `flock artifacts/.tmp-android-verify/mobile-build.lock <命令>`；测试临时目录优先 `/mnt/cache/data-cache`，
  沙箱不可写时回退到 `artifacts/.tmp-android-verify/rtg-tests`（不使用 `/tmp`）。

## 复用边界

生产执行只通过 `IHostControlBridge` → `HostControlClientBridge` → `MyPowerTools.HostControl.Client`（SDK 包）
与 Runner 的本机 IPC 通信，不修改 Runner 监听、`IpcChannelFactory`、`MptHostRuntime`/Broker，
也不引用任何模块内部执行类。测试注入的 fake 只替换这一个接口。

## 与 G2（手机端）的接口核对

只读检查 `android-integration/`（G2 独占，本工具不写入）后的结论：

- 端点路径、catalog/invocation 字段名、连接码 `mpt://control/<base64url-json>` 与手机端
  `MobileToolControlWire`/`MobileToolControlConnectionCode` 完全一致。
- 手机端把 `/cancel` 当 invocation 文档解析，并读取可选的 `notAllowedReason`；本端已按这两种读法返回
  （`cancel` 响应同时带 `accepted`/`cancelAccepted`）。
- 手机端 `MobileToolControlHttpClient.Failure(status)` 未把 `413` 归入 4xx 分支；超限请求在手机上会显示为
  协议错误，服务端语义仍是 `payload-too-large`（服务端行为按合同保持 413）。
- 手机端调用 ID 由 Shell 传入且不校验长度；本端接受 1–64 位 `[A-Za-z0-9._-]`。

## 未完成 / 待主代理安排

1. **Windows 真机最终验收**：本环境（Linux 沙箱）没有运行中的 Runner，真实链路用
   `HostControlGrpcService + MptHostRuntime` 嵌入方式验证（`EmbeddedRuntimeTests`）；Windows 上应再跑
   `RealHostControlTests`，确认真实 Runner 目录 + 一条只读命令，并实测 UAC/Broker 确认路径。
2. 根解决方案/包注册、产物策略条目、Dev overlay 部署与 Android 侧联调由主代理接线。
3. Surface 目前提供完整的授权/确认/审计操作，但未做手机端"待确认"推送；手机端靠轮询 `GET /invocations/{id}`。
   电脑页面本身已改为跟随 `SubscribeEvents` 事件刷新（突发合并、detach 释放、无闲置轮询），并保留手动"刷新"按钮。
4. **疑似审计断链（M8）不成立**：`permission-required` 在本实现中是**真实终态**（`terminal=true`，`errorCode=permission-required`，
   `retryable=true`），Surface 不会在收到它之后自动执行任何东西——`PermissionPromptViewModel` 只有审计入口。
   按合同保留该终态 + 重试路径，未新增任何审批机制。
5. 连接码已接入公共二维码控件 `MyPowerTools.AvaloniaSdk.Controls.MptQrCode`（SDK 0.3.0）：**仅在用户明确"创建授权/显示连接码"之后**才在桌面上显示可扫描的完整连接码，并附设备名 + 失效说明与"复制连接码"手动兜底；点"隐藏连接码"、离开页面（detach）、撤销授权、或该授权在别处消失后的下一次刷新，都会**丢弃**这段凭据（不是仅隐藏）。二维码控件从不进入 automation name / tooltip / 窗口标题 / 日志，`Status` 与 Surface 日志也不含连接码。最低 SDK 见 `tool-release.json` 的 `sdkRequirements`（AvaloniaSdk ≥ 0.3.0）。
