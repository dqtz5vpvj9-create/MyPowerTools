using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>
/// The file-assistant conversation: text, images and files written into one session shared by the
/// user's own devices.
///
/// This class is the only place the <c>file-transfer.assistant.*</c> JSON contract is read and
/// written. It owns no controls, so the page can be driven headlessly and the same state backs the
/// phone and desktop presentations. Every value comes from a module answer or a module event: there
/// is no local success path, no optimistic "sent", and no timers polling the session.
///
/// The contract is fixed in <c>docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md</c> and implemented by the
/// file-transfer module. The page never falls back to simulating a transfer: an answer it cannot get
/// is reported as a failure, not as a local success.
/// </summary>
internal sealed class AssistantCore : IDisposable
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private IDisposable? _events;
    private AssistantSnapshot _snapshot = AssistantSnapshot.Empty;
    private bool _syncQueued;
    private bool _capabilityMissing;
    private CancellationTokenSource? _discovery;

    /// <summary>
    /// True while the device picker is open. It is set by the page, so a discovery answer that lands
    /// after the user closed the sheet is discarded instead of publishing a stale list or an error.
    /// </summary>
    public bool PickerOpen
    {
        get => _pickerOpen;
        set
        {
            _pickerOpen = value;
            if (!value) CancelDiscovery();
        }
    }

    private volatile bool _pickerOpen;

    public AssistantCore(MptAvaloniaSurfaceContext context) => _context = context;

    /// <summary>Raised on the UI thread after every state change; subscribers re-read <see cref="Snapshot"/>.</summary>
    public event Action? Changed;

    public AssistantSnapshot Snapshot => _snapshot;

    /// <summary>
    /// True when the installed module does not implement <c>assistant.*</c>. The page then offers the
    /// advanced transfer form instead of pretending the conversation works.
    /// </summary>
    public bool IsUnsupported => _capabilityMissing;

    // ---- lifetime ---------------------------------------------------------------------------

    public void Attach()
    {
        _events ??= _context.SubscribeEvents?.Invoke(OnEvent);
    }

    /// <summary>
    /// Leaves the page: drops the event subscription and cancels a discovery pass this page started.
    /// A send the module already accepted is intentionally left alone — closing a page is not a cancel.
    /// </summary>
    public void Detach()
    {
        _events?.Dispose();
        _events = null;
        CancelDiscovery();
    }

    /// <summary>Ends a bounded discovery run. Safe to call when none is running.</summary>
    public void CancelDiscovery()
    {
        var running = Interlocked.Exchange(ref _discovery, null);
        if (running is null) return;
        try { running.Cancel(); } catch (ObjectDisposedException) { }
        running.Dispose();
    }

    public void Dispose()
    {
        Detach();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- contract ---------------------------------------------------------------------------

    /// <summary>Calls one assistant command and parses its JSON answer.</summary>
    private async Task<JsonNode> CallAsync(string command, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var response = await _context.ExecuteCommandAsync("file-transfer.assistant." + command, args, linked.Token);
        if (!response.Success)
        {
            var message = response.Error?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? response.Output : message);
        }
        if (string.IsNullOrWhiteSpace(response.Output)) return new JsonObject();
        try { return JsonNode.Parse(response.Output) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>Reads the cached session. It must not make the module go online.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var answer = await CallAsync("inspect");
            _capabilityMissing = false;
            ApplyInspect(answer);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsMissingCapability(ex))
            {
                _capabilityMissing = true;
                Publish(_snapshot with { Status = "当前文件互传模块还没有会话功能，可以先用高级设置里的经典传输。" });
                return;
            }
            Publish(_snapshot with { Status = ex.Message });
        }
    }

    /// <summary>
    /// One pull of the session: new entries, resumed pending sends and real receipts. It is a single
    /// cancellable call, never a poll; the page calls it on open, on resume and after a user action.
    /// </summary>
    public async Task SyncAsync()
    {
        try
        {
            var answer = await CallAsync("sync");
            _capabilityMissing = false;
            // Sync answers with the same shape as inspect plus a status, so one parser covers both.
            ApplyInspect(answer);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (IsMissingCapability(ex))
        {
            _capabilityMissing = true;
            Publish(_snapshot with { Status = "当前文件互传模块还没有会话功能。" });
        }
        catch (Exception ex)
        {
            // A failed sync is not a failed conversation: the cached entries stay readable and the
            // status explains that the network side did not answer.
            Publish(_snapshot with { Status = "同步失败：" + ex.Message });
        }
    }

    // ---- writing ----------------------------------------------------------------------------

    /// <summary>
    /// Writes into the conversation. Without a target this is "send to myself": no device has to be
    /// chosen first. The module persists before it answers, so <c>accepted</c> is what the page shows.
    /// </summary>
    public async Task<IReadOnlyList<string>> SendAsync(
        string? text = null,
        IReadOnlyList<string>? paths = null,
        string? targetDeviceId = null,
        CancellationToken cancellationToken = default)
    {
        text = text?.Trim();
        var args = new JsonObject();
        if (text is { Length: > 0 }) args["text"] = text;
        if (paths is { Count: > 0 })
            args["paths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray());
        if (targetDeviceId is { Length: > 0 }) args["targetDeviceId"] = targetDeviceId;
        if (args.Count == 0) throw new InvalidOperationException("没有要发送的内容。");

        var answer = await CallAsync("send", args, cancellationToken);
        var accepted = AssistantCore.Flag(answer, "accepted");
        var ids = (answer["itemIds"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "").Where(id => id.Length > 0).ToArray() ?? [];
        if (!accepted)
        {
            // The module refused, so nothing was persisted and the page must say so instead of
            // showing an entry that will never appear.
            Publish(_snapshot with { Status = "发送没有被接受，请重试。" });
            return [];
        }
        await RefreshAsync();
        return ids;
    }

    /// <summary>Retries one failed entry under its original id, so no duplicate message appears.</summary>
    public async Task RetryAsync(string itemId)
    {
        await CallAsync("retry", new JsonObject { ["itemId"] = itemId });
        await RefreshAsync();
    }

    /// <summary>
    /// Cancels one unfinished entry. The module decides whether that is still possible; a delivered
    /// entry cannot be turned into a cancelled one.
    /// </summary>
    public async Task CancelAsync(string itemId)
    {
        await CallAsync("cancel", new JsonObject { ["itemId"] = itemId });
        await RefreshAsync();
    }

    /// <summary>
    /// Resolves the content of one entry for opening. The module downloads a missing file first, so
    /// the returned path is real local content rather than a promise.
    /// </summary>
    public async Task<AssistantOpenResult> OpenAsync(string itemId)
    {
        var answer = await CallAsync("open", new JsonObject { ["itemId"] = itemId });
        var result = new AssistantOpenResult(
            Str(answer, "path"),
            Str(answer, "text"),
            Flag(answer, "needsDownload"));
        await RefreshAsync();
        return result;
    }

    // ---- devices ----------------------------------------------------------------------------

    /// <summary>
    /// One bounded discovery pass. The answer's own <c>discoveryState</c> decides what the page says:
    /// <c>partial</c> is a real but incomplete list and <c>unsupported</c> means this platform has no
    /// discovery source, neither of which may be rendered as "no devices found".
    ///
    /// A cancelled run publishes nothing even when the host ignores the token and answers anyway, and
    /// a run whose picker has already closed does not surface a late error.
    /// </summary>
    public async Task DiscoverAsync()
    {
        CancelDiscovery();
        var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _discovery = run;
        var token = run.Token;
        Publish(_snapshot with { Discovery = AssistantDiscoveryState.Searching, Status = "正在查找你的设备…" });
        try
        {
            var answer = await CallAsync("devices", null, token);
            // The host may ignore the token. A run that is no longer the current one, or whose picker
            // has closed, must not overwrite a newer list or reopen a closed panel with its answer.
            if (!IsCurrentRun(run) || !_pickerOpen) return;

            var devices = ReadDevices(answer["devices"] as JsonArray);
            var state = Str(answer, "discoveryState");
            var message = Str(answer, "message");
            var (discovery, status) = state switch
            {
                "partial" => (AssistantDiscoveryState.Partial,
                    message is { Length: > 0 } ? message : devices.Count == 0
                        ? "查找时间用完了，还没有设备应答。稍后可以再试一次。"
                        : $"已找到 {devices.Count} 台可用设备，还有设备没有应答。"),
                "unsupported" => (AssistantDiscoveryState.Unsupported,
                    message is { Length: > 0 } ? message : "这台设备当前无法自动查找设备，请用连接码添加。"),
                _ => (AssistantDiscoveryState.Completed,
                    message is { Length: > 0 } ? message
                        : devices.Count == 0 ? "没有找到可用的设备。" : $"找到 {devices.Count} 台设备。")
            };
            Publish(_snapshot with { Devices = devices, Discovery = discovery, Status = status });
        }
        catch (OperationCanceledException)
        {
            // A cancelled run reports nothing: the sheet is closed or a newer run already started.
        }
        catch (Exception ex) when (IsMissingCapability(ex))
        {
            if (IsCurrentRun(run) && _pickerOpen)
            {
                _capabilityMissing = true;
                Publish(_snapshot with { Discovery = AssistantDiscoveryState.Unsupported, Status = "当前文件互传模块还没有设备发现功能。" });
            }
        }
        catch (Exception ex)
        {
            // A failure that lands after the picker closed is noise; the user has moved on.
            if (IsCurrentRun(run) && _pickerOpen)
                Publish(_snapshot with { Discovery = AssistantDiscoveryState.Failed, Status = "查找设备失败：" + ex.Message });
        }
        finally
        {
            Interlocked.CompareExchange(ref _discovery, null, run);
            run.Dispose();
        }
    }

    /// <summary>True while this run is still the one the page is waiting for.</summary>
    private bool IsCurrentRun(CancellationTokenSource run) => ReferenceEquals(Volatile.Read(ref _discovery), run);

    /// <summary>Answers one inbound request. Refusing or letting it time out stores no credential.</summary>
    public async Task RespondAsync(string requestId, bool accept, bool remember)
    {
        await CallAsync("receive.respond", new JsonObject
        {
            ["requestId"] = requestId,
            ["accept"] = accept,
            ["remember"] = remember
        });
        await RefreshAsync();
        Publish(_snapshot with { Status = accept ? "已接受，正在接收文件。" : "已拒绝这次接收。" });
    }

    // ---- linking ----------------------------------------------------------------------------

    /// <summary>Full connection code for "connect my devices". Only fetched when the user asks.</summary>
    public async Task<string> ExportLinkAsync()
    {
        var answer = await CallAsync("link.export");
        return Str(answer, "code") ?? throw new InvalidOperationException("没有拿到连接码，请稍后重试。");
    }

    /// <summary>Name and scope of a code, decoded without storing or joining anything.</summary>
    public async Task<string> PreviewLinkAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("请先粘贴连接码。");
        var answer = await CallAsync("link.preview", new JsonObject { ["code"] = code });
        return DescribeLinkPreview(answer);
    }

    /// <summary>Joins this device to the conversation the code belongs to.</summary>
    public async Task ImportLinkAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidOperationException("请先粘贴连接码。");
        await CallAsync("link.import", new JsonObject { ["code"] = code });
        await RefreshAsync();
        Publish(_snapshot with { Status = "已加入你的设备会话。" });
    }

    private static string DescribeLinkPreview(JsonNode answer)
    {
        var name = Str(answer, "name");
        var scope = Str(answer, "scope");
        var devices = Number(answer, "deviceCount");
        var parts = new List<string>();
        if (name is { Length: > 0 }) parts.Add("设备：" + name);
        if (scope is { Length: > 0 }) parts.Add("范围：" + scope);
        if (devices > 0) parts.Add($"包含 {devices} 台设备");
        // The preview never carries the credential itself, which is why this text is safe to render.
        return parts.Count == 0 ? "连接码有效。" : string.Join(" · ", parts);
    }

    // ---- answers ----------------------------------------------------------------------------

    private void ApplyInspect(JsonNode answer)
    {
        var identity = answer["identity"] as JsonObject;
        var relay = answer["relay"] as JsonObject;
        var next = _snapshot with
        {
            Identity = new AssistantIdentity(
                Str(identity, "id") ?? "",
                Str(identity, "name") ?? "",
                Flag(identity, "linked")),
            Items = ReadItems(answer["items"] as JsonArray),
            PendingRequests = ReadRequests(answer["pendingRequests"] as JsonArray),
            Relay = ReadRelayState(Str(relay, "state")),
            RelayMessage = Str(relay, "message") ?? "",
            Receiving = Flag(answer, "receiving"),
            // inspect does not report discovery; keep the last real result instead of clearing it.
            Devices = _snapshot.Devices,
            Discovery = _snapshot.Discovery,
            Status = Str(answer, "message") is { Length: > 0 } message ? message : ""
        };
        Publish(next);
    }

    private static IReadOnlyList<AssistantItem> ReadItems(JsonArray? array)
    {
        if (array is null) return [];
        var items = new List<AssistantItem>();
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var id = Str(item, "id");
            if (id is not { Length: > 0 }) continue;
            items.Add(new AssistantItem(
                id,
                ReadKind(Str(item, "kind")),
                Str(item, "text"),
                Str(item, "name"),
                Number(item, "size"),
                DateTimeOffset.TryParse(Str(item, "createdAt"), out var created) ? created : null,
                Str(item, "senderDeviceId"),
                Str(item, "senderName") ?? "",
                Str(item, "targetDeviceId"),
                ReadItemState(Str(item, "state")),
                Number(item, "bytesDone"),
                Str(item, "localPath"),
                Str(item, "error"),
                ReadReceipts(item["receipts"] as JsonArray)));
        }
        // The composer and ScrollToEnd are below the thread: append new messages at the bottom.
        // The module returns newest first for its bounded history query, so reverse chronology here.
        // Entries without a time keep their relative module order.
        return items
            .OrderBy(item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static IReadOnlyList<AssistantReceipt> ReadReceipts(JsonArray? array)
    {
        if (array is null) return [];
        var receipts = new List<AssistantReceipt>();
        foreach (var node in array)
        {
            if (node is not JsonObject receipt) continue;
            var deviceId = Str(receipt, "deviceId");
            if (deviceId is not { Length: > 0 }) continue;
            receipts.Add(new AssistantReceipt(
                deviceId,
                Str(receipt, "deviceName") ?? Str(receipt, "name") ?? "",
                DateTimeOffset.TryParse(Str(receipt, "at"), out var at) ? at : null));
        }
        return receipts;
    }

    private static IReadOnlyList<AssistantPendingRequest> ReadRequests(JsonArray? array)
    {
        if (array is null) return [];
        var requests = new List<AssistantPendingRequest>();
        foreach (var node in array)
        {
            if (node is not JsonObject request) continue;
            var requestId = Str(request, "requestId");
            if (requestId is not { Length: > 0 }) continue;
            var names = (request["itemNames"] as JsonArray)?
                .Select(item => item?.GetValue<string>() ?? "")
                .Where(name => name.Length > 0)
                .ToArray() ?? [];
            requests.Add(new AssistantPendingRequest(
                requestId,
                Str(request, "deviceId") ?? "",
                Str(request, "name") ?? "另一台设备",
                names,
                DateTimeOffset.TryParse(Str(request, "expiresAt"), out var expires) ? expires : null));
        }
        return requests;
    }

    private static IReadOnlyList<AssistantDevice> ReadDevices(JsonArray? array)
    {
        if (array is null) return [];
        var devices = new List<AssistantDevice>();
        foreach (var node in array)
        {
            if (node is not JsonObject device) continue;
            var deviceId = Str(device, "deviceId");
            if (deviceId is not { Length: > 0 }) continue;
            devices.Add(new AssistantDevice(
                deviceId,
                Str(device, "name") is { Length: > 0 } name ? name : "未命名设备",
                Str(device, "address") ?? "",
                Str(device, "platform") ?? "",
                Flag(device, "paired"),
                Flag(device, "available")));
        }
        return devices;
    }

    private static AssistantItemKind ReadKind(string? value) => value switch
    {
        "text" => AssistantItemKind.Text,
        "image" => AssistantItemKind.Image,
        _ => AssistantItemKind.File
    };

    private static AssistantItemState ReadItemState(string? value) => value switch
    {
        "queued" => AssistantItemState.Queued,
        "sending" => AssistantItemState.Sending,
        "stored" => AssistantItemState.Stored,
        "delivered" => AssistantItemState.Delivered,
        "downloading" => AssistantItemState.Downloading,
        "available" => AssistantItemState.Available,
        "failed" => AssistantItemState.Failed,
        "cancelled" => AssistantItemState.Cancelled,
        // An unknown state from a newer module is shown as "waiting" rather than guessed as success.
        _ => AssistantItemState.Queued
    };

    private static AssistantRelayState ReadRelayState(string? value) => value switch
    {
        "unconfigured" => AssistantRelayState.Unconfigured,
        "available" => AssistantRelayState.Available,
        "unavailable" => AssistantRelayState.Unavailable,
        _ => AssistantRelayState.Unknown
    };

    // ---- events -----------------------------------------------------------------------------

    private void OnEvent(MptSurfaceEvent e)
    {
        if (e.SourceId != "file-transfer" || e.Type != "file-transfer.assistant.changed") return;
        // One coalesced read per burst: an event is a signal that the session changed, not a payload
        // to merge, so the page re-reads the module's own copy instead of patching a local one.
        if (_syncQueued) return;
        _syncQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            _syncQueued = false;
            await RefreshAsync();
        });
    }

    // ---- helpers ----------------------------------------------------------------------------

    private void Publish(AssistantSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke();
    }

    /// <summary>Publishes on the UI thread; module events can arrive from a HostControl read loop.</summary>
    public void PublishOnUi(AssistantSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) Publish(snapshot);
        else Dispatcher.UIThread.Post(() => Publish(snapshot));
    }

    /// <summary>True when the module rejected the command because it does not implement it.</summary>
    private static bool IsMissingCapability(Exception exception)
    {
        var message = exception.Message;
        return message.Contains("未知命令", StringComparison.Ordinal)
            || message.Contains("不支持的命令", StringComparison.Ordinal)
            || message.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未注册", StringComparison.Ordinal);
    }

    private static string? Str(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static long Number(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
}

/// <summary>What <c>assistant.open</c> resolved for one entry.</summary>
internal sealed record AssistantOpenResult(string? Path, string? Text, bool NeedsDownload)
{
    /// <summary>True when the module handed back a real local path to open.</summary>
    public bool HasPath => Path is { Length: > 0 };

    /// <summary>True when the entry is text, which the page shows instead of opening a file.</summary>
    public bool HasText => Text is { Length: > 0 };
}
