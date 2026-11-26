using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;

namespace MobileToolControl.Android;

/// <summary>
/// The phone's only network client for the remote tool gateway: a small, literal-address HTTP/JSON
/// client for the <c>/mpt-control/v1</c> wire in <c>docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md</c>.
///
/// Security properties, all enforced here and not by a setting:
/// <list type="bullet">
/// <item>the endpoint is re-validated against the Tailnet policy on every request, so a hand-edited
/// device record cannot redirect traffic to a LAN or public address;</item>
/// <item>redirects are never followed (<c>AllowAutoRedirect = false</c>); a 3xx is an error, which is
/// also what stops the bearer token from being replayed to a different host;</item>
/// <item>the token is attached as <c>Authorization: Bearer</c> and never appears in a message, an
/// event payload, a module file or a command result;</item>
/// <item>responses are size-limited and JSON-validated; a body that is not the contract's document is
/// a protocol error instead of a silent success.</item>
/// </list>
/// </summary>
internal sealed class MobileToolControlHttpClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        // No default credentials, no proxy credentials and no ambient authentication: the grant token
        // is the only credential this client ever presents.
        UseDefaultCredentials = false,
        PreAuthenticate = false
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly MobileToolEndpoint _endpoint;
    private readonly string _token;
    private readonly HttpClient _http;

    public MobileToolControlHttpClient(MobileToolEndpoint endpoint, string token, HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _http = httpClient ?? Http;
        if (_token.Length == 0)
        {
            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Unauthorized,
                "这台电脑的授权凭据已丢失，请重新导入连接码。");
        }
    }

    public Task<MobileToolCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Get,
            MobileToolControlOptions.WirePrefix + "/catalog",
            null,
            MobileToolControlOptions.CatalogTimeout,
            document => MobileToolControlWire.ParseCatalog(document),
            cancellationToken);

    public Task<MobileToolInvocation> PostInvocationAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["invocationId"] = invocationId,
            ["commandId"] = commandId,
            ["args"] = args.DeepClone()
        };

        return SendAsync(
            HttpMethod.Post,
            MobileToolControlOptions.WirePrefix + "/invocations",
            body,
            MobileToolControlOptions.InvocationTimeout,
            MobileToolControlWire.ParseInvocation,
            cancellationToken);
    }

    public Task<MobileToolInvocation> GetInvocationAsync(string invocationId, CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Get,
            MobileToolControlOptions.WirePrefix + "/invocations/" + Uri.EscapeDataString(invocationId),
            null,
            MobileToolControlOptions.CatalogTimeout,
            MobileToolControlWire.ParseInvocation,
            cancellationToken);

    /// <summary>
    /// Asks the computer to cancel. The answer is the gateway's own document: <c>cancelAccepted</c>
    /// (or its <c>accepted</c> spelling) is reported exactly as received, a refused cancel keeps
    /// <c>false</c>, and an already finished invocation keeps its terminal state. A 2xx status alone
    /// never means the call was cancelled.
    /// </summary>
    public Task<MobileToolInvocation> CancelInvocationAsync(
        string invocationId,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            MobileToolControlOptions.WirePrefix + "/invocations/" + Uri.EscapeDataString(invocationId) + "/cancel",
            null,
            MobileToolControlOptions.InvocationTimeout,
            MobileToolControlWire.ParseInvocation,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        JsonObject? body,
        TimeSpan timeout,
        Func<JsonObject, T> projector,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _endpoint.Origin + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", "MyPowerTools-MobileToolControl/1.0");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!timeoutSource.IsCancellationRequested)
            {
                // The module is stopping/being disabled: propagate so the command reports a
                // cancellation instead of a made-up timeout.
                throw;
            }

            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Timeout,
                "电脑没有在预期时间内响应，请确认它在 Tailnet 上在线。",
                retryable: true);
        }
        catch (HttpRequestException exception)
        {
            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Unreachable,
                $"无法连接电脑：{exception.Message}",
                retryable: true);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                // Never follow: the contract forbids redirects, and a followed redirect could replay
                // the bearer token to whatever host the response names.
                throw new MobileToolControlException(
                    MobileToolControlErrorCodes.RedirectRefused,
                    "电脑返回了重定向，已拒绝跟随。请检查连接码里的地址是否指向网关本身。",
                    statusCode: status);
            }

            var text = await ReadLimitedAsync(response, timeoutSource.Token).ConfigureAwait(false);
            var document = ParseObject(text, status);

            if (status is >= 200 and < 300)
            {
                return projector(document);
            }

            var (serverCode, serverMessage) = MobileToolControlWire.ParseError(document);
            throw Failure(status, serverCode, serverMessage);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MobileToolControlOptions.MaxResponseBytes)
        {
            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Protocol,
                "电脑返回的内容超出允许的大小。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > MobileToolControlOptions.MaxResponseBytes)
            {
                throw new MobileToolControlException(
                    MobileToolControlErrorCodes.Protocol,
                    "电脑返回的内容超出允许的大小。");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonObject ParseObject(string text, int status)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Protocol,
                "电脑返回的内容不是合同约定的 JSON。",
                statusCode: status);
        }
    }

    private static MobileToolControlException Failure(int status, string serverCode, string serverMessage) => status switch
    {
        401 or 403 => new MobileToolControlException(
            MobileToolControlErrorCodes.Unauthorized,
            Fallback(serverMessage, "授权已被电脑拒绝或撤销，请重新导入连接码。"),
            serverCode,
            status),
        404 => new MobileToolControlException(
            MobileToolControlErrorCodes.NotFound,
            Fallback(serverMessage, "电脑上没有找到这个命令或调用。"),
            serverCode,
            status),
        408 => new MobileToolControlException(
            MobileToolControlErrorCodes.Timeout,
            Fallback(serverMessage, "电脑处理超时。"),
            serverCode,
            status,
            retryable: true),
        // Every other normal client-side refusal (400/405/409/413/422/429 ...) is the computer
        // rejecting this request; it is never reported as a protocol or transport failure.
        >= 400 and < 500 => new MobileToolControlException(
            MobileToolControlErrorCodes.Rejected,
            Fallback(serverMessage, status == 413
                ? "调用内容过大，电脑拒绝了这次请求。"
                : "电脑拒绝了这次调用。"),
            serverCode,
            status),
        >= 500 => new MobileToolControlException(
            MobileToolControlErrorCodes.Unavailable,
            Fallback(serverMessage, "电脑端网关暂时不可用。"),
            serverCode,
            status,
            retryable: true),
        _ => new MobileToolControlException(
            MobileToolControlErrorCodes.Protocol,
            Fallback(serverMessage, $"电脑返回了未预期的状态 {status}。"),
            serverCode,
            status)
    };

    private static string Fallback(string message, string fallback) =>
        string.IsNullOrWhiteSpace(message) ? fallback : message;
}
