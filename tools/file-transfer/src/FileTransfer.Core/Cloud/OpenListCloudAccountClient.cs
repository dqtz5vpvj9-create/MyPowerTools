using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FileTransfer.Core.Cloud;

/// <summary>Local embedded runtime administration. Never returns server error bodies containing credentials.</summary>
public sealed class OpenListCloudAccountClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _password;
    private readonly TimeSpan _controlTimeout;
    public OpenListCloudAccountClient(Uri origin, string password) : this(origin, password, TimeSpan.FromMinutes(2)) { }
    internal OpenListCloudAccountClient(Uri origin, string password, TimeSpan controlTimeout)
    {
        if (!origin.IsLoopback) throw new ArgumentException("账号管理只允许本机嵌入服务。");
        _password = password;
        _controlTimeout = controlTimeout;
        // An upload can remain healthy for much longer than an administration request.
        // The transfer owner's cancellation token controls the payload lifetime.
        _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(30) })
            { BaseAddress = origin, Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task LoginAsync(CancellationToken token)
    {
        var auth = await ApiAsync("/api/auth/login", new { username = "admin", password = _password }, token);
        _http.DefaultRequestHeaders.Add("Authorization", auth.GetProperty("token").GetString());
    }
    public static object Addition(string provider, string credential) => provider switch
    {
        "quark" => new { cookie = credential, root_folder_id = "0", order_by = "none", order_direction = "asc" },
        "baidu" => new { refresh_token = credential, root_folder_path = "/", use_online_api = true,
            api_url_address = "https://api.oplist.org/baiduyun/renewapi", download_api = "official", custom_crack_ua = "netdisk",
            upload_thread = "3", upload_api = "https://d.pcs.baidu.com", use_dynamic_upload_api = true },
        _ => throw new ArgumentException("不支持该网盘。")
    };
    public async Task<int> MountAsync(string provider, string credential, string mount, CancellationToken token)
    {
        var data = await ApiAsync("/api/admin/storage/create", new { mount_path = mount, driver = provider == "quark" ? "Quark" : "BaiduNetdisk",
            addition = JsonSerializer.Serialize(Addition(provider, credential)), order = 0, cache_expiration = 30, web_proxy = true, webdav_policy = "native_proxy", disabled = false }, token);
        return data.GetProperty("id").GetInt32();
    }
    public async Task UnmountAsync(int storageId, CancellationToken token) =>
        await ApiAsync("/api/admin/storage/delete?id=" + storageId, null, token);
    public async Task SetPausedAsync(int storageId, bool paused, CancellationToken token) =>
        await ApiAsync($"/api/admin/storage/{(paused ? "disable" : "enable")}?id={storageId}", null, token);
    public async Task<object[]> FoldersAsync(string mount, string parent, CancellationToken token)
    {
        ValidatePath(mount, parent);
        var data = await ApiAsync("/api/fs/list", new { path = parent, password = "", page = 1, per_page = 0, refresh = true }, token);
        if (!data.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return [];
        return content.EnumerateArray().Where(x => x.GetProperty("is_dir").GetBoolean()).Select(x =>
        {
            var name = x.GetProperty("name").GetString()!;
            return (object)new CloudFolder(parent.TrimEnd('/') + "/" + name, name, parent);
        }).ToArray();
    }
    public async Task PrepareAsync(string mount, string folder, CancellationToken token)
    {
        ValidatePath(mount, folder);
        var parent = folder[..folder.LastIndexOf('/')];
        var existing = await FoldersAsync(mount, parent, token);
        if (!existing.Cast<CloudFolder>().Any(f => f.Id == folder)) await ApiAsync("/api/fs/mkdir", new { path = folder }, token);
        // Probe only an unguessable new object owned by this operation; never delete an existing user's file.
        var name = ".mpt-check-" + Guid.NewGuid().ToString("N");
        var path = folder + "/" + name;
        var content = Encoding.UTF8.GetBytes("MPT read/write check " + Guid.NewGuid().ToString("N"));
        await UploadAsync(mount, path, new MemoryStream(content), content.Length, token);
        try
        {
            using var read = await OpenReadAsync(mount, path, token);
            if (!(await read.Content.ReadAsByteArrayAsync(token)).SequenceEqual(content))
                throw new IOException("网盘目录读写确认失败，请重试。");
        }
        finally
        {
            await ApiAsync("/api/fs/remove", new { dir = folder, names = new[] { name } }, CancellationToken.None);
        }
    }
    public async Task UploadAsync(string mount, string path, Stream content, long length, CancellationToken token)
    {
        ValidatePath(mount, path);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/fs/put") { Content = new UploadContent(content, length) };
        request.Content.Headers.ContentLength = length;
        request.Headers.Add("File-Path", Uri.EscapeDataString(path));
        request.Headers.Add("As-Task", "false");
        request.Headers.Add("Overwrite", "false");
        using var response = await _http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new IOException($"网盘目录无法写入（HTTP {(int)response.StatusCode}），请检查登录与空间。");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (result.RootElement.GetProperty("code").GetInt32() != 200) throw new IOException("网盘上传失败，请检查账号权限与剩余空间。");
    }
    public async Task<HttpResponseMessage> OpenReadAsync(string mount, string path, CancellationToken token)
    {
        ValidatePath(mount, path);
        var info = await ApiAsync("/api/fs/get", new { path, password = "" }, token);
        var raw = new Uri(_http.BaseAddress!, info.GetProperty("raw_url").GetString()!);
        // This mount is configured for local proxy. Never forward the administrator token to a provider/CDN.
        if (raw.GetLeftPart(UriPartial.Authority) != _http.BaseAddress!.GetLeftPart(UriPartial.Authority) || !raw.AbsolutePath.StartsWith("/p/", StringComparison.Ordinal))
            throw new IOException("网盘没有返回本机代理读取入口，请重试。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(_controlTimeout);
        var response = await _http.GetAsync(raw, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (!response.IsSuccessStatusCode) { var status = (int)response.StatusCode; response.Dispose(); throw new IOException($"网盘读取失败（HTTP {status}）。"); }
        return response;
    }
    public static void ValidatePath(string mount, string path)
    {
        if (!(path == mount || path.StartsWith(mount + "/", StringComparison.Ordinal)) || path.Split('/').Any(p => p is "." or "..") || path.Contains('\\') || path.Any(char.IsControl))
            throw new ArgumentException("目录必须位于此账号中。");
    }
    private async Task<JsonElement> ApiAsync(string path, object? body, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(_controlTimeout);
        token = budget.Token;
        using var response = body is null ? await _http.PostAsync(path, null, token) : await _http.PostAsJsonAsync(path, body, token);
        if (!response.IsSuccessStatusCode) throw new IOException("本机网盘服务请求失败，请重试。");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        if (json.RootElement.GetProperty("code").GetInt32() != 200)
        {
            if (path == "/api/admin/storage/create" && json.RootElement.TryGetProperty("data", out var failureData)
                && failureData.ValueKind == JsonValueKind.Object && failureData.TryGetProperty("id", out var id)
                && id.TryGetInt32(out var createdId) && createdId > 0)
                await UnmountAsync(createdId, CancellationToken.None);
            var message = json.RootElement.TryGetProperty("message", out var error) ? error.GetString() ?? "" : "";
            var category = message.Contains("login", StringComparison.OrdinalIgnoreCase) || message.Contains("登录") ? "登录已失效"
                : message.Contains("space", StringComparison.OrdinalIgnoreCase) || message.Contains("空间") ? "空间或配额限制"
                : message.Contains("forbidden", StringComparison.OrdinalIgnoreCase) || message.Contains("permission", StringComparison.OrdinalIgnoreCase) ? "权限不足"
                : "提供方操作失败";
            throw new IOException($"{category}（{path.Split('?')[0]}，code={json.RootElement.GetProperty("code").GetInt32()}），请重新登录并检查空间。");
        }
        return json.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default;
    }
    public void Dispose() => _http.Dispose();

    private sealed class UploadContent(Stream input, long size) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = size; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) =>
            TransferFiles.CopyAsync(input, stream, size, null, token);
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
public sealed record CloudFolder(string Id, string Name, string? ParentId);
