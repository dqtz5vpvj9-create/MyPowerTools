# 共享附件有界代理（公网 relay 侧旧客户端兼容）

状态：已实现（relay 代码 + 测试），2026-09-29。范围仅限 `tools/file-transfer/relay/mpt_relay`
的公网服务；Tail relay 保持该功能关闭。本文档描述服务端兼容桥，不包含客户端路由实现、
成员能力协商、命名空间迁移或部署配置变更。

## 目标

新版共享会话把大文件只上传到固定 Tailnet relay（`http://mpt-relay.tail.lixinrui000.cn`），
同时往公网 relay 写一条小的定位记录（locator）。仍只会说 V1 的旧客户端不认识 locator，
只会按原路径 `GET /mpt/relay/dav/assistant/<C>/<M>/payload` 取内容。公网 relay 在本机没有
已提交 payload、但存在合法 locator 时，用**当前请求已经通过认证的同一个 Basic 凭据**从固定
Tail 源拉取，并按有界分块直接转发。旧客户端不需要任何改动即可拿到字节。

带宽语义：发送端仍然只上传一份大文件（到 Tail）。只有确实有公网-only 旧客户端来取该文件时，
才在公网方向上消耗一份等价带宽；不存在无条件的重复上传。公网 relay 不落盘 payload 副本，
内存中也不整文件缓冲。

## 不变量（实现必须保持）

1. **默认关闭**：只有显式 `MPT_RELAY_TAIL_PAYLOAD_PROXY=1` 才启用。Tail relay 必须保持关闭。
2. **固定上游**：目标 origin 只能取配置里的固定 Tail 域名，或部署的受信 loopback connector
   （测试同样走 loopback）；上游 `Host` 恒为 `mpt-relay.tail.lixinrui000.cn`。
   请求头、query、locator 里的任何字段都不能选择主机或路径。
3. **路径固定**：上游路径恒为 `<origin>/mpt/relay/dav/assistant/<C>/<M>/payload`，因此即使
   配置错误也不会递归到别的路径。
4. **凭据只做透传**：上游只收到本请求已认证的 `Authorization` 头，不落盘、不进日志、不写进
   locator、不写进响应；locator 不提供任何认证信息。
5. **有界**：locator 读取 ≤16 KiB；上游响应体按固定块（64 KiB）读取；不使用整文件缓冲，
   不在公网磁盘创建 payload 副本。
6. **不伪造成功**：上游状态、`Content-Length` 与 locator `size` 必须一致；上游提前结束会让
   本地响应截断并关闭连接，而不是补齐或返回 200 完整体。

## 配置

| 环境变量 / 配置文件键 | 默认 | 说明 |
| --- | --- | --- |
| `MPT_RELAY_TAIL_PAYLOAD_PROXY` | `0` | 仅公网 relay 显式设为 `1`；Tail relay 保持 `0` |
| `MPT_RELAY_TAIL_PAYLOAD_ORIGIN` | `http://mpt-relay.tail.lixinrui000.cn` | 固定受信源。只允许该域名或 loopback（部署用的本地 connector，例如 `http://127.0.0.1:18766`，以及测试 origin）；必须是无 userinfo/path/query/fragment 的 http(s) 绝对地址。无论取何值，上游 `Host` 恒为 `mpt-relay.tail.lixinrui000.cn` |
| `MPT_RELAY_TAIL_PAYLOAD_TIMEOUT_SECONDS` | `30` | 上游连接与每次读的空闲超时（1..600）。没有整体墙钟上限：大文件按块持续推进，慢速 drip 由空闲超时兜底 |

origin 即使功能关闭也会在启动时校验：它是新增键，不会破坏既有部署文件，也能避免拼写错误在
日后启用时才暴露。配置解析失败会以 `ConfigError` 拒绝启动（见 `mpt_relay/config.py`）。

## 健康检查能力位

`GET /mpt/relay/health` 仅在启用时增加能力位（客户端读取
`capabilities.sharedPayloadLocator`，见 `SharedLocatorClient.SupportsPayloadLocatorAsync`）：

```json
{"ok":true,"service":"mpt-relay","version":"…","uptimeSeconds":12,"longPollSeconds":25,
 "capabilities":{"sharedPayloadLocator":1}}
```

未启用时 `capabilities` **完全不存在**（不是空对象、不是 0、不是 false）。这是唯一的契约形状；
不提供顶层 `sharedPayloadLocator` 别名。能力位只说明"此公网 relay 可以代理旧客户端的共享
payload 读取"，不代表 Tail relay 或客户端已完成任何版本协商。

## 请求判定与处理顺序

只处理认证之后的 DAV `GET` / `HEAD`：

1. 路径必须是 `assistant/<C>/<M>/payload`：恰好 4 段、无尾斜杠、
   `C == 已认证 conversationId`、`M` 为 32 位小写十六进制（`AssistantValidation.ItemId` 的
   `Guid("N")` 形式；relay 沿用 inbox 的同一规则，非小写 id 不代理，退回普通 404）。
2. 现有 DAV 结果优先：本机已提交 payload 直接按原语义返回（`200` + 文件流），代理不被调用；
   目录仍是 `405`，保留名仍是 `404`。
3. 仅当本地为普通 `404`（无该文件）时才尝试代理。
4. 带 `X-MPT-Shared-Payload-Hop: 1` 的请求不再代理（上游代理自己也会带这个固定标记，防止
   配置成环时递归）。
5. 源地址与本请求 `Host` 完全相同的配置视为自环，返回安全 `502` 并记录告警。
6. 读取并校验 locator；任何不合法都当作"没有可代理内容"，保持普通 `404`，不打开上游连接。

被拒绝的情况（`403` 跨命名空间、`401` 未认证、`400` 路径穿越等）都在现有 DAV 层完成，代理
不会扩大认证面。跨命名空间 `assistant/<其他C>/...` 仍是 `403`；`assistant-locator/<C>/...`
里 `C != 已认证会话` 的记录永远不会被读取。

## locator 契约

路径：`assistant-locator/<C>/<M>/manifest.json`（认证命名空间内，`C` 必须等于已认证会话）。

```json
{
  "version": 1,
  "message": {
    "version": 1,
    "id": "0123456789abcdef0123456789abcdef",
    "kind": "file",
    "text": null,
    "name": "example.zip",
    "size": 104857600,
    "createdAt": "2026-09-29T04:00:00Z",
    "senderDeviceId": "pc-example",
    "senderName": "我的电脑"
  },
  "payloadRoute": "mpt-tail-relay-v1"
}
```

`payloadRoute` 是客户端内注册的受信路由 id，不是 URL。relay 只认识
`mpt-tail-relay-v1`（与 `SharedLocator.TailPayloadRoute` 一致）。

校验规则（任一不满足即不代理，返回普通 `404`，且不发起上游请求）：

| 检查 | 规则 |
| --- | --- |
| 文件大小 | `> 16 KiB`（`SharedLocatorRules.LocatorBytes`）或非普通文件 → 拒绝；读取时多读 1 字节防竞态 |
| 顶层 | 必须是 JSON 对象；`version` 必须为整数 `1`（`true` 不算） |
| 路由 | `payloadRoute == "mpt-tail-relay-v1"` |
| message | 必须是对象；`version` 必须为整数 `1` |
| id | `message.id` 必须与路径 `<M>` 完全一致 |
| kind | 只接受 `file` 与 `image`：两者都有 payload。`text` 没有 payload，其它 kind 不属于 V1 manifest 契约，全部拒绝 |
| 可见性 | `targetDeviceId` 缺失或为 `null`；出现任何其它值（包括空串）都拒绝，私聊条目不能进入共享代理 |
| size | 必须是非负整数（`bool` 不算），且 `<= max_file_bytes`（服务单文件上限） |
| 额外字段 | 容忍（例如客户端的 `legacyFallbackAt`），不参与校验 |

relay 不解析、不校验 locator 中的设备名/时间等展示字段，也不读取 `requests/` 目录（恢复请求的
写入见下一节，写入前会检查已有记录）。

## 上游请求

* 连接：固定 origin（scheme/host/port 来自配置）；`http` 用 `HTTPConnection`，`https` 用
  `HTTPSConnection`；连接与读超时为 `tail_payload_timeout_seconds`。生产可用两种受信 origin：
  固定 Tail 域名，或部署的 loopback connector（`deploy/connector/README.md`：`127.0.0.1:18766`
  socket activation → `tailscale nc 100.64.0.1 80`）。
* 方法：`GET` 或 `HEAD`（与客户端请求一致）。
* 路径：`/mpt/relay/dav/assistant/<C>/<M>/payload`（`C`、`M` 经过 URL 转义）。
* `Host: mpt-relay.tail.lixinrui000.cn` **恒定显式发送**，与配置的 origin 无关：connector 只提供
  到 nginx 共享 80 端口的 TCP 可达性，nginx 依赖这个固定 virtualhost 名字选站点；绝不能沿用
  connector 的 `127.0.0.1:18766`。
* 请求头白名单：`Host`（固定 Tail 名）、`Authorization`（当前请求的原始 Basic 值）、
  `Accept-Encoding: identity`、`Connection: close`、固定标记 `X-MPT-Shared-Payload-Hop: 1`。
  客户端的 `Range`、`Cookie`、`If-*` 等一律不转发。
* `http.client` 不跟随重定向：任何 3xx 都按非 200 处理，重定向目标永不接触，也不会有凭据
  发往其它主机。
* 响应校验：状态必须 `200`；`Content-Length` 必须存在且等于 locator `size`。缺失、非数字、
  不一致或使用 chunked 都拒绝。上游 `404` 映射为本地普通 `404`（"payload 尚未提交"）。
* 授权边界：公网 locator **不是**授权凭证。上游 Tail 用同一个 Basic 凭据独立认证；若公网
  命名空间与 Tail 命名空间的密钥不同，上游返回 401/403。这属于**认证结论而非网络不可用**：
  本地返回 `403` + 固定标记 `tail_auth_rejected`（响应头 `X-MPT-Relay-Error` 同值），不回显
  上游 body/header/凭据，**不写恢复请求**（GET/HEAD 一致）。客户端 `Check` 把 403 映射为
  `PublicRelayAuthException`，因此不会触发公网副本请求。代理不使用 relay 自己保存的任何凭据
  （服务端只保存 key 摘要）。

## 故障恢复请求

Tail 副本不可用时，旧客户端自己写不了新协议的请求记录，公网代理替它写一条，让在线的发送端
通过既有 change 游标发现并发布公网副本：

* 路径：`assistant-locator/<C>/<M>/requests/relay-public.json`（认证命名空间内）。
* 形状（客户端的 `SharedPublicCopyRequest`，字段名与其 JSON 契约一致）：

```json
{"version":1,"itemId":"0123456789abcdef0123456789abcdef","deviceId":"relay-public",
 "requestedAt":"2026-09-29T05:04:16Z","reason":"tail-unreachable"}
```

* **只在 `GET`**：HEAD 是元数据探测（客户端也会探测 health/目录），不产生任何写入。
* **只在这些上游不可用情形写入**：连接失败/超时、上游 `404`（未提交）、上游 `5xx`、上游长度
  缺失或不符、响应体中途结束/读失败。
* **不写入**：入站认证失败（`401`/`403`）、跨命名空间拒绝、locator 缺失或校验失败、自环、
  上游 `3xx`（含任意重定向）、上游 `401`/`403`（本地映射为 `403 tail_auth_rejected`，见上）、
  其它 4xx（如 `400`/`409`/`429`）、客户端主动断开（不是 Tail 故障）、功能关闭时。
* **有界**：记录由 relay 生成，内容固定且远小于 2 KiB（`SharedLocatorRules.RequestBytes`）；
  读取既有记录时同样以 2 KiB 为上限。
* **幂等**：写入前在 namespace 锁内校验已有记录（`version/itemId/deviceId/reason/requestedAt`
  均可被客户端接受，`reason` 允许 `tail-unreachable` 或 `tail-unavailable`）。合法记录不重写、
  不 bump revision；损坏记录会被原子替换一次。并发失败读取因此只产生一次写入。
* **复用现有机制**：namespace 锁 + `ensure_entry_budget` + `store.reserve/commit/release`
  配额预留 + `write_atomic`（临时文件、fsync、rename、目录 fsync）。写入成功后
  `bump_revision` 唤醒既有 `/v1/changes` 长轮询；不新增 API，不新增内容哈希。
* **不是回执**：请求只表示"旧客户端想要公网副本"，绝不表示任何设备已保存内容；relay 不写
  `receipts/`。
* **无凭据**：记录与日志都不含会话 key；日志只记录掩码路径与短原因码。

## 状态映射

| 情形 | 本地响应 | 恢复请求（仅 GET） |
| --- | --- | --- |
| 本机已提交 payload | 现有 DAV 语义（`200` 文件流 / `HEAD` 头），代理不介入 | 不写 |
| locator 缺失或校验失败 | `404`（普通"资源不存在"文本），无上游请求 | 不写 |
| 上游 `200` 且长度一致（`GET`） | `200`，`Content-Type: application/octet-stream`，`Content-Length = size`，`Accept-Ranges: none`，分块转发 | 不写 |
| 上游 `200` 且长度一致（`HEAD`） | `200` 与相同头部，无响应体，上游连接立即关闭 | 不写（HEAD 不写） |
| 上游 `404` | `404` | 写一条 |
| 上游 `5xx` | `502` + 固定中文提示 + `Retry-After: 5` | 写一条 |
| 上游 `401` / `403` | `403` + 固定标记 `tail_auth_rejected`（`X-MPT-Relay-Error` 同值），不回显上游内容 | 不写 |
| 上游 `3xx` / 其它 4xx | `502`（同上） | 不写 |
| 连接失败 / 超时 | `502`（同上） | 写一条 |
| 上游长度与 locator 不符 | 未发送响应体前 `502` | 写一条 |
| 上游中途结束 / 读错误 | 已宣告的 `Content-Length` 未发满：停止转发并关闭连接；客户端看到截断（例如 `IncompleteRead`），绝不补齐、不返回"成功完整体" | 写一条（在关闭流之前回调） |

错误响应只使用固定文案，不回显上游状态文本、URL、header 或任何凭据。服务日志只记录被掩码的
路径与短原因码。

## 连接与资源释放

`Response` 新增 `stream`（`BodyStream`，`length` + `chunks()` + `close()` + `failed`）与显式
`content_length`。`httpd._write_response` 在 `finally` 中只调用一次 `stream.close()`：正常读完、
客户端断开（写失败抛异常）、上游截断都会走到这里，从而及时关闭上游 socket，不会泄漏到超时。
流式响应不经过 `body` 整块缓冲，也不创建本地文件。客户端断开后上游连接随 `close()` 释放
（测试用可控上游观测到 `BrokenPipe`/连接关闭）。

## 与现有语义的关系

* `payload` 写入仍然是静默的：成功的代理读取不写盘、不增加 namespace revision。只有一次
  Tail 不可用的 `GET` 会写一条恢复请求并 `+1` revision（幂等，见故障恢复一节）；`/v1/changes`
  长轮询语义不变，只是多了一个小信令来源。
* locator 的 `PUT` 仍是非 payload 文件，照旧 `+1` revision 并唤醒长轮询——小信令复用现有
  change 游标，无需新 API。
* DAV 认证、命名空间隔离、配额、原子 PUT、DAV 自动注册策略全部不变；本功能不新增路由。
* 本功能不实现成员版本门控、不迁移命名空间、不改客户端 V1 发布语义；公网 V1 manifest 是否
  已发布、Tail 副本是否已提交，仍由发送端流程决定。

## 测试

`tools/file-transfer/relay/tests/test_shared_payload_proxy.py`（真实 loopback socket）：

* 两个真实 relay 进程（public + Tail）：旧式 DAV `GET` 返回 Tail 字节，公网磁盘无 payload
  文件、公网用量只含小 locator；`HEAD` 只有头部；3 MiB 往返；`image` kind 的旧客户端 `GET`
  同样透明返回。
* 本机 payload 优先：有本地副本时不访问 Tail（停掉 Tail 后仍 `200`），且不写恢复请求。
* 拒绝路径：未认证 `401`、跨命名空间 `403`、路径穿越 `400`、hop 标记、尾斜杠、非 item id，
  均不发起上游请求。
* locator 负例：错误 route/version、id 不一致、`kind=text`/其它 kind、`targetDeviceId` 非空、
  size 非法/超限、非 JSON、空文件、>16 KiB、写在别的 C 下——全部 `404` 且零上游请求，零写入。
* 上游故障：302 到外部主机不被跟随且外部主机零请求；`3xx`/`5xx`/其它 4xx 映射 `502`；长度
  不符/缺失在发送任何 body 前 `502`；短 body/RST 触发客户端截断错误；连接不可达 `502` 且无
  凭据回显。
* Tail 认证拒绝：上游 `401`/`403` 时 GET/HEAD 都返回本地 `403` + `tail_auth_rejected`
  标记（body 与 `X-MPT-Relay-Error`），不回显上游 error body，不写恢复请求/回执，无 revision
  churn，日志无 key。
* 上游 `Host` 恒定：GET/HEAD/多次请求都发送 `Host: mpt-relay.tail.lixinrui000.cn`，即使
  origin 是 loopback connector。
* 恢复请求：`5xx`/`404`/连接失败/长度错误/短流各写一条 2 KiB 内的
  `requests/relay-public.json` 并 `+1` revision、唤醒正在等待的 `/v1/changes` 长轮询；
  重复失败字节级不变且 revision 不churn；外部写入的合法记录（`tail-unavailable`）不被改写；
  `401`/`403`、跨命名空间、malformed locator、`3xx`/`401`/`403`/其它 4xx、HEAD、客户端断开、
  功能关闭都不写；不产生 `receipts/`，记录与日志都不含会话 key。
* 流式：上游暂停时客户端已能收到首块（证明不整文件缓冲）；客户端断开后上游连接被关闭。
* 头白名单：上游只收到认证 Basic + 固定标记，`Cookie`/`Range`/自定义头被丢弃；locator 内的
  `url`/`host`/`authorization` 字段不生效。
* 默认关闭：能力位缺失、`GET` 仍 `404`、零上游请求、零写入；启用时 health 只返回
  `capabilities.sharedPayloadLocator=1`（无顶层同名字段）。
* 配置：默认关闭、origin 白名单、超时范围。

运行：

```bash
python3 tools/file-transfer/relay/tests/run.py shared_payload
python3 tools/file-transfer/relay/tests/run.py
```

## 已知边界（需后续对齐）

* **恢复请求只解决"发送端在线"**：记录通过既有 changes 长轮询交给发送端；发送端离线时不会
  立即产生公网副本，旧客户端保持 `502`/`404` 并重试（客户端自身上传仍可能随后补齐）。
* **Tail 认证拒绝不会触发副本**：上游 401/403 是 `403 tail_auth_rejected`，客户端按认证错误
  处理；这是有意行为，不是缺省回退。
* **上游必须有 `Content-Length`**：Tail relay 自身始终发送长度；chunked 或长度缺失按 `502`
  拒绝并写恢复请求，以保证转发前就能校验 locator `size`。
* **没有整体墙钟超时**：大文件按块推进，由每次读的空闲超时（默认 30s）+ 有界块大小兜底；
  这是"支持任意大小文件"与"不会永久占用连接"之间的取舍。
* **恢复请求不覆盖旧客户端的本地语义**：它不改 V1 manifest、不写回执、不改任何客户端行为；
  旧客户端仍按 404/502 自行重试。

## 部署边界（本仓库不含部署改动）

* 公网 relay 才设 `MPT_RELAY_TAIL_PAYLOAD_PROXY=1`；Tail relay 的 env 必须保持未设置/`0`。
* 生产 origin 二选一：固定 Tail 域名，或已部署的 loopback connector
  （`MPT_RELAY_TAIL_PAYLOAD_ORIGIN=http://127.0.0.1:18766`，见 `deploy/connector/README.md`）。
  connector 只提供 TCP 可达性；两者都靠显式 `Host: mpt-relay.tail.lixinrui000.cn` 选中 nginx
  固定 virtualhost。生产配置校验仍拒绝除固定域名与 loopback 之外的一切地址。
* 旧客户端只会在公网 `GET` 时触发此代理；新版客户端直接走 Tail，不经过该路径。
* 具体域名、nginx、systemd 与网络配置不在本次改动范围。
