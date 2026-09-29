# F3：Assistant 目录公开接口（M4 调用说明）

本文件先于代码固定接口，代码完成后已同步为最终签名。命名空间 `FileTransfer.Core.Assistant`，
JSON 一律 camelCase。M4 只调用这里列出的签名，不要在模块或 Surface 里重新实现队列、分页或回执逻辑。

前置条件：`OpenListClient` 根声明必须是 `public sealed partial class OpenListClient`
（M4 已落地）。本目录的 `OpenListAssistantClient.cs` 是它的 partial 扩展。

**F3 依赖根类的这些私有成员，请勿改名/改签名**：`_root`、`RequestAsync`、`Check`、
`FollowDownloadAsync`、`UploadContent`。F3 不复制、不放宽凭据与跨站重定向规则，也不新增内容 hash。

## 1. 记录与枚举

```csharp
public enum AssistantItemKind { Text, Image, File }
public enum AssistantItemState { Queued, Sending, Stored, Delivered, Downloading, Available, Failed, Cancelled }

public sealed record AssistantIdentity(string DeviceId, string Name, string ConversationId);
// DeviceId / ConversationId 必须满足 TransferFiles.DeviceId（1–64 位字母、数字、-、_），Name 1–100 字符。

public sealed record AssistantItem
{
    public string Id { get; init; }                       // 32 位 hex（Guid "N"），唯一且内容不可变
    public AssistantItemKind Kind { get; init; }
    public string? Text { get; init; }                    // 仅 kind=text
    public string? Name { get; init; }                    // 仅 image/file，已通过 TransferFiles.FileName
    public long Size { get; init; }                       // text 为 UTF-8 字节数（仅显示），其余为文件长度
    public DateTimeOffset CreatedAt { get; init; }
    public string SenderDeviceId { get; init; }
    public string SenderName { get; init; }
    public string? TargetDeviceId { get; init; }          // 目标发送时保留；null = 发给自己
    public AssistantItemState State { get; set; }         // 本地投递状态，不发布到中转
    public long BytesDone { get; set; }
    public string? LocalPath { get; set; }                // 本机可打开路径；未落盘为 null
    public string? Error { get; set; }
    public List<AssistantReceipt> Receipts { get; set; }  // 其他设备回执合并结果
    public int Attempts { get; set; }                     // 本地重试计数，供 M4 退避；重试优先级按它升序
    public DateTimeOffset? ReceiptAt { get; set; }        // 本机为该条写回执的时间；null = 还没写
    public DateTimeOffset? ReceiptCheckedAt { get; set; } // 本机上次查该条回执的时间；回执轮转用

    [JsonIgnore] public bool CanCancel { get; }           // 计算属性，不进入 JSON
    public AssistantManifest ToManifest();                // 发布用不可变投影
}

public sealed record AssistantReceipt
{
    public string ItemId { get; init; }
    public string DeviceId { get; init; }                 // 保存该条目的设备
    public string DeviceName { get; init; }
    public DateTimeOffset SavedAt { get; init; }          // 只表示“已保存文件 / 已取到文本”，不是已阅读
    public long Bytes { get; init; }
}

public sealed record AssistantManifest                            // 发布到中转的不可变消息
{
    public int Version { get; init; } = 1;
    public string Id { get; init; }
    public AssistantItemKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Name { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string SenderDeviceId { get; init; }
    public string SenderName { get; init; }
    public string? TargetDeviceId { get; init; }
}

public sealed record AssistantOpenTarget(string? Path, string? Text, bool NeedsDownload);

public static class AssistantJson { public static JsonSerializerOptions Options { get; } }

public static class AssistantLimits
{
    public const int MaxTextCharacters = 8192;
    public const int MaxSenderNameLength = 100;
    public const long MaxPayloadBytes = 16L << 30;
    public const int MaxReceiptsPerItem = 64;
    public static readonly string[] ImageExtensions;      // 附件类型判定用
}

public static class AssistantContent
{
    public static bool HasLocalContent(AssistantItem item);
    public static AssistantOpenTarget OpenTarget(AssistantItem item);   // 文本→Text；有本地文件→Path；否则 NeedsDownload
}
```

`AssistantItem`/`AssistantReceipt` 用 `AssistantJson.Options`（或 `DirectTransfer.Json`）序列化即得到
合同里的 `items[]`：枚举带 `[JsonConverter]`，始终是 `"text"`/`"stored"` 这类小写字符串，不会退化成数字。
`ToManifest()` 生成发布用投影，不含 `state/bytesDone/localPath/error/receipts/attempts`。

## 2. AssistantStore

```csharp
public sealed class AssistantStore
{
    public const int MaxKnownRemoteIds = 2000;
    public AssistantStore(string directory);
    public string Directory { get; }
    public string PayloadRoot { get; }                     // <directory>/payload
    public string InboxRoot { get; }                       // <directory>/inbox

    public Task<AssistantState> LoadAsync(CancellationToken token);
    public Task<AssistantState> ConfigureAsync(AssistantIdentity identity, CancellationToken token);
    public Task<IReadOnlyList<AssistantItem>> EnqueueAsync(AssistantIdentity identity, AssistantDraft draft, CancellationToken token);
    public Task<AssistantItem> AdoptAsync(AssistantIdentity identity, AssistantItem item, CancellationToken token);
    public Task SaveAsync(AssistantState state, CancellationToken token);
    public Task<AssistantState> MutateAsync(Action<AssistantState> change, CancellationToken token);
    public string? GetPayloadPath(AssistantItem item);     // text → null；否则 <payload>/<id>/<name>
    public string GetInboxDirectory(AssistantItem item);   // 下载落点目录
    public string GetInboxPath(AssistantItem item);        // 下载落点文件（可能还不存在）
    public int SweepPartials();                            // 只删本工具 .mpt-*.part
}

public sealed record AssistantDraft
{
    public string? Text { get; init; }
    public IReadOnlyList<string> Paths { get; init; }
    public string? TargetDeviceId { get; init; }
    public static AssistantDraft ForText(string text, string? targetDeviceId = null);                     // 兼容保留
    public static AssistantDraft ForPaths(IEnumerable<string> paths, string? targetDeviceId = null);      // 兼容保留
    public static AssistantDraft ForContent(string? text, IEnumerable<string>? paths = null, string? targetDeviceId = null);
}

public sealed record AssistantState
{
    public int Version { get; set; }
    public AssistantIdentity? Identity { get; set; }
    public List<AssistantItem> Items { get; set; }         // 保存顺序；界面按 createdAt 排序
    public List<string> KnownRemoteIds { get; set; }
    public AssistantItem? Find(string itemId);
    public bool TryCancel(string itemId);
    public IEnumerable<AssistantItem> Outgoing(string deviceId);
    public IEnumerable<AssistantItem> Incoming(string deviceId);
    public void Remember(string itemId);
    public void RememberAll(IEnumerable<string> oldestFirst);
    public AssistantItem Add(AssistantItem item);
    public AssistantState Copy();
    public void RestoreFrom(AssistantState backup);
}
```

持久化：`<directory>/assistant.json`（临时文件 + 原子 move）。附件副本
`<directory>/payload/<itemId>/<name>`，下载落点 `<directory>/inbox/<itemId>/<name>`。
只读写本目录自己的文件，不动 `history.json`、secret 或用户旧文件；**不做任何条数裁剪**
（`TransferStore.MaxRecords = 50` 只属于旧历史，助手队列永不被它挤掉）。

- `LoadAsync` 返回进程内共享快照（同一目录只应有一个 `AssistantStore` 实例）。**首次调用**做重启恢复：
  `sending → queued`、`downloading → stored`，并清理本工具自己的 `.mpt-*.part` 残留；条目、payload、
  回执全部保留。之后调用只返回同一实例，不重读磁盘。
- `EnqueueAsync`（`assistant.send` 的持久化点）：**同一份 draft 可以同时带 Text 和 Paths**，生成
  1 条文本 + 每个附件各 1 条（文本在前，其余按 Paths 顺序），全部持久化成功后才返回全部条目；
  一个路径一个 `itemId`，同名附件互不覆盖（每条一个独立目录），文本无 payload 文件。
  整批原子：任何一步失败（找不到文件、复制失败、状态文件写失败）都不留半条记录，已复制的副本会删除，
  内存快照与磁盘都回到本次调用之前的状态。
- 所有写操作都是**先落盘、再发布内存快照**。`Enqueue/Adopt/Mutate/Configure` 在一个事务里完成
  “克隆回滚点 → 应用改动 → 原子写 → 失败就地回滚”，所以写失败时内存里的
  `Items/Identity/KnownRemoteIds` 精确回到上一次提交，不会出现“inspect 看得到、磁盘没有”的幽灵条目。
  `SaveAsync` 是唯一例外：它的改动**由调用方在调用前就写在内存里**，因此失败时只能把内存恢复成
  “磁盘上最后一次成功提交的那一代”（与重启后加载到的一致）；能改内存请优先用 `MutateAsync`，
  它不需要额外读盘也能精确回滚。列表字段用写时复制整体替换（`Items`/`KnownRemoteIds` 不做原地增删），
  而且**任何字段（含 `BytesDone` 进度）都只在 store 的锁内被修改**，因此并发读取/枚举/序列化
  不会看到“集合已修改”或半更新的记录。
- `MutateAsync` 是原子读改写，也是模块改状态的推荐入口；`change` 回调内不要再调用 store 方法。
- `AdoptAsync`：把**直传通道已经保存好**的来件并入同一条持久时间线（M4 的直接接收用）。
  按 id 去重（重复调用不产生第二条消息），校验字段但只保留消息字段，附件必须已经有**真实可打开**的
  本地文件，文本可无文件；只有 relay manifest（`stored`、没有本地文件）**不算已保存**。
  若同 id 条目已存在且本机尚无内容（例如先从中转清单认识的 `stored`/`failed` 条目，后来直传补上了
  payload），`AdoptAsync` 会把它**升级为 `available` 并写入 `LocalPath`**，而不是返回旧的 `stored`；
  已有本地内容的条目不覆盖；`cancelled` 条目永不复活。它**不**写回执——下一次 `SyncAsync` 会为
  `available && ReceiptAt == null` 的他人条目补写回执。

## 3. OpenListClient 扩展（partial，本目录实现）

命名空间固定 `assistant/<conversationId>/<itemId>/`：

```
assistant/<conversationId>/<itemId>/payload        kind != text 才有
assistant/<conversationId>/<itemId>/manifest.json   payload 写完后才发布
assistant/<conversationId>/<itemId>/receipts/<deviceId>.json
```

```csharp
public sealed record AssistantListRequest(
    int Limit = AssistantListRequest.DefaultLimit,          // 50，上限 200
    IReadOnlyCollection<string>? KnownItemIds = null,
    string? Cursor = null);
public sealed record AssistantPage(
    IReadOnlyList<AssistantManifest> Items,
    IReadOnlyList<string> InvalidItemIds,
    bool HasMore,
    int DiscoveredCount,
    string? NextCursor)
{ public int InvalidCount { get; } }

public sealed partial class OpenListClient
{
    public Task<AssistantManifest> PublishAssistantAsync(string conversationId, AssistantManifest manifest,
        string? payloadPath, Action<long, long>? progress, CancellationToken token);
    public Task<AssistantPage> ListAssistantAsync(string conversationId, AssistantListRequest request, CancellationToken token);
    public Task<string> DownloadAssistantAsync(string conversationId, AssistantManifest manifest, string directory,
        Action<long, long>? progress, CancellationToken token);
    public Task<AssistantReceipt> WriteAssistantReceiptAsync(string conversationId, AssistantReceipt receipt, CancellationToken token);
    public Task<IReadOnlyList<AssistantReceipt>> ListAssistantReceiptsAsync(string conversationId, string itemId, CancellationToken token);
}
```

- `PublishAssistantAsync`：MKCOL `assistant/` → MKCOL 会话目录 → MKCOL 条目目录 → PUT payload
  （kind != text 必须给 payloadPath）→ PUT `manifest.json`。`manifest.json` 已存在且内容相同则**不改写**，
  直接返回远端值；同 id 内容不同则抛 `InvalidDataException`；远端记录损坏（截断/非法）时重写修复。
  payload PUT 失败即中止，远端不留 manifest（“失败中断无 ready”）。
- `ListAssistantAsync`：PROPFIND 会话目录 Depth 1，按 `getlastmodified` 倒序（缺失则 id 倒序）取
  “不在 `KnownItemIds` 里”的至多 `Limit` 条再 GET manifest；单次调用最多 `Limit` 次 manifest GET。
  无 manifest（发布中断）静默跳过且不记为已知；JSON 解析失败不记为已知、下轮重试；字段非法计入
  `InvalidItemIds`（调用方 `Remember` 后不再重复抓取），不抛异常毒化时间线。
  重复同步在无新条目时只有 1 次 PROPFIND、0 次 GET，因此历史不会无限重复下载。
- `DownloadAssistantAsync`：沿用现有重定向规则（跨站必须 HTTPS，跟随的重定向不带凭据），
  校验长度等于 `manifest.Size`，用 `.mpt-*.part` 在目标目录内原子提交。
- `WriteAssistantReceiptAsync`：确保 `receipts/` 存在后写 `receipts/<deviceId>.json`；
  每设备独立文件，不覆盖全局 index。回执只由“已保存该内容”的设备写。
- `ListAssistantReceiptsAsync`：PROPFIND `receipts/` Depth 1 + GET，最多 64 份，按设备去重取最新。
- 校验（对端 manifest 视为不可信输入）：id 32 位 hex、`Version==1`、枚举合法、name 过
  `TransferFiles.FileName`、size ∈ [0, 16 GiB]、text ≤ 8192 字符且无非法控制字符、
  text 的 size 必须等于 UTF-8 字节数、senderDeviceId/targetDeviceId 过 `TransferFiles.DeviceId`、
  senderName ≤ 100 字符。**不引入内容 hash。**

## 4. AssistantSync（模块调度复用同一实现）

```csharp
public sealed record AssistantSyncLimits(int Publish = 8, int Pull = 50, int Download = 4, int Receipts = 12,
    int ReceiptWindowDays = 7, int RetryDelaySeconds = 30)
{ public static AssistantSyncLimits Default { get; } }

public sealed record AssistantSyncResult(int Published, int Failed, int Received, int Downloaded,
    int ReceiptsWritten, int ReceiptsRead, bool HasMore, string? Message,
    int ReceiptsChecked = 0, TimeSpan? RetryAfter = null);

public sealed class AssistantSync
{
    public AssistantSync(AssistantStore store, OpenListClient client, AssistantSyncLimits? limits = null);
    public Task<AssistantSyncResult> SyncAsync(AssistantIdentity identity, CancellationToken token);
    public Task<AssistantOpenTarget> EnsureLocalAsync(AssistantIdentity identity, string itemId, CancellationToken token);
}
```

`assistant.sync`（前台一次）和后台调度都必须调用同一个 `SyncAsync`。**并发安全在 store 上**：
同一 `AssistantStore` 的所有 `AssistantSync` 实例共用一把同步锁（M4 每轮 `new AssistantSync(...)` 也成立），
同步的**每一个**权威状态迁移与 `KnownRemoteIds` 记账都在 store 事务里完成（不在锁外改内存快照），
因此同步期间并发 `EnqueueAsync`/取消不会丢新消息、不会复活 `cancelled`、不会枚举崩，
写失败时也不会留下只存在于内存的条目。跨进程不在保证范围（仍按“单进程单实例”）。
取消（`OperationCanceledException`，仅在 `token` 真正取消时）会把在途条目还原为 `queued`/`stored`，
但**只在该条目仍是本次操作的 `sending`/`downloading` 时**：用户取消、直传升级成 `available`、
或真实回执带来的 `delivered` 都会被保留，不会被迟到的取消回滚。
一次同步最多发布 `Publish` 条、拉取 `Pull` 条 manifest、下载 `Download` 个附件、检查并写入各 `Receipts` 条回执。步骤：

1. 发布本机 `queued`/`failed` 待发条目：成功 → `stored`，失败 → `failed` + `Error` + `Attempts++`，
   逐条事务落盘；按 `Attempts` 升序再按时间升序，坏条目不会饿死新条目；遇到连接类错误立即停止本轮，
   并且**这一轮不再发起任何后续中转请求**（回执检查、清单拉取、下载、回执写入全部跳过），
   只保留不需要网络的本地记账（例如文本转 `available`）。
2. 为本机已发布条目补真实回执：目标发送拿到 `TargetDeviceId` 的回执后 → `delivered`；发给自己保持
   `stored`（=“已同步到中转”），回执只作展示。**回执检查是有界公平轮转**：没有任何回执的条目
   （任意年龄）优先，其余在 7 天窗口内按 `ReceiptCheckedAt` 最久未查优先，单轮不超过 `Receipts` 条，
   检查过的条目记 `ReceiptCheckedAt`，所以 25 条以上的历史也会在多轮内有界地全部拿到真实回执。
3. 拉取未知 manifest：**按 id 去重**，自己发的条目不会变成重复消息；文本条目立即 `available`
   （不下载 payload）；`TargetDeviceId` 为空或等于本机时下载附件，原子保存成功后才 → `available`；
   目标是别的设备则保持 `stored`，不下载、不写回执。
4. 本机 `available` 且 `ReceiptAt == null` 的**他人**条目写入本机回执（保存成功之后才写，
   失败只记 `Error` 并保持 `available`，下轮重试）。自己发的条目不写回执。
5. **`HasMore` 只在中转健康、且本轮确有进展时才为 true**，表示“还有立即可做的工作”（有发布/接收/下载/回执
   进展，或还有下一页，或还有未发布的排队条目）。已失败条目等下一轮；中转不可达或不健康时
   `HasMore=false` 且 `RetryAfter` 给出受控间隔；一页 manifest 因发布未完成而无法推进时也会
   `HasMore=false`（那些 id 仍可再次抓取，晚到的 manifest 不会丢），避免调度空转。
   `HasMore=false` 且 `RetryAfter=null` 表示本轮把能做的都做了，按常规间隔再来即可。

`EnsureLocalAsync` 供 `assistant.open`：本地已有内容直接返回；文本返回 `Text`；附件缺失时先下载
（用户删掉本地文件后再次打开会重新下载），失败返回 `NeedsDownload=true` 并把条目置 `failed`。

## 5. 状态机（M4 必须照此显示）

| 状态 | 含义 | 谁进入 |
| --- | --- | --- |
| `queued` | 已持久保存，等发布 | 本机入队 |
| `sending` | 正在 PUT | 本机发布中 |
| `stored` | 中转已保存（发给自己即“已同步”）；他人条目=中转有、本机未落地 | 发布成功 / 拉取到 |
| `downloading` | 正在下载附件 | 本机接收中 |
| `available` | 本机已有可打开内容（文本不需要下载） | 下载/取文本成功 |
| `delivered` | 目标设备已写回执（目标发送的终态） | 收到目标回执 |
| `failed` | 就地失败，下一轮同步自动重试 | 发布/下载出错 |
| `cancelled` | 用户取消，不再重试 | `TryCancel` |

- 本机发送的条目**永远不会**变 `available`：本地有副本不等于跨设备同步。
- 没有网盘授权时待发条目停在 `queued`，不显示成已同步。
- `TryCancel` 仅允许 `queued|sending|stored|failed|downloading` 且 `Receipts` 为空；
  `delivered`/`available` 或已有回执时返回 false（“已确认送达不能改成取消成功”）。
  取消只改状态，不删除 payload 副本；已发布到中转的内容无法撤回。
- 回执只表达“该设备已保存文件 / 已取到文本”，任何字段都不表示已阅读。
- 拉取不会覆盖本地 `cancelled` 条目，也不会用远端内容改写已存在的本地条目内容。

## 6. M4 接线要点

- 目录建议：`AssistantStore(<模块数据目录>/assistant)`；`OpenListClient` 沿用现有凭据与 URL 校验，
  模块与后台调度共用同一个 store 与 client 实例（`AssistantSync` 可以每轮新建，锁在 store 上）。
- `assistant.send`：`EnqueueAsync` 返回后才回 `{accepted:true,itemIds}`；异常即未接受。
  M3 一次 compose 同时给文字和附件时直接用 `AssistantDraft.ForContent(text, paths)`，
  得到的是多条 itemId（1 条文本 + 每个附件 1 条）。
- `assistant.open`：先 `AssistantContent.OpenTarget(item)`，`NeedsDownload` 时调
  `EnsureLocalAsync`，再返回 `path`/`text`。文本直接返回，不下载。
- `assistant.inspect`：读缓存（`LoadAsync` 或内存 `AssistantState`），不联网。
- `assistant.retry`：用 `MutateAsync` 置 `State=Queued`、`Error=null`、`Attempts=0`，复用原 `itemId`，
  再触发一次 `SyncAsync`。
- `assistant.cancel`：**必须** `MutateAsync(s => s.TryCancel(itemId))`（false 即不能取消）——
  直接在内存对象上调用不会落盘；同步中的在途条目会看到 `cancelled` 并停止把它标成成功。
- 后台调度：`SyncAsync` 返回后看 `HasMore`（立即续一轮）与 `RetryAfter`（至少等这么久，
  或等网络恢复事件），不要在中转不可达时紧密循环。
- 事件 `file-transfer.assistant.changed` 由 M4 在状态变化后发布；F3 不发事件、不碰 Surface。

### 直传通道（M4 的 AssistantWire v3）与助手时间线的接线

- 接收端：文件已原子保存后 `store.AdoptAsync(identity, new AssistantItem{ Id=帧 ItemId, Kind, Name, Size,
  CreatedAt, SenderDeviceId=对端, SenderName, TargetDeviceId=本机, LocalPath=已保存路径 }, token)`，
  再触发（或等待）一次 `SyncAsync` 让本机回执写到中转。重复帧因 id 相同被忽略；
  若该 id 已先从中转清单认识（`stored`、没有本地文件），`AdoptAsync` 会升级为 `available` 并写入
  `LocalPath`，所以直传加速对已知条目同样生效；`cancelled` 不复活。
  注意：`AdoptAsync` 的 `LocalPath` 必须是**保留给用户的 durable 原件**；对外发布/分享时另给副本，
  不要把 publish 用的临时文件传进来（M4 侧已在处理）。
- 发送端：直传得到 `ItemReply{State="delivered"}` 后，用 `MutateAsync` 把本机条目置 `Delivered` 并补一条
  `AssistantReceipt{DeviceId=目标设备}`——`delivered` 必须来自目标设备真实保存，不能凭本地发送成功自报。


## V3 conversation grouping and isolated drafts

Every new `AssistantItem` persists its origin in `provenance`: `local-shared`, `local-device`,
`shared-relay`, `paired-inbox`, `direct-shared`, or `direct-device`. Shared records also persist
`conversationId`, taken from the authenticated source namespace or the direct frame. A message
never changes its conversation when the local device later joins another shared conversation.
Missing legacy provenance remains `history`; sender and target alone do not establish a shared
message's original scope. No migration removes messages, files, or resends ambiguous old work.

`assistant.inspect.items[].conversationKey` is `shared:<source conversationId>`, `device:<peer id>`,
or `history`. `identity` includes `conversationId` and the current shared `conversationKey`.
`members` contains actual confirmed shared members, with `id`, `name`, `address`,
`canPrivateMessage`, and `requiresPairing`. Ordinary pairing and shared membership remain separate.
A member without ordinary pairing must pair before starting a private conversation.

Private sends use their paired deposit inbox and directed transport. `AssistantSync` excludes
private records from shared manifest/payload publication, receipt reads/writes, and downloads even
when a caller supplies an unrestricted `PublishFilter`. Direct frames append optional `scope`
(`shared` or `device`) without changing framing or protocol version. Legacy frames are accepted;
a targeted frame without this evidence stays in history. A private send never substitutes the
shared conversation credential for a pairing token. Legacy remembered direct contacts without a
token can still use the existing receiver confirmation flow.

`assistant.preferences.inspect` returns `conversationKey`, `drafts` keyed by conversation, and the
original flat fields for the active draft. Each draft carries `draftText`, `attachmentPaths`,
`missingAttachments`, `targetDeviceId`, `targetName`, `targetUsable`, `savedAt`, `scrollOffset`,
and `lastReadAt`. `preferences.update` with `conversationKey` updates only that draft and selects it;
missing fields retain their value, explicit null clears nullable fields. `scrollOffset` must be finite
and nonnegative; `lastReadAt` is an ISO timestamp or null. Invalid updates leave every draft intact.
Requests without a key retain the old flat semantics. The former single draft migrates using its
saved target or original shared identity before an identity change. Drafts never grant permission.

`assistant.send` also accepts optional `conversationKey`; it must agree with any `targetDeviceId`.
History is read-only and a different shared namespace requires reconnecting before sending.

## Private inbox relay routing

The default private inbox registers the same locally persisted owner/deposit identity independently
on fixed `tail` and `public` services. Each owner loop has its own revision and cancellation scope;
having no local Tailscale IP does not stop either loop. The trusted Tail endpoint is
`http://mpt-relay.tail.lixinrui000.cn:80`; no user-supplied plaintext host is accepted.

Outgoing items persist `depositRoutes[]` (`relayId`, `attemptedAt`, `storedAt`) before uploading.
Tail is attempted first. Network failures and retryable server errors permit public fallback;
401/403 and message conflicts do not. A receipt probe recognizes an already stored item after a
lost response, avoiding a second payload upload. A Tail copy that remains unconfirmed for 30 seconds
may gain a public copy under the same item id. The scheduler rechecks original-route receipts before
copying, and a real saved receipt ends the private delivery task. Restart keeps all route metadata.

Incoming items persist `sourceRelay`. Two concurrent sources for one id serialize local adoption,
reuse the saved payload, and acknowledge each source without duplicate downloads. Route health is
reported individually in `inbox.routes`; one unavailable route cannot hide another working route.

A custom WebDAV configuration disables both built-in private inbox transports and keeps private
direct delivery; the UI receives a clear reason when no direct delivery succeeds. Missing custom
credentials never select the public shared store implicitly. Shared conversations continue using
their existing public/custom transport; this private routing change does not implement shared
locator publication or shared Tail-first delivery.
