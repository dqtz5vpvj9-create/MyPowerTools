using System.Net;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using MyPowerTools.Abstractions;

namespace FileTransfer.MyPowerTools;

/// <summary>
/// Device-pairing delivery over the public relay's deposit inbox (PROTOCOL.md §6). A normal pair code
/// carries only <c>inboxId</c> + <c>depositKey</c>: the sender may deposit files and read one item's
/// receipt, and nothing else. The owner key and the conversation key never appear in a pairing code
/// (the owner key is still a relay credential and travels over HTTPS Basic like every other one).
///
/// The inbox is a namespace of its own, separate from the assistant conversation, so a paired device
/// that is not part of the user's own conversation still receives real files. Its local queue is the
/// existing durable <see cref="AssistantStore"/> — no second queue, no fake upload, no fake delivery —
/// and nothing that arrives here is ever treated as proof of conversation membership.
/// </summary>
public sealed partial class FileTransferModule
{
    private PublicInboxIdentity? _inbox;
    private bool _inboxAvailable;
    private DateTimeOffset? _inboxCheckedAt;
    private string _inboxMessage = "";
    private int _inboxLastItems;
    private int _inboxRejected;
    private int _depositRuns;
    private string _depositLastError = "";
    private int _depositConfirmReads;
    private int _depositConfirmMarked;
    private Task? _inboxLoop;
    private Task? _depositLoop;
    private CancellationTokenSource? _inboxCts;
    private long _inboxRevision = -1;
    private readonly SemaphoreSlim _inboxGate = new(1, 1);
    private readonly SemaphoreSlim _inboxItemGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _inboxRevisions = new();
    private sealed record InboxRouteHealth(bool Available, string Message, DateTimeOffset CheckedAt);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InboxRouteHealth> _inboxHealth = new();
    private IReadOnlyList<InboxRelay> InboxRoutes => CustomRelayConfigured ? [] : InboxRelays.All;
    private readonly SemaphoreSlim _inboxSignal = new(0, 1);
    // Retained wake-up, exactly like the relay leg: one pending run covers a signal that arrives while a
    // run is in flight, so a send during a slow deposit is never lost.
    private readonly SemaphoreSlim _depositSignal = new(0, 1);

    private const string InboxIdentitySecret = "inbox-identity";

    /// <summary>
    /// The device's own deposit inbox, generated locally and stored only in the secret store. Following
    /// the protocol's sizing rule, the module picks a 128 bit id (<c>inbox-&lt;32hex&gt;</c>) instead of
    /// the client's short default.
    /// </summary>
    private async Task<PublicInboxIdentity> EnsureInboxAsync(CancellationToken token)
    {
        if (_inbox is not null) return _inbox;
        await _inboxGate.WaitAsync(token);
        try
        {
            if (_inbox is not null) return _inbox;
            var saved = await SecretAsync(InboxIdentitySecret, token);
            if (PublicInboxIdentity.TryParseJson(saved, out var existing)) return _inbox = existing;
            var created = PublicInboxIdentity.CreateNew("inbox-" + Guid.NewGuid().ToString("N"));
            await _secrets.SaveAsync(Id, InboxIdentitySecret, created.ToJson(), token);
            return _inbox = created;
        }
        finally { _inboxGate.Release(); }
    }

    private string PeerInboxId(string deviceId) => FindPeer(deviceId) is { } peer ? PeerText(peer, "inboxId") : "";

    /// <summary>The deposit credential of a paired device, read from the secret store, or null.</summary>
    private async Task<PublicInboxPairing?> PeerInboxAsync(string deviceId, CancellationToken token)
    {
        var inboxId = PeerInboxId(deviceId);
        if (inboxId.Length == 0) return null;
        var depositKey = await SecretAsync("inbox-deposit-" + deviceId, token);
        if (depositKey is not { Length: 64 }) return null;
        return new PublicInboxPairing(inboxId, depositKey);
    }

    // ---- owner side: register and receive ---------------------------------------------------------

    /// <summary>Starts the inbox receive loop; it lives with the module and stops with receiving.</summary>
    private void StartInboxReceive()
    {
        if (_inboxLoop is not null) return;
        _inboxLoop = Task.Run(InboxLoopAsync);
    }

    private void WakeInbox()
    {
        try { _inboxSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Registers this device's inbox once and then waits in the owner long poll. Registration is
    /// idempotent; the client retries the retryable states (an inbox that is not registered yet, the
    /// relay's own 429/502/504) with its bounded policy, so a 503 resolves without user action.
    /// </summary>
    private async Task InboxLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_receivingEnabled || CustomRelayConfigured)
            {
                await WaitInboxEventAsync(Timeout.InfiniteTimeSpan);
                continue;
            }
            using var poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _inboxCts = poll;
            try
            {
                var identity = await EnsureInboxAsync(poll.Token);
                await Task.WhenAll(InboxRoutes.Select(route => ReceiveInboxRouteAsync(route, identity, poll.Token)));
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(_inboxCts, poll)) _inboxCts = null; }
        }
    }

    private async Task ReceiveInboxRouteAsync(InboxRelay route, PublicInboxIdentity identity, CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (_receivingEnabled && !token.IsCancellationRequested)
        {
            try
            {
                using var client = PublicInboxClient.Owner(identity, PublicInboxRetryPolicy.None, route.Address);
                await client.RegisterAsync(token);
                NoteInboxAvailable(route.Id);
                while (_receivingEnabled && !token.IsCancellationRequested)
                {
                    var revision = _inboxRevisions.GetValueOrDefault(route.Id, -1);
                    var page = await client.PollAsync(revision < 0 ? null : revision, token: token);
                    _inboxRevisions[route.Id] = page.HasMore ? -1 : page.Revision;
                    _inboxRevision = page.Revision;
                    _inboxLastItems = page.Items.Count;
                    NoteInboxAvailable(route.Id);
                    var failed = false;
                    foreach (var item in page.Items)
                    {
                        // The two relays may deliver the same id together. Serialize adoption so only
                        // one payload is downloaded; the other route receives its own real receipt.
                        await _inboxItemGate.WaitAsync(token);
                        try { if (!await ReceiveInboxItemAsync(client, item, route.Id, token)) failed = true; }
                        finally { _inboxItemGate.Release(); }
                    }
                    if (failed) { await Task.Delay(backoff, token); backoff = Bump(backoff); }
                    else backoff = TimeSpan.FromSeconds(2);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (PublicInboxAuthException ex)
            {
                NoteInboxUnavailable(ex.Message, route.Id);
                await Task.Delay(TimeSpan.FromMinutes(5), token);
            }
            catch (Exception ex)
            {
                NoteInboxUnavailable(MptLogRedactor.Redact(ex.Message), route.Id);
                await Task.Delay(backoff, token);
                backoff = Bump(backoff);
            }
        }
    }

    private static TimeSpan Bump(TimeSpan backoff) =>
        TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(2).Ticks));

    // A saved private item no longer appears in the pending-inbox listing. Reopening a deleted
    // local copy must retrieve that same owned item explicitly, without entering the shared namespace.
    private async Task RestoreInboxAttachmentAsync(AssistantItem item, CancellationToken token)
    {
        using var scope = ItemScope(item.Id, token);
        token = scope.Token;
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var identity = await EnsureInboxAsync(token);
        await _inboxItemGate.WaitAsync(token);
        try
        {
            var current = (await store.LoadAsync(token)).Find(item.Id);
            if (current is not null && AssistantContent.HasLocalContent(current)) return;
            await store.MutateAsync(state =>
            {
                var row = state.Find(item.Id)!;
                row.State = AssistantItemState.Downloading;
                row.BytesDone = 0;
                row.Error = null;
            }, token);
            EmitAssistantChanged("inbox.downloading");
            var descriptor = new PublicInboxItem
            {
                ItemId = item.Id,
                Kind = item.Kind == AssistantItemKind.Image ? PublicInboxItemKind.Image : PublicInboxItemKind.File,
                Name = item.Name, Size = item.Size, CreatedAt = item.CreatedAt,
                SenderDeviceId = item.SenderDeviceId, SenderName = item.SenderName, TargetDeviceId = item.TargetDeviceId
            };
            foreach (var route in InboxRoutes.OrderByDescending(route => route.Id == item.SourceRelay))
            {
                using var client = PublicInboxClient.Owner(identity, PublicInboxRetryPolicy.None, route.Address);
                await store.MutateAsync(state =>
                {
                    var row = state.Find(item.Id)!;
                    row.TransportRoute = route.Id == InboxRelays.Tail ? "tail-relay" : "public-relay";
                }, token);
                EmitAssistantChanged("inbox.downloading");
                if (await ReceiveInboxItemAsync(client, descriptor, route.Id, token)) return;
            }
            throw new IOException("文件暂时无法重新下载，请确认中转服务可用且文件仍在保留期内后重试。");
        }
        catch (Exception ex)
        {
            await store.MutateAsync(state =>
            {
                var row = state.Find(item.Id);
                if (row is null || AssistantContent.HasLocalContent(row) || row.State == AssistantItemState.Cancelled) return;
                row.State = ex is OperationCanceledException ? AssistantItemState.Stored : AssistantItemState.Failed;
                row.Error = ex is OperationCanceledException ? null : MptLogRedactor.Redact(ex.Message);
            }, CancellationToken.None);
            throw;
        }
        finally
        {
            _inboxItemGate.Release();
            EmitAssistantChanged("inbox.open");
        }
    }

    /// <summary>
    /// One deposited item: reuse an already saved copy, otherwise download once, adopt it into the durable
    /// store, and only then write the receipt the sender reads. The item is deliberately never deleted,
    /// because deleting it would also remove the receipt the sender still has to confirm.
    /// Returns false when this item needs a backoff before the next attempt.
    /// </summary>
    private async Task<bool> ReceiveInboxItemAsync(PublicInboxClient client, PublicInboxItem item, string sourceRelay, CancellationToken token)
    {
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var spool = Path.Combine(_data, "inbox-spool");
        Directory.CreateDirectory(spool);
        var rejected = Path.Combine(spool, item.ItemId + ".rejected");
        if (File.Exists(rejected))
        {
            _inboxRejected++;
            await client.DeleteAsync(item.ItemId, token);
            return true; // rejected content is not acknowledged as saved
        }
        var cached = await store.LoadAsync(token);
        var existing = cached.Find(item.ItemId);
        long bytes = existing?.BytesDone ?? item.Size;
        try
        {
            if (existing is { State: AssistantItemState.Available } && AssistantContent.HasLocalContent(existing))
            {
                // A previous round saved the file but the receipt did not land; reuse it instead of
                // downloading and committing a second copy.
                await client.AcknowledgeAsync(item.ItemId, bytes, deviceId: Setting("deviceId"), deviceName: DeviceName(), token: token);
                NoteInboxAvailable();
                return true;
            }
            if (item.Kind == PublicInboxItemKind.Text)
            {
                // The relay may accept a very large text; the client must not buffer it unbounded.
                if (item.Size > AssistantLimits.MaxTextCharacters * 4L)
                {
                    await RejectInboxItemAsync(rejected, $"文本超过 {AssistantLimits.MaxTextCharacters} 个字符，已跳过。");
                    await client.DeleteAsync(item.ItemId, token);
                    return true;
                }
                using var buffer = new MemoryStream();
                using (var scope = ItemScope(item.ItemId, token))
                {
                    var download = await client.DownloadAsync(item.ItemId, buffer, scope.Token);
                    bytes = download.Bytes;
                }
                var text = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                if (text.Length > AssistantLimits.MaxTextCharacters)
                {
                    await RejectInboxItemAsync(rejected, $"文本超过 {AssistantLimits.MaxTextCharacters} 个字符，已跳过。");
                    await client.DeleteAsync(item.ItemId, token);
                    return true;
                }
                await store.AdoptAsync(Identity(), AdoptedItem(item, null, bytes, text, sourceRelay), token);
            }
            else
            {
                var temporary = Path.Combine(spool, item.ItemId + ".part");
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
                    {
                        using var scope = ItemScope(item.ItemId, token);
                        var download = await client.DownloadAsync(item.ItemId, output, scope.Token);
                        bytes = download.Bytes;
                    }
                    var directory = Path.Combine(store.InboxRoot, item.ItemId);
                    Directory.CreateDirectory(directory);
                    var saved = TransferFiles.Commit(temporary, directory, TransferFiles.FileName(item.Name ?? "来件"));
                    await store.AdoptAsync(Identity(), AdoptedItem(item, saved, bytes, null, sourceRelay), token);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            // The content is local now, so the entry is available here immediately: the conversation
            // sync pass only promotes what its own conversation pulled, and an inbox delivery must not
            // wait for an unrelated long poll to be usable.
            await store.MutateAsync(current =>
            {
                var row = current.Find(item.ItemId);
                if (row is null || row.State == AssistantItemState.Cancelled) return;
                row.State = AssistantItemState.Available;
                row.BytesDone = bytes;
                row.Error = null;
            }, token);
            // A real save happened: the receipt is the only thing that may turn the sender's entry into
            // "delivered", and it is written after the local record is durable.
            using (var scope = ItemScope(item.ItemId, token))
            {
                await client.AcknowledgeAsync(item.ItemId, bytes, deviceId: Setting("deviceId"), deviceName: DeviceName(), token: scope.Token);
            }
            NoteInboxAvailable();
            EmitAssistantChanged("inbox.received");
            SignalAssistant();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or PublicInboxException or InvalidOperationException or ArgumentException)
        {
            // The item stays in the inbox: the next poll retries it instead of losing the file.
            NoteInboxUnavailable(MptLogRedactor.Redact(ex.Message));
            return false;
        }
    }

    /// <summary>Remembers an item the client will not accept, so it does not block the queue forever.</summary>
    private async Task RejectInboxItemAsync(string marker, string reason)
    {
        await File.WriteAllTextAsync(marker, reason);
        _inboxRejected++;
        NoteInboxUnavailable(reason);
    }

    private AssistantItem AdoptedItem(PublicInboxItem item, string? path, long bytes, string? text, string sourceRelay) => new()
    {
        Id = item.ItemId,
        Kind = item.Kind switch
        {
            PublicInboxItemKind.Text => AssistantItemKind.Text,
            PublicInboxItemKind.Image => AssistantItemKind.Image,
            _ => AssistantItemKind.File
        },
        Text = text,
        Name = item.Name,
        Size = bytes,
        CreatedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt,
        SenderDeviceId = item.SenderDeviceId is { Length: > 0 } sender ? sender : "paired-device",
        SenderName = item.SenderName is { Length: > 0 } name ? name : "配对设备",
        TargetDeviceId = Setting("deviceId"),
        Provenance = item.SenderDeviceId is { Length: > 0 } ? AssistantConversations.PairedInbox : null,
        SourceRelay = sourceRelay,
        TransportRoute = item.Kind == PublicInboxItemKind.Text ? null : sourceRelay == InboxRelays.Tail ? "tail-relay" : "public-relay",
        State = AssistantItemState.Available,
        LocalPath = path,
        BytesDone = bytes
    };

    // ---- deposit side: an independent send scheduler ---------------------------------------------

    /// <summary>
    /// Wakes the deposit scheduler. It is separate from the direct leg on purpose: a direct attempt may
    /// legitimately wait minutes for a first-contact confirmation, and that must never delay the public
    /// relay's durable copy (the same rule that keeps the relay leg independent).
    /// </summary>
    private void KickDeposit()
    {
        try { _depositSignal.Release(); }
        catch (SemaphoreFullException) { /* one pending run already covers this wake-up */ }
    }

    private void StartDepositLoop()
    {
        if (_depositLoop is not null) return;
        _depositLoop = Task.Run(DepositLoopAsync);
    }

    /// <summary>
    /// Sending and receipt confirmation have their own bounded scheduler: it blocks completely when there
    /// is nothing to do, retries with a bounded backoff while work remains (a 503 inbox, an unconfirmed
    /// receipt), and keeps running while receiving is disabled — stopping receiving stops sockets and the
    /// owner long poll, never the outgoing queue.
    /// </summary>
    private async Task DepositLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(2);
        var remaining = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            // While work is outstanding the wait is the backoff; with nothing to do it blocks until a
            // signal. The signal is retained, so a send that arrives during a run is not lost.
            try { await _depositSignal.WaitAsync(remaining > 0 ? backoff : Timeout.InfiniteTimeSpan, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            remaining = 0;
            _depositRuns++;
            try
            {
                var identity = Identity();
                remaining += await ConfirmDepositReceiptsAsync(_lifetime.Token);
                if (_conversationId.Length > 0) remaining += await DepositPendingAsync(identity, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _depositLastError = MptLogRedactor.Redact(ex.Message);
                NoteInboxUnavailable(_depositLastError);
                remaining = 1;
            }
            backoff = remaining > 0 ? TimeSpan.FromSeconds(Math.Min(5, backoff.TotalSeconds * 2)) : TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>
    /// Deposits the queued messages whose target is a paired device. Returns how many entries still need
    /// work. The entry only becomes <c>delivered</c> after the owner acknowledged a saved file; a deposit
    /// that is not accepted yet stays queued with a retryable reason, while a permanent failure is parked
    /// as <c>failed</c> and only the user's explicit retry puts it back in the queue.
    /// </summary>
    private async Task<int> DepositPendingAsync(AssistantIdentity identity, CancellationToken token)
    {
        var store = _assistantStore!;
        var state = await store.LoadAsync(token);
        var candidates = state.Outgoing(identity.DeviceId)
            .Where(item => item.TargetDeviceId is { Length: > 0 } && item.Receipts.Count == 0
                && AssistantConversations.IsPrivate(item)
                && (!CloudOnly || item.Kind == AssistantItemKind.Text)
                && (item.State == AssistantItemState.Queued || NeedsPublicFallback(item)))
            .OrderBy(item => item.Attempts).ThenBy(item => item.CreatedAt).ToArray();
        var pending = candidates.Length > 8 ? 1 : 0;
        foreach (var item in candidates.Take(8))
        {
            token.ThrowIfCancellationRequested();
            if (CloudOnly && item.Kind != AssistantItemKind.Text) continue;
            var target = item.TargetDeviceId!;
            try
            {
                if (CustomRelayConfigured)
                {
                    await SetDepositStateAsync(store, item.Id, AssistantItemState.Failed,
                        "当前使用自定义存储，私聊仅通过设备直连发送，不会转存到内置中转。", token);
                    continue;
                }
                var inbox = await PeerInboxAsync(target, token);
                if (inbox is null)
                {
                    await SetDepositStateAsync(store, item.Id, AssistantItemState.Failed,
                        "对方是旧版配对码，没有中转投递权限；请让对方重新扫码后再发送。", token);
                    continue;
                }
                var routes = InboxRoutes;
                if (NeedsPublicFallback(item))
                {
                    foreach (var previous in routes.Where(route => item.DepositRoutes.Any(saved => saved.RelayId == route.Id && saved.StoredAt is not null)))
                    {
                        using var original = PublicInboxClient.Deposit(inbox, PublicInboxRetryPolicy.None, previous.Address);
                        try
                        {
                            var receipt = await ProbeDepositReceiptAsync(original, item.Id, token);
                            if (receipt?.Saved == true)
                                await ConfirmInboxReceiptAsync(store, item.Id, target, receipt, token);
                        }
                        catch (Exception ex) when (IsTransientDepositFailure(ex)) { }
                    }
                    if (item.Receipts.Count > 0 || item.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) continue;
                }
                // Once public was attempted, retries remain on that route. A stored Tail copy gets a
                // public copy only after the receipt grace, preserving its original id and provenance.
                if (item.DepositRoutes.Any(row => row.RelayId == InboxRelays.Public)
                    || item.DepositRoutes.Any(row => row.StoredAt is not null))
                    routes = routes.Where(route => route.Id == InboxRelays.Public).ToArray();
                Exception? lastFailure = null;
                foreach (var route in routes)
                {
                    if (item.Receipts.Count > 0 || item.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) break;
                    using var client = PublicInboxClient.Deposit(inbox, PublicInboxRetryPolicy.None, route.Address);
                    using var scope = ItemScope(item.Id, token);
                    try
                    {
                        // Probe this exact id before sending bytes. A lost PUT response may already have
                        // stored or delivered it; neither case needs another file upload.
                        var receipt = await ProbeDepositReceiptAsync(client, item.Id, scope.Token);
                        if (receipt is not null)
                        {
                            await RememberDepositRouteAsync(store, item.Id, route.Id, stored: true, token);
                            if (receipt.Saved) await ConfirmInboxReceiptAsync(store, item.Id, target, receipt, token);
                            else await SetDepositStateAsync(store, item.Id, AssistantItemState.Stored, null, token);
                            break;
                        }
                        await RememberDepositRouteAsync(store, item.Id, route.Id, stored: false, token);
                        await SetAssistantTransportRouteAsync(item.Id, route.Id == InboxRelays.Tail ? "tail-relay" : "public-relay", scope.Token);
                        if (item.Receipts.Count > 0 || item.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) break;
                        var payload = store.GetPayloadPath(item);
                        _ = item.Kind == AssistantItemKind.Text
                            ? await client.DepositTextAsync(item.Id, item.Text ?? "", identity.DeviceId, identity.Name, item.CreatedAt, target, scope.Token)
                            : await client.DepositFileAsync(item.Id, payload ?? throw new IOException("待发副本不存在。"), item.Name, identity.DeviceId, identity.Name, item.CreatedAt, target, scope.Token);
                        if (scope.IsCancellationRequested) break;
                        await RememberDepositRouteAsync(store, item.Id, route.Id, stored: true, token);
                        await SetDepositStateAsync(store, item.Id, AssistantItemState.Stored, null, token);
                        NoteInboxAvailable();
                        lastFailure = null;
                        break;
                    }
                    catch (Exception ex) when (IsTransientDepositFailure(ex))
                    {
                        lastFailure = ex;
                        // Only a transient error moves to the next fixed route. Rejected credentials,
                        // redirects, malformed metadata and other permanent failures leave this loop.
                    }
                }
                if (lastFailure is not null)
                    await SetDepositStateAsync(store, item.Id,
                        item.DepositRoutes.Any(route => route.StoredAt is not null) ? AssistantItemState.Stored : AssistantItemState.Queued,
                        lastFailure.Message, token);
                if (item.Receipts.Count == 0 && item.State is AssistantItemState.Queued or AssistantItemState.Stored) pending++;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { pending++; }
            catch (Exception ex) when (ex is PublicInboxException or IOException or InvalidOperationException or ArgumentException)
            {
                var retryable = IsTransientDepositFailure(ex);
                await SetDepositStateAsync(store, item.Id,
                    retryable ? AssistantItemState.Queued : AssistantItemState.Failed,
                    ex is FileNotFoundException or DirectoryNotFoundException ? "待发副本不存在。" : ex.Message, token);
                if (retryable) pending++;
            }
        }
        return pending;
    }

    private static bool NeedsPublicFallback(AssistantItem item) => item.State == AssistantItemState.Stored
        && !item.DepositRoutes.Any(route => route.RelayId == InboxRelays.Public && route.StoredAt is not null)
        && item.DepositRoutes.Any(route => route.RelayId == InboxRelays.Tail && route.StoredAt is { } at
            && DateTimeOffset.UtcNow - at >= InboxRelays.FallbackDelay);

    private static async Task<PublicInboxReceipt?> ProbeDepositReceiptAsync(PublicInboxClient client, string itemId, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try { return await client.GetReceiptAsync(itemId, budget.Token); }
        catch (PublicInboxNotFoundException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new PublicInboxUnavailableException("中转服务暂时无法连接。"); }
    }

    private static Task RememberDepositRouteAsync(AssistantStore store, string itemId, string relayId, bool stored, CancellationToken token) =>
        store.MutateAsync(state =>
        {
            var item = state.Find(itemId);
            if (item is null || item.State is AssistantItemState.Cancelled or AssistantItemState.Delivered) return;
            var old = item.DepositRoutes.FirstOrDefault(route => route.RelayId == relayId);
            var now = DateTimeOffset.UtcNow;
            var route = new AssistantDepositRoute(relayId, old?.AttemptedAt ?? now, stored ? old?.StoredAt ?? now : old?.StoredAt);
            item.DepositRoutes = [.. item.DepositRoutes.Where(row => row.RelayId != relayId), route];
            if (!stored) item.Attempts++;
        }, token);

    /// <summary>
    /// True when retrying the same deposit can succeed later with no user action: the inbox is not
    /// registered yet, the relay is throttled or busy, the transport failed, or the relay answered a
    /// server-side 5xx (which its own bounded retry may already have exhausted). Every other failure in
    /// this path — a rejected credential, an oversized entry, a missing local copy, a malformed response
    /// — is permanent, and the entry waits for the user instead of being re-uploaded every few seconds.
    /// </summary>
    private static bool IsTransientDepositFailure(Exception exception) =>
        exception is PublicInboxUnavailableException
        || exception is PublicInboxException
        {
            Status: HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
        };

    private async Task SetDepositStateAsync(AssistantStore store, string itemId, AssistantItemState state, string? message, CancellationToken token)
    {
        var changed = false;
        var error = message is null ? null : MptLogRedactor.Redact(message);
        await store.MutateIfChangedAsync(current =>
        {
            var row = current.Find(itemId);
            if (row is null || row.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) return false;
            var bytesDone = state == AssistantItemState.Stored ? row.Size : row.BytesDone;
            if (row.State == state && row.Error == error && row.BytesDone == bytesDone) return false;
            row.State = state;
            row.Error = error;
            row.BytesDone = bytesDone;
            return changed = true;
        }, token);
        // Deposit runs independently of conversation sync. Notify only after its visible state has
        // committed, so the last send refreshes without another send, receipt or page navigation.
        // Repeated identical transient errors and receipt-only scheduler rounds stay silent.
        if (changed) EmitAssistantChanged("inbox.deposit");
    }

    /// <summary>
    /// Reads the receipt of every deposited entry that has not been confirmed yet and only then marks it
    /// delivered. This runs in the send scheduler, so it keeps working while receiving is stopped and it
    /// never waits behind the owner's 25 second long poll. Returns how many receipts are still outstanding.
    /// </summary>
    private async Task<int> ConfirmDepositReceiptsAsync(CancellationToken token)
    {
        var store = _assistantStore;
        if (store is null || CustomRelayConfigured) return 0;
        var state = await store.LoadAsync(token);
        var candidates = state.Outgoing(Setting("deviceId"))
            .Where(item => AssistantConversations.IsPrivate(item)
                && item.State is AssistantItemState.Stored or AssistantItemState.Queued && item.Receipts.Count == 0
                && item.TargetDeviceId is { Length: > 0 } target && PeerInboxId(target).Length > 0
                && (item.DepositRoutes.Count > 0 || item.State == AssistantItemState.Stored))
            .OrderBy(item => item.ReceiptCheckedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.CreatedAt).ToArray();
        var pending = candidates.Take(8).ToArray();
        var outstanding = candidates.Length - pending.Length;
        foreach (var item in pending)
        {
            token.ThrowIfCancellationRequested();
            var inbox = await PeerInboxAsync(item.TargetDeviceId!, token);
            if (inbox is null) continue;
            // Old stored entries were sent only to public. Missing new metadata never redirects them.
            var routeIds = item.DepositRoutes.Count == 0 ? [InboxRelays.Public]
                : item.DepositRoutes.Select(route => route.RelayId).ToArray();
            foreach (var route in InboxRoutes.Where(route => routeIds.Contains(route.Id)))
            {
                if (item.Receipts.Count > 0 || item.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) break;
                try
                {
                    using var client = PublicInboxClient.Deposit(inbox, PublicInboxRetryPolicy.None, route.Address);
                    using var scope = ItemScope(item.Id, token);
                    await store.MutateAsync(current =>
                    {
                        if (current.Find(item.Id) is { } row) row.ReceiptCheckedAt = DateTimeOffset.UtcNow;
                    }, token);
                    var receipt = await ProbeDepositReceiptAsync(client, item.Id, scope.Token);
                    _depositConfirmReads++;
                    if (receipt?.Saved == true)
                        await ConfirmInboxReceiptAsync(store, item.Id, item.TargetDeviceId!, receipt, token);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { }
                catch (PublicInboxException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // In particular, an auth failure on Tail cannot cause a copy to be sent to public.
                    await SetDepositStateAsync(store, item.Id, AssistantItemState.Failed, ex.Message, token);
                    break;
                }
                catch (Exception ex) when (ex is PublicInboxException or IOException or InvalidOperationException or ArgumentException)
                { NoteInboxUnavailable(MptLogRedactor.Redact(ex.Message)); }
            }
            if (item.Receipts.Count == 0 && item.State is AssistantItemState.Stored or AssistantItemState.Queued) outstanding++;
        }
        return outstanding;
    }

    private async Task ConfirmInboxReceiptAsync(AssistantStore store, string itemId, string target, PublicInboxReceipt receipt, CancellationToken token)
    {
        var confirmed = false;
        await store.MutateIfChangedAsync(current =>
        {
            var row = current.Find(itemId);
            if (row is null || row.State is AssistantItemState.Cancelled or AssistantItemState.Delivered) return false;
            MergeInboxReceipt(current, row, target, receipt);
            row.State = AssistantItemState.Delivered;
            row.Error = null;
            return confirmed = true;
        }, token);
        if (confirmed)
        {
            _depositConfirmMarked++;
            NoteInboxAvailable();
            EmitAssistantChanged("inbox.delivered");
        }
    }

    /// <summary>
    /// Records the owner's receipt on the sender's entry. The values come from the receipt the owner
    /// wrote after a real save, so nothing here invents a delivery.
    /// </summary>
    private static void MergeInboxReceipt(AssistantState state, AssistantItem item, string deviceId, PublicInboxReceipt receipt)
    {
        item.Receipts.RemoveAll(row => string.Equals(row.DeviceId, deviceId, StringComparison.Ordinal));
        item.Receipts.Add(new AssistantReceipt
        {
            ItemId = item.Id,
            DeviceId = deviceId,
            DeviceName = receipt.DeviceName is { Length: > 0 } name ? name : deviceId,
            SavedAt = receipt.SavedAt ?? DateTimeOffset.UtcNow,
            Bytes = receipt.Bytes ?? 0
        });
        state.Remember(item.Id);
    }

    private async Task WaitInboxEventAsync(TimeSpan wait)
    {
        try { await _inboxSignal.WaitAsync(wait, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    private void NoteInboxAvailable(string? routeId = null)
    {
        if (routeId is not null) _inboxHealth[routeId] = new(true, "", DateTimeOffset.UtcNow);
        lock (_stateLock)
        {
            _inboxMessage = "";
            _inboxCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    private void NoteInboxUnavailable(string message, string? routeId = null)
    {
        if (routeId is not null) _inboxHealth[routeId] = new(false, message, DateTimeOffset.UtcNow);
        lock (_stateLock)
        {
            _inboxMessage = message;
            _inboxCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>The inbox state the surface can show, next to the conversation relay state.</summary>
    private JsonObject InboxJson()
    {
        string message;
        DateTimeOffset? checkedAt;
        lock (_stateLock)
        {
            message = _inboxMessage;
            checkedAt = _inboxCheckedAt;
            _inboxAvailable = message.Length == 0 && checkedAt is not null;
        }
        var routes = new JsonArray();
        var healthyRoute = false;
        foreach (var route in InboxRoutes)
        {
            _inboxHealth.TryGetValue(route.Id, out var health);
            healthyRoute |= health?.Available == true;
            routes.Add(new JsonObject
            {
                ["id"] = route.Id,
                ["state"] = health is null ? "unknown" : health.Available ? "available" : "unavailable",
                ["message"] = health?.Message ?? "",
                ["revision"] = Math.Max(0, _inboxRevisions.GetValueOrDefault(route.Id, -1))
            });
        }
        // One unreachable relay cannot make a working receiving route appear unavailable.
        if (healthyRoute) { _inboxAvailable = true; message = ""; }
        return new JsonObject
        {
            ["configured"] = _inbox is not null,
            ["id"] = _inbox?.InboxId ?? "",
            ["state"] = _inbox is null ? "unknown" : _inboxAvailable ? "available" : checkedAt is null ? "unknown" : "unavailable",
            ["message"] = message,
            ["routes"] = routes,
            ["revision"] = Math.Max(0, _inboxRevision),
            ["lastItems"] = _inboxLastItems,
            ["rejected"] = _inboxRejected,
            ["depositRuns"] = _depositRuns,
            ["depositError"] = _depositLastError,
            ["confirmReads"] = _depositConfirmReads,
            ["confirmMarked"] = _depositConfirmMarked
        };
    }
}
