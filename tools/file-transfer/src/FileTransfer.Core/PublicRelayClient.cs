using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Core;

/// <summary>
/// The public relay's conversation credentials were rejected. It is deliberately distinct from a
/// transient network failure, because a wrong key must never be retried and never fall back.
/// </summary>
public sealed class PublicRelayAuthException(string message) : Exception(message);

/// <summary>
/// The default transport of the file assistant when no custom OpenList is configured: the fixed public
/// relay, addressed only by the conversation identity the user already shares with their own devices.
///
/// Nothing here needs a user account, a Tailscale network, a WebDAV address or a port. The conversation
/// id is the Basic user name and the 64 hex conversation key is the password, so the relay only ever
/// sees an opaque private namespace; the DAV side reuses <see cref="OpenListClient"/> and therefore the
/// same <c>assistant/&lt;conversationId&gt;/...</c> layout, redirect rules and credential boundaries.
/// </summary>
public sealed class PublicRelayClient : IDisposable
{
    /// <summary>The production relay. It is fixed; tests override <see cref="BaseAddressOverride"/> only.</summary>
    public static readonly Uri ProductionBaseAddress = new("https://proxy.lixinrui000.cn");

    /// <summary>Test-only seam for a simulated relay; production always uses <see cref="ProductionBaseAddress"/>.</summary>
    internal static Func<Uri>? BaseAddressOverride { get; set; }

    public static Uri BaseAddress => BaseAddressOverride?.Invoke() ?? ProductionBaseAddress;
    public const string DavPath = "/mpt/relay/dav/";
    public const string ConversationsPath = "/mpt/relay/v1/conversations";
    public const string ChangesPath = "/mpt/relay/v1/changes";

    /// <summary>The server holds a long poll for at most 25 seconds, so the client allows a short grace.</summary>
    public static readonly TimeSpan LongPollBudget = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan RegistrationBudget = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly string _conversationId;
    private readonly string _conversationKey;

    /// <param name="conversationId">The shared conversation id; also the Basic user name.</param>
    /// <param name="conversationKey">The 64 hex conversation key; also the Basic password.</param>
    public PublicRelayClient(string conversationId, string conversationKey)
    {
        _conversationId = TransferFiles.DeviceId(conversationId);
        if (conversationKey.Length != 64 || !conversationKey.All(Uri.IsHexDigit))
            throw new ArgumentException("会话密钥格式不正确。", nameof(conversationKey));
        _conversationKey = conversationKey;
        _base = BaseAddress;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>The WebDAV root of this conversation's private namespace.</summary>
    public string DavRoot => new Uri(_base, DavPath).ToString();

    /// <summary>A DAV client over this conversation; the same assistant namespace as a self-hosted relay.</summary>
    public OpenListClient CreateDavClient() => new(DavRoot, _conversationId, _conversationKey);

    /// <summary>
    /// Registers this conversation's first session. It is idempotent for the same key and needs no user
    /// account; a rejected key is reported as an authentication failure instead of a retryable error.
    /// </summary>
    public async Task RegisterAsync(CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Post, ConversationsPath, content: null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(RegistrationBudget);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("注册文件助手会话超时，请检查公网连接。");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PublicRelayAuthException("文件助手会话密钥与服务器不一致，请重新扫描连接码。");
            if (!response.IsSuccessStatusCode)
                throw new IOException($"文件助手服务器返回 HTTP {(int)response.StatusCode}。");
        }
    }

    /// <summary>
    /// Waits for the conversation to change. The server holds the request until a manifest or a receipt
    /// is written, or until its own 25 second cap; a caller with no <paramref name="since"/> gets the
    /// current revision immediately, and a <paramref name="since"/> ahead of the server returns at once.
    /// </summary>
    public async Task<long> ChangesAsync(long? since, CancellationToken token)
    {
        var path = since is null ? ChangesPath : $"{ChangesPath}?since={since.Value}";
        using var request = CreateRequest(HttpMethod.Get, path, content: null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(LongPollBudget);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // The long poll simply ended without news; that is not an error for the caller.
            throw new IOException("等待文件助手事件超时。");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new PublicRelayAuthException("文件助手会话密钥与服务器不一致，请重新扫描连接码。");
            if (response.StatusCode == HttpStatusCode.NoContent) return since ?? 0;
            if (!response.IsSuccessStatusCode)
                throw new IOException($"文件助手服务器返回 HTTP {(int)response.StatusCode}。");
            var payload = await response.Content.ReadFromJsonAsync<Changes>(DirectTransfer.Json, token);
            if (payload is null) throw new InvalidDataException("文件助手服务器没有返回 revision。");
            return payload.Revision;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_conversationId}:{_conversationKey}")));
        return request;
    }

    private sealed record Changes(long Revision);

    public void Dispose() => _http.Dispose();
}
