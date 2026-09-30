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
        try
        {
            using var relay = new CloudRelayClient(conversation, conversationKey);
            _cloudTransportAvailable = await relay.AvailableAsync(token);
            if (!_cloudTransportAvailable)
            {
                if (CloudOnly) throw new InvalidOperationException("待发附件已保留，网盘单文件领取服务暂不可用。");
                return false;
            }
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
            await store.SaveAsync(mapping, token); // ownership is durable before creating a remote object
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
                mapping = mapping with { Uploaded = true };
                await store.SaveAsync(mapping, token);
            }
            if (!_cloudAccounts.Accounts.Any(a => a.Id == mapping.AccountId && a.Status == "ready"))
                throw new InvalidOperationException("网盘已暂停，待发附件已保留。");
            await relay.PublishAsync(mapping.Offer, token);
            WakeCloudPayloads();
            EmitCloudAccountsChanged("payload");
            return true;
        }
        catch (Exception ex) when (!CloudOnly && ex is IOException or HttpRequestException or InvalidOperationException)
        {
            // Auto explicitly permits the existing app relay. The mapping remains for cleanup/retry ownership.
            return false;
        }
    }
    private async Task CloudPayloadLoopAsync()
    {
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
                foreach (var request in await relay.RequestsAsync(Setting("deviceId"), _lifetime.Token))
                {
                    var mapping = active.SingleOrDefault(m => m.Offer.Matches(request, conversation, DateTimeOffset.UtcNow));
                    if (mapping is null || !_cloudAccounts.Accounts.Any(a => a.Id == mapping.AccountId && a.Status == "ready")) continue;
                    var item = (await _assistantStore!.LoadAsync(_lifetime.Token)).Find(mapping.Offer.Message.Id);
                    if (item?.State == AssistantItemState.Cancelled) continue;
                    try
                    {
                        using var api = await CloudAdminAsync(_lifetime.Token);
                        using var response = await api.OpenReadAsync(mapping.MountPath, mapping.ObjectPath, _lifetime.Token);
                        if (response.Content.Headers.ContentLength is { } size && size != mapping.Offer.Message.Size) continue;
                        using var content = await response.Content.ReadAsStreamAsync(_lifetime.Token);
                        await relay.FulfilAsync(request, content, _lifetime.Token);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException) { /* receiver retry creates a new one-use request */ }
                }
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
