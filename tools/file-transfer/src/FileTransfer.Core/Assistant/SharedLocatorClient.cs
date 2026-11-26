using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace FileTransfer.Core.Assistant;

/// <summary>
/// One authenticated shared namespace on one fixed relay. This has no scheduler and does not change
/// the existing V1 client. Public metadata and Tail payloads are deliberately separate instances.
/// </summary>
public sealed class SharedLocatorClient : IDisposable
{
    private const string LocatorRoot = "assistant-locator";
    private readonly HttpClient _http;
    private readonly OpenListClient _content;
    private readonly Uri _endpoint;
    private readonly AuthenticationHeaderValue _authorization;
    public string ConversationId { get; }
    public SharedRelayRoute Route { get; }

    public SharedLocatorClient(string conversationId, string conversationKey, SharedRelayRoute route)
        : this(conversationId, conversationKey, route,
            route == SharedRelayRoute.Tail
                ? InboxRelays.All.FirstOrDefault(relay => relay.Id == InboxRelays.Tail)?.Address ?? InboxRelays.TailAddress
                : PublicRelayClient.BaseAddress) { }

    // Test-only explicit loopback endpoints. Wire records can never select this address.
    internal SharedLocatorClient(string conversationId, string conversationKey, SharedRelayRoute route, Uri endpoint)
    {
        ConversationId = AssistantValidation.ConversationId(conversationId);
        if (conversationKey.Length != 64 || !conversationKey.All(Uri.IsHexDigit))
            throw new ArgumentException("共享会话密钥格式无效。", nameof(conversationKey));
        if (!Enum.IsDefined(route)) throw new ArgumentOutOfRangeException(nameof(route));
        if (!endpoint.IsLoopback && endpoint != (route == SharedRelayRoute.Tail ? InboxRelays.TailAddress : PublicRelayClient.ProductionBaseAddress))
            throw new ArgumentException("共享附件只能使用固定的可信中转。", nameof(endpoint));
        Route = route;
        _endpoint = endpoint;
        _authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{conversationId}:{conversationKey}")));
        _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4) })
        { Timeout = Timeout.InfiniteTimeSpan };
        _content = new OpenListClient(new Uri(endpoint, PublicRelayClient.DavPath).ToString(), conversationId, conversationKey);
    }

    public async Task RegisterAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await SendAsync(HttpMethod.Post, new(_endpoint, PublicRelayClient.ConversationsPath), null, budget.Token);
        Check(response);
    }

    /// <summary>Only the fixed public relay may advertise transparent V1 payload forwarding.</summary>
    public async Task<bool> SupportsPayloadLocatorAsync(CancellationToken token)
    {
        PublicOnly();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            // Health is unauthenticated; no namespace credentials are sent to this endpoint.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "/mpt/relay/health"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // An old service may protect or omit health. No credentials were presented, so this is
            // absence of the capability, not rejection of the authenticated namespace.
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await ReadBytesAsync(response, SharedLocatorRules.LocatorBytes, budget.Token));
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("capabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("sharedPayloadLocator", out var supported)
                && supported.ValueKind == JsonValueKind.Number && supported.TryGetInt32(out var version) && version == 1;
        }
        catch (Exception error) when (!token.IsCancellationRequested
            && error is HttpRequestException or IOException or OperationCanceledException or JsonException)
        { return false; }
    }

    public async Task<long> ChangesAsync(long? since, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(PublicRelayClient.LongPollBudget);
        var path = PublicRelayClient.ChangesPath + (since is null ? "" : "?since=" + since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await SendAsync(HttpMethod.Get, new(_endpoint, path), null, budget.Token);
        Check(response);
        if (response.StatusCode == HttpStatusCode.NoContent) return since ?? 0;
        using var json = JsonDocument.Parse(await ReadBytesAsync(response, SharedLocatorRules.RequestBytes, budget.Token));
        return json.RootElement.GetProperty("revision").GetInt64();
    }

    public async Task<AssistantManifest?> ReadContentManifestAsync(string itemId, CancellationToken token)
    {
        AssistantValidation.ItemId(itemId);
        var manifest = await ReadJsonAsync<AssistantManifest>(Dav("assistant", ConversationId, itemId, "manifest.json"),
            SharedLocatorRules.LocatorBytes * 4, token);
        return manifest is null ? null : SharedLocatorRules.Message(manifest, itemId);
    }

    public async Task PublishContentAsync(AssistantManifest message, string? payloadPath, CancellationToken token, Action<long, long>? progress = null)
    {
        message = SharedLocatorRules.Message(message);
        var existing = await ReadContentManifestAsync(message.Id, token);
        if (existing is not null) { SharedLocatorRules.SameMessage(message, existing); return; }
        await _content.PublishAssistantAsync(ConversationId, message, payloadPath, progress, token);
    }

    // Called by the coordinator only after live capability negotiation and Tail publication succeeded.
    internal async Task CommitLocatedManifestAsync(AssistantManifest message, CancellationToken token)
    {
        PublicOnly();
        message = SharedLocatorRules.Message(message);
        var locator = await ReadLocatorAsync(message.Id, token)
            ?? throw new InvalidOperationException("发布共享条目前必须先提交附件定位记录。");
        SharedLocatorRules.SameMessage(message, locator.Message);
        var existing = await ReadContentManifestAsync(message.Id, token);
        if (existing is not null) { SharedLocatorRules.SameMessage(message, existing); return; }
        await EnsureCollectionsAsync(["assistant", ConversationId, message.Id], token);
        await PutJsonAsync(Dav("assistant", ConversationId, message.Id, "manifest.json"), message, SharedLocatorRules.LocatorBytes, token);
    }

    public async Task<bool> HasPublicCopyAsync(string itemId, CancellationToken token)
    {
        PublicOnly();
        AssistantValidation.ItemId(itemId);
        var completion = await ReadJsonAsync<SharedPublicCopyCompletion>(Dav(LocatorRoot, ConversationId, itemId, "public-copy.json"), SharedLocatorRules.RequestBytes, token);
        if (completion is null) return false;
        if (completion.Version != 1 || completion.ItemId != itemId || completion.CommittedAt == default)
            throw new InvalidDataException("公网附件副本记录与目录不一致或格式无效。");
        return true;
    }

    /// <summary>An existing V1 manifest may use proxy storage, so copying its payload cannot use V1's manifest-only fast path.</summary>
    public async Task PublishPublicCopyAsync(AssistantManifest message, string payloadPath, CancellationToken token, Action<long, long>? progress = null)
    {
        PublicOnly();
        message = SharedLocatorRules.Message(message);
        if (message.Kind == AssistantItemKind.Text) throw new ArgumentException("文字消息没有附件副本。");
        var existing = await ReadContentManifestAsync(message.Id, token);
        if (existing is not null) SharedLocatorRules.SameMessage(message, existing);
        if (await HasPublicCopyAsync(message.Id, token)) return;
        if (existing is not null && await ReadLocatorAsync(message.Id, token) is null)
        {
            // An original V1 publication with no locator already committed its complete payload.
            await MarkPublicCopyAsync(message.Id, token);
            return;
        }
        await EnsureCollectionsAsync(["assistant", ConversationId, message.Id], token);
        await using (var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
        {
            if (input.Length != message.Size) throw new InvalidDataException("待发副本长度与条目记录不符。");
            using var content = new UploadContent(input, message.Size, progress);
            using var response = await SendAsync(HttpMethod.Put, Dav("assistant", ConversationId, message.Id, "payload"), content, token);
            Check(response);
        }
        await PutJsonAsync(Dav("assistant", ConversationId, message.Id, "manifest.json"), message, SharedLocatorRules.LocatorBytes, token);
        await MarkPublicCopyAsync(message.Id, token);
    }

    private async Task MarkPublicCopyAsync(string itemId, CancellationToken token)
    {
        await EnsureCollectionsAsync([LocatorRoot, ConversationId, itemId], token);
        await PutJsonAsync(Dav(LocatorRoot, ConversationId, itemId, "public-copy.json"),
            new SharedPublicCopyCompletion(1, itemId, DateTimeOffset.UtcNow), SharedLocatorRules.RequestBytes, token);
    }

    public async Task<string> DownloadContentAsync(AssistantManifest message, string directory, CancellationToken token, Action<long, long>? progress = null)
    {
        message = SharedLocatorRules.Message(message);
        if (message.Kind == AssistantItemKind.Text) throw new ArgumentException("文字消息没有附件。");
        Directory.CreateDirectory(directory);
        var temporary = TransferFiles.PartialPath(directory);
        try
        {
            using var response = await SendAsync(HttpMethod.Get, Dav("assistant", ConversationId, message.Id, "payload"), null, token);
            Check(response);
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await TransferFiles.CopyAsync(source, destination, message.Size, progress, token);
                if (await source.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("共享附件长度与记录不符。");
            }
            return TransferFiles.Commit(temporary, directory, message.Name!);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<SharedLocator?> ReadLocatorAsync(string itemId, CancellationToken token)
    {
        PublicOnly();
        AssistantValidation.ItemId(itemId);
        var locator = await ReadJsonAsync<SharedLocator>(Dav(LocatorRoot, ConversationId, itemId, "manifest.json"), SharedLocatorRules.LocatorBytes, token);
        return locator is null ? null : SharedLocatorRules.Locator(locator, itemId);
    }

    public async Task PublishLocatorAsync(SharedLocator locator, CancellationToken token)
    {
        PublicOnly();
        locator = SharedLocatorRules.Locator(locator);
        var existing = await ReadLocatorAsync(locator.Message.Id, token);
        if (existing is not null)
        {
            SharedLocatorRules.SameMessage(locator.Message, existing.Message);
            return;
        }
        await EnsureCollectionsAsync([LocatorRoot, ConversationId, locator.Message.Id], token);
        await PutJsonAsync(Dav(LocatorRoot, ConversationId, locator.Message.Id, "manifest.json"), locator, SharedLocatorRules.LocatorBytes, token);
    }

    public async Task<SharedLocatorPage> ListLocatorsAsync(AssistantListRequest request, CancellationToken token)
    {
        PublicOnly();
        var names = await ListNamesAsync([LocatorRoot, ConversationId], token);
        var ids = names.Where(name => Guid.TryParseExact(name, "N", out _)).Order(StringComparer.Ordinal).ToArray();
        var known = new HashSet<string>(request.KnownItemIds ?? [], StringComparer.Ordinal);
        var remaining = ids.Where(id => !known.Contains(id) && (request.Cursor is null || string.CompareOrdinal(id, request.Cursor) > 0)).ToArray();
        var selected = remaining.Take(Math.Clamp(request.Limit, 1, AssistantListRequest.MaxLimit)).ToArray();
        var items = new List<SharedLocator>();
        var invalid = new List<string>();
        foreach (var id in selected)
        {
            try { if (await ReadLocatorAsync(id, token) is { } locator) items.Add(locator); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException) { invalid.Add(id); }
        }
        var more = remaining.Length > selected.Length;
        return new(items, invalid, more, more ? selected.LastOrDefault() : null);
    }

    public async Task RequestPublicCopyAsync(string itemId, string deviceId, string reason, CancellationToken token)
    {
        PublicOnly();
        AssistantValidation.ItemId(itemId);
        AssistantValidation.DeviceId(deviceId);
        var request = SharedLocatorRules.Request(new() { ItemId = itemId, DeviceId = deviceId, RequestedAt = DateTimeOffset.UtcNow, Reason = reason }, itemId, deviceId);
        var url = Dav(LocatorRoot, ConversationId, itemId, "requests", deviceId + ".json");
        var existing = await ReadJsonAsync<SharedPublicCopyRequest>(url, SharedLocatorRules.RequestBytes, token);
        if (existing is not null) { SharedLocatorRules.Request(existing, itemId, deviceId); return; }
        await EnsureCollectionsAsync([LocatorRoot, ConversationId, itemId, "requests"], token);
        await PutJsonAsync(url, request, SharedLocatorRules.RequestBytes, token);
    }

    public async Task<SharedRequestPage> ListRequestsAsync(string itemId, CancellationToken token)
    {
        PublicOnly();
        AssistantValidation.ItemId(itemId);
        var names = (await ListNamesAsync([LocatorRoot, ConversationId, itemId, "requests"], token))
            .Where(name => name.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var items = new List<SharedPublicCopyRequest>();
        var invalid = new List<string>();
        foreach (var name in names.Take(AssistantLimits.MaxReceiptsPerItem))
        {
            try
            {
                var device = AssistantValidation.DeviceId(name[..^5]);
                var request = await ReadJsonAsync<SharedPublicCopyRequest>(Dav(LocatorRoot, ConversationId, itemId, "requests", name), SharedLocatorRules.RequestBytes, token);
                if (request is not null) items.Add(SharedLocatorRules.Request(request, itemId, device));
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException or ArgumentException) { invalid.Add(name); }
        }
        return new(items, invalid, names.Length > AssistantLimits.MaxReceiptsPerItem);
    }

    public Task<AssistantReceipt> WriteReceiptAsync(AssistantReceipt receipt, CancellationToken token)
    { PublicOnly(); return _content.WriteAssistantReceiptAsync(ConversationId, receipt, token); }
    public Task<IReadOnlyList<AssistantReceipt>> ListReceiptsAsync(string itemId, CancellationToken token)
    { PublicOnly(); return _content.ListAssistantReceiptsAsync(ConversationId, itemId, token); }

    private Uri Dav(params string[] segments) => new(_endpoint, PublicRelayClient.DavPath + string.Join('/', segments.Select(Uri.EscapeDataString)));

    private async Task<IReadOnlyList<string>> ListNamesAsync(string[] segments, CancellationToken token)
    {
        var url = new Uri(Dav(segments).AbsoluteUri + "/");
        using var response = await SendAsync(new HttpMethod("PROPFIND"), url, null, token, depth: "1");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return [];
        Check(response);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(await ReadBytesAsync(response, SharedLocatorRules.ListingBytes, token)));
        XNamespace dav = "DAV:";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in xml.Descendants(dav + "response"))
        {
            var href = row.Element(dav + "href")?.Value;
            if (href is null || !Uri.TryCreate(url, href, out var entry) || entry.Authority != url.Authority || entry.Scheme != url.Scheme) continue;
            var path = entry.AbsolutePath.TrimEnd('/');
            if (!path.StartsWith(url.AbsolutePath, StringComparison.Ordinal)) continue;
            var name = Uri.UnescapeDataString(path[url.AbsolutePath.Length..]);
            if (name.Length > 0 && !name.Contains('/') && !name.Contains('\\')) names.Add(name);
        }
        return names.ToArray();
    }

    private async Task EnsureCollectionsAsync(string[] segments, CancellationToken token)
    {
        for (var length = 1; length <= segments.Length; length++)
        {
            using var response = await SendAsync(new HttpMethod("MKCOL"), new(Dav(segments[..length]).AbsoluteUri + "/"), null, token);
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed) Check(response);
        }
    }

    private async Task<T?> ReadJsonAsync<T>(Uri url, int maximum, CancellationToken token) where T : class
    {
        using var response = await SendAsync(HttpMethod.Get, url, null, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response);
        return JsonSerializer.Deserialize<T>(await ReadBytesAsync(response, maximum, token), AssistantJson.Options)
            ?? throw new InvalidDataException("共享记录为空。");
    }

    private async Task PutJsonAsync<T>(Uri url, T value, int maximum, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AssistantJson.Options);
        if (bytes.Length > maximum) throw new InvalidDataException("共享信令超过大小限制。");
        using var response = await SendAsync(HttpMethod.Put, url, new ByteArrayContent(bytes), token);
        Check(response);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, HttpContent? content, CancellationToken token, string? depth = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = _authorization;
        if (depth is not null) request.Headers.Add("Depth", depth);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("共享信令超过大小限制。");
        using var buffer = new MemoryStream();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var block = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(block, token)) > 0)
        {
            if (buffer.Length + count > maximum) throw new InvalidDataException("共享信令超过大小限制。");
            buffer.Write(block, 0, count);
        }
        return buffer.ToArray();
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new PublicRelayAuthException("共享会话凭据被中转拒绝。");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("共享中转返回 HTTP " + (int)response.StatusCode, null, response.StatusCode);
    }

    private void PublicOnly()
    { if (Route != SharedRelayRoute.Public) throw new InvalidOperationException("共享发现、请求和回执必须写入公网控制命名空间。"); }
    public void Dispose() { _http.Dispose(); _content.Dispose(); }

    private sealed class UploadContent(Stream input, long size, Action<long, long>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            TransferFiles.CopyAsync(input, stream, size, progress, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            TransferFiles.CopyAsync(input, stream, size, progress, token);
    }
}
