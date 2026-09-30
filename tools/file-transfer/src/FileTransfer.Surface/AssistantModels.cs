using System.Text.Json.Nodes;

namespace FileTransfer.Surface;

/// <summary>
/// What one entry in the shared conversation is. It mirrors the module contract exactly
/// (<c>docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md</c>): the page renders what the module reports and
/// never invents a state of its own.
/// </summary>
internal enum AssistantItemKind
{
    Text,
    Image,
    File
}

/// <summary>
/// Where one entry is in its life. <c>Stored</c> only means the relay saved it; <c>Delivered</c>
/// requires a receipt from a device that actually saved the file, so the two must never be shown as
/// the same thing.
/// </summary>
internal enum AssistantItemState
{
    Queued,
    Sending,
    Stored,
    Delivered,
    Downloading,
    Available,
    Failed,
    Cancelled
}

/// <summary>
/// One conversation entry: text, an image or a file, written by this device or received from another.
/// </summary>
internal sealed record AssistantItem(
    string Id,
    AssistantItemKind Kind,
    string? Text,
    string? Name,
    long Size,
    DateTimeOffset? CreatedAt,
    string? SenderDeviceId,
    string SenderName,
    string? TargetDeviceId,
    AssistantItemState State,
    long BytesDone,
    string? LocalPath,
    string? Error,
    IReadOnlyList<AssistantReceipt> Receipts)
{
    public string ConversationKey { get; init; } = "";
    public string? TransportRoute { get; init; }
    public bool IsIncoming { get; init; }

    // A route describes this payload's actual transfer, not the current device configuration.
    // Older modules and future route codes keep the existing status without guessing a provider.
    public string TransportRouteLabel => IsFile ? TransportRoute switch
    {
        "cloud-quark" => "夸克网盘",
        "cloud-baidu" => "百度网盘",
        "cloud" => "网盘中转",
        "direct" => "设备直传",
        "tail-relay" => "Tailscale 中转",
        "public-relay" => "公网中转",
        "webdav" => "WebDAV 中转",
        _ => ""
    } : "";

    public string TransferStateText => TransportRouteLabel is { Length: > 0 } route
        ? route + " · " + StateText : StateText;

    public bool IsText => Kind == AssistantItemKind.Text;

    public bool IsImage => Kind == AssistantItemKind.Image;

    public bool IsFile => Kind is AssistantItemKind.File or AssistantItemKind.Image;

    /// <summary>The name shown on a card: text entries have no file name.</summary>
    public string DisplayName => Name is { Length: > 0 } name ? name : IsText ? Text ?? "" : "文件";

    public string SizeText => IsFile && Size > 0 ? FormatSize(Size) : "";

    /// <summary>True while the module is still working on this entry, so the row shows progress.</summary>
    public bool InFlight => State is AssistantItemState.Sending or AssistantItemState.Downloading or AssistantItemState.Queued;

    /// <summary>True when the entry finished with a problem, so the row offers a retry.</summary>
    public bool CanRetry => State is AssistantItemState.Failed;

    /// <summary>True while the entry has not finished, so the row offers cancel.</summary>
    public bool CanCancel => Receipts.Count == 0 && State is AssistantItemState.Queued or AssistantItemState.Sending
        or AssistantItemState.Downloading or AssistantItemState.Failed;

    // The module currently supplies an error string, not a typed failure code. Match known module
    // messages explicitly; never display arbitrary server detail, local paths or exception stacks.
    public string ErrorExplanation => IsIncoming ? Error switch
    {
        null or "" => "",
        "操作过于频繁（HTTP 429），请稍后重试。" => "接收请求过于频繁，请稍后重试接收。",
        "中转服务暂时不可用（HTTP 503），请稍后重试。" => "中转服务暂时不可用，请稍后重试接收。",
        _ => "接收未完成，可以重试接收或取消此条接收。若再次失败，请在连接诊断中检查服务配置。"
    } : SendErrorExplanation;

    private string SendErrorExplanation => Error switch
    {
        null or "" => "",
        "配对码里的投递密钥与服务器不一致，请让收件设备重新出示配对码。"
            or "该收件箱已经用另一个投递密钥注册；旧配对码仍然有效，请让收件设备重新出示配对码。"
            or "对方是旧版配对码，没有公网投递权限；请让对方重新扫码后再发送。"
            => "这台设备的配对信息需要更新。请让对方重新出示连接码，再添加这台设备后重试。",
        "文件超过中转单文件上限，无法投递。"
            => "文件超过单文件大小上限。请压缩或拆分文件后重新发送。",
        "中转空间不足，请先在收件设备上清理已接收的文件。"
            => "中转空间不足。请在收件设备上清理已接收的文件，然后重试。",
        "待发副本不存在。"
            => "待发送的文件副本已丢失。请取消此条记录，重新选择原文件发送。",
        "投递密钥只能投递和查询自己那一条回执，不能列出、读取内容或写回执。"
            or "该操作需要投递密钥，owner 凭据不能用于投递。"
            => "当前配对信息没有此次发送所需的权限。请重新添加收件设备后重试。",
        "操作过于频繁（HTTP 429），请稍后重试。"
            => "发送过于频繁，正在等待重试。也可以取消此条发送。",
        "收件端还没有注册这个收件箱（HTTP 503），稍后会自动重试。"
            => "对方尚未准备好接收。请让对方打开文件助手，本条会自动重试。",
        "中转服务暂时不可用（HTTP 503），请稍后重试。"
            => "中转服务暂时不可用，内容已保留，稍后会自动重试。",
        "同一条目 id 已经存在且内容不同，请换一个新的 itemId 重试。"
            => "此条记录与已暂存的内容冲突。请取消此条记录，重新选择内容发送。",
        _ => "发送未完成，内容仍保留。可以重试或取消此条发送；若再次失败，请在连接诊断中检查服务配置。"
    };

    /// <summary>True when there is local content to open on this device.</summary>
    public bool CanOpen => LocalPath is { Length: > 0 };

    /// <summary>True when the module can fetch the payload on demand rather than open a local path.</summary>
    public bool NeedsDownload => IsFile && LocalPath is not { Length: > 0 } &&
        State is AssistantItemState.Available or AssistantItemState.Stored or AssistantItemState.Delivered;

    public double Progress => Size > 0 ? Math.Clamp(BytesDone * 100d / Size, 0, 100) : 0;

    /// <summary>
    /// The state wording. Relay-stored is "已同步到中转" and never "已送达": only a device receipt
    /// proves delivery, and this method is the single place that decides the wording.
    /// </summary>
    public string StateText => State switch
    {
        AssistantItemState.Queued => IsIncoming ? "等待接收" : "等待发送",
        AssistantItemState.Sending => IsIncoming
            ? Size > 0 ? $"接收中 {Progress:F0}%" : "接收中"
            : Size > 0 ? $"发送中 {Progress:F0}%" : "发送中",
        AssistantItemState.Stored => IsIncoming ? "等待接收" : TargetDeviceId is { Length: > 0 } ? "已暂存，等待接收" : "已同步",
        AssistantItemState.Delivered => "已送达",
        AssistantItemState.Downloading => Size > 0 ? $"接收中 {Progress:F0}%" : "接收中",
        AssistantItemState.Available => IsText || CanOpen ? "已接收" : "待下载",
        AssistantItemState.Failed => IsIncoming ? "接收失败，可重试" : "发送失败，可重试",
        AssistantItemState.Cancelled => "已取消",
        _ => ""
    };

    /// <summary>Which devices reported saving this entry. Empty means nothing confirmed it yet.</summary>
    public string ReceiptText => Receipts.Count == 0
        ? ""
        : string.Join("、", Receipts.Select(receipt => receipt.DeviceName is { Length: > 0 } name ? name : receipt.DeviceId));

    public static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576d:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:F0} KB" : $"{bytes} B";
}

/// <summary>One device that reported saving an entry. Optimistic local writes are never receipts.</summary>
internal sealed record AssistantReceipt(string DeviceId, string DeviceName, DateTimeOffset? At);

/// <summary>This device's identity inside the shared conversation.</summary>
internal sealed record AssistantIdentity(string Id, string Name, bool Linked)
{
    public string ConversationKey { get; init; } = "";
    public string DisplayName => Name is { Length: > 0 } name ? name : "本机";
}

/// <summary>
/// A device that could receive a file. An address is deliberately not part of the presentation: the
/// normal UI never shows IPs, and reachability comes from the module's own answer.
/// </summary>
internal sealed record AssistantDevice(
    string DeviceId,
    string Name,
    string Address,
    string Platform,
    bool Paired,
    bool Available)
{
    public bool CanPrivateMessage { get; init; } = true;
    public bool RequiresPairing { get; init; }

    public string PlatformText => Platform switch
    {
        "windows" => "Windows",
        "macos" => "macOS",
        "android" => "Android",
        "linux" => "Linux",
        _ => Platform is { Length: > 0 } ? Platform : "未知平台"
    };

    /// <summary>Only the module's answer turns a discovered device into a reachable one.</summary>
    public string StateText => Available ? "当前可连接" : Paired ? "已配对" : "需要对方确认";
}

/// <summary>One inbound transfer waiting for this device to accept or reject it.</summary>
internal sealed record AssistantPendingRequest(
    string RequestId,
    string DeviceId,
    string Name,
    IReadOnlyList<string> ItemNames,
    DateTimeOffset? ExpiresAt)
{
    public string ItemText => ItemNames.Count switch
    {
        0 => "一个文件",
        1 => ItemNames[0],
        _ => $"{ItemNames.Count} 个文件"
    };
}

/// <summary>
/// Relay health for the conversation. <c>Unknown</c> means "never checked" and must not be rendered
/// as "the relay is down".
/// </summary>
internal enum AssistantRelayState
{
    Unconfigured,
    Unknown,
    Available,
    Unavailable
}

/// <summary>
/// How the last device discovery ended, so the picker can explain an empty result truthfully.
/// <c>partial</c> means the bounded window expired and the list is the real completed part;
/// <c>unsupported</c> means this platform has no discovery source at all, which must not be shown as
/// "no devices found".
/// </summary>
internal enum AssistantDiscoveryState
{
    Idle,
    Searching,
    Completed,
    Partial,
    Unsupported,
    Failed
}

/// <summary>Everything the assistant page renders, derived only from module answers and events.</summary>
internal sealed record AssistantSnapshot(
    AssistantIdentity Identity,
    IReadOnlyList<AssistantItem> Items,
    IReadOnlyList<AssistantPendingRequest> PendingRequests,
    AssistantRelayState Relay,
    string RelayMessage,
    bool Receiving,
    IReadOnlyList<AssistantDevice> Devices,
    AssistantDiscoveryState Discovery,
    string Status,
    bool Busy,
    string DiscoveryMessage = "")
{
    public IReadOnlyList<AssistantDevice> Members { get; init; } = [];

    public static readonly AssistantSnapshot Empty = new(
        new AssistantIdentity("", "", false), [], [], AssistantRelayState.Unknown, "", false, [],
        AssistantDiscoveryState.Idle, "", false);

    /// <summary>True while a bounded discovery pass is running.</summary>
    public bool IsDiscovering => Discovery == AssistantDiscoveryState.Searching;

    /// <summary>The conversation's own name: this is "send to myself", not a device picker.</summary>
    public string Title => "文件助手";

    /// <summary>True when nothing has been written yet, so the page shows an actionable empty state.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Entries this device still has to act on, used for the pending badge.</summary>
    public int FailedCount => Items.Count(item => item.State == AssistantItemState.Failed);

    public int QueuedCount => Items.Count(item => item.State == AssistantItemState.Queued);

    public bool HasPendingRequest => PendingRequests.Count > 0;

    /// <summary>True when the relay is known to be unusable, so the page can say why sync is paused.</summary>
    public bool RelayBlocked => Relay is AssistantRelayState.Unconfigured or AssistantRelayState.Unavailable;

    public string RelayText => Relay switch
    {
        AssistantRelayState.Unconfigured => "同步服务尚未就绪，待发内容会保存在本机。",
        AssistantRelayState.Available => "同步服务可用。",
        AssistantRelayState.Unavailable => RelayMessage is { Length: > 0 } message ? "中转暂时不可用：" + message : "中转暂时不可用，恢复后会自动继续。",
        _ => "等待同步。"
    };

    /// <summary>Only a checked-and-available relay may claim that other devices will see an entry.</summary>
    public bool CanSync => Relay == AssistantRelayState.Available;
}
