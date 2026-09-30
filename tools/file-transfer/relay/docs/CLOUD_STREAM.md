# 网盘文件的反向流式领取

此扩展让共享会话接收端继续使用原 V1 文件下载地址，由在线发送端从自己的网盘读取文件，再通过中转服务流式交给接收端。接收端不用登录发送者的网盘，也不用知道发送端 IP。网盘账号凭据只在发送端使用；中转服务只保存文件 manifest 和限文件的领取能力，不保存文件正文。

目前只支持共享会话。私聊 inbox 的 owner/deposit 凭据不能调用这些接口，带 `targetDeviceId` 的 locator 会被拒绝。客户端不能把私聊转写共享会话来使用网盘。

## 开关与资源上限

`MPT_RELAY_CLOUD_PAYLOAD_STREAM=1` 开启后，`GET /mpt/relay/health` 才返回 `capabilities.cloudPayloadStream: 1`。默认关闭，关闭时 sender API 和 locator PUT 返回 404；既有 DAV、本地 payload 和 Tail 代理继续原有行为。

| 环境变量 | 默认 | 含义 |
| --- | --- | --- |
| `MPT_RELAY_CLOUD_MAX_REQUESTS` | 16 | 全服务活动领取上限；实际还受 `(max_connections - 4) / 3` 的连接预算限制 |
| `MPT_RELAY_CLOUD_REQUESTS_PER_CONVERSATION` | 4 | 单会话活动领取上限，不大于全局上限 |
| `MPT_RELAY_CLOUD_WAIT_SECONDS` | 30 | 从接收 GET 到发送端提供首块正文的期限，1–120 秒 |
| `MPT_RELAY_CLOUD_STREAM_SECONDS` | 900 | 从接收 GET 到完整传输结束的总期限，1–3600 秒 |

每个请求最多排队两块 64 KiB 正文；队列满时发送连接停止读取，上游自然受到背压。除队列外，HTTP 读写各持有有界块，操作系统仍有 socket 缓冲。活动请求和 sender 长轮询数量都有上限，不因文件大小增长。空闲轮询通过 socket 通知与 `select` 阻塞，正文队列通过条件变量等待；没有定时扫描线程。

网关需要同时保持 `proxy_request_buffering off` 和 `proxy_buffering off`，否则网关可能先缓存文件。仓库现有 nginx 配置已经包含这两项；运营方自定义配置也应保持它们。网关、网络或 provider 自身的较短超时仍可能先终止传输。

## 发布顺序

所有操作均使用既有 `Basic(conversationId, conversationKey)`，只访问已注册的共享 namespace。先创建 DAV 集合，然后依次写入：

1. `cloud-locator/<C>/<M>/manifest.json`。
2. `assistant/<C>/<M>/manifest.json`，内容为同一个原 V1 manifest。

其中 `<M>` 是 32 位小写十六进制 item ID。locator 的 JSON 示例结构如下，示例不包含可用凭据：

```json
{
  "version": 1,
  "conversationId": "shared-conversation-id",
  "message": {
    "version": 1,
    "id": "0123456789abcdef0123456789abcdef",
    "kind": "file",
    "text": null,
    "name": "example.pdf",
    "size": 1048576,
    "createdAt": "2026-09-29T12:00:00Z",
    "senderDeviceId": "sender-device",
    "senderName": "我的电脑",
    "targetDeviceId": null
  },
  "capability": "<64 lowercase hex characters>",
  "senderDeviceId": "sender-device",
  "expiresAt": "<future UTC time, within 7 days>",
  "route": "mpt-cloud-stream-v1"
}
```

locator 最多 16 KiB，只允许上述字段。外层 `conversationId` 可省略以兼容最初合同；如提供，必须与路径及 Basic namespace 相同。V1 manifest 本身通常不包含 `conversationId`，如包含也必须相同。`message.id` 必须等于路径 M；`kind` 仅允许 `file` 或 `image`；`targetDeviceId` 必须为空；文件大小是非负整数且不超过现有单文件上限。发送设备必须与 manifest 一致，过期时间必须是 UTC、尚未过期、距当前不超过 7 天。不接受 provider URL、cookie、token 或可选上游地址。

locator 在 PUT 前验证，然后经原有原子 DAV 写入和配额规则保存。领取前还要检查原 V1 manifest 已发布且字段内容一致；因此只写了 locator 的半成品不能领取。修改或删除 locator 会使尚未 claim 的旧请求失效。已经开始的流使用 claim 时验证过的能力，仍受其原始截止时间约束。

## 接收与供流

接收端仍访问 `GET /mpt/relay/dav/assistant/<C>/<M>/payload`：

- 本地已有 payload 时直接返回本地文件，不创建云领取请求。
- 没有本地文件、存在有效云 locator 时，创建一个仅内存中的领取请求，并等待发送端。
- 没有云 locator 时，继续既有 Tail locator 代理路径。
- 云 locator 失效或云发送者离线时明确失败，不创建公网完整副本或 Tail fallback 请求。

`HEAD` 只验证 locator 与已发布 manifest，返回文件大小，不创建领取请求。

发送端使用同一 namespace 凭据发起：

```text
GET /mpt/relay/v1/cloud/requests?deviceId=<senderDeviceId>&wait=25
```

`wait` 可为 0 到服务器 `longpoll_max_seconds`。结果只包含本 namespace、本发送设备、未 claim 且未过期的请求：

```json
{"serverTime":"<relay UTC now>","requests":[{"requestId":"<32 lower hex>","itemId":"<M>","capability":"<K>","size":1048576,"expiresAt":"<UTC claim deadline>"}]}
```

有请求立即返回；无请求时等待事件或期限。断开轮询连接会释放对应 waiter。客户端用 `serverTime` 将领取期限换算到自己的时钟，避免设备时钟较快时把有效请求当成过期。服务仍用自己的原始期限验证 claim，不因客户端换算延长领取权限。时间戳支持 .NET 的七位小数，在 Python 3.10 上解析前转换为微秒精度。发送端从本机配置的网盘读取文件，然后供流：

```text
PUT /mpt/relay/v1/cloud/requests/<requestId>/body
Authorization: Basic <same shared namespace>
X-MPT-Cloud-Capability: <K>
Content-Length: <exact original file size>
```

必须有精确 `Content-Length`，不接受 chunked 或 `Content-Range`。服务再次核对当前 locator、namespace 和能力后，原子地 claim 请求；同一请求只能供流一次。每个接收 GET 都有独立请求，因此第一位成员的接收或 receipt 不会阻止其他成员稍后领取同一文件。

发送 PUT 的成功响应是 `200 {"transferred":N}`，只在接收响应写完全部 N 字节后返回。这说明 HTTP 层完整写出；应用落盘是否成功仍由现有每设备 receipt 表示。短读、超时、发送者取消、接收者断开都不会得到成功供流确认。响应头已经发出后失败时，服务关闭连接，让接收端检测到截断。

| 情况 | 状态 |
| --- | --- |
| Basic 缺失、错误，或使用 inbox 凭据 | 401 |
| 跨会话 locator、私聊 manifest、能力错误或被替换 | 403 |
| 请求不存在、已结束或服务重启后旧请求 ID | 404 |
| 同一活动请求已 claim，或公开 manifest 不匹配 | 409 |
| locator 或尚存请求已过期 | 410 |
| 无 Content-Length 或 chunked 供流 | 411 |
| locator 超 16 KiB、size 超原单文件上限 | 413 |
| 数量已满、等待发送者超时、服务关闭或流中断 | 503（已发出的响应则截断） |
| 声明大小不符、Content-Range、发送正文短读 | 400 |

服务重启保留小型 locator/manifest，但不恢复旧连接和旧 requestId。发送端重连长轮询、接收端重试 GET 后会产生新请求；发送端须保留文件到网盘的本地映射。发送者离线时文件暂时不可领取，云账号暂停或失效也可能让领取失败。此实现不承诺所有网盘都能在发送端离线时领取。

## 可重复验证

```bash
TMPDIR=/mnt/cache/data-cache python3 -m unittest discover \
  -s tools/file-transfer/relay/tests -p test_cloud_stream.py -v
```

专项测试使用真实 HTTP socket，覆盖三方 source→sender→relay→receiver、逐字节一致、无 payload 落盘、背压、发送与接收卡住、截断、取消、服务关闭、鉴权、能力重用、过期、HEAD、本地 payload 优先、跨成员领取与空闲事件等待。完整回归使用同一命令，将模式改为 `'test_*.py'`；已有 Tail 代理、配对 inbox 与普通 DAV 测试保持独立。
