using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;

namespace FileTransfer.MyPowerTools;

public sealed partial class FileTransferModule
{
    private CloudPayloadStore? _cloudPayloads;
    private Task? _cloudPayloadWorker;
    private readonly SemaphoreSlim _cloudPayloadSignal = new(0, 1);
    private volatile bool _cloudTransportAvailable;
    private CloudAccount? DefaultCloudAccount => _cloudAccounts.Accounts.FirstOrDefault(a =>
        a.Id == _cloudAccounts.Preferences.DefaultAccountId && a.Status == "ready");

    private void StartCloudPayloadWorker()
    {
        _cloudPayloads ??= new CloudPayloadStore(Path.Combine(_data, "cloud-payloads.json"));
        _cloudPayloadWorker ??= Task.Run(CloudPayloadLoopAsync);
    }
    private void WakeCloudPayloads()
    {
        try { _cloudPayloadSignal.Release(); } catch (SemaphoreFullException) { }
    }
    private async Task StopNonCloudAttachmentsAsync(CancellationToken token)
    {
        if (_assistantStore is null) return;
        var state = await _assistantStore.LoadAsync(token);
        foreach (var item in state.Outgoing(Setting("deviceId")).Where(i => i.Kind != AssistantItemKind.Text))
        {
            if (_itemCancellation.TryRemove(item.Id, out var scope))
            {
                try { scope.Cancel(); } finally { scope.Dispose(); }
            }
        }
        await _assistantStore.MutateAsync(current =>
        {
            foreach (var item in current.Outgoing(Setting("deviceId")).Where(i => i.Kind != AssistantItemKind.Text && i.State is AssistantItemState.Queued or AssistantItemState.Sending or AssistantItemState.Failed))
            { item.State = AssistantItemState.Queued; item.Error = CloudAccountRules.PendingReason; }
        }, token);
        EmitAssistantChanged("cloud-policy");
    }
    private async Task<bool> PublishCloudAttachmentAsync(AssistantIdentity identity, AssistantManifest message, string? payload, CancellationToken token)
    {
        if (message.Kind == AssistantItemKind.Text) return false;
        if (message.TargetDeviceId is not null) throw new InvalidOperationException("私聊附件不能进入共享网盘领取通道。");
        if (identity.ConversationId != _conversationId) throw new InvalidOperationException("共享会话已切换，原待发附件已保留。");
        var conversation = identity.ConversationId;
        var conversationKey = _conversationKey;
        var account = DefaultCloudAccount;
        if (account is null || CustomRelayConfigured)
        {
            if (CloudOnly) throw new InvalidOperationException("待发附件已保留。请连接可用网盘；现有自定义中转不会被新网盘替换。");
            return false;
        }
        var uploaded = false;
        var stage = "准备网盘";
        try
        {
            using var relay = new CloudRelayClient(conversation, conversationKey);
            // Upload and share publication are durable work. A short health probe of
            // the optional legacy stream must not gate or cancel a provider upload.
            var store = _cloudPayloads!;
            var existing = (await store.ReadAsync(token)).SingleOrDefault(m => m.Offer.ConversationId == conversation && m.Offer.Message.Id == message.Id);
            var mapping = existing ?? new CloudPayloadMapping(CloudAttachmentOffer.Create(conversation, message), account.Id,
                account.MountPath, account.FolderId + "/MPT-file-" + message.Id + Path.GetExtension(message.Name ?? ""), false);
            if (mapping.AccountId != account.Id)
            {
                // An upload already belongs to an explicit account; changing defaults cannot relabel or expose it.
                account = _cloudAccounts.Accounts.FirstOrDefault(a => a.Id == mapping.AccountId && a.Status == "ready")
                    ?? throw new InvalidOperationException("此待发附件所属网盘已暂停，请恢复该账号后重试。");
            }
            await SetAssistantTransportRouteAsync(message.Id, account.ProviderId == "quark" ? "cloud-quark" : account.ProviderId == "baidu" ? "cloud-baidu" : "cloud", token);
            await store.SaveAsync(mapping, token); // ownership is durable before creating a remote object
            uploaded = mapping.Uploaded;
            stage = "上传文件";
            using var api = await CloudAdminAsync(token);
            if (!mapping.Uploaded)
            {
                var recovered = false;
                if (existing is not null)
                {
                    try
                    {
                        using var remote = await api.OpenReadAsync(mapping.MountPath, mapping.ObjectPath, token);
                        recovered = remote.Content.Headers.ContentLength == message.Size;
                    }
                    catch (IOException) { /* no confirmed remote object; normal upload supplies the actual outcome */ }
                }
                if (!recovered)
                {
                    if (payload is null) throw new IOException("待发附件副本不存在。");
                    await api.UploadAsync(mapping.MountPath, mapping.ObjectPath, File.OpenRead(payload), message.Size, token);
                }
                uploaded = true;
                mapping = mapping with { Uploaded = true };
                await store.SaveAsync(mapping, token);
            }
            if (!_cloudAccounts.Accounts.Any(a => a.Id == mapping.AccountId && a.Status == "ready"))
                throw new InvalidOperationException("网盘已暂停，待发附件已保留。");
            stage = "创建分享";
            if (account.ProviderId == "quark" && mapping.Share is null)
            {
                var cookie = await SecretAsync("cloud-account-" + account.Id, token)
                    ?? throw new InvalidOperationException("夸克登录已失效，请重新登录。");
                using var shares = new QuarkShareClient();
                var relativePath = mapping.ObjectPath[mapping.MountPath.Length..];
                var share = await shares.CreateAsync(cookie, relativePath, mapping.Offer.ExpiresAt, token);
                mapping = mapping with { Share = share };
                await store.SaveAsync(mapping, token);
            }
            var locator = mapping.Share is null ? null : new CloudShareLocator(1, conversation,
                mapping.Offer.Message, mapping.Offer.ExpiresAt, mapping.Share);
            stage = "发布消息";
            await relay.PublishAsync(mapping.Offer, token, locator);
            _cloudTransportAvailable = true;
            WakeCloudPayloads();
            EmitCloudAccountsChanged("payload");
            return true;
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new IOException(uploaded ? $"文件已上传；{stage}连接中断，原消息等待重试。"
                : $"{stage}连接中断，待发文件已保留。", ex);
        }
        catch (Exception ex) when (uploaded && ex is IOException or HttpRequestException)
        {
            throw new IOException($"文件已上传；{stage}未完成，原消息等待重试。", ex);
        }
        catch (Exception ex) when (!CloudOnly && ex is IOException or HttpRequestException or InvalidOperationException)
        {
            // Auto explicitly permits the existing app relay. The mapping remains for cleanup/retry ownership.
            return false;
        }
    }

    private async Task<string?> DownloadCloudShareAsync(AssistantManifest message, string directory,
        Action<long, long> progress, CancellationToken token)
    {
        using var relay = new CloudRelayClient(_conversationId, _conversationKey);
        var share = await relay.ReadShareAsync(message, token);
        if (share is null) return null;
        await SetAssistantTransportRouteAsync(message.Id, "cloud-quark", token);
        using var provider = new QuarkShareClient();
        using var response = await provider.OpenReadAsync(share.Share, token);
        if (response.Content.Headers.ContentLength is { } length && length != message.Size)
            throw new IOException("网盘分享文件长度与消息不符。");
        Directory.CreateDirectory(directory);
        var temporary = TransferFiles.PartialPath(directory);
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await TransferFiles.CopyAsync(input, output, message.Size, progress, token);
            return TransferFiles.Commit(temporary, directory, message.Name!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task CloudPayloadLoopAsync()
    {
        var retries = new Dictionary<string, (DateTimeOffset NextAttempt, DateTimeOffset ExpiresAt)>(StringComparer.Ordinal);
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var conversation = _conversationId;
                var conversationKey = _conversationKey;
                var mappings = await _cloudPayloads!.ReadAsync(_lifetime.Token);
                var active = mappings.Where(m => m.Uploaded && m.Offer.ConversationId == conversation
                    && m.Offer.ExpiresAt > DateTimeOffset.UtcNow
                    && _cloudAccounts.Accounts.Any(a => a.Id == m.AccountId && a.Status == "ready")).ToArray();
                if (active.Length == 0 || conversation.Length == 0)
                {
                    await _cloudPayloadSignal.WaitAsync(_lifetime.Token);
                    continue;
                }
                using var relay = new CloudRelayClient(conversation, conversationKey);
                var now = DateTimeOffset.UtcNow;
                foreach (var expired in retries.Where(row => row.Value.ExpiresAt <= now).Select(row => row.Key).ToArray())
                    retries.Remove(expired);
                var waitBeforePollingAgain = false;
                foreach (var request in await relay.RequestsAsync(Setting("deviceId"), _lifetime.Token))
                {
                    var mapping = active.SingleOrDefault(m => m.Offer.Matches(request, conversation, DateTimeOffset.UtcNow));
                    if (mapping is null || !_cloudAccounts.Accounts.Any(a => a.Id == mapping.AccountId && a.Status == "ready"))
                    { waitBeforePollingAgain = true; continue; }
                    if (retries.TryGetValue(request.RequestId, out var retry) && retry.NextAttempt > DateTimeOffset.UtcNow)
                    { waitBeforePollingAgain = true; continue; }
                    var item = (await _assistantStore!.LoadAsync(_lifetime.Token)).Find(mapping.Offer.Message.Id);
                    if (item?.State == AssistantItemState.Cancelled) { waitBeforePollingAgain = true; continue; }
                    try
                    {
                        using var api = await CloudAdminAsync(_lifetime.Token);
                        using var response = await api.OpenReadAsync(mapping.MountPath, mapping.ObjectPath, _lifetime.Token);
                        if (response.Content.Headers.ContentLength is { } size && size != mapping.Offer.Message.Size)
                            throw new IOException("网盘文件长度与助手条目记录不符。");
                        using var content = await response.Content.ReadAsStreamAsync(_lifetime.Token);
                        await relay.FulfilAsync(request, content, _lifetime.Token);
                        retries.Remove(request.RequestId);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException)
                    {
                        retries[request.RequestId] = (DateTimeOffset.UtcNow.AddSeconds(2), request.ExpiresAt);
                        waitBeforePollingAgain = true;
                    }
                }
                // Polling does not claim a request. An unreadable source keeps returning the same
                // request immediately, so skipping it alone would spin. Bound that poll to once a
                // second while still admitting new requests, and retry a recovered source in-place.
                if (waitBeforePollingAgain)
                    await _cloudPayloadSignal.WaitAsync(TimeSpan.FromSeconds(1), _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception)
            {
                try { await _cloudPayloadSignal.WaitAsync(TimeSpan.FromSeconds(30), _lifetime.Token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
