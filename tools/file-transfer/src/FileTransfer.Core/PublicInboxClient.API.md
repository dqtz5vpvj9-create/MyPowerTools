# 设备配对公网收件箱客户端 API（M4 拥有，供模块接入）

`FileTransfer.Core/PublicInboxClient.cs` + `PublicInboxRecords.cs` 是**协议客户端**：
只实现 `relay/docs/PROTOCOL.md` 第 6 节的收件箱协议，不含 UI、不做本地存储决策。
对应服务端实现：`relay/mpt_relay/inbox.py`、`relay/mpt_relay/app.py`（已上线，公网地址
`https://proxy.lixinrui000.cn/mpt/relay/`）。

## 1. 角色：两个独立构造器，没有默认角色

| 工厂 | 凭据 | 能力 |
| --- | --- | --- |
| `PublicInboxClient.Owner(identity, retry?, baseAddress?)` | `ownerKey`（私有） | 注册、长轮询列表、下载 payload、写真实回执、删除 |
| `PublicInboxClient.Deposit(pairing, retry?, baseAddress?)` | `depositKey`（配对码里的） | 投递一条（PUT）、按随机 `itemId` 读回那一条回执 |

`Deposit` 也有接收 `PublicInboxIdentity` 的重载，但只取其中的 `Pairing`。
调用者不可能"默认按 owner 处理"：owner 方法在投递实例上抛 `InvalidOperationException`，
投递方法在 owner 实例上同样抛；服务端还会用 403 再挡一次。

`baseAddress` 只给测试注入；生产省略即可，等价于 `PublicInboxClient.ResolveBaseAddress()`
= `PublicRelayClient.BaseAddress`（代码内固定生产地址，无用户设置）。

## 2. 本地值：谁生成、谁保存

| 值 | 生成 | 保存 | 说明 |
| --- | --- | --- | --- |
| `inboxId` | 收件端离线 `PublicInboxIdentity.CreateNew()`（`inbox-<8hex>`） | 模块（secret store） | 与会话 id 同规则 `[A-Za-z0-9_-]{1,64}`，与会话/其它收件箱同名互斥 |
| `ownerKey` | 同上，独立随机 64 hex | 模块，**只在本机** | 注册与接收；永远不进配对码、不进日志（`ToString()` 已隐藏） |
| `depositKey` | 同上，另一个独立随机 64 hex | 模块 + 随配对码给出 | 投递与该条回执查询 |
| `itemId` | 发送端 `PublicInboxIds.NewItemId()`（32 位小写 hex，128 bit） | 发送队列 | **重试必须复用同一个 id**，否则服务端会当成新条目 |

`PublicInboxIdentity.ToJson()` / `TryParseJson()` 给模块持久化用；
`identity.Pairing.Encode()` / `PublicInboxPairing.TryParse()` 给配对码用，
载荷只有 `inboxId` + `depositKey`——**不含 `ownerKey`，也不含会话 key**（有测试证明）。
模块若要嵌进自己的 `mpt://pair/...` 码，直接搬运这三/四个字段即可。

## 3. 方法表

| 方法 | 角色 | 端点 | 预算 / 取消 |
| --- | --- | --- | --- |
| `RegisterAsync` | owner | `POST /v1/inboxes`（body `{"depositKey":…}`） | 20s；201/200 幂等；不自动重试 |
| `PollAsync(since, limit)` | owner | `GET /v1/inboxes/items?since&limit` | 无 `since` 用 40s 预算立即返回；有 `since` 用 **35s 长轮询预算**（服务端上限 25s）；204 → 空页 |
| `HeadAsync(itemId)` | owner | `HEAD /v1/inboxes/items/{id}` | 40s；只取 `X-MPT-*` 元信息 |
| `DownloadAsync(itemId, Stream)` | owner | `GET /v1/inboxes/items/{id}` | 流式写调用者的流（不接管生命周期、不关闭），校验声明长度；下载不设墙钟预算，由 `CancellationToken` 控制 |
| `AcknowledgeAsync(itemId, bytes, savedAt?, deviceId?, deviceName?)` | owner | `POST …/{id}/receipt` | 40s；**只有这里产生 `saved:true` 回执**；重复回执 `duplicate:true` |
| `DeleteAsync(itemId)` | owner | `DELETE …/{id}` | 40s；释放配额 |
| `GetReceiptAsync(itemId)` | 两者 | `GET …/{id}/receipt` | 40s；投递端凭随机 `itemId` 读自己那一条 |
| `DepositFileAsync(itemId, path, …)` | deposit | `PUT …/{id}` | 每次尝试新开 `FileStream`（可重投）；`X-MPT-Name` 百分号编码 UTF-8 |
| `DepositTextAsync(itemId, text, …)` | deposit | `PUT …/{id}`，`X-MPT-Kind: text` | 无文件名；空文本本地即拒 |
| `DepositAsync(PublicInboxDeposit, Stream)` | deposit | 同上 | 可 seek 的流每次重试前回绕；不可 seek 的流只发一次（`PublicInboxRetryPolicy.None`） |

`PublicInboxDeposit` 描述一次投递的元信息：`ItemId`、`Kind`（text/image/file）、`Name`、
`Length`、`SenderDeviceId`、`SenderName`、`CreatedAt`、`TargetDeviceId`。
本地会先按服务端规则校验（文件名 1–180 字符、Windows 保留名、发送设备名 ≤100 字符、
设备 id 模式等），失败抛 `PublicInboxRequestException`，不浪费一次网络往返。

## 4. 失败语义（`UserMessage` 可直接展示）

| 异常 | 触发 | 可重试 | 用户文案要点 |
| --- | --- | --- | --- |
| `PublicInboxAuthException` | 401 | 否（`Attempts` 恒为 1） | owner：收件箱密钥不一致；deposit：配对码里的投递密钥不一致，请收件端重新出示 |
| `PublicInboxPermissionException` | 403 | 否 | 投递密钥不能列出/读取/写回执；owner 不能投递 |
| `PublicInboxRequestException` | 400 / 本地校验 | 否 | 条目 id、文件名、回执字段不合法 |
| `PublicInboxNotFoundException` | 404 | 否 | 条目不存在、已被删除，或 `itemId` 不属于本收件箱 |
| `PublicInboxConflictException` | 409 | 否 | `deposit_key_locked` → 用旧配对码；`item_conflict` → 换新 `itemId` |
| `PublicInboxQuotaException` | 413 / 507 | 否 | 413「文件超过中转单文件上限」；507「中转空间不足，请先在收件设备上清理已接收的文件」；`TooLarge` 区分两者 |
| `PublicInboxUnavailableException` | 503 / 429 / 502 / 504 / 超时 / 连不上 | **是**，带 `RetryAfter` | `inbox_not_ready` →「收件端还没有注册这个收件箱（HTTP 503），稍后会自动重试」；429 →「操作过于频繁（HTTP 429）」 |
| `PublicInboxProtocolException` | 响应非法/超限、3xx 重定向 | 否 | 拒绝跟随重定向，避免把 Basic 凭据发给第三方 |

`Code`、`Detail`（服务端原文，仅日志）、`Status`、`Attempts` 都在异常上。

## 5. 重试与预算

- `PublicInboxRetryPolicy{ MaxAttempts=5, InitialDelay=1s, MaxDelay=10s }`；
  `PublicInboxRetryPolicy.None` = 只试一次。
- 只有投递（`PUT`）自动重试 503/429/502/504，退避 `InitialDelay × 2^(n-1)`，
  服务端 `Retry-After` 会被 `MaxDelay` 截断；其余调用把状态翻译成上面的异常交给调用方。
- 401/403/400/404/409/413/507 **永不重试**（`Attempts` 证明只发过一次）。
- 长轮询：客户端 35s 预算 = 服务端 25s + 宽限；`since` 超前立即返回；204 表示服务端提前收尾，
  返回 `since` 处的空页，接收循环直接继续。

## 6. 安全与"不依赖 Tailnet"

- 生产只连固定的 `https://proxy.lixinrui000.cn`；`AllowAutoRedirect = false`，3xx 直接报错。
- 客户端只用 `HttpClient` + `BaseAddress`，不碰 Tailscale、不做发现、不需要监听端口或账号。
- 元信息头一律 `A-Za-z0-9-_.~` 之外的百分号编码 UTF-8（`PublicInboxHeader.Encode/Decode`），
  中文/emoji 文件名与设备名可双向保真。
- 响应解析有界：列表 JSON ≤2 MiB、条目/回执 ≤64 KiB、错误体 ≤8 KiB、错误 detail 截断 512 字符，
  超出即 `PublicInboxProtocolException`；收到的文件名按落盘规则复验（长度、控制字符、路径分隔符、
  Windows 保留名），文本条目不接受文件名——模块不会因为服务端返回 `../` 之类名字而写到目录外；
  回执只有 owner 能写，投递端只能凭 128 bit 随机 `itemId` 读回那一条，没有列表/枚举入口。
- `PublicInboxIdentity`/`PublicInboxPairing` 的 `ToString()` 隐藏密钥，避免误入日志。

## 7. 模块接入示意

```csharp
// 收件端（身份由模块持久化；启动接收时注册 + 常驻长轮询）
using var owner = PublicInboxClient.Owner(storedIdentity);
await owner.RegisterAsync(token);
long revision = 0;
while (!token.IsCancellationRequested)
{
    var page = await owner.PollAsync(revision, limit: 50, token);   // 25s 挂起，投递即醒
    revision = page.Revision;
    foreach (var item in page.Items)
    {
        var target = store.CreateIncomingPath(item);                 // 模块决定落地位置
        var payload = await owner.DownloadAsync(item.ItemId, target.Stream, token);
        target.Commit();                                             // 真正保存成功之后
        await owner.AcknowledgeAsync(item.ItemId, payload.Bytes, target.SavedAt,
                                     deviceId, deviceName, token);   // 真实回执，不伪造
    }
}

// 发送端（只需要配对码；itemId 由发送队列生成并在重试间复用）
using var sender = PublicInboxClient.Deposit(pairing);
var itemId = PublicInboxIds.NewItemId();
await sender.DepositFileAsync(itemId, filePath, senderDeviceId: myId, senderName: myName, token: token);
var receipt = await sender.GetReceiptAsync(itemId, token);            // saved=true 才算送达
```

## 8. 验证

`tests/FileTransfer.Core.Tests/PublicInboxClientTests.cs`：每个测试都启动真实的仓库 Python 服务
子进程（随机端口、`--port 0`、隔离 data 目录、dispose 时 kill 并删除目录），覆盖注册幂等与
409/401、离线配对码 503 后重试成功、文件与文字双向、Unicode 百分号编码头（含服务端
`item.json` 原文）、长轮询唤醒、真实回执落盘与回读、deposit 权限矩阵（含裸 HTTP 403/401 探针）、
取消不留残条目与临时文件、413/507 文案、401 不重试、重定向与超限响应拒绝。

```powershell
dotnet test tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj `
  -c Debug -p:StageRepositoryModule=false `
  -p:ArtifactsPath=<独占目录> --filter "FullyQualifiedName~PublicInboxClientTests"
```

日志（含真实分母）与每个服务进程的 stdout/stderr 落在
`artifacts/.tmp-android-verify/public-inbox-client/`。
