# 文件助手公网 relay 接口

本文描述客户端与模块实现的公网传输接口。

## 1. 公网 relay 合同（服务端已固定，客户端按此实现）

| 项 | 值 |
| --- | --- |
| 会话注册 | `POST /mpt/relay/v1/conversations`，空 body，Basic `conversationId:conversationKey`；同 key 幂等，wrong key → 401 |
| DAV root | `/mpt/relay/dav/`，认证后的私有 namespace，布局 `assistant/<conversationId>/...` 与既有 OpenListClient 相同 |
| 事件 | `GET /mpt/relay/v1/changes?since=N`，认证 long poll ≤25s，返回 `{"revision":long}`；不传 since 立即返回当前 revision；manifest/receipt 写入唤醒；revision 持久；`since>current` 立即返回 |
| 生产地址 | `https://proxy.lixinrui000.cn`（`PublicRelayClient.ProductionBaseAddress`，代码内固定，无用户设置） |

客户端：`FileTransfer.Core/PublicRelayClient.cs`（`RegisterAsync` / `ChangesAsync` / `CreateDavClient`）。
测试通过 internal `PublicRelayClient.BaseAddressOverride` 注入模拟 relay，生产不使用。

## 2. transport 选择（模块）

1. 用户配置了 `webDavUrl` + `username` + `password` → **沿用其自定义 OpenList**，不被覆盖。
2. 否则 → 公网 relay，用 secret store 里已持久化的 `conversationId` / `conversationKey`（现有 `self-` 随机标识保持兼容）。
3. 公网 relay 的**注册只在接收/同步真正开始时发生**；身份导出（`assistant.link.export`、`file-transfer.pairing`）不注册、不探测、不联网。
4. `inspect.relay = {configured,state:unconfigured|unknown|available|unavailable,message,public,custom,revision,receiving}`；`configured` 只表示“有可用 transport”，**不等于已连通**；assistant 通道的健康与旧 `cloud.check` 缓存互相独立。

## 3. R0 连接码合同（已实现）

- **一个稳定码**：`file-transfer.pairing` → `{kind:"pair", code:"mpt://pair/<base64url>", address:"<可选直连候选>"}`；永远不因本机有无网卡而切换码种类。
- `Pairing{DeviceId,Name,Address,Token}`：`DeviceId` 是主键；`Address` **可选**，仅作直连候选；非空时仍必须是 Tailscale 地址（旧安全检查不放宽）。
- `file-transfer.pair.preview {code}` → `{deviceId,name,address}`（不含 token）；`file-transfer.pair.import {code}` → 保存无地址目标也成功。
- 自有设备加入会话用另一套：`assistant.link.export/preview/import`（`mpt://assistant/...`，含 conversationId/key，可选自定义网盘配置）。
- 两类码互相给错时，命令报出对方应使用的命令名（`file-transfer.pair.import` / `file-transfer.assistant.link.import`），不再抛“请先连接 Tailscale”。
- 探测（`peer.check`）只按**目标地址**判断路径可达性，不以本机网卡作开关；无地址目标报 `unknown`（“已配对，尚未检查”）。

## 4. 收件生命周期（已实现）

- 模块启用即启用接收：公网 relay 的 long poll 常驻等待事件；Tailnet TCP listener 只是**可用时的直传加速**，无网卡时不报错、不提示设置。
- `receive.stop`：取消在途接收 long poll 与 listener，发送队列继续处理；禁用模块停止全部后台任务；`receive.start` 无 Tailscale 也成功。
- 失败队列自动恢复：long poll 有界 backoff（2s 起、上限 2min），新发送会立即打断 backoff；relay 恢复且仍有待发项时自动补发，无需手动 retry。
- `assistant.inspect.receiving` = 模块真实接收状态（bool），细节在 `receivingDetails`（`engine/tailnetListener/publicRelay/note/...`）；不谎称公网已在投递。

## 5. 普通配对：设备收件箱（已实现，PROTOCOL.md §6）

普通配对**不共享会话身份**：每台设备本地生成并持久化一份 `PublicInboxIdentity`
（`inboxId` = `inbox-<32hex>`、独立随机 `ownerKey`、独立随机 `depositKey`），只把
`inboxId` + `depositKey` 追加进既有 `mpt://pair/` 码（`Pairing.Inbox`，末尾可选字段，旧码无此字段）。
`ownerKey` **不随配对码扩散**；它仍然是 relay 凭据，注册与 owner 操作时经 HTTPS Basic 发给服务器
（与 conversationKey 同等对待：只在 HTTPS 上传输、只以加盐摘要落库、从不进日志）。
本设备的 `conversationId`/`conversationKey` **永不**出现在配对码里。

- **权限**：持配对码者只能投递（`PUT /v1/inboxes/items/{id}`）并按随机 itemId 读该条回执（§6.6）；
  不能注册、不能列条目、不能下载、不能删除、不能写回执。
- **发送**：`TargetDeviceId` 不在本会话时，条目进入独立发送调度，按其 deposit inbox 上传（重试复用同一
  `itemId`），上传成功记 `stored`，只有读到 owner 的**真实回执**才 `delivered`；503/401 分别按可重试/
  不可重试处理，503 有界退避自动重试；没有 inbox 的旧码如实提示重新扫码，绝不假报上传或送达。
- **接收**：owner 注册（503 自动退避）→ 长轮询待取件 → 下载 → 复用已保存副本或落地 → 写入 `AssistantStore`
  → **保存成功后**写回执；保留成功条目供发送端查询回执。超长文本按 `AssistantLimits` 拒收、记录并删除远端条目，
  坏条目不会阻塞其余队列。
- **成员身份**：只有**经共享会话 DAV 读回**的 manifest writer / receipt writer 才被确认为自有会话设备
  （`AssistantSyncResult.Verified`）。收件箱条目与回执虽落在同一 store，但绝不提升为会话成员，普通配对设备
  也不会被自发送直传。
- **取消与生命周期**：投递、下载、回执读写都挂在条目级取消 scope 上（用户取消即中断真实请求）；发送/回执
  确认在独立调度（有待确认任务时有界退避，无任务完全阻塞，接收禁用/停止不影响发送）；`Dispose` 取消全部
  长轮询并有界等待。

## 6. 验证入口

`PublicRelayTests` 验证会话中转与本地身份；`PublicInboxPairingTests` 验证普通配对、批量调度和权限边界；
`RealPublicInboxModuleTests` 用真实 Python 中转进程连接两个模块；`PublicInboxClientTests` 验证客户端协议。
设备验收记录与发布说明单独记录运行环境和结果，避免把单元测试当作设备验收。
