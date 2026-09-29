using System.Text.Json;

namespace FileTransfer.Core.Assistant;

/// <summary>Per-sync request budget. Everything above these numbers waits for the next round.</summary>
public sealed record AssistantSyncLimits(int Publish = 8, int Pull = 50, int Download = 4, int Receipts = 12,
    int ReceiptWindowDays = 7, int RetryDelaySeconds = 30)
{
    public static AssistantSyncLimits Default { get; } = new();
}

/// <summary>
/// Outcome of one pass. <see cref="HasMore"/> is only true when the relay is healthy and more work can start
/// immediately; <see cref="RetryAfter"/> is set instead when the relay could not be used, so the caller waits
/// for that interval (or a connectivity event) rather than spinning.
/// </summary>
public sealed record AssistantSyncResult(int Published, int Failed, int Received, int Downloaded,
    int ReceiptsWritten, int ReceiptsRead, bool HasMore, string? Message,
    int ReceiptsChecked = 0, TimeSpan? RetryAfter = null,
    IReadOnlyList<AssistantMemberProof>? Verified = null);

/// <summary>
/// Proof of conversation membership, produced only by data this pass really read back over the shared
/// conversation (a manifest listed from the relay, or a receipt fetched by <c>ListAssistantReceiptsAsync</c>).
/// Local store contents are never proof: an entry that arrived through a device-pairing inbox lands in the
/// same store, and it must not make its sender an own device of the conversation.
/// </summary>
public sealed record AssistantMemberProof(string DeviceId, string Name);

/// <summary>
/// One reusable synchronisation pass: publish what is pending, resolve real receipts, pull new entries and
/// save the attachments addressed to this device. The <c>assistant.sync</c> command and any background
/// scheduler must both call this, so there is exactly one implementation of the queue and recovery rules.
/// All instances sharing one <see cref="AssistantStore"/> are serialized on that store, so M4 may create a
/// new instance per round; store transactions stay short and never wait for the network.
/// </summary>
public sealed class AssistantSync
{
    private readonly AssistantStore _store;
    private readonly OpenListClient _client;
    private readonly AssistantSyncLimits _limits;

    /// <summary>
    /// Optional per-item cancellation. When set, one item's upload or download is linked to the token
    /// this hook returns for that id, so the module can stop exactly that transfer without ending the
    /// pass. Everything else (transactions, restore rules, retry budget) stays as it is, and a null
    /// hook keeps the original pass-wide behaviour.
    /// </summary>
    public Func<string, CancellationToken>? ItemCancellation { get; set; }

    /// <summary>
    /// Optional: returns false for an entry this conversation must not publish. The module uses it so a
    /// message addressed to a device that is not part of the conversation is never written into the
    /// sender's own namespace as if the target could read it. A filtered entry also stops counting as
    /// pending work, so it cannot make the scheduler spin.
    /// </summary>
    public Func<AssistantItem, bool>? PublishFilter { get; set; }

    /// <summary>Visible committed transitions (false), or changed in-memory transfer progress (true).
    /// The host may throttle progress; final states must be reported immediately.</summary>
    public Action<bool>? Changed { get; set; }

    private async Task MutateItemAsync(string itemId, Action<AssistantState> change, CancellationToken token)
    {
        var visibleChange = false;
        await _store.MutateAsync(state =>
        {
            var before = state.Find(itemId)?.Copy();
            change(state);
            var after = state.Find(itemId);
            visibleChange = before is not null && after is not null &&
                (before.State != after.State || before.BytesDone != after.BytesDone || before.Error != after.Error
                 || before.LocalPath != after.LocalPath || !before.Receipts.SequenceEqual(after.Receipts));
        }, token);
        // Notify after the transaction releases its gate and successfully persists; inspectors read
        // the committed store, never a potentially stale transport-owned snapshot.
        if (visibleChange) Changed?.Invoke(false);
    }

    private void ReportProgress(string itemId, long done)
    {
        if (_store.ReportProgress(itemId, done)) Changed?.Invoke(true);
    }

    private CancellationTokenSource ItemScope(string itemId, CancellationToken token) =>
        ItemCancellation is { } hook
            ? CancellationTokenSource.CreateLinkedTokenSource(token, hook(itemId))
            : CancellationTokenSource.CreateLinkedTokenSource(token);

    public AssistantSync(AssistantStore store, OpenListClient client, AssistantSyncLimits? limits = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _limits = limits ?? AssistantSyncLimits.Default;
    }

    /// <summary>
    /// Cancelling this call never loses work: an in-flight entry returns to <c>queued</c>/<c>stored</c>,
    /// which is also what a restart produces.
    /// </summary>
    public async Task<AssistantSyncResult> SyncAsync(AssistantIdentity identity, CancellationToken token)
    {
        AssistantValidation.Identity(identity);
        await _store.SyncGate.WaitAsync(token);
        try
        {
            return await SyncCoreAsync(identity, token);
        }
        finally { _store.SyncGate.Release(); }
    }

    private async Task<AssistantSyncResult> SyncCoreAsync(AssistantIdentity identity, CancellationToken token)
    {
        var state = await _store.ConfigureAsync(identity, token);
        var published = 0;
        var failed = 0;
        var received = 0;
        var downloaded = 0;
        var written = 0;
        var read = 0;
        var checkedReceipts = 0;
        var relayUnhealthy = false;

        // Every authoritative transition below goes through a store transaction: the snapshot is never edited
        // outside the gate, so a write failure restores the last committed generation and a concurrent command
        // can never serialize a half-updated record.

        // 1. Publish local pending entries. Fewer previous attempts first, so one bad entry never starves new ones.
        var queue = state.Outgoing(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
            .Where(item => item.State is AssistantItemState.Queued or AssistantItemState.Failed)
            .Where(item => PublishFilter?.Invoke(item) ?? true)
            .OrderBy(item => item.Attempts).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(_limits.Publish).ToArray();
        foreach (var item in queue)
        {
            token.ThrowIfCancellationRequested();
            using var scope = ItemScope(item.Id, token);
            var error = await PublishAsync(state, identity, item, scope.Token);
            // The user cancelled this single item: the store transaction already refused to turn it into
            // a success, and the module owns the final cancelled state.
            if (scope.IsCancellationRequested && !token.IsCancellationRequested) continue;
            if (error is null) { published++; continue; }
            failed++;
            // The relay answered badly or not at all: no later stage may keep talking to it this round.
            if (IsRelayFailure(error)) { relayUnhealthy = true; break; }
        }

        // 2. Real receipts turn a targeted send into "delivered"; a self send keeps its per-device receipts.
        // The order is a bounded fair rotation: never-confirmed entries first, then the least recently checked.
        string? pullError = null;
        var verified = new List<AssistantMemberProof>();
        if (!relayUnhealthy)
        {
            var now = DateTimeOffset.UtcNow;
            var checks = state.Outgoing(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
                .Where(item => NeedsReceiptCheck(item, now))
                .OrderBy(item => item.TargetDeviceId is not null ? 0 : item.Receipts.Count == 0 ? 1 : 2)
                .ThenBy(item => item.ReceiptCheckedAt ?? DateTimeOffset.MinValue)
                .ThenBy(item => item.CreatedAt)
                .Take(_limits.Receipts).ToArray();
            foreach (var item in checks)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<AssistantReceipt> receipts;
                try { receipts = await _client.ListAssistantReceiptsAsync(identity.ConversationId, item.Id, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRelayFailure(ex)) { relayUnhealthy = true; break; }
                read += receipts.Count;
                checkedReceipts++;
                // Only receipts actually read from the shared conversation prove their writer is a member.
                foreach (var receipt in receipts)
                {
                    if (receipt.DeviceId.Length > 0 && receipt.DeviceId != identity.DeviceId)
                        verified.Add(new AssistantMemberProof(receipt.DeviceId, receipt.DeviceName));
                }
                var itemId = item.Id;
                await MutateItemAsync(itemId, changed =>
                {
                    var live = changed.Find(itemId);
                    if (live is null || live.State == AssistantItemState.Cancelled) return;
                    live.ReceiptCheckedAt = DateTimeOffset.UtcNow;
                    if (receipts.Count == 0) return;
                    MergeReceipts(live, receipts);
                    if (live.TargetDeviceId is not null && receipts.Any(receipt => receipt.DeviceId == live.TargetDeviceId))
                        live.State = AssistantItemState.Delivered;
                }, token);
            }
        }

        // 3. Pull manifests that were never seen before; entries are deduplicated by their immutable id.
        // An unreachable relay must not throw away the work already done above: it is reported instead.
        AssistantPage page = new([], [], false, 0, null);
        if (!relayUnhealthy)
        {
            try
            {
                page = await _client.ListAssistantAsync(identity.ConversationId,
                    new AssistantListRequest(_limits.Pull, state.KnownRemoteIds), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsRelayFailure(ex))
            {
                pullError = ex.Message;
                relayUnhealthy = true;
            }
            if (!relayUnhealthy)
            {
                // A manifest listed from the shared conversation proves its writer holds its credentials.
                foreach (var manifest in page.Items)
                {
                    if (manifest.SenderDeviceId.Length > 0 && manifest.SenderDeviceId != identity.DeviceId)
                        verified.Add(new AssistantMemberProof(manifest.SenderDeviceId, manifest.SenderName));
                }
                // Oldest discovery first, so the newest ids stay in front of the bounded known set.
                var known = page.Items.Select(manifest => manifest.Id).Reverse().ToArray();
                var invalid = page.InvalidItemIds.ToArray();
                var discovered = page.Items
                    .Where(manifest => state.Find(manifest.Id) is null)
                    .Select(manifest => FromManifest(manifest, identity.ConversationId)).ToArray();
                if (known.Length > 0 || invalid.Length > 0 || discovered.Length > 0)
                {
                    // One transaction: bookkeeping and a whole page of entries commit together or not at all.
                    await _store.MutateAsync(changed =>
                    {
                        changed.RememberAll(known);
                        changed.RememberAll(invalid);
                        foreach (var item in discovered) changed.Add(item);
                    }, token);
                    if (discovered.Length > 0) Changed?.Invoke(false);
                    received += discovered.Length;
                }
            }
        }

        // 4. Local-only bookkeeping: text needs no payload, so it becomes available without any relay call.
        var texts = state.Incoming(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
            .Where(item => item.Kind == AssistantItemKind.Text && item.State == AssistantItemState.Stored
                && IsAddressedToMe(item, identity.DeviceId)).Select(item => item.Id).ToArray();
        if (texts.Length > 0)
        {
            var textChanged = false;
            await _store.MutateAsync(changed =>
            {
                foreach (var itemId in texts)
                {
                    var live = changed.Find(itemId);
                    if (live is null || live.State != AssistantItemState.Stored) continue;
                    live.State = AssistantItemState.Available;
                    live.Error = null;
                    textChanged = true;
                }
            }, token);
            if (textChanged) Changed?.Invoke(false);
        }

        if (!relayUnhealthy)
        {
            var downloads = state.Incoming(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
                .Where(item => item.Kind != AssistantItemKind.Text && IsAddressedToMe(item, identity.DeviceId)
                    && item.State is AssistantItemState.Stored or AssistantItemState.Failed)
                .OrderBy(item => item.Attempts).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(_limits.Download).ToArray();
            foreach (var item in downloads)
            {
                token.ThrowIfCancellationRequested();
                using var scope = ItemScope(item.Id, token);
                var error = await DownloadAsync(state, identity, item, scope.Token);
                // A cancelled item is not a relay failure and must not poison the rest of the pass.
                if (scope.IsCancellationRequested && !token.IsCancellationRequested) continue;
                if (error is null) { downloaded++; continue; }
                failed++;
                if (IsRelayFailure(error)) { relayUnhealthy = true; break; }
            }
        }

        // 5. A receipt is written only after the content was saved locally, and only for another device's entry.
        if (!relayUnhealthy)
        {
            var acknowledgements = state.Incoming(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
                .Where(item => item.State == AssistantItemState.Available && item.ReceiptAt is null
                    && IsAddressedToMe(item, identity.DeviceId))
                .OrderBy(item => item.CreatedAt).Take(_limits.Receipts).ToArray();
            foreach (var item in acknowledgements)
            {
                token.ThrowIfCancellationRequested();
                var receipt = new AssistantReceipt
                {
                    ItemId = item.Id,
                    DeviceId = identity.DeviceId,
                    DeviceName = identity.Name,
                    SavedAt = DateTimeOffset.UtcNow,
                    Bytes = item.Kind == AssistantItemKind.Text ? 0 : item.Size
                };
                try
                {
                    await _client.WriteAssistantReceiptAsync(identity.ConversationId, receipt, token);
                    var itemId = item.Id;
                    await MutateItemAsync(itemId, changed =>
                    {
                        var live = changed.Find(itemId);
                        if (live is null || live.State != AssistantItemState.Available) return;
                        live.ReceiptAt = receipt.SavedAt;
                        live.Error = null;
                    }, token);
                    written++;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRelayFailure(ex))
                {
                    // The entry itself is available; only the acknowledgement has to be retried next round.
                    var itemId = item.Id;
                    var reason = ex.Message;
                    await MutateItemAsync(itemId, changed =>
                    {
                        var live = changed.Find(itemId);
                        if (live is null || live.State != AssistantItemState.Available || live.ReceiptAt is not null) return;
                        live.Error = $"回执写入失败：{reason}";
                    }, token);
                    relayUnhealthy = true;
                    break;
                }
            }
        }

        // "More" claims immediately startable work, and only while the relay is usable and this round actually
        // got somewhere. A failed relay returns a controlled retry delay; a page that could not advance (for
        // example directories whose publish never completed) falls back to the normal cadence instead of
        // spinning, and those ids stay re-fetchable so a late manifest is still picked up later.
        var progressed = published > 0 || received > 0 || downloaded > 0 || written > 0 || checkedReceipts > 0
            || page.InvalidItemIds.Count > 0;
        var pending = state.Outgoing(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId))
                .Any(item => item.State == AssistantItemState.Queued && (PublishFilter?.Invoke(item) ?? true))
            || state.Incoming(identity.DeviceId).Where(item => AssistantConversations.IsShared(item, identity.ConversationId)).Any(item => item.Kind != AssistantItemKind.Text
                && IsAddressedToMe(item, identity.DeviceId) && item.State == AssistantItemState.Stored);
        var hasMore = !relayUnhealthy && progressed && (page.HasMore || pending);
        var message = $"发布 {published} 条，失败 {failed} 条，接收 {received} 条，下载 {downloaded} 个，回执 {written} 份。";
        if (pullError is not null) message += $" 拉取失败：{pullError}";
        return new(published, failed, received, downloaded, written, read, hasMore, message,
            checkedReceipts, relayUnhealthy ? TimeSpan.FromSeconds(_limits.RetryDelaySeconds) : null,
            verified.Count == 0 ? null : verified);
    }

    /// <summary>
    /// Backs <c>assistant.open</c>: downloads the entry first when it is not local yet, then reports whether
    /// a local path or the inline text is ready.
    /// </summary>
    public async Task<AssistantOpenTarget> EnsureLocalAsync(AssistantIdentity identity, string itemId, CancellationToken token)
    {
        AssistantValidation.Identity(identity);
        AssistantValidation.ItemId(itemId);
        await _store.SyncGate.WaitAsync(token);
        try
        {
            var state = await _store.ConfigureAsync(identity, token);
            var item = state.Find(itemId) ?? throw new KeyNotFoundException($"会话中没有这条记录：{itemId}。");
            if (AssistantContent.HasLocalContent(item)) return AssistantContent.OpenTarget(item);
            if (!AssistantConversations.IsShared(item, identity.ConversationId))
                throw new InvalidOperationException("这条记录不属于当前共享会话，无法从该会话下载。请重新连接原设备或会话。");
            if (item.Kind == AssistantItemKind.Text)
            {
                await MutateItemAsync(itemId, changed =>
                {
                    var live = changed.Find(itemId);
                    if (live is null || live.State == AssistantItemState.Cancelled) return;
                    live.State = AssistantItemState.Available;
                    live.Error = null;
                }, token);
                return AssistantContent.OpenTarget(state.Find(itemId) ?? item);
            }
            if (string.Equals(item.SenderDeviceId, identity.DeviceId, StringComparison.Ordinal))
                throw new IOException("本机待发副本不存在，无法打开，请重新发送。");

            var claimed = false;
            await MutateItemAsync(itemId, changed =>
            {
                var live = changed.Find(itemId);
                if (live is null || live.State == AssistantItemState.Cancelled) return;
                live.State = AssistantItemState.Downloading;
                live.Error = null;
                claimed = true;
            }, token);
            if (!claimed) throw new OperationCanceledException("条目已取消。");

            string? saved = null;
            Exception? failure = null;
            try
            {
                saved = await _client.DownloadAssistantAsync(identity.ConversationId, item.ToManifest(),
                    _store.GetInboxDirectory(item), (done, _) => ReportProgress(itemId, done), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await RestoreAsync(itemId, AssistantItemState.Stored, CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (saved is not null)
            {
                var path = saved;
                await MutateItemAsync(itemId, changed =>
                {
                    var live = changed.Find(itemId);
                    // A cancel, or content that a direct receive delivered meanwhile, always wins over this download.
                    if (live is null || live.State == AssistantItemState.Cancelled) return;
                    live.LocalPath = path;
                    live.State = AssistantItemState.Available;
                    live.BytesDone = live.Size;
                    live.Error = null;
                }, token);
            }
            else if (failure is not null)
            {
                await FailAsync(itemId, failure.Message, token);
            }
            return AssistantContent.OpenTarget(state.Find(itemId) ?? item);
        }
        finally { _store.SyncGate.Release(); }
    }

    private async Task<Exception?> PublishAsync(AssistantState state, AssistantIdentity identity, AssistantItem item, CancellationToken token)
    {
        var payload = _store.GetPayloadPath(item);
        if (item.Kind != AssistantItemKind.Text && (payload is null || !File.Exists(payload)))
        {
            await FailAsync(item.Id, "待发副本不存在，无法发送。", token);
            return new FileNotFoundException("待发副本不存在，无法发送。");
        }

        // Claim the entry inside a store transaction: a cancel that lands between the selection and the claim
        // wins, so a cancelled entry is never re-published by this round.
        var claimed = false;
        await MutateItemAsync(item.Id, changed =>
        {
            var live = changed.Find(item.Id);
            if (live is null || live.State is not (AssistantItemState.Queued or AssistantItemState.Failed)) return;
            live.State = AssistantItemState.Sending;
            live.Error = null;
            live.BytesDone = 0;
            claimed = true;
        }, token);
        if (!claimed) return new OperationCanceledException("条目已取消或已处理。");

        try
        {
            await _client.PublishAssistantAsync(identity.ConversationId, item.ToManifest(), payload,
                (done, _) => ReportProgress(item.Id, done), token);
            var stored = false;
            await MutateItemAsync(item.Id, changed =>
            {
                var live = changed.Find(item.Id);
                // Cancelled or moved on while the upload was in flight: nothing already stored can be recalled,
                // but the local record must not be turned back into a success.
                if (live is null || live.State != AssistantItemState.Sending) return;
                live.State = AssistantItemState.Stored;
                live.BytesDone = live.Size;
                live.Error = null;
                stored = true;
            }, token);
            return stored ? null : new OperationCanceledException("条目已取消。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RestoreAsync(item.Id, AssistantItemState.Queued, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(item.Id, ex.Message, token);
            return ex;
        }
    }

    private async Task<Exception?> DownloadAsync(AssistantState state, AssistantIdentity identity, AssistantItem item, CancellationToken token)
    {
        if (item.LocalPath is { Length: > 0 } local && File.Exists(local))
        {
            await MutateItemAsync(item.Id, changed =>
            {
                var live = changed.Find(item.Id);
                if (live is null || live.State == AssistantItemState.Cancelled) return;
                live.State = AssistantItemState.Available;
                live.Error = null;
            }, token);
            return null;
        }
        if (item.State == AssistantItemState.Cancelled) return new OperationCanceledException("条目已取消。");

        var claimed = false;
        await MutateItemAsync(item.Id, changed =>
        {
            var live = changed.Find(item.Id);
            if (live is null || live.State is not (AssistantItemState.Stored or AssistantItemState.Failed)) return;
            live.State = AssistantItemState.Downloading;
            live.Error = null;
            claimed = true;
        }, token);
        if (!claimed) return new OperationCanceledException("条目已取消或已处理。");

        try
        {
            var saved = await _client.DownloadAssistantAsync(identity.ConversationId, item.ToManifest(),
                _store.GetInboxDirectory(item), (done, _) => ReportProgress(item.Id, done), token);
            var received = false;
            await MutateItemAsync(item.Id, changed =>
            {
                var live = changed.Find(item.Id);
                if (live is null || live.State != AssistantItemState.Downloading) return;
                live.LocalPath = saved;
                live.State = AssistantItemState.Available;
                live.BytesDone = live.Size;
                live.Error = null;
                received = true;
            }, token);
            return received ? null : new OperationCanceledException("条目已取消。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RestoreAsync(item.Id, AssistantItemState.Stored, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await FailAsync(item.Id, ex.Message, token);
            return ex;
        }
    }

    /// <summary>Marks an in-flight entry failed without ever overwriting a concurrent cancel or arrival.</summary>
    private Task FailAsync(string itemId, string message, CancellationToken token) =>
        MutateItemAsync(itemId, changed =>
        {
            var live = changed.Find(itemId);
            if (live is null || live.State is AssistantItemState.Cancelled or AssistantItemState.Delivered
                or AssistantItemState.Available) return;
            live.State = AssistantItemState.Failed;
            live.Error = message;
            live.Attempts++;
        }, token);

    /// <summary>
    /// Puts an interrupted entry back into a retryable state, even while the caller is being cancelled — but
    /// only when the entry is still the in-flight claim of this very operation. A user cancel, a direct-receive
    /// upgrade to <c>available</c>, or a real receipt (<c>delivered</c>) that landed meanwhile always wins:
    /// a late network cancellation must never revive or downgrade them.
    /// </summary>
    private async Task RestoreAsync(string itemId, AssistantItemState target, CancellationToken token)
    {
        try
        {
            await MutateItemAsync(itemId, changed =>
            {
                var live = changed.Find(itemId);
                if (live is null) return;
                if (live.State is not (AssistantItemState.Sending or AssistantItemState.Downloading)) return;
                live.State = target;
                live.BytesDone = 0;
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The restart path demotes in-flight entries anyway, so a failed restore is not fatal.
        }
    }

    private bool NeedsReceiptCheck(AssistantItem item, DateTimeOffset now)
    {
        if (item.State != AssistantItemState.Stored) return false;
        if (item.TargetDeviceId is not null) return !item.Receipts.Any(receipt => receipt.DeviceId == item.TargetDeviceId);
        // Self sends have no single target. An entry no device ever confirmed stays eligible at any age; one
        // that already has receipts is refreshed only inside the window, and always via the fair rotation.
        return item.Receipts.Count == 0 || item.CreatedAt >= now.AddDays(-_limits.ReceiptWindowDays);
    }

    private static bool IsAddressedToMe(AssistantItem item, string deviceId) =>
        item.TargetDeviceId is null || string.Equals(item.TargetDeviceId, deviceId, StringComparison.Ordinal);

    /// <summary>Transport and protocol failures of the relay, as opposed to a bug in this process.</summary>
    private static bool IsRelayFailure(Exception exception) => exception is HttpRequestException or TaskCanceledException
        or IOException or JsonException or System.Xml.XmlException or InvalidDataException or NotSupportedException or UriFormatException;

    private static AssistantItem FromManifest(AssistantManifest manifest, string conversationId) => new()
    {
        Id = manifest.Id,
        Kind = manifest.Kind,
        Text = manifest.Text,
        Name = manifest.Name,
        Size = manifest.Size,
        CreatedAt = manifest.CreatedAt,
        SenderDeviceId = manifest.SenderDeviceId,
        SenderName = manifest.SenderName,
        TargetDeviceId = manifest.TargetDeviceId,
        ConversationId = conversationId,
        Provenance = AssistantConversations.SharedRelay,
        State = AssistantItemState.Stored
    };

    private static void MergeReceipts(AssistantItem item, IReadOnlyList<AssistantReceipt> receipts)
    {
        var merged = new Dictionary<string, AssistantReceipt>(StringComparer.Ordinal);
        foreach (var receipt in item.Receipts)
            if (receipt is not null) merged[receipt.DeviceId] = receipt;
        foreach (var receipt in receipts)
            if (!merged.TryGetValue(receipt.DeviceId, out var previous) || receipt.SavedAt > previous.SavedAt)
                merged[receipt.DeviceId] = receipt;
        item.Receipts = [.. merged.Values.OrderBy(receipt => receipt.SavedAt)];
    }
}
