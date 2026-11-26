namespace FileTransfer.Core.Assistant;

/// <summary>
/// Bounded operations for the shared scheduler. Discovery, requests and receipts live on the public
/// relay. Its negotiated proxy preserves V1 readers while new clients fetch payloads directly from Tail.
/// The caller retains outgoing messages after receipts and serves requests on public revision changes.
/// </summary>
public sealed class SharedLocatorTransfer : IDisposable
{
    private readonly SharedLocatorClient _public;
    private readonly SharedLocatorClient _tail;
    private readonly string _copyDirectory;
    private readonly SemaphoreSlim _copyGate = new(1, 1);

    public SharedLocatorTransfer(SharedLocatorClient publicClient, SharedLocatorClient tailClient, string copyDirectory)
    {
        if (publicClient.Route != SharedRelayRoute.Public || tailClient.Route != SharedRelayRoute.Tail
            || publicClient.ConversationId != tailClient.ConversationId)
            throw new ArgumentException("共享传输需要同一会话的公网和 Tail 中转。");
        _public = publicClient;
        _tail = tailClient;
        _copyDirectory = copyDirectory;
    }

    /// <summary>Negotiates the actual public service before publishing any proxy-dependent V1 manifest.</summary>
    public async Task<SharedPublishResult> PublishAsync(AssistantManifest message, string? payloadPath,
        CancellationToken token, Action<long, long>? progress = null, Func<string, Task>? routeChanged = null)
    {
        message = SharedLocatorRules.Message(message);
        await _public.RegisterAsync(token);
        if (message.Kind == AssistantItemKind.Text)
        {
            await _public.PublishContentAsync(message, null, token);
            return new(false, false, true);
        }
        if (await _public.ReadContentManifestAsync(message.Id, token) is { } existing)
        {
            SharedLocatorRules.SameMessage(message, existing);
            if (await _public.HasPublicCopyAsync(message.Id, token)
                || await _public.ReadLocatorAsync(message.Id, token) is null)
                return new(false, false, true);
        }
        if (!await _public.SupportsPayloadLocatorAsync(token))
        {
            // Older public relays cannot resolve missing payloads. Keep their original complete V1 path.
            if (routeChanged is not null) await routeChanged("public-relay");
            await _public.PublishPublicCopyAsync(message, payloadPath!, token, progress);
            return new(false, false, true);
        }
        try
        {
            await _tail.RegisterAsync(token);
            if (routeChanged is not null) await routeChanged("tail-relay");
            await _tail.PublishContentAsync(message, payloadPath, token, progress);
        }
        catch (Exception ex) when (IsUnavailable(ex, token))
        {
            // A transiently unreachable Tail relay must not prevent a public-only sender from sending.
            if (routeChanged is not null) await routeChanged("public-relay");
            await _public.PublishPublicCopyAsync(message, payloadPath!, token, progress);
            return new(false, false, true);
        }
        await _public.PublishLocatorAsync(new() { Message = message }, token);
        await _public.CommitLocatedManifestAsync(message, token);
        return new(true, true, false);
    }

    /// <summary>
    /// Prefers Tail, then the public relay's local or proxied V1 payload. Waiting never writes a saved receipt.
    /// The caller passes existingPath only after matching this message to its durable local item.
    /// </summary>
    public async Task<SharedReceiveResult> ReceiveAsync(SharedLocator locator, string deviceId, string deviceName,
        string directory, string? existingPath, Func<string, CancellationToken, Task> commitSaved, CancellationToken token)
    {
        AssistantValidation.Name(deviceName);
        var result = await FetchAsync(locator, deviceId, directory, existingPath, token);
        if (result.WaitingForPublicCopy) return result;
        // A receipt must follow, rather than precede, durable adoption by the caller's store.
        await commitSaved(result.Path!, token);
        await _public.WriteReceiptAsync(new()
        {
            ItemId = locator.Message.Id, DeviceId = deviceId, DeviceName = deviceName,
            SavedAt = DateTimeOffset.UtcNow, Bytes = locator.Message.Size
        }, token);
        return result;
    }

    public Task<SharedLocator?> LocateAsync(string itemId, CancellationToken token) => _public.ReadLocatorAsync(itemId, token);
    public Task<long> CurrentRevisionAsync(CancellationToken token) => _public.ChangesAsync(null, token);

    /// <summary>Downloads or records an unmet request; the existing host sync transaction owns adoption and acknowledgement.</summary>
    public async Task<SharedReceiveResult> FetchAsync(SharedLocator locator, string deviceId,
        string directory, string? existingPath, CancellationToken token, Action<long, long>? progress = null, Func<string, Task>? routeChanged = null)
    {
        locator = SharedLocatorRules.Locator(locator);
        AssistantValidation.DeviceId(deviceId);
        await _public.RegisterAsync(token);
        var message = locator.Message;
        string? path = existingPath is { Length: > 0 } && File.Exists(existingPath)
            && new FileInfo(existingPath).Length == message.Size ? existingPath : null;
        if (path is null)
        {
            var publicMessage = await _public.ReadContentManifestAsync(message.Id, token);
            if (publicMessage is not null) SharedLocatorRules.SameMessage(message, publicMessage);
            AssistantManifest? tailMessage = null;
            Exception? tailError = null;
            try
            {
                await _tail.RegisterAsync(token);
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(TimeSpan.FromSeconds(4));
                tailMessage = await _tail.ReadContentManifestAsync(message.Id, probe.Token);
                if (tailMessage is null) throw new IOException("Tail 中转还没有该附件的完整记录。");
            }
            catch (Exception ex) when (CanTryPublic(ex, token)) { tailError = ex; }
            if (tailMessage is not null)
            {
                // A valid record with conflicting immutable content is never merged or overwritten.
                SharedLocatorRules.SameMessage(message, tailMessage);
                try { if (routeChanged is not null) await routeChanged("tail-relay"); path = await _tail.DownloadContentAsync(message, directory, token, progress); }
                catch (Exception ex) when (CanTryPublic(ex, token)) { tailError = ex; }
            }
            if (path is null)
            {
                try
                {
                    if (publicMessage is null) throw new IOException("共享条目尚未在公网提交。");
                    if (routeChanged is not null) await routeChanged("public-relay");
                    path = await _public.DownloadContentAsync(message, directory, token, progress);
                }
                catch (Exception ex) when (IsUnavailable(ex, token) || !token.IsCancellationRequested && ex is InvalidDataException)
                { return await WaitForPublicAsync(message.Id, deviceId, tailError ?? ex, token); }
            }
        }
        return new(path, false);
    }

    /// <summary>
    /// Returns true once a full public V1 copy is committed. Receipt count is deliberately absent from
    /// this decision. Remote requests survive sender restarts; repeated calls reuse the immutable id.
    /// </summary>
    public async Task<bool> ServeRequestsAsync(AssistantManifest message, string senderDeviceId, string? localPayload,
        CancellationToken token, Func<string, Task>? routeChanged = null)
    {
        message = SharedLocatorRules.Message(message);
        if (message.SenderDeviceId != AssistantValidation.DeviceId(senderDeviceId))
            throw new InvalidOperationException("第一版仅由原发送设备响应附件中转请求。");
        await _copyGate.WaitAsync(token);
        try
        {
            await _public.RegisterAsync(token);
            var existing = await _public.ReadContentManifestAsync(message.Id, token);
            if (existing is not null) SharedLocatorRules.SameMessage(message, existing);
            if (await _public.HasPublicCopyAsync(message.Id, token)) return true;
            var locator = await _public.ReadLocatorAsync(message.Id, token);
            if (locator is null) return false;
            SharedLocatorRules.SameMessage(message, locator.Message);
            var requests = await _public.ListRequestsAsync(message.Id, token);
            if (requests.Items.Count == 0) return false;

            string? downloaded = null;
            string? directory = null;
            try
            {
                var payload = localPayload;
                if (payload is null || !File.Exists(payload))
                {
                    await _tail.RegisterAsync(token);
                    var tailMessage = await _tail.ReadContentManifestAsync(message.Id, token)
                        ?? throw new IOException("附件的 Tail 副本暂不可用，公网请求将保留等待重试。");
                    SharedLocatorRules.SameMessage(message, tailMessage);
                    directory = Path.Combine(_copyDirectory, message.Id, Guid.NewGuid().ToString("N"));
                    downloaded = payload = await _tail.DownloadContentAsync(message, directory, token);
                }
                if (routeChanged is not null) await routeChanged("public-relay");
                await _public.PublishPublicCopyAsync(message, payload, token);
                return true;
            }
            finally
            {
                if (downloaded is not null && File.Exists(downloaded)) File.Delete(downloaded);
                if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory);
            }
        }
        finally { _copyGate.Release(); }
    }

    private async Task<SharedReceiveResult> WaitForPublicAsync(string itemId, string deviceId, Exception error, CancellationToken token)
    {
        await _public.RequestPublicCopyAsync(itemId, deviceId,
            error is InvalidDataException ? "tail-unavailable" : "tail-unreachable", token);
        return new(null, true, "Tail 附件暂不可用，等待发送设备提供公网副本。");
    }

    private static bool IsUnavailable(Exception error, CancellationToken token) => !token.IsCancellationRequested
        && error is HttpRequestException or IOException or OperationCanceledException
        && error is not InvalidDataException;

    private static bool CanTryPublic(Exception error, CancellationToken token) => IsUnavailable(error, token)
        || !token.IsCancellationRequested && error is InvalidDataException;

    public void Dispose() { _public.Dispose(); _tail.Dispose(); _copyGate.Dispose(); }
}
