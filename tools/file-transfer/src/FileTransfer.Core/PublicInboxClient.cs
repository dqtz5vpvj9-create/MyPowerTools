using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// The device-pairing inbox of the fixed public relay (PROTOCOL.md §6): files and text delivered to
/// another device without handing over the conversation identity.
///
/// Two independent credentials exist and this type never mixes them up:
///
/// <list type="bullet">
/// <item><see cref="Owner"/> holds the private <c>ownerKey</c>: register the inbox, long-poll pending
/// items, download payloads, write real receipts, delete items.</item>
/// <item><see cref="Deposit"/> holds only the <c>depositKey</c> from a pairing code: deliver one item
/// under a random <c>itemId</c>, and read back its receipt. It can never list, read a payload or
/// write a receipt — both the client and the relay refuse that.</item>
/// </list>
///
/// The class is transport-only: key generation (<see cref="PublicInboxIdentity.CreateNew"/>), the
/// pairing code, and persistence belong to the module. Nothing here needs a user account, Tailscale,
/// a WebDAV address or a port; the production transport is the fixed
/// <see cref="PublicRelayClient.ProductionBaseAddress"/> and redirects are refused so the Basic
/// credential can never be forwarded to a third party.
/// </summary>
public sealed class PublicInboxClient : IDisposable
{
    /// <summary><c>POST</c> here registers an inbox; <c>GET</c> on <see cref="ItemsPath"/> lists it.</summary>
    public const string InboxesPath = "/mpt/relay/v1/inboxes";

    /// <summary>Owner list/long-poll, owner payload, deposit <c>PUT</c>, receipts.</summary>
    public const string ItemsPath = "/mpt/relay/v1/inboxes/items";

    private const string ReceiptPathSuffix = "/receipt";

    /// <summary>The relay holds a long poll for at most 25 seconds; the same 35 second grace as the session client.</summary>
    public static TimeSpan LongPollBudget => PublicRelayClient.LongPollBudget;

    /// <summary>Budget of the small, non-blocking JSON calls (registration, receipts, delete, HEAD).</summary>
    public static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(40);

    private static readonly TimeSpan RegistrationBudget = TimeSpan.FromSeconds(20);

    /// <summary>A list page is small; anything larger is not the fixed protocol.</summary>
    private const int MaxListJsonBytes = 2 * 1024 * 1024;

    private const int MaxSmallJsonBytes = 64 * 1024;
    private const int MaxErrorJsonBytes = 8 * 1024;
    private const int CopyBufferBytes = 128 * 1024;

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly PublicInboxRole _role;
    private readonly string _inboxId;
    private readonly string _key;
    private readonly string? _ownerDepositKey;
    private readonly PublicInboxRetryPolicy _retry;

    private PublicInboxClient(
        PublicInboxRole role,
        string inboxId,
        string key,
        string? ownerDepositKey,
        Uri baseAddress,
        PublicInboxRetryPolicy retry)
    {
        if (baseAddress.Scheme != Uri.UriSchemeHttps
            && !(baseAddress.Scheme == Uri.UriSchemeHttp && (baseAddress.IsLoopback || InboxRelays.IsTrustedHttp(baseAddress))))
            throw new ArgumentException("投递凭据只允许通过 HTTPS 或内置的可信 Tail relay 传输。", nameof(baseAddress));
        _role = role;
        _inboxId = inboxId;
        _key = key;
        _ownerDepositKey = ownerDepositKey;
        BaseAddress = baseAddress;
        _retry = retry;
        _http = new HttpClient(new SocketsHttpHandler
        {
            // A redirect would replay the Basic credential at an address the pairing code never named.
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(4)
        })
        {
            // Every call carries its own budget or is explicitly cancellable by the caller.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>The relay this instance talks to. Production omits the argument and gets the fixed address.</summary>
    public Uri BaseAddress { get; }

    public PublicInboxRole Role => _role;

    public string InboxId => _inboxId;

    public bool IsOwner => _role == PublicInboxRole.Owner;

    public bool IsDeposit => _role == PublicInboxRole.Deposit;

    public PublicInboxRetryPolicy RetryPolicy => _retry;

    /// <summary>
    /// The address a production instance uses: <see cref="PublicRelayClient.BaseAddress"/>, which is the
    /// fixed HTTPS endpoint unless a test installed its own seam. Overriding it is never a user setting.
    /// </summary>
    public static Uri ResolveBaseAddress() => PublicRelayClient.BaseAddress;

    /// <summary>
    /// The receiving device's client. Pass the persisted <see cref="PublicInboxIdentity"/>; the owner key
    /// is used for every call this instance makes.
    /// </summary>
    /// <param name="identity">The locally stored identity; the owner key is never exported.</param>
    /// <param name="retry">Automatic retry for the retryable relay states; defaults to <see cref="PublicInboxRetryPolicy.Default"/>.</param>
    /// <param name="baseAddress">Test-only transport seam; production omits it and uses <see cref="ResolveBaseAddress"/>.</param>
    public static PublicInboxClient Owner(
        PublicInboxIdentity identity,
        PublicInboxRetryPolicy? retry = null,
        Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        EnsureInboxId(identity.InboxId);
        EnsureKey(identity.OwnerKey, nameof(identity.OwnerKey));
        EnsureKey(identity.DepositKey, nameof(identity.DepositKey));
        if (string.Equals(identity.OwnerKey, identity.DepositKey, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("ownerKey 与 depositKey 必须是两个独立的随机值。", nameof(identity));
        return new PublicInboxClient(
            PublicInboxRole.Owner,
            identity.InboxId,
            identity.OwnerKey.ToLowerInvariant(),
            identity.DepositKey.ToLowerInvariant(),
            baseAddress ?? ResolveBaseAddress(),
            retry ?? PublicInboxRetryPolicy.Default);
    }

    /// <summary>
    /// The sending device's client, built from the pairing code alone. There is no constructor that takes
    /// an ambiguous id+key pair, so a caller can never end up with the wrong role by accident.
    /// </summary>
    public static PublicInboxClient Deposit(
        PublicInboxPairing pairing,
        PublicInboxRetryPolicy? retry = null,
        Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        EnsureInboxId(pairing.InboxId);
        EnsureKey(pairing.DepositKey, nameof(pairing.DepositKey));
        return new PublicInboxClient(
            PublicInboxRole.Deposit,
            pairing.InboxId,
            pairing.DepositKey.ToLowerInvariant(),
            ownerDepositKey: null,
            baseAddress ?? ResolveBaseAddress(),
            retry ?? PublicInboxRetryPolicy.Default);
    }

    /// <summary>Convenience overload: a device may deliver to its own inbox with its own pairing.</summary>
    public static PublicInboxClient Deposit(
        PublicInboxIdentity identity,
        PublicInboxRetryPolicy? retry = null,
        Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return Deposit(identity.Pairing, retry, baseAddress);
    }

    // -- owner: registration -----------------------------------------------------------------

    /// <summary>
    /// <c>POST /v1/inboxes</c>: registers the inbox with its deposit key. Idempotent for the same key
    /// pair (200); the first registration is 201. A rejected owner key is a
    /// <see cref="PublicInboxAuthException"/> and a different deposit key is a
    /// <see cref="PublicInboxConflictException"/> — neither is retried.
    /// </summary>
    public async Task<PublicInboxRegistration> RegisterAsync(CancellationToken token = default)
    {
        RequireOwner(nameof(RegisterAsync));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new RegisterBody(_ownerDepositKey!), RequestJson);
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Post, InboxesPath, () => JsonContent(payload)),
            "注册收件箱",
            RegistrationBudget,
            MaxSmallJsonBytes,
            token,
            retryable: false).ConfigureAwait(false);
        EnsureSuccess(call, "注册收件箱");
        var root = RequireJson(call, "注册收件箱");
        return new PublicInboxRegistration(
            PublicInboxJson.OptionalString(root, "inboxId") ?? _inboxId,
            PublicInboxJson.OptionalBool(root, "created") ?? (call.Status == HttpStatusCode.Created),
            PublicInboxJson.OptionalInt64(root, "revision") ?? 0);
    }

    // -- owner: receive ----------------------------------------------------------------------

    /// <summary>
    /// <c>GET /v1/inboxes/items</c>: the pending items, newest first, that have no receipt yet.
    /// With <paramref name="since"/> equal to the current revision the relay holds the request for up
    /// to 25 seconds and returns as soon as something is delivered (the client allows 35 seconds);
    /// without it, or with a revision ahead of the server, the current revision comes back at once.
    /// A 204 (the relay ended the poll early) is reported as an empty page at <paramref name="since"/>.
    /// </summary>
    public async Task<PublicInboxPage> PollAsync(long? since = null, int? limit = null, CancellationToken token = default)
    {
        RequireOwner(nameof(PollAsync));
        if (since is < 0) throw new ArgumentOutOfRangeException(nameof(since), "since 不能为负数。");
        var effectiveLimit = PublicInboxPage.ClampLimit(limit);
        var query = new StringBuilder();
        if (since is not null) query.Append("?since=").Append(since.Value.ToString(CultureInfo.InvariantCulture));
        if (limit is not null)
            query.Append(query.Length == 0 ? '?' : '&').Append("limit=").Append(effectiveLimit.ToString(CultureInfo.InvariantCulture));
        var budget = since is null ? RequestBudget : LongPollBudget;
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Get, ItemsPath + query, content: null),
            "读取收件箱条目",
            budget,
            MaxListJsonBytes,
            token,
            retryable: false).ConfigureAwait(false);
        if (call.Status == HttpStatusCode.NoContent) return PublicInboxPage.Empty(since ?? 0);
        EnsureSuccess(call, "读取收件箱条目");
        return PublicInboxPage.FromJson(RequireJson(call, "读取收件箱条目"));
    }

    /// <summary><c>HEAD /v1/inboxes/items/{itemId}</c>: the descriptor and size without the payload.</summary>
    public async Task<PublicInboxItem> HeadAsync(string itemId, CancellationToken token = default)
    {
        RequireOwner(nameof(HeadAsync));
        EnsureItemId(itemId);
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Head, ItemPath(itemId), content: null),
            "读取收件箱条目信息",
            RequestBudget,
            MaxErrorJsonBytes,
            token,
            retryable: false,
            headerItemId: itemId).ConfigureAwait(false);
        EnsureSuccess(call, "读取收件箱条目信息");
        return call.Item ?? throw new PublicInboxProtocolException("读取收件箱条目信息：服务器没有返回 X-MPT-* 元信息。");
    }

    /// <summary>
    /// <c>GET /v1/inboxes/items/{itemId}</c>: streams the payload into <paramref name="destination"/>
    /// (which is never disposed, and is written exactly once) and returns the descriptor plus the byte
    /// count. The response length is verified against the declared <c>Content-Length</c>.
    /// </summary>
    public async Task<PublicInboxPayload> DownloadAsync(string itemId, Stream destination, CancellationToken token = default)
    {
        RequireOwner(nameof(DownloadAsync));
        ArgumentNullException.ThrowIfNull(destination);
        EnsureItemId(itemId);
        using var request = CreateRequest(HttpMethod.Get, ItemPath(itemId), content: null);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new PublicInboxUnavailableException("下载收件箱条目超时，请检查公网连接后重试。");
        }
        catch (HttpRequestException error)
        {
            throw new PublicInboxUnavailableException($"下载收件箱条目失败：无法连接中转服务：{error.Message}", inner: error);
        }
        using (response)
        {
            ThrowIfRedirect(response.StatusCode, "下载收件箱条目");
            if (response.StatusCode != HttpStatusCode.OK)
                throw await BuildErrorAsync(response, "下载收件箱条目", token).ConfigureAwait(false);
            var item = ItemFromResponse(response, itemId);
            var declared = response.Content.Headers.ContentLength;
            long copied = 0;
            await using (var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    while (true)
                    {
                        var read = await body.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), token).ConfigureAwait(false);
                        if (read == 0) break;
                        await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        copied += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            if (declared is not null && declared.Value != copied)
                throw new PublicInboxProtocolException(
                    $"下载收件箱条目时响应长度不符：声明 {declared.Value} 字节，实际 {copied} 字节。");
            return new PublicInboxPayload(item, copied);
        }
    }

    /// <summary>
    /// <c>POST /v1/inboxes/items/{itemId}/receipt</c>: the owner records that the payload was really
    /// saved. Only this call produces the <c>saved:true</c> receipt the sender can read back; a repeat
    /// with identical values answers <c>duplicate:true</c> without rewriting anything.
    /// </summary>
    public async Task<PublicInboxReceipt> AcknowledgeAsync(
        string itemId,
        long bytes,
        DateTimeOffset? savedAt = null,
        string? deviceId = null,
        string? deviceName = null,
        CancellationToken token = default)
    {
        RequireOwner(nameof(AcknowledgeAsync));
        EnsureItemId(itemId);
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes), "bytes 不能为负数。");
        PublicInboxValidation.EnsureDeviceId(deviceId, nameof(deviceId));
        PublicInboxValidation.EnsureSenderName(deviceName);
        var saved = (savedAt ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ReceiptBody(itemId, Iso8601(saved), bytes, deviceId, deviceName), RequestJson);
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Post, ItemPath(itemId) + ReceiptPathSuffix, () => JsonContent(payload)),
            "写入投递回执",
            RequestBudget,
            MaxSmallJsonBytes,
            token,
            retryable: false).ConfigureAwait(false);
        EnsureSuccess(call, "写入投递回执");
        return PublicInboxReceipt.FromJson(RequireJson(call, "写入投递回执"));
    }

    /// <summary><c>DELETE /v1/inboxes/items/{itemId}</c>: drops the item and frees its quota (owner only).</summary>
    public async Task DeleteAsync(string itemId, CancellationToken token = default)
    {
        RequireOwner(nameof(DeleteAsync));
        EnsureItemId(itemId);
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Delete, ItemPath(itemId), content: null),
            "删除收件箱条目",
            RequestBudget,
            MaxErrorJsonBytes,
            token,
            retryable: false).ConfigureAwait(false);
        if (call.Status == HttpStatusCode.NoContent) return;
        EnsureSuccess(call, "删除收件箱条目");
    }

    // -- both roles: the receipt of one known item -------------------------------------------

    /// <summary>
    /// <c>GET /v1/inboxes/items/{itemId}/receipt</c>: the delivery confirmation. The random 128 bit
    /// <paramref name="itemId"/> is the capability, so the sender can read exactly the one receipt it
    /// created — there is no listing and no enumeration endpoint for a deposit credential.
    /// </summary>
    public async Task<PublicInboxReceipt> GetReceiptAsync(string itemId, CancellationToken token = default)
    {
        EnsureItemId(itemId);
        using var call = await CallAsync(
            () => CreateRequest(HttpMethod.Get, ItemPath(itemId) + ReceiptPathSuffix, content: null),
            "查询投递回执",
            RequestBudget,
            MaxSmallJsonBytes,
            token,
            retryable: false).ConfigureAwait(false);
        EnsureSuccess(call, "查询投递回执");
        return PublicInboxReceipt.FromJson(RequireJson(call, "查询投递回执"));
    }

    // -- deposit: send -----------------------------------------------------------------------

    /// <summary>
    /// <c>PUT /v1/inboxes/items/{itemId}</c>: delivers one file. The metadata travels in bounded
    /// percent-encoded UTF-8 headers, the content is streamed from disk (one fresh
    /// <see cref="FileStream"/> per attempt, so a retry re-sends the whole file), and
    /// <c>503 inbox_not_ready</c> is retried with backoff.
    /// </summary>
    /// <param name="itemId">32 hex characters generated by <see cref="PublicInboxIds.NewItemId"/>; a retry must reuse it.</param>
    /// <param name="filePath">The local file to send.</param>
    /// <param name="displayName">Name the receiver sees; defaults to the local file name.</param>
    public Task<PublicInboxDepositResult> DepositFileAsync(
        string itemId,
        string filePath,
        string? displayName = null,
        string? senderDeviceId = null,
        string? senderName = null,
        DateTimeOffset? createdAt = null,
        string? targetDeviceId = null,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var length = new FileInfo(filePath).Length;
        var deposit = new PublicInboxDeposit
        {
            ItemId = itemId,
            Kind = PublicInboxItemKind.File,
            Name = displayName ?? Path.GetFileName(filePath),
            Length = length,
            SenderDeviceId = senderDeviceId,
            SenderName = senderName,
            CreatedAt = createdAt,
            TargetDeviceId = targetDeviceId
        };
        return DepositCoreAsync(
            deposit,
            _ => new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan),
            ownsStreams: true,
            policy: null,
            token);
    }

    /// <summary><c>PUT /v1/inboxes/items/{itemId}</c> with <c>X-MPT-Kind: text</c> and a UTF-8 body; no file name.</summary>
    public Task<PublicInboxDepositResult> DepositTextAsync(
        string itemId,
        string text,
        string? senderDeviceId = null,
        string? senderName = null,
        DateTimeOffset? createdAt = null,
        string? targetDeviceId = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        var deposit = new PublicInboxDeposit
        {
            ItemId = itemId,
            Kind = PublicInboxItemKind.Text,
            Name = null,
            Length = bytes.Length,
            SenderDeviceId = senderDeviceId,
            SenderName = senderName,
            CreatedAt = createdAt,
            TargetDeviceId = targetDeviceId
        };
        return DepositCoreAsync(
            deposit,
            _ => new MemoryStream(bytes, writable: false),
            ownsStreams: true,
            policy: null,
            token);
    }

    /// <summary>
    /// <c>PUT /v1/inboxes/items/{itemId}</c> from a caller-owned stream, which is neither disposed nor
    /// rewound permanently. A seekable stream is rewound for every retry; a non-seekable one is sent
    /// exactly once, because it cannot be replayed
    /// (<see cref="DepositFileAsync"/> and <see cref="DepositTextAsync"/> open a fresh stream per attempt
    /// and therefore do retry).
    /// </summary>
    public Task<PublicInboxDepositResult> DepositAsync(
        PublicInboxDeposit deposit,
        Stream content,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(deposit);
        ArgumentNullException.ThrowIfNull(content);
        var start = content.CanSeek ? content.Position : 0;
        var effective = deposit.Length is null && content.CanSeek ? deposit with { Length = content.Length - start } : deposit;
        if (content.CanSeek)
        {
            return DepositCoreAsync(
                effective,
                _ =>
                {
                    content.Position = start;
                    return new BorrowedStream(content);
                },
                ownsStreams: false,
                policy: null,
                token);
        }
        return DepositCoreAsync(
            effective,
            _ => new BorrowedStream(content),
            ownsStreams: false,
            policy: PublicInboxRetryPolicy.None,
            token);
    }

    private async Task<PublicInboxDepositResult> DepositCoreAsync(
        PublicInboxDeposit deposit,
        Func<int, Stream> openStream,
        bool ownsStreams,
        PublicInboxRetryPolicy? policy,
        CancellationToken token)
    {
        RequireDeposit(nameof(DepositAsync));
        ValidateDeposit(deposit);
        var retry = policy ?? _retry;
        var attempts = retry.EffectiveAttempts;
        var path = ItemPath(deposit.ItemId);
        for (var attempt = 1; ; attempt++)
        {
            var stream = openStream(attempt);
            HttpResponseMessage response;
            try
            {
                using var request = CreateRequest(
                    HttpMethod.Put,
                    path,
                    () => StreamContentFor(stream, deposit.Length),
                    message => ApplyDepositHeaders(message, deposit));
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ownsStreams) await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (HttpRequestException error)
            {
                if (ownsStreams) await stream.DisposeAsync().ConfigureAwait(false);
                if (attempt < attempts)
                {
                    await DelayAsync(attempt, null, retry, token).ConfigureAwait(false);
                    continue;
                }
                throw new PublicInboxUnavailableException(
                    $"投递到收件箱失败：无法连接中转服务：{error.Message}", attempts: attempt, inner: error);
            }
            using (response)
            {
                ThrowIfRedirect(response.StatusCode, "投递到收件箱");
                var retryAfter = ParseRetryAfter(response);
                if (attempt < attempts && IsRetryableStatus(response.StatusCode))
                {
                    await DelayAsync(attempt, retryAfter, retry, token).ConfigureAwait(false);
                    continue;
                }
                var (json, code, detail) = await ReadJsonAsync(response, MaxSmallJsonBytes, "投递到收件箱", token).ConfigureAwait(false);
                using (json)
                {
                    if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                        return DepositResultFromJson(json, deposit, attempt);
                    throw PublicInboxErrors.FromStatus(response.StatusCode, "投递到收件箱", code, detail, retryAfter, _role, attempt);
                }
            }
        }
    }

    // -- transport ---------------------------------------------------------------------------

    private async Task<RelayCall> CallAsync(
        Func<HttpRequestMessage> factory,
        string operation,
        TimeSpan? budget,
        int maxBytes,
        CancellationToken token,
        bool retryable,
        string? headerItemId = null)
    {
        var attempts = retryable ? _retry.EffectiveAttempts : 1;
        for (var attempt = 1; ; attempt++)
        {
            using var request = factory();
            using var linked = budget is null ? null : CancellationTokenSource.CreateLinkedTokenSource(token);
            if (linked is not null) linked.CancelAfter(budget!.Value);
            var sendToken = linked?.Token ?? token;
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, sendToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt < attempts)
                {
                    await DelayAsync(attempt, null, _retry, token).ConfigureAwait(false);
                    continue;
                }
                throw new PublicInboxUnavailableException(
                    $"{operation}超时（{budget?.TotalSeconds ?? 0:0} 秒预算），请检查公网连接后重试。", attempts: attempt);
            }
            catch (HttpRequestException error)
            {
                if (attempt < attempts)
                {
                    await DelayAsync(attempt, null, _retry, token).ConfigureAwait(false);
                    continue;
                }
                throw new PublicInboxUnavailableException(
                    $"{operation}失败：无法连接中转服务：{error.Message}", attempts: attempt, inner: error);
            }
            using (response)
            {
                ThrowIfRedirect(response.StatusCode, operation);
                var retryAfter = ParseRetryAfter(response);
                if (retryable && attempt < attempts && IsRetryableStatus(response.StatusCode))
                {
                    await DelayAsync(attempt, retryAfter, _retry, token).ConfigureAwait(false);
                    continue;
                }
                var (json, code, detail) = await ReadJsonAsync(response, maxBytes, operation, sendToken).ConfigureAwait(false);
                return new RelayCall(response.StatusCode, json, code, detail, retryAfter, attempt)
                {
                    // Only a 200 carries the descriptor headers; an error body has none.
                    Item = headerItemId is null || response.StatusCode != HttpStatusCode.OK
                        ? null
                        : ItemFromResponse(response, headerItemId)
                };
            }
        }
    }

    private async Task<Exception> BuildErrorAsync(HttpResponseMessage response, string operation, CancellationToken token)
    {
        var retryAfter = ParseRetryAfter(response);
        var (json, code, detail) = await ReadJsonAsync(response, MaxErrorJsonBytes, operation, token).ConfigureAwait(false);
        json?.Dispose();
        return PublicInboxErrors.FromStatus(response.StatusCode, operation, code, detail, retryAfter, _role, attempts: 1);
    }

    /// <summary>Reads a bounded JSON body; a malformed or oversized body never becomes an unbounded buffer.</summary>
    private static async Task<(JsonDocument? Json, string? Code, string? Detail)> ReadJsonAsync(
        HttpResponseMessage response,
        int maxBytes,
        string operation,
        CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.NoContent) return (null, null, null);
        var declared = response.Content.Headers.ContentLength;
        if (declared is not null && declared.Value > maxBytes)
            throw new PublicInboxProtocolException($"{operation}：服务器声明了 {declared.Value} 字节响应，超过 {maxBytes} 字节上限。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var collected = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                if (collected.Length + read > maxBytes)
                    throw new PublicInboxProtocolException($"{operation}：服务器响应超过 {maxBytes} 字节上限，拒绝继续读取。");
                collected.Write(buffer, 0, read);
            }
            if (collected.Length == 0) return (null, null, null);
            var bytes = collected.ToArray();
            try
            {
                var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    throw new PublicInboxProtocolException($"{operation}：服务器响应不是 JSON 对象。");
                }
                var code = PublicInboxJson.OptionalString(root, "error");
                var detail = PublicInboxJson.OptionalString(root, "detail");
                return (document, code, Truncate(detail, 512));
            }
            catch (JsonException)
            {
                // Keep the status meaningful; the raw body is only a truncated diagnostic.
                return (null, null, Truncate(Encoding.UTF8.GetString(bytes), 200));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        Func<HttpContent>? content,
        Action<HttpRequestMessage>? decorate = null)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseAddress, path)) { Content = content?.Invoke() };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_inboxId}:{_key}")));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        decorate?.Invoke(request);
        return request;
    }

    private static void ApplyDepositHeaders(HttpRequestMessage request, PublicInboxDeposit deposit)
    {
        Header(request, "X-MPT-Kind", deposit.Kind.ToWire());
        if (deposit.Name is not null) Header(request, "X-MPT-Name", PublicInboxHeader.Encode(deposit.Name));
        if (deposit.SenderDeviceId is not null) Header(request, "X-MPT-Sender-Id", PublicInboxHeader.Encode(deposit.SenderDeviceId));
        if (deposit.SenderName is not null) Header(request, "X-MPT-Sender-Name", PublicInboxHeader.Encode(deposit.SenderName));
        if (deposit.CreatedAt is not null) Header(request, "X-MPT-Created-At", Iso8601(deposit.CreatedAt.Value));
        if (deposit.TargetDeviceId is not null) Header(request, "X-MPT-Target-Device-Id", PublicInboxHeader.Encode(deposit.TargetDeviceId));
    }

    private static void Header(HttpRequestMessage request, string name, string value) =>
        request.Headers.TryAddWithoutValidation(name, value);

    private static HttpContent StreamContentFor(Stream stream, long? length)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (length is not null) content.Headers.ContentLength = length.Value;
        return content;
    }

    private static HttpContent JsonContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static PublicInboxItem ItemFromResponse(HttpResponseMessage response, string fallbackItemId)
    {
        string? Value(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        var itemId = Value("X-MPT-Item-Id") ?? fallbackItemId;
        if (!PublicInboxIds.IsItemId(itemId))
            throw new PublicInboxProtocolException($"条目响应的 X-MPT-Item-Id 非法：{itemId}");
        var kindText = Value("X-MPT-Kind") ?? PublicInboxWire.FileKind;
        if (!PublicInboxWire.TryParseKind(kindText, out var kind))
            throw new PublicInboxProtocolException($"条目响应的 X-MPT-Kind 非法：{kindText}");
        var name = DecodeHeader(Value("X-MPT-Name"));
        if (kind == PublicInboxItemKind.Text)
        {
            if (name is not null) throw new PublicInboxProtocolException("条目响应给文本条目带了文件名。");
        }
        else if (!PublicInboxValidation.IsValidFileName(name, out var nameReason))
        {
            throw new PublicInboxProtocolException($"条目响应的文件名不可用：{nameReason}");
        }
        var rawSize = Value("X-MPT-Size");
        long size;
        if (rawSize is null) size = response.Content.Headers.ContentLength ?? 0;
        else if (!long.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size < 0)
            throw new PublicInboxProtocolException($"条目响应的 X-MPT-Size 非法：{rawSize}");
        var createdAt = default(DateTimeOffset);
        var rawCreatedAt = Value("X-MPT-Created-At");
        if (rawCreatedAt is not null &&
            !DateTimeOffset.TryParse(PublicInboxHeader.Decode(rawCreatedAt), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out createdAt))
            throw new PublicInboxProtocolException("条目响应的 X-MPT-Created-At 不是 ISO-8601 时间。");
        return new PublicInboxItem
        {
            ItemId = itemId,
            Kind = kind,
            Size = size,
            CreatedAt = createdAt,
            Name = name,
            SenderDeviceId = DecodeHeader(Value("X-MPT-Sender-Id")),
            SenderName = DecodeHeader(Value("X-MPT-Sender-Name")),
            TargetDeviceId = DecodeHeader(Value("X-MPT-Target-Device-Id"))
        };
    }

    private static string? DecodeHeader(string? value) => value is null ? null : PublicInboxHeader.Decode(value);

    private static PublicInboxDepositResult DepositResultFromJson(JsonDocument? json, PublicInboxDeposit deposit, int attempts)
    {
        if (json is null) throw new PublicInboxProtocolException("投递到收件箱：服务器没有返回 JSON。");
        var root = json.RootElement;
        var itemId = PublicInboxJson.OptionalString(root, "itemId") ?? deposit.ItemId;
        if (!PublicInboxIds.IsItemId(itemId))
            throw new PublicInboxProtocolException($"投递结果里的 itemId 非法：{itemId}");
        var size = PublicInboxJson.OptionalInt64(root, "size") ?? deposit.Length ?? 0;
        if (size < 0) throw new PublicInboxProtocolException("投递结果里的 size 不能为负数。");
        return new PublicInboxDepositResult(
            itemId,
            size,
            PublicInboxJson.OptionalInt64(root, "revision") ?? 0,
            PublicInboxJson.OptionalBool(root, "duplicate") ?? false,
            attempts);
    }

    private static void ValidateDeposit(PublicInboxDeposit deposit)
    {
        EnsureItemId(deposit.ItemId);
        if (deposit.Length is < 0) throw new PublicInboxRequestException("条目长度不能为负数。");
        if (deposit.Kind == PublicInboxItemKind.Text)
        {
            if (deposit.Name is not null) throw new PublicInboxRequestException("文本条目不应带文件名。");
            if (deposit.Length == 0) throw new PublicInboxRequestException("文本条目不能为空。");
        }
        else
        {
            PublicInboxValidation.EnsureFileName(deposit.Name);
        }
        PublicInboxValidation.EnsureSenderName(deposit.SenderName);
        PublicInboxValidation.EnsureDeviceId(deposit.SenderDeviceId, nameof(deposit.SenderDeviceId));
        PublicInboxValidation.EnsureDeviceId(deposit.TargetDeviceId, nameof(deposit.TargetDeviceId));
    }

    private void EnsureSuccess(RelayCall call, string operation)
    {
        if ((int)call.Status < 400) return;
        throw PublicInboxErrors.FromStatus(call.Status, operation, call.ErrorCode, call.ErrorDetail, call.RetryAfter, _role, call.Attempts);
    }

    private static JsonElement RequireJson(RelayCall call, string operation) =>
        call.Json?.RootElement ?? throw new PublicInboxProtocolException($"{operation}：服务器没有返回 JSON。");

    private static void ThrowIfRedirect(HttpStatusCode status, string operation)
    {
        if ((int)status is >= 300 and < 400)
            throw new PublicInboxProtocolException(
                $"{operation}被中转服务要求重定向；为避免把收件箱凭据发给第三方，客户端拒绝跟随重定向。");
    }

    private static bool IsRetryableStatus(HttpStatusCode status) =>
        status is HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.GatewayTimeout;

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    private static async Task DelayAsync(int attempt, TimeSpan? retryAfter, PublicInboxRetryPolicy policy, CancellationToken token)
    {
        var delay = policy.Clamp(retryAfter ?? policy.Backoff(attempt));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
    }

    private void RequireOwner(string operation)
    {
        if (_role != PublicInboxRole.Owner)
            throw new InvalidOperationException($"{operation} 需要 ownerKey 收件箱实例；当前实例只有投递密钥。");
    }

    private void RequireDeposit(string operation)
    {
        if (_role != PublicInboxRole.Deposit)
            throw new InvalidOperationException($"{operation} 需要投递密钥实例；owner 凭据不能投递。");
    }

    private static void EnsureInboxId(string inboxId)
    {
        if (!PublicInboxIds.IsInboxId(inboxId))
            throw new ArgumentException("收件箱 id 必须是 1–64 位英文字母、数字、短横线或下划线。", nameof(inboxId));
    }

    private static void EnsureKey(string key, string name)
    {
        if (!PublicInboxIds.IsKey(key))
            throw new ArgumentException("收件箱密钥必须是 64 位十六进制字符。", name);
    }

    private static void EnsureItemId(string itemId)
    {
        if (!PublicInboxIds.IsItemId(itemId))
            throw new PublicInboxRequestException("条目 id 必须是 32 位小写十六进制（128 bit 随机值）。");
    }

    private static string ItemPath(string itemId) => $"{ItemsPath}/{itemId}";

    private static string Iso8601(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];

    public void Dispose() => _http.Dispose();

    private sealed record RegisterBody(string DepositKey);

    private sealed record ReceiptBody(string ItemId, string SavedAt, long Bytes, string? DeviceId, string? DeviceName);

    private sealed class RelayCall(
        HttpStatusCode status,
        JsonDocument? json,
        string? errorCode,
        string? errorDetail,
        TimeSpan? retryAfter,
        int attempts) : IDisposable
    {
        public HttpStatusCode Status { get; } = status;
        public JsonDocument? Json { get; } = json;
        public string? ErrorCode { get; } = errorCode;
        public string? ErrorDetail { get; } = errorDetail;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public int Attempts { get; } = attempts;

        /// <summary>The descriptor parsed from the <c>X-MPT-*</c> response headers, when the call carried them.</summary>
        public PublicInboxItem? Item { get; init; }

        public void Dispose() => Json?.Dispose();
    }

    /// <summary>
    /// A pass-through stream whose <see cref="Dispose"/> is a no-op, so the <see cref="StreamContent"/>
    /// the retry loop builds can never close a stream the caller still owns.
    /// </summary>
    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing) { /* the caller owns the inner stream */ }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
