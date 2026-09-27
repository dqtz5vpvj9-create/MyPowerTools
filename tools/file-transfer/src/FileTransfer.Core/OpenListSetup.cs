using System.Net.Http.Json;
using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// Creates a dedicated WebDAV account scoped to the user's selected mounted directory. An account is
/// reused or updated only when the identity saved on this device matches the remote account exactly;
/// an unknown identity always creates a new random account instead of claiming an existing one.
/// </summary>
public static class OpenListSetup
{
    public const string ManagedPrefix = "mpt-";
    // Upload/create-directory plus WebDAV read/write; no admin, delete, move, or rename rights.
    private const int Permission = (1 << 3) | (1 << 8) | (1 << 9);

    public sealed record RelayAccount(int Id, string Username);

    public static async Task<RelayAccount> ConnectAsync(Uri server, string adminPassword, Uri directory,
        string relayPassword, RelayAccount? existing, CancellationToken token)
    {
        OpenListClient.ValidateUrl(server);
        OpenListClient.ValidateUrl(directory);
        if (!string.Equals(server.GetLeftPart(UriPartial.Authority), directory.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"请填写本机 OpenList 的目录地址：{server.GetLeftPart(UriPartial.Authority)}/dav/网盘/互传目录。");
        var basePath = BasePath(directory);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = adminPassword }, token);
        var auth = await ReadAsync(login, token);
        http.DefaultRequestHeaders.Add("Authorization", auth.GetProperty("token").GetString());
        var account = MatchSavedAccount(await ListAsync(http, token), existing);
        JsonElement? created = null;
        var username = account?.Username ?? ManagedPrefix + Guid.NewGuid().ToString("N")[..12];
        if (account is not null) await UpdateAsync(http, account.Id, account.Username, basePath, relayPassword, token);
        else created = await CreateAsync(http, username, basePath, relayPassword, token);
        var id = account?.Id ?? CreatedId(created);
        if (id <= 0) id = (await ListAsync(http, token)).FirstOrDefault(item => item.Username == username)?.Id ?? 0;
        if (id <= 0) throw new IOException("已配置 OpenList 专用账号，但未能确认账号编号，请重试。");
        return new RelayAccount(id, username);
    }

    /// <summary>
    /// Reuses an account only from the identity this device saved: the id and username must both match
    /// the remote user, or the saved username alone must match exactly. Accounts are never claimed by
    /// name prefix or by position, because another MPT device may own them.
    /// </summary>
    public static RelayAccount? MatchSavedAccount(IReadOnlyList<RelayAccount> users, RelayAccount? existing)
    {
        if (existing is null || string.IsNullOrEmpty(existing.Username)) return null;
        var byIdentity = users.FirstOrDefault(item => item.Id == existing.Id && item.Username == existing.Username);
        if (byIdentity is not null) return byIdentity;
        // The saved username is still explicit proof when the server renumbered the account.
        return users.FirstOrDefault(item => item.Username == existing.Username);
    }

    private static int CreatedId(JsonElement? created) =>
        created is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("id", out var id) && id.TryGetInt32(out var number)
            ? number : 0;

    private static string BasePath(Uri directory)
    {
        if (!directory.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal) || directory.AbsolutePath.TrimEnd('/') == "/dav")
            throw new ArgumentException("请先在网盘管理中挂载网盘，再填写这台 OpenList 上的 /dav/网盘/互传目录。");
        var path = Uri.UnescapeDataString(directory.AbsolutePath[4..]).Trim('/');
        if (path.Length == 0 || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("互传目录请填写已挂载网盘中的具体文件夹。");
        return path;
    }

    private static async Task<IReadOnlyList<RelayAccount>> ListAsync(HttpClient http, CancellationToken token)
    {
        using var list = await http.GetAsync("/api/admin/user/list?page=1&per_page=0", token);
        var data = await ReadAsync(list, token);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array) return [];
        var users = new List<RelayAccount>();
        foreach (var user in content.EnumerateArray())
        {
            if (user.ValueKind != JsonValueKind.Object) continue;
            var name = user.TryGetProperty("username", out var username) ? username.GetString() : null;
            var id = user.TryGetProperty("id", out var number) && number.TryGetInt32(out var value) ? value : 0;
            if (id > 0 && !string.IsNullOrEmpty(name)) users.Add(new(id, name));
        }
        return users;
    }

    private static async Task<JsonElement?> CreateAsync(HttpClient http, string username, string basePath,
        string relayPassword, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("/api/admin/user/create", new
        {
            username, password = relayPassword, base_path = basePath, role = 0, permission = Permission, disabled = false
        }, token);
        var data = await ReadAsync(response, token);
        return data.ValueKind == JsonValueKind.Object ? data : null;
    }

    private static async Task UpdateAsync(HttpClient http, int id, string username, string basePath,
        string relayPassword, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("/api/admin/user/update", new
        {
            id, username, password = relayPassword, base_path = basePath, role = 0, permission = Permission, disabled = false
        }, token);
        await ReadAsync(response, token);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        if (body.GetProperty("code").GetInt32() != 200) throw new IOException("OpenList 配置失败：" + body.GetProperty("message").GetString());
        return body.TryGetProperty("data", out var data) ? data : default;
    }
}
