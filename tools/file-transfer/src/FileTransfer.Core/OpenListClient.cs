using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace FileTransfer.Core;

public sealed record CloudFile(int Version, string Id, string Name, long Size, string Sender, DateTimeOffset CreatedAt);

public sealed class OpenListClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly AuthenticationHeaderValue _authorization;

    public OpenListClient(string webDavDirectory, string username, string password)
    {
        _root = new Uri(webDavDirectory.TrimEnd('/') + "/");
        ValidateUrl(_root);
        _authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        // No automatic redirects: a storage redirect must never receive the OpenList password.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static void ValidateUrl(Uri url)
    {
        if (!string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new ArgumentException("WebDAV 地址不能包含账号、查询参数或片段。");
        if (url.Scheme == "https") return;
        if (url.Scheme == "http" && IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip) &&
            (IPAddress.IsLoopback(ip) || TransferFiles.IsTailAddress(ip))) return;
        throw new ArgumentException("OpenList 请使用 HTTPS，或 Tailscale IP 上的 HTTP 地址。");
    }

    private Uri Url(params string[] segments) => new(_root, string.Join('/', segments.Select(Uri.EscapeDataString)));

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, Uri uri, HttpContent? content, CancellationToken token,
        string? depth = null)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = _authorization;
        if (depth is not null) request.Headers.Add("Depth", depth);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }

    private static void Check(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new IOException($"OpenList 返回 HTTP {(int)response.StatusCode}。请检查地址、网盘挂载和账号权限。");
    }

    public async Task CheckAsync(CancellationToken token)
    {
        using var response = await RequestAsync(new HttpMethod("PROPFIND"), _root, null, token, "0");
        Check(response);
    }

    private async Task MkcolAsync(Uri uri, CancellationToken token)
    {
        using var response = await RequestAsync(new HttpMethod("MKCOL"), uri, null, token);
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed) Check(response);
    }

    public async Task<CloudFile> UploadAsync(string path, string recipient, string sender,
        Action<long, long>? progress, CancellationToken token)
    {
        TransferFiles.DeviceId(recipient);
        var name = TransferFiles.FileName(Path.GetFileName(path));
        await CheckAsync(token); // The selected mount directory must already exist.
        await MkcolAsync(Url(recipient), token);
        var id = Guid.NewGuid().ToString("N");
        await MkcolAsync(Url(recipient, id), token);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var item = new CloudFile(1, id, name, input.Length, sender, DateTimeOffset.UtcNow);
        using (var response = await RequestAsync(HttpMethod.Put, Url(recipient, id, "payload"),
            new UploadContent(input, input.Length, progress), token)) Check(response);
        // Publish only after the storage driver completed the payload. Incomplete uploads have no ready.json.
        using (var response = await RequestAsync(HttpMethod.Put, Url(recipient, id, "ready.json"),
            new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(item, DirectTransfer.Json)), token)) Check(response);
        return item;
    }

    public async Task<IReadOnlyList<CloudFile>> ListAsync(string recipient, CancellationToken token)
    {
        TransferFiles.DeviceId(recipient);
        using var response = await RequestAsync(new HttpMethod("PROPFIND"), Url(recipient) , null, token, "1");
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        Check(response);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
        XNamespace dav = "DAV:";
        var result = new List<CloudFile>();
        foreach (var row in xml.Descendants(dav + "response"))
        {
            var href = row.Element(dav + "href")?.Value;
            if (href is null) continue;
            var id = Uri.UnescapeDataString(new Uri(_root, href).AbsolutePath.TrimEnd('/').Split('/').Last());
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            using var initial = await RequestAsync(HttpMethod.Get, Url(recipient, id, "ready.json"), null, token);
            if (initial.StatusCode == HttpStatusCode.NotFound) continue;
            using var redirected = await FollowDownloadAsync(initial, token);
            var manifest = redirected ?? initial;
            Check(manifest);
            var item = await manifest.Content.ReadFromJsonAsync<CloudFile>(DirectTransfer.Json, token);
            if (item is null || item.Version != 1 || item.Id != id) throw new InvalidDataException("网盘收件记录无效。");
            ValidateManifest(item);
            result.Add(item);
        }
        return result.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<string> DownloadAsync(string recipient, CloudFile file, string directory,
        Action<long, long>? progress, CancellationToken token)
    {
        TransferFiles.DeviceId(recipient);
        ValidateManifest(file);
        if (!Guid.TryParseExact(file.Id, "N", out _)) throw new InvalidDataException("收件记录无效。");
        Directory.CreateDirectory(directory);
        var temporary = TransferFiles.PartialPath(directory);
        try
        {
            using var initial = await RequestAsync(HttpMethod.Get, Url(recipient, file.Id, "payload"), null, token);
            using var redirected = await FollowDownloadAsync(initial, token);
            var response = redirected ?? initial;
            Check(response);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await TransferFiles.CopyAsync(input, output, file.Size, progress, token);
                if (await input.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("网盘文件长度与收件记录不符。");
            }
            return TransferFiles.Commit(temporary, directory, file.Name);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage?> FollowDownloadAsync(HttpResponseMessage initial, CancellationToken token)
    {
        HttpResponseMessage current = initial;
        try
        {
            for (var i = 0; (int)current.StatusCode is 301 or 302 or 303 or 307 or 308; i++)
            {
                if (i == 5) throw new IOException("网盘下载重定向次数过多。");
                var next = new Uri(current.RequestMessage!.RequestUri!, current.Headers.Location ?? throw new IOException("缺少下载地址。"));
                // The relay may redirect to its own plain-HTTP Tailscale address (it is the same
                // server we validated above); any foreign host must be HTTPS.
                if (next.Scheme != "https" && !SameAuthority(next, _root)) throw new IOException("网盘下载重定向必须使用 HTTPS。");
                if (current != initial) current.Dispose();
                // A fresh request deliberately has no Authorization header.
                current = await _http.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, token);
            }
            return current == initial ? null : current;
        }
        catch { if (current != initial) current.Dispose(); throw; }
    }

    private static bool SameAuthority(Uri left, Uri right) =>
        left.Port == right.Port && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>An inbox manifest is written by another device; treat every field as untrusted input.</summary>
    private static void ValidateManifest(CloudFile item)
    {
        TransferFiles.FileName(item.Name);
        if (item.Size < 0) throw new InvalidDataException("网盘收件记录无效。");
        var sender = item.Sender ?? "";
        if (sender.Length > 100 || sender.Any(char.IsControl)) throw new InvalidDataException("网盘收件记录无效。");
    }

    public void Dispose() => _http.Dispose();

    private sealed class UploadContent(Stream input, long size, Action<long, long>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            TransferFiles.CopyAsync(input, stream, size, progress, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            TransferFiles.CopyAsync(input, stream, size, progress, token);
    }
}
