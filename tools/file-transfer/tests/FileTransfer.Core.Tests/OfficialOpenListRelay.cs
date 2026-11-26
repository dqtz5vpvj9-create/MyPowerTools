using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FileTransfer.Core;

namespace FileTransfer.Tests;

/// <summary>
/// One official OpenList process owned by a single test, backed by the Local storage driver.
///
/// The replay of the official binary is read from <c>MPT_OPENLIST_TEST_BINARY</c> and copied into this
/// instance's own random temp root; nothing is downloaded and no other fixture is touched. The runtime port
/// is fixed (<see cref="OpenListRuntime.Port"/>), so a busy port is reported instead of killing whatever
/// holds it.
/// </summary>
internal sealed class OfficialOpenListRelay : IAsyncDisposable
{
    private OfficialOpenListRelay(OpenListRuntime runtime, Uri server, string adminPassword, string storagePath, OpenListSetup.RelayAccount account)
    {
        Runtime = runtime;
        ServerUrl = server;
        AdminPassword = adminPassword;
        StoragePath = storagePath;
        Account = account;
    }

    public OpenListRuntime Runtime { get; }
    public Uri ServerUrl { get; }
    public string AdminPassword { get; }
    /// <summary>Directory the Local driver serves as the mounted WebDAV root.</summary>
    public string StoragePath { get; }
    public OpenListSetup.RelayAccount Account { get; }
    /// <summary>WebDAV root the dedicated relay account sees.</summary>
    public string DavUrl => new Uri(ServerUrl, "/dav").ToString();

    /// <summary>Maps a path inside the mounted directory to the file the Local driver actually wrote.</summary>
    public string DiskPath(string relative) =>
        Path.Combine(StoragePath, relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar));

    public static async Task<OfficialOpenListRelay> StartAsync(string root, CancellationToken token)
    {
        var binary = Environment.GetEnvironmentVariable("MPT_OPENLIST_TEST_BINARY");
        if (string.IsNullOrEmpty(binary) || !File.Exists(binary))
            throw new InvalidOperationException("请把 MPT_OPENLIST_TEST_BINARY 指向官方 OpenList 可执行文件。");
        RequireFreePort();

        var serverRoot = Path.Combine(root, "server");
        var executable = Path.Combine(serverRoot, OpenListRuntime.Version, Path.GetFileName(binary));
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.Copy(binary, executable, true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var runtime = new OpenListRuntime(serverRoot);
        try
        {
            var adminPassword = await runtime.InitializeAdminAsync(token)
                ?? throw new IOException("官方 OpenList 未返回管理员口令。");
            await runtime.StartAsync("127.0.0.1", token);
            var server = new Uri($"http://127.0.0.1:{OpenListRuntime.Port}");
            var storagePath = Path.Combine(root, "storage");
            Directory.CreateDirectory(storagePath);
            using (var http = await CreateAdminClientAsync(server, adminPassword, token))
            {
                using var mount = await http.PostAsJsonAsync("/api/admin/storage/create", new
                {
                    mount_path = "/mpt",
                    driver = "Local",
                    order = 0,
                    cache_expiration = 0,
                    addition = JsonSerializer.Serialize(new { root_folder_path = storagePath, show_hidden = true, mkdir_perm = "700" }),
                    webdav_policy = "native_proxy",
                    disabled = false
                }, token);
                var mounted = await mount.Content.ReadFromJsonAsync<JsonElement>(token);
                if (mounted.GetProperty("code").GetInt32() != 200)
                    throw new IOException("官方 OpenList 挂载失败：" + mounted.GetProperty("message").GetString());
            }
            var account = await OpenListSetup.ConnectAsync(server, adminPassword, new Uri(server, "/dav/mpt"),
                adminPassword, null, token);
            return new OfficialOpenListRelay(runtime, server, adminPassword, storagePath, account);
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }
    }

    /// <summary>An admin API client with the bearer token already attached.</summary>
    public static async Task<HttpClient> CreateAdminClientAsync(Uri server, string adminPassword, CancellationToken token)
    {
        var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = adminPassword }, token);
            var body = await login.Content.ReadAsStringAsync(token);
            JsonElement auth;
            try { auth = JsonSerializer.Deserialize<JsonElement>(body); }
            catch (JsonException ex)
            {
                throw new IOException($"官方 OpenList 管理员登录返回了非 JSON 响应：HTTP {(int)login.StatusCode} {Excerpt(body)}", ex);
            }
            if (auth.ValueKind != JsonValueKind.Object || !auth.TryGetProperty("code", out var code) || code.GetInt32() != 200 ||
                !auth.TryGetProperty("data", out var data) || !data.TryGetProperty("token", out var bearer))
                throw new IOException($"官方 OpenList 管理员登录失败：HTTP {(int)login.StatusCode} {Excerpt(body)}");
            http.DefaultRequestHeaders.Add("Authorization", bearer.GetString());
            return http;
        }
        catch { http.Dispose(); throw; }
    }

    private static string Excerpt(string body) => body.Length <= 300 ? body : body[..300];

    public async ValueTask DisposeAsync() => await Runtime.DisposeAsync();

    /// <summary>
    /// Reports a busy runtime port instead of touching other processes: the port belongs to
    /// <see cref="OpenListRuntime.Port"/> and another fixture may legitimately be using it right now.
    /// </summary>
    private static void RequireFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, OpenListRuntime.Port);
        try
        {
            probe.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"端口 {OpenListRuntime.Port} 已被占用：可能有另一个 OpenList fixture 正在运行。" +
                "本测试不会结束别人的进程，请等它结束后重跑。", ex);
        }
        finally
        {
            probe.Stop();
        }
    }
}
