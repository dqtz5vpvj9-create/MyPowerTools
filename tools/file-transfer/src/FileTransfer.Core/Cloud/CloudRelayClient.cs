using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FileTransfer.Core.Assistant;
namespace FileTransfer.Core.Cloud;

/// <summary>Small shared metadata and transient payload streaming; fixed origin, namespace credentials on every request.</summary>
public sealed class CloudRelayClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _conversation;
    public CloudRelayClient(string conversation, string key) : this(conversation, key, PublicRelayClient.BaseAddress) { }
    internal CloudRelayClient(string conversation, string key, Uri endpoint)
    {
        _conversation = AssistantValidation.ConversationId(conversation);
        if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("会话凭据无效。");
        _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(30) })
            { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(conversation + ":" + key)));
    }
    public async Task<string?> ReadPayloadRouteAsync(AssistantManifest message, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await _http.GetAsync($"/mpt/relay/dav/cloud-locator/{_conversation}/{AssistantValidation.ItemId(message.Id)}/manifest.json", budget.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return "public-relay";
            if (!response.IsSuccessStatusCode) return null;
            var offer = await response.Content.ReadFromJsonAsync<CloudAttachmentOffer>(AssistantJson.Options, budget.Token);
            if (offer is null || offer.ConversationId != _conversation || offer.Route != "mpt-cloud-stream-v1") return null;
            SharedLocatorRules.SameMessage(message, offer.Message);
            // PROPFIND inspects physical storage, unlike GET/HEAD's virtual cloud stream.
            using var physical = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/mpt/relay/dav/assistant/{_conversation}/{message.Id}/payload");
            physical.Headers.Add("Depth", "0");
            using var stored = await _http.SendAsync(physical, budget.Token);
            return stored.StatusCode == HttpStatusCode.NotFound ? "cloud" : stored.IsSuccessStatusCode ? "public-relay" : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or ArgumentException) { return null; }
    }
    public async Task<bool> AvailableAsync(CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync("/mpt/relay/health", budget.Token);
        if (!response.IsSuccessStatusCode) return false;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(budget.Token));
        return json.RootElement.TryGetProperty("capabilities", out var caps) && caps.TryGetProperty("cloudPayloadStream", out var enabled) && enabled.TryGetInt32(out var value) && value == 1;
    }
    public async Task PublishAsync(CloudAttachmentOffer offer, CancellationToken token, CloudShareLocator? share = null)
    {
        if (offer.ConversationId != _conversation || offer.Message.TargetDeviceId is not null || offer.Message.Kind == AssistantItemKind.Text) throw new ArgumentException("网盘附件会话不符。");
        await SendAsync(HttpMethod.Post, PublicRelayClient.ConversationsPath, null, token);
        foreach (var root in new[] { "cloud-locator", "assistant" })
        {
            foreach (var suffix in new[] { root + "/", root + "/" + _conversation + "/", root + "/" + _conversation + "/" + offer.Message.Id + "/" })
                await SendAsync(new HttpMethod("MKCOL"), PublicRelayClient.DavPath + suffix, null, token, allowExisting: true);
        }
        if (share is not null)
        {
            share.Validate(_conversation, offer.Message);
            await SendAsync(HttpMethod.Put, PublicRelayClient.DavPath + $"assistant/{_conversation}/{offer.Message.Id}/cloud-share.json",
                JsonContent.Create(share, options: AssistantJson.Options), token);
        }
        await SendAsync(HttpMethod.Put, PublicRelayClient.DavPath + $"cloud-locator/{_conversation}/{offer.Message.Id}/manifest.json", JsonContent.Create(offer, options: AssistantJson.Options), token, allowCloudSizeLimit: share is not null);
        await SendAsync(HttpMethod.Put, PublicRelayClient.DavPath + $"assistant/{_conversation}/{offer.Message.Id}/manifest.json", JsonContent.Create(offer.Message, options: AssistantJson.Options), token);
    }

    public async Task<CloudShareLocator?> ReadShareAsync(AssistantManifest message, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.GetAsync(PublicRelayClient.DavPath +
            $"assistant/{_conversation}/{AssistantValidation.ItemId(message.Id)}/cloud-share.json",
            HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new IOException("暂时无法读取网盘分享，稍后自动重试。");
        // Conversation members can publish DAV objects. Bound this metadata read before parsing.
        await response.Content.LoadIntoBufferAsync(16 * 1024, budget.Token);
        var share = await response.Content.ReadFromJsonAsync<CloudShareLocator>(AssistantJson.Options, budget.Token)
            ?? throw new InvalidDataException("网盘分享信息不完整。");
        share.Validate(_conversation, message);
        return share;
    }
    public async Task<CloudPayloadRequest[]> RequestsAsync(string deviceId, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var response = await _http.GetAsync("/mpt/relay/v1/cloud/requests?deviceId=" + Uri.EscapeDataString(deviceId) + "&wait=25", timeout.Token);
        if (!response.IsSuccessStatusCode) throw new IOException("网盘附件领取通道暂不可用。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var requests = json.RootElement.GetProperty("requests").Deserialize<CloudPayloadRequest[]>(AssistantJson.Options) ?? [];
        if (json.RootElement.TryGetProperty("serverTime", out var serverTime))
            return AlignRequestClocks(requests, serverTime.GetDateTimeOffset(), DateTimeOffset.UtcNow);
        return requests;
    }
    // The relay owns the claim deadline. Translate its timestamp to this device's clock;
    // an ahead client must not discard a request that is still valid on the relay.
    // The relay checks its original deadline again when the sender claims the request.
    internal static CloudPayloadRequest[] AlignRequestClocks(CloudPayloadRequest[] requests, DateTimeOffset serverTime, DateTimeOffset localTime) =>
        requests.Select(request => request with { ExpiresAt = request.ExpiresAt + (localTime - serverTime) }).ToArray();
    public async Task FulfilAsync(CloudPayloadRequest request, Stream content, CancellationToken token)
    {
        // Request id is an opaque server-issued path segment, never a destination URL.
        if (request.RequestId.Length is < 1 or > 128 || !request.RequestId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) throw new IOException("附件领取请求无效。");
        using var message = new HttpRequestMessage(HttpMethod.Put, "/mpt/relay/v1/cloud/requests/" + request.RequestId + "/body") { Content = new PayloadContent(content, request.Size) };
        message.Headers.Add("X-MPT-Cloud-Capability", request.Capability);
        message.Content.Headers.ContentLength = request.Size;
        using var response = await _http.SendAsync(message, token);
        if (!response.IsSuccessStatusCode) throw new IOException("接收方尚未完成附件领取，请稍后重试。");
    }
    private async Task SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken token, bool allowExisting = false, bool allowCloudSizeLimit = false)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await _http.SendAsync(request, token);
        // A provider share downloads directly from the provider. The legacy streaming
        // relay's payload limit still applies to that relay, not to this share.
        if (allowCloudSizeLimit && response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            await response.Content.LoadIntoBufferAsync(4096, token);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (error.RootElement.TryGetProperty("error", out var code) && code.GetString() == "invalid_cloud_size") return;
        }
        if (!response.IsSuccessStatusCode && !(allowExisting && response.StatusCode == HttpStatusCode.MethodNotAllowed))
            throw new IOException($"网盘附件消息发布失败（HTTP {(int)response.StatusCode}），待发记录已保留。");
    }
    private sealed class PayloadContent(Stream source, long size) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream output, TransportContext? context) =>
            TransferFiles.CopyAsync(source, output, size, null, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream output, TransportContext? context, CancellationToken token) =>
            TransferFiles.CopyAsync(source, output, size, null, token);
    }
    public void Dispose() => _http.Dispose();
}
