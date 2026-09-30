# 公网文件助手中转服务：固定协议

本文是 `tools/file-transfer/relay/` 服务的对外契约，供 M4 客户端、GPT UI 与主代理部署使用。
实现为 Python 3.10+ 标准库常驻进程，只监听 `127.0.0.1:18765`；nginx 终止 TLS 并把
`/mpt/relay/` 前缀**原样**转发（`proxy_pass http://127.0.0.1:18765;`，不带 URI 部分）。

- 公网根：`https://proxy.lixinrui000.cn`
- WebDAV 根：`https://proxy.lixinrui000.cn/mpt/relay/dav/`
- 客户端实现：`src/FileTransfer.Core/PublicRelayClient.cs`（本服务按其字段与语义实现）

## 1. 端点

| 方法 | 路径 | 认证 | 说明 |
| --- | --- | --- | --- |
| `GET` | `/mpt/relay/health` | 无 | 简短 JSON：`{"ok":true,"service":"mpt-relay","version":"…","uptimeSeconds":N,"longPollSeconds":25}` |
| `POST` | `/mpt/relay/v1/conversations` | Basic | 空 body；首次注册 namespace，成功 201 |
| `POST` | 同上（已注册、同 key） | Basic | 幂等，200，不会改写已有 key |
| `GET` | `/mpt/relay/v1/changes[?since=N]` | Basic | 长轮询，最长 25 秒，返回 `{"revision":N}` |
| `POST` | `/mpt/relay/v1/inboxes` | Basic owner | 设备配对收件箱注册（见第 6 节） |
| 任意 | `/mpt/relay/v1/inboxes/items…` | Basic owner/deposit | 投递、接收、回执（见第 6 节） |
| `GET` | `/mpt/relay/v1/cloud/requests?deviceId=D&wait=25` | Basic shared | 可选网盘领取事件，见 [CLOUD_STREAM.md](CLOUD_STREAM.md) |
| `PUT` | `/mpt/relay/v1/cloud/requests/R/body` | Basic shared + file capability | 可选反向供流，精确长度、一次 claim、不落文件正文盘 |
| 任意 | `/mpt/relay/dav/…` | Basic | WebDAV：`OPTIONS/GET/HEAD/PUT/DELETE/PROPFIND/MKCOL` |

认证一律 `Authorization: Basic base64(conversationId:conversationKey)`：

- `conversationId` = 1–64 位 `[A-Za-z0-9_-]`（与 `AssistantValidation.ConversationId` 相同）；
- `conversationKey` = 严格 64 位十六进制（大小写都接受，内部按小写比较；客户端本身
  `Uri.IsHexDigit` 也接受大写）；
- 缺失/格式非法/密钥错误 → **401**（带 `WWW-Authenticate: Basic realm="mpt-relay"`）。
  401/403 对客户端是“密钥不一致、不可重试”，因此本服务不会把可重试的临时故障报成 401/403。

### 注册语义（与 R0 的边界）

- **设备身份、连接码/二维码的生成与导出完全不经过本服务**，也不应该经过：R0 要求断网可用。
- 只有**发送或接收**前才需要一次 `POST /v1/conversations`（`PublicRelayClient.RegisterAsync`）。
  已注册 namespace 用同一 key 重复注册返回 200，不会产生第二个副本，也不能更换 key
  （换 key → 401，原 key 继续有效）。
- **出厂默认 `MPT_RELAY_DAV_AUTO_REGISTER=0`**：只有上面的 POST 能建 namespace，因此未认证的
  DAV 请求无法凭空建账。需要兼容“只配地址+账号密码、没有注册步骤”的纯 WebDAV 客户端时设成 1，
  此时首次 DAV 认证会自动建 namespace（同样受限速与 `max_conversations` 约束）。
- namespace 是“共享密钥即所有权”的模型，没有账号体系：客户端必须在首次发送时立刻注册，
  否则知道 id 的第三方可能抢先注册（这是既有连接码设计的固有限制，不是本服务可单独解决的）。

### `since` 语义

| 请求 | 行为 |
| --- | --- |
| 不带 `since` | 立即返回当前 revision |
| `since == current` | 挂起最多 25 秒；期间有变更立即返回新 revision，超时返回未变的 revision |
| `since > current`（例如服务被回滚/换库） | 立即返回当前 revision |
| `since` 非整数或负数 | 400 `{"error":"invalid_since"}` |

- revision 是**每 namespace 独立**的单调计数器，双会话/双用户互不可见。
- **只有** `manifest.json`、`receipts/*.json` 这类“发布标记”PUT 成功才 +1 并唤醒；
  `payload` PUT/GET/DELETE 不唤醒。规则实现为“文件名等于 `payload` 的写入不计数”，
  因此旧版 `android/<guid>/ready.json` 布局同样只在 ready.json 落盘时唤醒。
- `MKCOL` 不改 revision；`DELETE` 对非 `payload` 的文件会 +1。
- revision 存 SQLite，**SIGTERM/重启后保持**；崩溃窗口（文件已 rename、revision 未 commit）
  最多让一次通知丢失，接收端下一轮 PROPFIND 仍能看到条目，不会丢数据。
- 客户端断线会被服务在 ≤0.5 秒内发现并结束挂起，不会占住线程到 25 秒。

## 2. 存储布局与隔离

每个 conversation 有**私有根目录** `/var/lib/mpt-relay/conversations/<conversationId>/`，
WebDAV 根 `/mpt/relay/dav/` 就映射到这里。因此：

```
/mpt/relay/dav/assistant/<conversationId>/<itemId>/payload
/mpt/relay/dav/assistant/<conversationId>/<itemId>/manifest.json
/mpt/relay/dav/assistant/<conversationId>/<itemId>/receipts/<deviceId>.json
/mpt/relay/dav/android/<guid>/{payload,ready.json}        # 旧版普通互传布局同样可用
```

- `assistant/` 下的第二段必须等于 Basic 用户名：`assistant/<别的会话>/…` 一律 **403**
  （不是 404，避免用存在性探测他人 namespace）。
- PROPFIND 只列本会话自己的树；任何时候都不存在“全服列表”接口。
- 路径校验：拒绝空段、`.`、`..`、`%2f`/`%5c` 解码出的分隔符、控制字符、首尾空白、
  段长 >128、层级 >8；再做一次 `commonpath` 包含性检查。所有拒绝都是 400/403，不触盘。
- `.mpt-relay-upload-*` 是服务自己的上传临时文件：不出现在 PROPFIND/GET 里，启动时清扫。

## 3. WebDAV 方法语义（与 OpenListClient 对齐）

| 方法 | 关键状态码 |
| --- | --- |
| `MKCOL` | 201 新建；**405** 已存在（客户端把 405 当成功）；409 父集合不存在；415 带 body；507 条目数超限 |
| `PROPFIND` | 207 `D:multistatus`（`href` 保留 `/mpt/relay/dav/` 前缀，含 `getlastmodified`/`getcontentlength`/`resourcetype`）；404 目标不存在；403 `Depth: infinity`；Depth 0/1，缺省按 1 |
| `PUT` | 201 新建 / 204 覆盖；409 父集合不存在；405 目标是集合或目标带尾斜杠；413 超过单文件上限；507 配额不足；400 分片 PUT |
| `GET`/`HEAD` | 200 `application/octet-stream` + 精确 `Content-Length`；404 缺失；405 目标是集合 |
| `DELETE` | 204；404 缺失；403 会话根 |
| `OPTIONS` | 200，`DAV: 1`，`Allow: OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, MKCOL` |
| 其他 | 405（`PROPPATCH/LOCK/MOVE/COPY` 未实现） |

PUT 是原子的：写同目录隐藏临时文件 → `fsync` → `os.replace` → 目录 `fsync`。
**上传中断/取消/超限都不会留下可见文件**，覆盖失败时旧内容保持可读。
请求体流式写盘（`Content-Length` 或 `Transfer-Encoding: chunked` 均可），大文件不进内存。

## 4. 配额与失败语义

请求体的读取有**墙钟预算**（`MPT_RELAY_BODY_BUDGET_SECONDS`，默认 900 秒）：慢滴客户端会被
截止时间终止（每次 read 的 socket 超时也会被收窄到剩余预算），不会长期占用连接槽。长轮询没有
请求体，不受这个预算影响（只受 `longpoll_max_seconds` 与 nginx `proxy_read_timeout` 约束）。

配额在**读取请求体之前**按 `Content-Length` 预留；chunked 没有声明长度时，按已写入字节逐段预留。
预留保证跨会话并发上传不会各自通过检查后一起写爆全局配额，失败/取消/断线会立刻释放。

| 情况 | 响应 |
| --- | --- |
| 单文件 > `max_file_bytes`（默认 512 MiB） | **413** |
| 会话用量 + 本次 > `per_conversation_bytes`（默认 1 GiB） | **507** |
| 全服用量 + 本次 > `global_bytes`（默认 8 GiB） | **507** |
| 会话条目数 > `max_entries_per_conversation` | **507** |
| 注册过频 / 认证失败过多 / namespace 数达上限 | **429** + `Retry-After` |
| 认证并发闸门已满 | 503 + `Retry-After: 2` |

- 声明了 `Content-Length` 的超限请求在**读取请求体之前**就失败，不浪费带宽。
- 空间不足是**明确失败**：服务从不为了腾空间删除用户文件；`empty_namespace_ttl_days`
  默认 0（关闭），即使开启也只回收“0 字节 + revision 0 + 长期无认证”的空 namespace。
- 覆盖同名文件按增量计费，所以“重传同一条目”不会因为旧副本被双算而误报 507。
- chunked 的长度行按十六进制严格解析（负数、非十六进制、超 64 位、缺 CRLF、超长行一律 400），
  单个 chunk 分段读取，每次最多 64 KiB；trailers 总量有界（8 KiB）；EOF 截断一律失败。

## 5. 对客户端的提示（供 M4 决定文案，本服务不改客户端）

- `507`/`413` 在 `OpenListClient.Check` 里会变成
  “OpenList 返回 HTTP 507。请检查地址、网盘挂载和账号权限。”，
  对公网中转来说这句提示不准确；建议 M4 在公网通道上把 413 显示为“文件超过中转单文件上限”、
  507 显示为“中转空间不足”，429 显示为“操作过于频繁，稍后自动重试”。
- `PublicRelayClient.LongPollBudget = 35s` 与服务端 25s 上限匹配，nginx `proxy_read_timeout`
  必须大于 25s（模板给 70s）。
- 服务端 401/403 都被 `PublicRelayClient` 视为 `PublicRelayAuthException`（不可重试）；
  因此正常路径不会返回 403：403 只用于跨 namespace 的 DAV 路径。

## 6. 设备配对收件箱（inbox 投递）

用途：把文件投递给另一台设备，而**不把会话身份交出去**。收件端离线生成三个值，配对码里只有
`inboxId` + `depositKey`；`ownerKey` 和本会话的 `conversationKey` **永远不出现在配对码里**。
注意 `ownerKey` 并不是"不上传"：注册与所有 owner 操作都要用它做 HTTPS Basic 的密码发给服务器
（与现有会话凭据一样，只在 HTTPS 上传输、只以加盐摘要落库、从不进日志）；它只是不随配对码扩散。
收件箱是与会话并列的一类 namespace（同名标识互斥，配额与限速共用）。

### 6.1 本地生成（不经网络）

| 值 | 规则 |
| --- | --- |
| `inboxId` | 1–64 位 `[A-Za-z0-9_-]`（建议 `inbox-<32hex>`，随机 128 bit，避免无谓碰撞） |
| `ownerKey` | 独立随机 64 hex，**私有**：只用于注册与接收，经 HTTPS Basic 发给服务器，绝不进配对码 |
| `depositKey` | 独立随机 64 hex，随配对码给出，只用于投递与该条回执查询 |

配对码内容：`inboxId` + `depositKey`（可另带显示名等展示信息）。三个值都必须持久保存。
`depositKey` 同样经 HTTPS Basic 传输（投递时），泄露配对码只等于泄露"可投递"这一个权限。

### 6.2 注册（收件端，先于任何投递）

```
POST /mpt/relay/v1/inboxes
Authorization: Basic base64(inboxId:ownerKey)
Content-Type: application/json

{"depositKey":"<64hex>"}
```

| 状态 | 含义 |
| --- | --- |
| 201 | `{"inboxId","created":true,"revision":0,"itemsPath":"/mpt/relay/v1/inboxes/items"}` |
| 200 | 同 ownerKey + 同 depositKey 的幂等重复注册（不消耗注册限速） |
| 400 | `invalid_deposit_key` / `keys_must_differ`（两个 key 必须不同）/ `invalid_body` |
| 401 | owner 密钥不一致；**用 depositKey 冒充 owner 也在这里被拒**；标识已被会话占用 |
| 409 | `deposit_key_locked`：owner 正确但 depositKey 不同（旧配对码继续有效，不接受新 key） |
| 429 | 建账限速（按 IP + 全服）或 `max_inboxes` 上限 |

### 6.3 投递（发送端，只需配对码）

```
PUT /mpt/relay/v1/inboxes/items/{itemId}
Authorization: Basic base64(inboxId:depositKey)
X-MPT-Kind: file|image|text        缺省 file
X-MPT-Name: <百分号编码 UTF-8>      file/image 必填，text 禁止
X-MPT-Sender-Id: <1–64 位>          可选
X-MPT-Sender-Name: <百分号编码>      可选，≤100 字符
X-MPT-Created-At: <ISO-8601>        可选，缺省为服务器时间
X-MPT-Target-Device-Id: <1–64 位>   可选
<body: 原始字节；text 时直接是 UTF-8 文本>
```

`itemId`：**32 位小写 hex 随机值（128 bit）**，由发送端生成；重试必须复用同一 id。

| 状态 | 含义 |
| --- | --- |
| 201 | `{"itemId","size","revision"}`，新条目已发布并唤醒 owner 长轮询 |
| 200 | `{"itemId","duplicate":true,…}`：同 id 且 kind/name/size/sender 相同 → 幂等，不重复唤醒 |
| 400 | `invalid_item_id` / `invalid_kind` / `invalid_name` / `invalid_sender` / `header_too_long` / `metadata_too_large` / `incomplete_body` / `empty_text` |
| 401 | 密钥不对（该 inbox 的 owner/deposit 都不匹配） |
| 403 | `deposit_required`（owner 不走投递口） |
| 409 | `item_conflict`：同 id 但元信息不同 → 客户端应换新 itemId |
| 413 | 超过单文件上限；507 超出命名空间或全局配额 |
| **503** | `inbox_not_ready` + `Retry-After: 5`：**收件端还没注册** → 这是可重试状态，不是 401，客户端应自动退避重试 |

元信息头总量 ≤4 KiB、单个 ≤512 B；payload 与 DAV 共用原子 PUT、请求体墙钟预算、配额预留与取消语义
（`payload` + 元信息两步：先落 payload 再发布 `item.json`，中途失败不留可见条目）。

### 6.4 owner 接收

```
GET /mpt/relay/v1/inboxes/items?since=N[&limit=K]     Authorization: owner
→ 200 {"revision":R,"items":[{"itemId","kind","name","size","createdAt",
                              "senderDeviceId","senderName","targetDeviceId"}…],"hasMore":bool}
```
`items` 只含**尚无回执**的条目，按到达时间倒序，最多 `limit`（缺省 50，上限 200）。`since` 语义与
`/v1/changes` 完全一致：无 `since` 立即返回；`since == current` 挂起 ≤25 秒、投递发生立即返回；
`since > current` 立即返回；客户端断线 → 204 并结束。

```
GET|HEAD /mpt/relay/v1/inboxes/items/{itemId}          Authorization: owner
→ 200 application/octet-stream + X-MPT-*（name/sender 为百分号编码）；404 unknown_item

DELETE /mpt/relay/v1/inboxes/items/{itemId}            Authorization: owner
→ 204（释放配额；不唤醒长轮询；删除后回执也不可读）

POST /mpt/relay/v1/inboxes/items/{itemId}/receipt      Authorization: owner
{"savedAt":"<ISO-8601>","bytes":N,"deviceId":"…"?,"deviceName":"…"?}
→ 200 {"itemId","saved":true,"duplicate":bool,…}
→ 400 invalid_receipt / invalid_saved_at / receipt_item_mismatch（body.itemId 与路径不一致、bytes<0 或 >条目长度、设备字段非法）
→ 404 unknown_item
```
回执只由 owner 写，**不递增 revision、不唤醒长轮询**（owner 自己的记账，只是把条目移出待处理列表）；
重复回执返回 `duplicate:true`。

### 6.5 发送端确认（deposit 权限）

```
GET /mpt/relay/v1/inboxes/items/{itemId}/receipt       Authorization: deposit（owner 亦可）
→ 200 {"itemId","saved":false,"size":N}                                尚无回执
→ 200 {"itemId","saved":true,"savedAt","bytes","deviceId","deviceName"} 收件端已保存
→ 404 unknown_item（含从未投递过的 id）；400 invalid_item_id
```
`itemId` 是 128 bit 随机值，因此“知道 itemId”本身就是读这一条回执的凭据：没有列表接口、
没有枚举入口、也不能写别人的回执。

### 6.6 权限矩阵

| 路由 | ownerKey | depositKey |
| --- | --- | --- |
| `POST /v1/inboxes` | 注册 / 幂等 | 401 |
| `GET /v1/inboxes/items` | ✅ 列表 + 长轮询 | 403 |
| `GET /v1/inboxes/items/{id}` | ✅ payload | 403 |
| `DELETE /v1/inboxes/items/{id}` | ✅ | 403 |
| `POST /v1/inboxes/items/{id}/receipt` | ✅ 写 | 403 |
| `GET /v1/inboxes/items/{id}/receipt` | ✅ | ✅（凭随机 itemId） |
| `PUT /v1/inboxes/items/{id}` | 403 | ✅ |
| 会话 DAV / `/v1/changes` / `/v1/conversations` | 不适用 | 401（inbox 凭据不是会话凭据） |

`ownerKey` 与 `depositKey` 都只以加盐 PBKDF2 摘要存储；日志中的 inbox id 与 conversation id 一样脱敏。
收件箱的用量计入全局配额，单命名空间配额、单文件上限、连接数、认证失败窗口与建账限速全部沿用现有配置。
