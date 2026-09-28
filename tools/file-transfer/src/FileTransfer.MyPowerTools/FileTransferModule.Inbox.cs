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

    private string? PeerInboxId(string deviceId) => FindPeer(deviceId) is { } peer ? PeerText(peer, "inboxId") : "";

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
        var backoff = TimeSpan.FromSeconds(2);
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_receivingEnabled)
            {
                await WaitInboxEventAsync(Timeout.InfiniteTimeSpan);
                continue;
            }
            var poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _inboxCts = poll;
            try
            {
                var identity = await EnsureInboxAsync(poll.Token);
                using var client = PublicInboxClient.Owner(identity);
                await client.RegisterAsync(poll.Token);
                NoteInboxAvailable();
                while (_receivingEnabled && !poll.IsCancellationRequested)
                {
                    var page = await client.PollAsync(_inboxRevision < 0 ? null : _inboxRevision, token: poll.Token);
                    if (page.Revision != _inboxRevision) _inboxRevision = page.Revision;
                    _inboxLastItems = page.Items.Count;
                    NoteInboxAvailable();
                    var failed = false;
                    foreach (var item in page.Items)
                    {
                        if (!await ReceiveInboxItemAsync(client, item, poll.Token)) failed = true;
                    }
                    if (page.HasMore) _inboxRevision = -1;
                    // A failure backs off before the next round; additional pages do not wait for a new upload.
                    if (failed) { await WaitInboxEventAsync(backoff); backoff = Bump(backoff); }
                    else backoff = TimeSpan.FromSeconds(2);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* receive.stop or a new identity cancelled this poll */ }
            catch (PublicInboxAuthException ex) { NoteInboxUnavailable(ex.Message); await WaitInboxEventAsync(TimeSpan.FromMinutes(5)); }
            catch (Exception ex)
            {
                NoteInboxUnavailable(MptLogRedactor.Redact(ex.Message));
                await WaitInboxEventAsync(backoff);
                backoff = Bump(backoff);
            }
            finally
            {
                if (ReferenceEquals(_inboxCts, poll)) _inboxCts = null;
                poll.Dispose();
            }
        }
    }

    private static TimeSpan Bump(TimeSpan backoff) =>
        TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(2).Ticks));

    /// <summary>
    /// One deposited item: reuse an already saved copy, otherwise download once, adopt it into the durable
    /// store, and only then write the receipt the sender reads. The item is deliberately never deleted,
    /// because deleting it would also remove the receipt the sender still has to confirm.
    /// Returns false when this item needs a backoff before the next attempt.
    /// </summary>
    private async Task<bool> ReceiveInboxItemAsync(PublicInboxClient client, PublicInboxItem item, CancellationToken token)
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
            if (existing is { State: AssistantItemState.Available, LocalPath: { Length: > 0 } localCopy } && File.Exists(localCopy))
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
                await store.AdoptAsync(Identity(), AdoptedItem(item, null, bytes, text), token);
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
                    await store.AdoptAsync(Identity(), AdoptedItem(item, saved, bytes, null), token);
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

    private AssistantItem AdoptedItem(PublicInboxItem item, string? path, long bytes, string? text) => new()
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
                if (_conversationId.Length > 0) remaining += await DepositPendingAsync(identity, _lifetime.Token);
                remaining += await ConfirmDepositReceiptsAsync(_lifetime.Token);
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
        // The scheduler owns only what is still queued. Stored means the deposit already succeeded and
        // only the owner's receipt is outstanding, and Failed means a permanent failure the user has not
        // retried: neither may be re-uploaded by an automatic round.
        var candidates = state.Outgoing(identity.DeviceId)
            .Where(item => item.TargetDeviceId is { Length: > 0 } && item.Receipts.Count == 0
                && !IsConversationMember(item.TargetDeviceId)
                && item.State == AssistantItemState.Queued)
            .OrderBy(item => item.Attempts).ThenBy(item => item.CreatedAt).ToArray();
        var pending = 0;
        foreach (var item in candidates.Take(8))
        {
            token.ThrowIfCancellationRequested();
            var target = item.TargetDeviceId!;
            try
            {
                var inbox = await PeerInboxAsync(target, token);
                if (inbox is null)
                {
                    // An old pairing code has no deposit permission: say so instead of pretending to
                    // send, and park the entry — re-scanning the code is a user action, not a retry.
                    await SetDepositStateAsync(store, item.Id, AssistantItemState.Failed,
                        "对方是旧版配对码，没有公网投递权限；请让对方重新扫码后再发送。", token);
                    continue;
                }
                // Client construction validates the stored credential; a bad pairing must surface on the
                // entry, never disappear into a generic sync failure.
                using var client = PublicInboxClient.Deposit(inbox);
                await store.MutateAsync(current =>
                {
                    if (current.Find(item.Id) is { } row) row.Attempts++;
                }, token);
                if (item.State != AssistantItemState.Stored)
                {
                    var payload = store.GetPayloadPath(item);
                    // The same itemId is reused on every retry, which is what makes the relay's duplicate
                    // detection work; a text needs no payload file. The item scope is what makes the user's
                    // cancel abort the upload instead of letting it finish.
                    using var scope = ItemScope(item.Id, token);
                    _ = item.Kind == AssistantItemKind.Text
                        ? await client.DepositTextAsync(item.Id, item.Text ?? "", identity.DeviceId, identity.Name, item.CreatedAt, target, scope.Token)
                        : await client.DepositFileAsync(item.Id, payload ?? throw new IOException("待发副本不存在。"), item.Name, identity.DeviceId, identity.Name, item.CreatedAt, target, scope.Token);
                    if (scope.IsCancellationRequested && !token.IsCancellationRequested) continue;
                    await store.MutateAsync(current =>
                    {
                        var row = current.Find(item.Id);
                        if (row is null || row.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) return;
                        row.State = AssistantItemState.Stored;
                        row.Error = null;
                        row.BytesDone = row.Size;
                    }, token);
                    // Real success only: a 503 or a rejected key must never mark the inbox healthy.
                    NoteInboxAvailable();
                }
                pending++;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // The user cancelled this item (or its scope went stale): keep the loop alive.
                pending++;
            }
            catch (Exception ex) when (ex is PublicInboxException or IOException or InvalidOperationException or ArgumentException)
            {
                // A transient relay state stays queued and recovers on its own; a permanent one is parked
                // in failed until the user explicitly retries it, so the same bytes are never re-uploaded
                // by every automatic round.
                var retryable = IsTransientDepositFailure(ex);
                await SetDepositStateAsync(store, item.Id,
                    retryable ? AssistantItemState.Queued : AssistantItemState.Failed,
                    ex is FileNotFoundException or DirectoryNotFoundException ? "待发副本不存在。" : ex.Message, token);
                if (retryable) pending++;
            }
        }
        // A full batch must schedule the next batch even if this one needed no retry. Only an entry that
        // is still queued counts: a parked failure must not keep the scheduler awake.
        if (candidates.Any(item => item.State == AssistantItemState.Queued)) pending++;
        return pending;
    }

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

    private static async Task SetDepositStateAsync(AssistantStore store, string itemId, AssistantItemState state, string message, CancellationToken token)
    {
        await store.MutateAsync(current =>
        {
            var row = current.Find(itemId);
            if (row is null || row.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) return;
            row.State = state;
            row.Error = MptLogRedactor.Redact(message);
        }, token);
    }

    /// <summary>
    /// Reads the receipt of every deposited entry that has not been confirmed yet and only then marks it
    /// delivered. This runs in the send scheduler, so it keeps working while receiving is stopped and it
    /// never waits behind the owner's 25 second long poll. Returns how many receipts are still outstanding.
    /// </summary>
    private async Task<int> ConfirmDepositReceiptsAsync(CancellationToken token)
    {
        var store = _assistantStore;
        if (store is null) return 0;
        var state = await store.LoadAsync(token);
        var outstanding = 0;
        var candidates = state.Outgoing(Setting("deviceId"))
            .Where(item => item.State == AssistantItemState.Stored && item.Receipts.Count == 0
                && item.TargetDeviceId is { Length: > 0 } target && PeerInboxId(target)?.Length > 0)
            .OrderBy(item => item.ReceiptCheckedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.CreatedAt).ToArray();
        var pending = candidates.Take(8).ToArray();
        outstanding = candidates.Length - pending.Length;
        foreach (var item in pending)
        {
            token.ThrowIfCancellationRequested();
            var target = item.TargetDeviceId!;
            try
            {
                var inbox = await PeerInboxAsync(target, token);
                if (inbox is null) continue;
                using var client = PublicInboxClient.Deposit(inbox);
                using var scope = ItemScope(item.Id, token);
                await store.MutateAsync(current =>
                {
                    if (current.Find(item.Id) is { } row) row.ReceiptCheckedAt = DateTimeOffset.UtcNow;
                }, token);
                var receipt = await client.GetReceiptAsync(item.Id, scope.Token);
                _depositConfirmReads++;
                if (!receipt.Saved) { outstanding++; continue; }
                var confirmed = 0;
                await store.MutateAsync(current =>
                {
                    var row = current.Find(item.Id);
                    if (row is null || row.State == AssistantItemState.Cancelled) return;
                    MergeInboxReceipt(current, row, target, receipt);
                    row.State = AssistantItemState.Delivered;
                    row.Error = null;
                    confirmed = 1;
                }, token);
                _depositConfirmMarked += confirmed;
                if (confirmed == 0) _depositLastError = "回执已读回，但条目不在可确认状态。";
                NoteInboxAvailable();
                EmitAssistantChanged("inbox.delivered");
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { throw; }
            catch (OperationCanceledException)
            {
                // A cancelled item keeps its stored state and is confirmed on a later round.
                outstanding++;
            }
            catch (Exception ex) when (ex is PublicInboxException or IOException or InvalidOperationException or ArgumentException)
            {
                // A receipt read is best effort; the entry keeps its stored state and is retried later.
                NoteInboxUnavailable(MptLogRedactor.Redact(ex.Message));
                outstanding++;
            }
        }
        return outstanding;
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

    private void NoteInboxAvailable()
    {
        lock (_stateLock)
        {
            _inboxMessage = "";
            _inboxCheckedAt = DateTimeOffset.UtcNow;
        }
    }

    private void NoteInboxUnavailable(string message)
    {
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
        return new JsonObject
        {
            ["configured"] = _inbox is not null,
            ["id"] = _inbox?.InboxId ?? "",
            ["state"] = _inbox is null ? "unknown" : _inboxAvailable ? "available" : checkedAt is null ? "unknown" : "unavailable",
            ["message"] = message,
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
