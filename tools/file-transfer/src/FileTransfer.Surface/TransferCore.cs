using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>
/// Phase of the legacy send flow, kept for the advanced page and the desktop form. The primary
/// surface is the file-assistant conversation; this describes the classic pick-files/choose-device
/// transfer that still backs the advanced settings path.
/// </summary>
internal enum TransferPhase
{
    SelectFiles,
    SelectDevice,
    Transferring,
    Failed,
    Succeeded
}

/// <summary>How a transfer reaches the peer. Relay means "stored in the cloud, waiting to be claimed".</summary>
internal enum TransferRoute
{
    Direct,
    Relay
}

/// <summary>
/// Everything the legacy UI is allowed to know, derived only from module responses and module events.
/// No view keeps its own copy, so two presentations cannot disagree about the same transfer.
/// </summary>
internal sealed record TransferSnapshot(
    TransferPhase Phase,
    IReadOnlyList<TransferFile> Files,
    IReadOnlyList<TransferPeer> Peers,
    string? PeerId,
    TransferRoute Route,
    bool Busy,
    bool Cancelled,
    bool Receiving,
    double Progress,
    string ProgressText,
    string Status,
    string? Failure,
    bool RelayConfigured,
    bool RelayRunning,
    string RelayDescription,
    bool OpenListRunning,
    IReadOnlyList<TransferHistoryEntry> History,
    bool? RelayReachable = null,
    string RelayMessage = "",
    TransferBatchSummary? Batch = null)
{
    public long TotalBytes => Files.Sum(file => file.Length);

    public string FileLabel => Files.Count switch
    {
        0 => "未选择文件",
        1 => Files[0].Name,
        _ => $"{Files.Count} 个文件"
    };

    public TransferPeer? Peer => PeerId is null ? null : Peers.FirstOrDefault(peer => peer.DeviceId == PeerId);

    public bool CanSend => !Busy && Files.Count > 0 && PeerId is not null;

    /// <summary>Only a module-reported terminal batch sets this; an address alone never proves reachability.</summary>
    public bool HasResult => Phase is TransferPhase.Succeeded or TransferPhase.Failed;
}

/// <summary>One pending file plus the size read once, so the page never re-stats per frame.</summary>
internal sealed record TransferFile(string Path, string Name, long Length, bool Staged);

/// <summary>
/// One remembered device. <paramref name="State"/> is the module's own reachability answer
/// (<c>unknown</c> / <c>online</c> / <c>offline</c>); an address alone must never be rendered as
/// "online", so an absent state stays <c>unknown</c>.
/// </summary>
internal sealed record TransferPeer(
    string DeviceId,
    string Name,
    string Address,
    string State = "unknown",
    DateTimeOffset? CheckedAt = null,
    string Message = "",
    bool SupportsControl = false)
{
    public bool IsOnline => string.Equals(State, "online", StringComparison.Ordinal);

    public bool IsOffline => string.Equals(State, "offline", StringComparison.Ordinal);

    /// <summary>True once the module has a real check result, so "重新检查" is only offered then.</summary>
    public bool WasChecked => CheckedAt is not null;
}

internal sealed record TransferHistoryEntry(string Name, string State, string Message, string Time, long Done, long Total);

/// <summary>
/// What the module reported for one file. <paramref name="Delivery"/> is its own evidence:
/// <c>direct</c> (the peer acknowledged the committed file), <c>relay-uploaded</c> (the relay accepted
/// the payload and the recipient confirmed nothing), <c>relay-downloaded</c>/<c>local</c> (received
/// here) or empty (nothing was confirmed). The page never infers this from the route the user picked.
/// </summary>
internal sealed record TransferOutcome(string Name, string State, string Delivery, string Peer, string Message)
{
    public bool Cancelled => State == "cancelled";

    public bool Failed => State is "failed" or "cancelled";

    /// <summary>True only when the module reported proof that the peer has the file.</summary>
    public bool Delivered => Delivery is "direct" or "relay-downloaded" or "local";
}

/// <summary>The files one send command is carrying, so a per-file event can be attributed to it.</summary>
internal sealed record ActiveBatch(string Command, IReadOnlyList<string> Paths, TransferRoute Route);

/// <summary>
/// The module's per-file reports for one finished batch. The result card renders its wording from
/// this, so no delivery claim comes from the route the user happened to select.
/// </summary>
internal sealed record TransferBatchSummary(TransferRoute Route, IReadOnlyList<TransferOutcome> Outcomes)
{
    public int Total => Outcomes.Count;

    public int Delivered => Outcomes.Count(outcome => !outcome.Failed && outcome.Delivered);

    public int Uploaded => Outcomes.Count(outcome => !outcome.Failed && outcome.Delivery == "relay-uploaded");

    /// <summary>Completed with no delivery evidence at all: an older module that confirms nothing.</summary>
    public int ConfirmedOnly => Outcomes.Count(outcome => !outcome.Failed && !outcome.Delivered && outcome.Delivery != "relay-uploaded");

    public int FailedCount => Outcomes.Count(outcome => outcome.Failed);

    public bool AllCancelled => FailedCount > 0 && Outcomes.All(outcome => outcome.Cancelled);

    public bool Succeeded => FailedCount == 0;

    public string Peer => Outcomes.FirstOrDefault(outcome => outcome.Peer.Length > 0)?.Peer ?? "";

    /// <summary>
    /// The result heading. It states only what the module confirmed: a relay upload is "uploaded",
    /// never "delivered", and a completion with no delivery evidence is just "completed".
    /// </summary>
    public string Title => AllCancelled
        ? "已取消"
        : FailedCount > 0 ? "没有全部发出去"
        : ConfirmedOnly == Total ? "已完成"
        : Uploaded > 0 && Delivered == 0 ? "已上传，等待领取"
        : Delivered > 0 && Uploaded > 0 ? "部分已送达，部分等待领取"
        : "已送达";

    /// <summary>The honest explanation under the heading, with no promise the sender cannot keep.</summary>
    public string Detail
    {
        get
        {
            if (AllCancelled) return "传输已取消。文件仍在待发送列表里，可以换个设备或直接重试。";
            if (FailedCount > 0)
                return $"共 {Total} 个文件，{Delivered + Uploaded + ConfirmedOnly} 个完成，{FailedCount} 个未完成。可以重试未完成的部分。";
            if (ConfirmedOnly == Total)
                return "当前模块只报告了完成，没有提供对方已收到的确认，因此这里不显示“已送达”。";
            if (Uploaded > 0 && Delivered == 0)
                return $"{Uploaded} 个文件已上传到中转网盘，等待接收设备领取；暂无法确认对方是否已领取。";
            if (Uploaded > 0)
                return $"{Delivered} 个文件已送达，{Uploaded} 个已上传到中转网盘、等待领取；暂无法确认对方是否已领取。";
            return $"{Delivered} 个文件已被接收方确认收到。";
        }
    }

    /// <summary>The status column for one file, again from the module's own evidence.</summary>
    public static string StateText(TransferOutcome outcome) => outcome.State switch
    {
        "failed" => "失败",
        "cancelled" => "已取消",
        _ when outcome.Delivery == "direct" => "已送达",
        _ when outcome.Delivery == "relay-uploaded" => "已上传 · 等待领取",
        _ when outcome.Delivery is "relay-downloaded" or "local" => "已接收",
        _ => "已完成"
    };
}

/// <summary>
/// Legacy file-transfer state and command boundary, used by the desktop form and by the advanced
/// settings page. It owns the staged-copy lifecycle, the module event subscription and the retry
/// record. The file-assistant conversation lives in <see cref="AssistantCore"/>.
/// </summary>
internal sealed class TransferCore : IDisposable
{
    private const int MaximumFiles = 200;
    private const int MaximumHistory = 30;

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<TransferFile> _files = [];
    private readonly List<string> _confirmedStaged = [];
    private readonly List<TransferHistoryEntry> _history = [];
    private readonly List<TransferOutcome> _outcomes = [];
    private ActiveBatch? _batch;
    private TransferBatchSummary? _lastBatch;
    private JsonObject[] _lastRecords = [];
    private IDisposable? _events;
    private bool _busyQueued;
    private bool? _peerCheckSupported;
    private TransferSnapshot _snapshot = Empty;

    public TransferCore(MptAvaloniaSurfaceContext context) => _context = context;

    private static readonly TransferSnapshot Empty = new(
        TransferPhase.SelectFiles, [], [], null, TransferRoute.Direct, false, false, false, 0, "", "",
        null, false, false, "网盘未配置。", false, []);

    /// <summary>Raised on the UI thread after every state change; subscribers re-read <see cref="Snapshot"/>.</summary>
    public event Action? Changed;

    public TransferSnapshot Snapshot => _snapshot;

    public IReadOnlyList<TransferFile> Files => _files;

    public bool HasFailure => _lastFailure is not null;

    public bool IsStaged(string path) => IsInside(OutboxRoot, path);

    /// <summary>
    /// The follow-up read scheduled by the last terminal event, or null when none is pending. A test
    /// awaits it to settle a batch without guessing a delay; the page never waits on it.
    /// </summary>
    internal Task? PendingSettle { get; private set; }

    /// <summary>
    /// How long to let the module run before asking whether the batch is really over. A send reports
    /// one file at a time, so the busy flag right after a file finishes may still belong to the next
    /// file.
    /// </summary>
    internal TimeSpan BusyConfirmDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    public string OutboxRoot => Path.Combine(_context.DataDirectory, "outbox");

    private PendingRequest? _lastFailure;
    private string _statusBeforeStart = "";

    // ---- lifetime ---------------------------------------------------------------------------

    /// <summary>Starts the module event subscription. Safe to call more than once.</summary>
    public void Attach() => _events ??= _context.SubscribeEvents?.Invoke(OnEvent);

    /// <summary>Drops the subscription and reclaims staged copies this page created. Never deletes user
    /// files, and never deletes a copy while a transfer can still be reading it.</summary>
    public void Detach()
    {
        _events?.Dispose();
        _events = null;
        if (_snapshot.Busy || _batch is not null) return;
        foreach (var file in _files.Where(file => file.Staged).ToArray()) DiscardStaged(file.Path);
    }

    public void Dispose()
    {
        Detach();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    // ---- commands ---------------------------------------------------------------------------

    public async Task<JsonNode> CallAsync(string command, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var response = await _context.ExecuteCommandAsync("file-transfer." + command, args, linked.Token);
        if (!response.Success)
        {
            var message = response.Error?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? response.Output : message);
        }
        if (string.IsNullOrWhiteSpace(response.Output)) return new JsonObject();
        try { return JsonNode.Parse(response.Output) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    /// <summary>Runs one module read and republishes the snapshot. A failure becomes the visible status.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            ApplyInspect(await CallAsync("inspect"));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Publish(_snapshot with { Status = ex.Message }); }
    }

    // ---- inspection -------------------------------------------------------------------------

    private void ApplyInspect(JsonNode state)
    {
        var settings = state["settings"] as JsonObject;
        var cloud = state["cloud"] as JsonObject;
        _lastRecords = (state["history"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var peers = new List<TransferPeer>();
        if (settings?["peers"] is JsonArray list)
        {
            foreach (var node in list)
            {
                if (node is not JsonObject peer) continue;
                var id = Str(peer, "deviceId");
                if (id is not { Length: > 0 }) continue;
                var name = Str(peer, "name") is { Length: > 0 } value ? value : "未命名设备";
                peers.Add(new TransferPeer(
                    id,
                    name,
                    Str(peer, "address") ?? "",
                    // Absent state stays "unknown": only the module's own answer may claim online.
                    Str(peer, "state") is { Length: > 0 } reported ? reported : "unknown",
                    DateTimeOffset.TryParse(Str(peer, "checkedAt"), out var checkedAt) ? checkedAt : null,
                    Str(peer, "message") ?? "",
                    Flag(peer, "supportsControl")));
            }
        }

        // Keep the user's device choice across refreshes; fall back to the module's last-used peer.
        var remembered = _snapshot.PeerId is { Length: > 0 } current && peers.Any(peer => peer.DeviceId == current)
            ? current
            : Str(settings, "lastPeer") is { Length: > 0 } last && peers.Any(peer => peer.DeviceId == last) ? last : null;
        remembered ??= peers.Count > 0 ? peers[0].DeviceId : null;

        var relay = (Str(settings, "webDavUrl") ?? "").Trim();
        var receiving = Flag(state, "receiving");
        var busy = Flag(state, "busy");
        var openList = Flag(state, "openListRunning");
        var relayRunning = openList && Uri.TryCreate(relay, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal);

        var next = _snapshot with
        {
            Peers = peers,
            PeerId = remembered,
            Busy = busy,
            Receiving = receiving,
            Batch = _lastBatch,
            RelayConfigured = relay.Length > 0,
            RelayRunning = relayRunning,
            RelayDescription = relay,
            OpenListRunning = openList,
            History = _history.ToArray(),
            Progress = busy ? _snapshot.Progress : 0,
            ProgressText = busy ? _snapshot.ProgressText : "",
            // A module without the cloud summary, or one that has never checked, leaves this null;
            // "never checked" must not be rendered as "the relay is down".
            RelayReachable = cloud is null ? null : FlagOrNull(cloud, "reachable"),
            RelayMessage = cloud is null ? "" : Str(cloud, "message") ?? ""
        };

        var active = state["progress"] is JsonObject progress && IsActive(Str(progress, "state") ?? "");

        if (!busy && _batch is not null && !active)
        {
            // The module is idle while this page still has a queued batch. Either every file has
            // reported, or the batch command finished without reporting all of them; reconcile from
            // the module's own transfer history so a batch cannot be left running forever.
            ReconcileMissingOutcomes();
            Publish(next);
            SettleBatch();
            return;
        }

        if (!busy && IsTerminalPhase(next.Phase))
        {
            next = next with { Progress = 0, ProgressText = "" };
        }
        else if (!busy)
        {
            next = next with { Phase = next.Files.Count == 0 ? TransferPhase.SelectFiles : TransferPhase.SelectDevice };
        }

        if (active && state["progress"] is JsonObject inFlight && TransferEventReport.From(inFlight) is { } report)
            next = ApplyProgress(next, report);
        else if (next.Status.Length == 0)
            // The first read must not leave the user with a blank line where the next step belongs.
            next = next with { Status = next.Files.Count == 0 ? "选择要发送的文件。" : $"已选择 {next.Files.Count} 个文件 · {Size(next.TotalBytes)}" };

        Publish(next);
    }

    /// <summary>
    /// Fills in files the batch never reported, using the records the module persisted. A batch whose
    /// command failed halfway must still produce a verdict instead of leaving the page on "sending".
    /// </summary>
    private void ReconcileMissingOutcomes()
    {
        if (_batch is null) return;
        foreach (var path in _batch.Paths)
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0) continue;
            if (_outcomes.Any(outcome => string.Equals(outcome.Name, name, StringComparison.Ordinal))) continue;
            var record = _lastRecords.FirstOrDefault(item => string.Equals(Str(item, "name"), name, StringComparison.Ordinal));
            if (record is null) continue;
            var state = Str(record, "state");
            if (state is not ("completed" or "received" or "failed" or "cancelled")) continue;
            _outcomes.Add(new TransferOutcome(name, state, Str(record, "delivery") ?? "", Str(record, "peer") ?? "", Str(record, "message") ?? ""));
        }
    }

    private static bool IsTerminalPhase(TransferPhase phase) =>
        phase is TransferPhase.Succeeded or TransferPhase.Failed;

    // ---- files ------------------------------------------------------------------------------

    /// <summary>Adds a real file the user picked or shared. Duplicates and missing paths are ignored.</summary>
    public bool AddFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (_files.Any(file => string.Equals(file.Path, path, StringComparison.Ordinal))) return false;
        if (_files.Count >= MaximumFiles) { Publish(_snapshot with { Status = $"一次最多发送 {MaximumFiles} 个文件。" }); return false; }
        _files.Add(Describe(path));
        PublishFiles();
        return true;
    }

    /// <summary>Adds a copy this surface made for a share whose grant can expire.</summary>
    public bool AddStage(string path) => AddFile(path);

    public void RemoveFile(string path)
    {
        var match = _files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal));
        if (match is null) return;
        _files.Remove(match);
        DiscardStaged(path);
        PublishFiles();
    }

    public void ClearFiles()
    {
        if (!_snapshot.Busy && _batch is null) foreach (var file in _files.ToArray()) DiscardStaged(file.Path);
        _files.Clear();
        _confirmedStaged.Clear();
        PublishFiles();
    }

    private void PublishFiles()
    {
        // Adding or removing files must not silently discard a finished result the user is reading.
        var phase = _snapshot.Phase switch
        {
            TransferPhase.Transferring => TransferPhase.Transferring,
            TransferPhase.Succeeded or TransferPhase.Failed => _snapshot.Phase,
            _ => _files.Count == 0 ? TransferPhase.SelectFiles : TransferPhase.SelectDevice
        };
        Publish(_snapshot with
        {
            Files = _files.ToArray(),
            Phase = phase,
            Failure = phase == TransferPhase.Failed ? _snapshot.Failure : null,
            Status = _files.Count == 0 ? "选择要发送的文件。" : $"已选择 {_files.Count} 个文件 · {Size(_files.Sum(file => file.Length))}"
        });
    }

    /// <summary>Derives "staged" from the path itself, so a copy under the outbox is always reclaimable
    /// and a file the user picked never is, whatever entry point added it.</summary>
    private TransferFile Describe(string path)
    {
        var name = Path.GetFileName(path);
        return new TransferFile(path, name.Length > 0 ? name : path, Length(path), IsInside(OutboxRoot, path));
    }

    // ---- peers and route --------------------------------------------------------------------

    public void SelectPeer(string? deviceId)
    {
        if (deviceId is { Length: > 0 } && !_snapshot.Peers.Any(peer => peer.DeviceId == deviceId)) return;
        var phase = _snapshot.Phase == TransferPhase.SelectFiles && deviceId is not null
            ? TransferPhase.SelectDevice
            : _snapshot.Phase;
        Publish(_snapshot with { PeerId = deviceId, Phase = phase });
    }

    public void SelectRoute(TransferRoute route)
    {
        // Relay without a configured cloud account cannot complete, so the choice is refused instead
        // of being accepted and then failing after the upload starts.
        if (route == TransferRoute.Relay && !_snapshot.RelayConfigured)
        {
            Publish(_snapshot with { Status = "网盘中转还没有配置：先在『网盘中转设置』里连接网盘，再选择中转。" });
            return;
        }
        Publish(_snapshot with { Route = route });
    }

    // ---- staged outbox cleanup --------------------------------------------------------------

    private void DiscardStaged(string path)
    {
        // Only a running transfer protects a copy. The batch record itself stays set until the verdict
        // is published, and by then the module has already reported it is idle.
        if (_snapshot.Busy || !IsStaged(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes staged copies older than a day once the module reports no transfer in flight.
    /// The age gate also covers a transfer started from a previous surface instance.</summary>
    public void SweepOutbox()
    {
        if (_snapshot.Busy || _batch is not null || !Directory.Exists(OutboxRoot)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(OutboxRoot))
            {
                if (Directory.GetLastWriteTimeUtc(folder) >= cutoff) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (!_files.Any(entry => string.Equals(entry.Path, file, StringComparison.Ordinal))) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Marks the staged copy of one file this batch sent, so it can be reclaimed at the end.
    /// An incoming transfer with the same name never comes through here.</summary>
    private void NoteCompleted(string name)
    {
        if (_batch is null || !_batch.Paths.Any(path => string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))) return;
        var staged = _files.FirstOrDefault(file => file.Staged
            && !_confirmedStaged.Contains(file.Path, StringComparer.Ordinal)
            && string.Equals(file.Name, name, StringComparison.Ordinal));
        if (staged is not null) _confirmedStaged.Add(staged.Path);
    }

    /// <summary>Reclaims the staged copies of files this batch actually sent. This is the only place
    /// the send flow deletes staged copies, and it runs after the module reported the batch idle.</summary>
    private void RetireStagedCopies(IReadOnlyList<TransferOutcome> outcomes)
    {
        var confirmed = _confirmedStaged.ToList();
        foreach (var outcome in outcomes.Where(outcome => outcome.State is "completed" or "received"))
        {
            var match = _files.FirstOrDefault(file => file.Staged
                && !confirmed.Contains(file.Path, StringComparer.Ordinal)
                && string.Equals(file.Name, outcome.Name, StringComparison.Ordinal));
            if (match is not null) confirmed.Add(match.Path);
        }
        if (confirmed.Count == 0) return;
        foreach (var path in confirmed)
        {
            _files.RemoveAll(file => string.Equals(file.Path, path, StringComparison.Ordinal));
            DiscardStaged(path);
        }
        _confirmedStaged.Clear();
        _snapshot = _snapshot with { Files = _files.ToArray() };
    }

    // ---- sending ----------------------------------------------------------------------------

    public async Task SendAsync()
    {
        if (_snapshot.Busy || _batch is not null) throw new InvalidOperationException("已有传输正在进行，请等待完成或点『取消传输』。");
        if (_files.Count == 0) throw new InvalidOperationException("请先选择要发送的文件。");
        if (_snapshot.PeerId is not { Length: > 0 } peerId) throw new InvalidOperationException("请先添加并选择接收设备。");
        var missing = _files.Where(file => !File.Exists(file.Path)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var file in missing) _files.Remove(file);
            PublishFiles();
            throw new InvalidOperationException($"{missing.Length} 个文件已不在设备上，已从列表移除。");
        }
        var route = _snapshot.Route;
        if (route == TransferRoute.Relay && !_snapshot.RelayConfigured)
            throw new InvalidOperationException("请先在『网盘中转设置』里连接网盘，再发送。");
        var args = new JsonObject
        {
            ["paths"] = new JsonArray(_files.Select(file => (JsonNode?)JsonValue.Create(file.Path)).ToArray()),
            ["peerId"] = peerId
        };
        await StartAsync(route == TransferRoute.Relay ? "send.cloud" : "send.direct", args, _snapshot.FileLabel, route);
    }

    /// <summary>Pulls one cloud inbox entry to this device, reported through the same events.</summary>
    public Task StartDownloadAsync(JsonObject file, string name) =>
        StartAsync("cloud.download", new JsonObject { ["file"] = file.DeepClone() }, name, TransferRoute.Relay);

    private async Task StartAsync(string command, JsonObject args, string label, TransferRoute route)
    {
        // Register the request and show the pending state *before* the call. A small file can report
        // its terminal transfer.changed event while the command response is still in flight; a late
        // response must never overwrite that result with "正在传输" again.
        var request = new PendingRequest(command, args.DeepClone().AsObject(), label, route);
        _confirmedStaged.Clear();
        _outcomes.Clear();
        _lastBatch = null;
        _lastFailure = null;
        _statusBeforeStart = _snapshot.Status;
        // The retry target is the request being attempted now, so a failure reported while the
        // command response is still in flight already knows what to retry.
        _pending = request;
        _batch = new ActiveBatch(command, ArgsPaths(args).ToArray(), route);
        Publish(_snapshot with
        {
            Phase = TransferPhase.Transferring,
            Busy = true,
            Cancelled = false,
            Route = route,
            Batch = null,
            Progress = 0,
            ProgressText = "",
            Failure = null,
            Status = route == TransferRoute.Relay ? $"正在存入网盘 {label}…" : $"正在发送 {label}…"
        });
        try
        {
            await CallAsync(command, args);
        }
        catch
        {
            // A rejected command must never leave a phantom transfer behind; re-derive the module's
            // real state, then still surface the rejection to the user.
            _pending = null;
            await RollbackStartAsync();
            throw;
        }
    }

    private PendingRequest? _pending;

    /// <summary>Re-derives the module's real state after a rejected command instead of assuming idle.</summary>
    private async Task RollbackStartAsync()
    {
        var state = await CallAsync("inspect");
        var busy = Flag(state, "busy");
        if (!busy) _batch = null;
        Publish(_snapshot with
        {
            Busy = busy,
            Phase = busy ? TransferPhase.Transferring : _snapshot.Files.Count == 0 ? TransferPhase.SelectFiles : TransferPhase.SelectDevice,
            Failure = busy ? null : _snapshot.Failure,
            Progress = busy ? _snapshot.Progress : 0,
            ProgressText = busy ? _snapshot.ProgressText : "",
            // A refused command must not leave "正在发送…" behind as if the transfer were running.
            Status = busy ? _snapshot.Status : _statusBeforeStart
        });
    }

    public async Task CancelAsync()
    {
        await CallAsync("cancel");
        Publish(_snapshot with { Status = "已请求取消当前传输。" });
    }

    /// <summary>Retries the exact request that failed, after proving its files are still on the device.</summary>
    public async Task RetryAsync()
    {
        if (_lastFailure is not { } request) throw new InvalidOperationException("没有可重试的传输。");
        var args = request.Args.DeepClone().AsObject();
        var missing = ArgsPaths(args).Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            _lastFailure = null;
            Publish(_snapshot with { Failure = null, Status = "原文件已不在设备上，请重新选择文件。" });
            throw new InvalidOperationException("原文件已不在设备上，请重新选择文件。");
        }
        await StartAsync(request.Command, args, request.Label, request.Route);
    }

    public void DismissResult()
    {
        _lastFailure = null;
        Publish(_snapshot with
        {
            Phase = _files.Count == 0 ? TransferPhase.SelectFiles : TransferPhase.SelectDevice,
            Failure = null,
            Progress = 0,
            ProgressText = "",
            Status = _files.Count == 0 ? "选择要发送的文件。" : $"已选择 {_files.Count} 个文件 · {Size(_files.Sum(file => file.Length))}"
        });
    }

    private static IEnumerable<string> ArgsPaths(JsonObject args) =>
        args["paths"] is JsonArray paths
            ? paths.Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0)
            : [];

    /// <summary>
    /// Asks the module for its real busy flag shortly after a file finishes. The batch verdict comes
    /// from this answer, not from the event: while the module is still busy the remaining files are
    /// still being sent, so the page must not announce success or accept a second batch yet.
    /// </summary>
    private void ScheduleBusyRefresh()
    {
        if (_busyQueued) return;
        _busyQueued = true;
        var delay = BusyConfirmDelay;
        PendingSettle = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay);
                var state = await CallAsync("inspect");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var busy = Flag(state, "busy");
                    Publish(_snapshot with { Busy = busy });
                    // A batch that has not reported every file yet is still running even if the module
                    // briefly looked idle between two files.
                    if (!busy && BatchReported()) SettleBatch();
                });
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception) { /* the module is unreachable; the next event or refresh retries */ }
            finally { _busyQueued = false; }
        });
    }

    /// <summary>True once every file in the queued batch has reported a terminal state.</summary>
    private bool BatchReported() => _batch is not null && _outcomes.Count >= _batch.Paths.Count;

    /// <summary>
    /// Turns the collected per-file reports into the batch verdict. Success requires the module to be
    /// idle and every file accounted for, so a partly-sent batch is never presented as done.
    /// </summary>
    private void SettleBatch()
    {
        if (_batch is null) return;
        var outcomes = _outcomes.ToList();
        var summary = new TransferBatchSummary(_batch.Route, outcomes);
        _lastBatch = summary;
        _outcomes.Clear();

        var failed = outcomes.Where(outcome => outcome.Failed).ToList();
        var success = outcomes.Count - failed.Count;
        var cancelled = failed.Count > 0 && failed.All(outcome => outcome.Cancelled);
        var phase = failed.Count == 0 ? TransferPhase.Succeeded : TransferPhase.Failed;

        RetireStagedCopies(outcomes);
        _batch = null;
        Publish(_snapshot with
        {
            Busy = false,
            Phase = phase,
            Cancelled = cancelled,
            Failure = cancelled || failed.Count == 0 ? null : SummarizeFailure(outcomes),
            Progress = 0,
            ProgressText = "",
            Route = summary.Route,
            Batch = summary,
            Status = SummaryText(outcomes, success, failed.Count),
            Files = _files.ToArray(),
            History = _history.ToArray()
        });
    }

    private static string SummarizeFailure(IReadOnlyList<TransferOutcome> outcomes)
    {
        var first = outcomes.FirstOrDefault(outcome => !outcome.Cancelled && outcome.Message.Length > 0);
        return first is null ? "部分文件没有发送成功。" : $"{first.Name}：{first.Message}";
    }

    private static string SummaryText(IReadOnlyList<TransferOutcome> outcomes, int success, int failedCount)
    {
        if (failedCount == 0) return success == 1 ? "1 个文件已完成。" : $"{success} 个文件已完成。";
        return $"共 {outcomes.Count} 个文件，成功 {success} 个，未完成 {failedCount} 个。";
    }

    // ---- events -----------------------------------------------------------------------------

    private void OnEvent(MptSurfaceEvent e)
    {
        if (e.SourceId != "file-transfer" || e.Type != "transfer.changed") return;
        var payload = (JsonObject)e.Payload.DeepClone();
        Dispatcher.UIThread.Post(() => ApplyEvent(payload));
    }

    /// <summary>
    /// Applies one module event.
    ///
    /// A per-file terminal event is not the end of the batch: a multi-file send reports one file at a
    /// time and the module is still busy with the rest. So a terminal event only records that file's
    /// outcome and asks the module for its real busy flag; the batch is summarised once the module
    /// reports it is idle. That is also why nothing here reclaims a staged copy: a file that has not
    /// been sent yet must survive its namesake finishing first.
    /// </summary>
    public void ApplyEvent(JsonObject item)
    {
        if (TransferEventReport.From(item) is not { } report) return;

        if (IsActive(report.State))
        {
            Publish(ApplyProgress(_snapshot, report));
            return;
        }

        var entry = new TransferHistoryEntry(report.Name, report.State, report.Message, report.Time, report.Done, report.Total);
        _history.Insert(0, entry);
        while (_history.Count > MaximumHistory) _history.RemoveAt(_history.Count - 1);

        // Only an event for a request this page started belongs to the send flow. A file arriving from
        // the peer reports the same "received"/"completed" states and must not settle our batch.
        var belongsToBatch = _batch is not null
            && report.Direction != "receive"
            && _batch.Paths.Any(path => string.Equals(Path.GetFileName(path), report.Name, StringComparison.Ordinal));

        if (belongsToBatch && report.IsTerminal)
        {
            _outcomes.RemoveAll(outcome => string.Equals(outcome.Name, report.Name, StringComparison.Ordinal));
            _outcomes.Add(new TransferOutcome(report.Name, report.State, report.Delivery, report.Peer, report.Message));
            if (report.State is "completed" or "received") NoteCompleted(report.Name);
            // Only a real failure earns the retry prompt; cancelling is the user's own decision.
            if (report.State == "failed") _lastFailure = _pending;
        }

        Publish(_snapshot with
        {
            Route = belongsToBatch && _batch is not null ? _batch.Route : _snapshot.Route,
            // Reported per file as evidence, never as the batch verdict.
            Status = Describe(entry),
            History = _history.ToArray()
        });

        if (belongsToBatch && report.IsTerminal) ScheduleBusyRefresh();
    }

    /// <summary>One parsed <c>transfer.changed</c> payload, with the module's own delivery evidence.</summary>
    private sealed record TransferEventReport(
        string Name, string State, string Message, string Time, long Done, long Total,
        string Direction, string Peer, string Delivery)
    {
        public bool IsTerminal => State is "completed" or "received" or "failed" or "cancelled";

        public static TransferEventReport? From(JsonObject? item)
        {
            if (item is null || Str(item, "state") is not { Length: > 0 } state) return null;
            return new TransferEventReport(
                Str(item, "name") ?? "文件",
                state,
                Str(item, "message") ?? "",
                Str(item, "time") ?? "",
                Number(item, "done"),
                Number(item, "total"),
                Str(item, "direction") ?? "",
                Str(item, "peer") ?? "",
                Str(item, "delivery") ?? "");
        }
    }

    private TransferSnapshot ApplyProgress(TransferSnapshot snapshot, TransferEventReport report)
    {
        var total = report.Total;
        var done = report.Done;
        // Whole-batch position, so cancelling a five-file send does not look like cancelling one file.
        var position = _batch is null || _batch.Paths.Count <= 1
            ? ""
            : $" · 第 {BatchPosition(report.Name)}/{_batch.Paths.Count} 个文件";
        var text = total > 0 ? $"{done * 100d / total:F0}% · {Size(done)} / {Size(total)}{position}" : position.TrimStart(' ', '·');
        return snapshot with
        {
            Busy = true,
            Phase = TransferPhase.Transferring,
            Progress = total > 0 ? Math.Clamp(done * 100d / total, 0, 100) : 0,
            ProgressText = text,
            Status = Describe(new TransferHistoryEntry(report.Name, report.State, report.Message, "", done, total))
        };
    }

    /// <summary>Which file of the batch is being reported, counting the first matching pending path.</summary>
    private int BatchPosition(string name)
    {
        if (_batch is null) return 1;
        for (var index = 0; index < _batch.Paths.Count; index++)
            if (string.Equals(Path.GetFileName(_batch.Paths[index]), name, StringComparison.Ordinal))
                return index + 1;
        return 1;
    }

    // ---- optional module capabilities ---------------------------------------------------------
    //
    // peer.check / peers.remove and the cloud reachability summary arrived with the device-state
    // contract. A host that ships an older file-transfer module must keep working, so each call is
    // attempted and a "command not available" answer is reported as a missing capability instead of
    // being allowed to look like a device or relay failure.

    /// <summary>Asks the peer to answer, then re-reads the module's own verdict. Never guesses.</summary>
    public async Task<string> CheckPeerAsync(string deviceId)
    {
        if (_peerCheckSupported == false) return "当前模块版本不支持检查设备连接。";
        try
        {
            await CallAsync("peer.check", new JsonObject { ["peerId"] = deviceId });
            _peerCheckSupported = true;
        }
        catch (Exception ex) when (IsMissingCapability(ex))
        {
            _peerCheckSupported = false;
            Publish(_snapshot with { Status = "当前模块版本不支持检查设备连接，请更新文件互传模块。" });
            return _snapshot.Status;
        }
        await RefreshAsync();
        var peer = _snapshot.Peers.FirstOrDefault(item => item.DeviceId == deviceId);
        var text = peer is null
            ? "已请求检查，但设备列表里没有这台设备。"
            : peer.State switch
            {
                "online" => $"{peer.Name} 已应答，现在可以发送。",
                "offline" => $"{peer.Name} 没有应答：确认对方 MPT 正在运行且在同一网络。",
                _ => $"{peer.Name} 的结果还不确定。{peer.Message}".Trim()
            };
        Publish(_snapshot with { Status = text });
        return text;
    }

    /// <summary>Forgets one paired device through the module, so the module stays the owner of the list.</summary>
    public async Task RemovePeerAsync(string deviceId)
    {
        try
        {
            await CallAsync("peers.remove", new JsonObject { ["peerId"] = deviceId });
        }
        catch (Exception ex) when (IsMissingCapability(ex))
        {
            Publish(_snapshot with { Status = "当前模块版本不支持移除设备，请在电脑上解除连接。" });
            return;
        }
        await RefreshAsync();
        Publish(_snapshot with { Status = "已解除该设备的连接。" });
    }

    /// <summary>Waits for a real relay conversation. Without the summary this reports "not supported".</summary>
    public async Task<string> CheckRelayAsync()
    {
        try
        {
            await CallAsync("cloud.check");
        }
        catch (Exception ex) when (IsMissingCapability(ex))
        {
            Publish(_snapshot with { Status = "当前模块版本不支持测试网盘连接。" });
            return _snapshot.Status;
        }
        await RefreshAsync();
        var text = _snapshot.RelayReachable switch
        {
            true => "网盘连接正常，可以中转文件。",
            false => string.IsNullOrWhiteSpace(_snapshot.RelayMessage) ? "网盘暂时连不上，请检查地址、网络和网盘状态。" : _snapshot.RelayMessage,
            _ => "已发出检查，但模块没有返回连接结果。"
        };
        Publish(_snapshot with { Status = text });
        return text;
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

    // ---- helpers ----------------------------------------------------------------------------

    private void Publish(TransferSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke();
    }

    /// <summary>Publishes on the UI thread; module events can arrive from a HostControl read loop.</summary>
    public void PublishOnUi(TransferSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) Publish(snapshot);
        else Dispatcher.UIThread.Post(() => Publish(snapshot));
    }

    public static bool IsActive(string state) =>
        state is "sending" or "uploading" or "downloading" or "receiving";

    public static string Describe(TransferHistoryEntry entry)
    {
        var state = entry.State switch
        {
            "completed" => "已完成", "received" => "已接收", "cancelled" => "已取消", "failed" => "失败",
            "uploading" => "上传中", "downloading" => "下载中", "receiving" => "接收中", _ => "发送中"
        };
        return string.IsNullOrWhiteSpace(entry.Message) ? $"{entry.Name} · {state}" : $"{entry.Name} · {state} · {entry.Message}";
    }

    public static string Size(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576d:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:F0} KB" : $"{bytes} B";

    public static string ShortPath(string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length <= 36) return value;
        var parts = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? "…/" + string.Join('/', parts[^2..]) : "…" + value[^32..];
    }

    public static string Authority(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "";

    private static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    private static bool IsInside(string root, string path)
    {
        try
        {
            var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string? Str(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static bool Flag(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>Tri-state flag: absent or JSON null stays null, which means "never checked".</summary>
    private static bool? FlagOrNull(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    public static long Number(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private sealed record PendingRequest(string Command, JsonObject Args, string Label, TransferRoute Route);
}
