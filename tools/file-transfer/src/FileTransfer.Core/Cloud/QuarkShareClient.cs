using System.Net.Http.Json;
using System.Text.Json;

namespace FileTransfer.Core.Cloud;

/// <summary>A capability for one shared file, never an account credential or a signed CDN URL.</summary>
public sealed record QuarkShareDescriptor(string ShareId, string FileId, string? Passcode = null);

/// <summary>Quark's first-party share-page protocol. Receiving requires no owner's Cookie.</summary>
public sealed class QuarkShareClient : IDisposable
{
    private static readonly Uri OwnerOrigin = new("https://drive-pc.quark.cn/1/clouddrive/");
    private static readonly Uri ShareOrigin = new("https://drive.quark.cn/1/clouddrive/");
    private readonly HttpClient _http;
    private readonly HttpClient _anonymousHttp;
    public QuarkShareClient() : this(
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
        new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = true }) { }
    internal QuarkShareClient(HttpMessageHandler handler) : this(handler, handler) { }
    private QuarkShareClient(HttpMessageHandler ownerHandler, HttpMessageHandler anonymousHandler)
    {
        _http = new(ownerHandler) { Timeout = TimeSpan.FromMinutes(2) };
        // The provider issues anonymous visitor cookies for its CDN. This jar must never
        // see owner requests or owner Set-Cookie responses, even if this instance is reused.
        _anonymousHttp = new(anonymousHandler) { Timeout = TimeSpan.FromMinutes(2) };
        foreach (var client in new[] { _http, _anonymousHttp })
        {
            client.DefaultRequestHeaders.Referrer = new Uri("https://pan.quark.cn/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        }
    }

    public async Task<QuarkShareDescriptor> CreateAsync(string cookie, string providerPath, DateTimeOffset expiresAt, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(cookie)) throw new ArgumentException("请先登录夸克网盘。");
        if (expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddDays(7))
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "附件分享有效期应在未来七天内。");
        var parts = providerPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains('\\'))) throw new ArgumentException("网盘文件路径无效。");
        var parent = "0";
        foreach (var name in parts)
        {
            string? found = null;
            for (var page = 1; found is null; page++)
            {
                var data = await ApiAsync(OwnerOrigin, "file/sort", HttpMethod.Get, null, cookie, token,
                    $"&pdir_fid={Uri.EscapeDataString(parent)}&_page={page}&_size=100");
                var entries = data.GetProperty("list");
                foreach (var file in entries.EnumerateArray())
                    if (file.GetProperty("file_name").GetString() == name) { found = file.GetProperty("fid").GetString(); break; }
                if (entries.GetArrayLength() < 100) break;
            }
            parent = found ?? throw new IOException("网盘中尚未找到已上传的附件，请重试。");
        }
        var created = await ApiAsync(OwnerOrigin, "share", HttpMethod.Post,
            // Quark ignores arbitrary expired_at: type 2 is one day, type 3 is seven days.
            // The conversation locator may expire earlier, but never outlive the provider share.
            new { fid_list = new[] { parent }, title = parts[^1], url_type = 1, expired_type = 3 }, cookie, token);
        var task = created.GetProperty("task_id").GetString()!;
        string? ownerShareId = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var result = await ApiAsync(OwnerOrigin, "task", HttpMethod.Get, null, cookie, token,
                "&task_id=" + Uri.EscapeDataString(task) + "&retry_index=" + attempt);
            if (result.GetProperty("status").GetInt32() == 2) { ownerShareId = result.GetProperty("share_id").GetString(); break; }
            if (result.GetProperty("status").GetInt32() == 3) throw new IOException("夸克网盘创建附件分享失败，请重试。");
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        if (ownerShareId is null) throw new IOException("夸克网盘仍在创建附件分享，请重试。");
        var share = await ApiAsync(OwnerOrigin, "share/password", HttpMethod.Post, new { share_id = ownerShareId }, cookie, token);
        return new(share.GetProperty("pwd_id").GetString()!, parent,
            share.TryGetProperty("passcode", out var passcode) ? passcode.GetString() : null);
    }

    public async Task<HttpResponseMessage> OpenReadAsync(QuarkShareDescriptor share, CancellationToken token)
    {
        var auth = await ApiAsync(ShareOrigin, "share/sharepage/token", HttpMethod.Post,
            new { pwd_id = share.ShareId, passcode = share.Passcode ?? "", support_visit_limit_private_share = true }, null, token);
        var stoken = auth.GetProperty("stoken").GetString()!;
        var listing = await ApiAsync(ShareOrigin, "share/sharepage/detail", HttpMethod.Get, null, null, token,
            "&pwd_id=" + Uri.EscapeDataString(share.ShareId) + "&stoken=" + Uri.EscapeDataString(stoken) + "&pdir_fid=0&_page=1&_size=100");
        var file = listing.GetProperty("list").EnumerateArray().FirstOrDefault(f => f.GetProperty("fid").GetString() == share.FileId);
        if (file.ValueKind == JsonValueKind.Undefined) throw new IOException("附件分享中已找不到这个文件，请让发送方重新发送。");
        var download = await ApiAsync(ShareOrigin, "file/share/download", HttpMethod.Post,
            new { fids = new[] { share.FileId }, pwd_id = share.ShareId, stoken,
                fids_token = new[] { file.GetProperty("share_fid_token").GetString()! } }, null, token);
        var url = new Uri(download[0].GetProperty("download_url").GetString()!);
        if (url.Scheme != Uri.UriSchemeHttps || url.IsLoopback) throw new IOException("网盘未返回有效的附件下载入口。");
        var response = await _anonymousHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode; response.Dispose();
            throw new IOException($"网盘分享下载暂不可用（HTTP {status}），请重试。");
        }
        return response;
    }

    private async Task<JsonElement> ApiAsync(Uri origin, string path, HttpMethod method, object? body,
        string? cookie, CancellationToken token, string query = "")
    {
        using var request = new HttpRequestMessage(method, new Uri(origin, path + "?pr=ucpro&fr=pc" + query));
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await (cookie is null ? _anonymousHttp : _http).SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException($"夸克网盘分享请求失败（HTTP {(int)response.StatusCode}）。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (root.GetProperty("status").GetInt32() != 200 || root.GetProperty("code").GetInt32() != 0)
            throw new IOException($"夸克网盘分享不可用（code={root.GetProperty("code").GetInt32()}），请检查分享是否过期或被撤销。");
        return root.GetProperty("data").Clone();
    }
    public void Dispose() { _http.Dispose(); _anonymousHttp.Dispose(); }
}
