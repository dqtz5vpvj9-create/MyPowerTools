using System.Net.Http.Json;
using System.Text.Json;
using FileTransfer.Core;

namespace FileTransfer.Tests;

// Explicit opt-in to an official binary; no network download or daemon during ordinary unit tests.
public sealed class OpenListIntegrationTests
{
    [OfficialOpenListFact]
    public async Task OfficialServerInitializesUploadsListsDownloadsAndStops()
    {
        var binary = Environment.GetEnvironmentVariable("MPT_OPENLIST_TEST_BINARY");
        Assert.False(string.IsNullOrEmpty(binary));
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-openlist-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "server", OpenListRuntime.Version));
        File.Copy(binary, Path.Combine(root, "server", OpenListRuntime.Version, Path.GetFileName(binary)));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(root, "server", OpenListRuntime.Version, "openlist"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await using var server = new OpenListRuntime(Path.Combine(root, "server"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var password = await server.InitializeAdminAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(password));
            await server.StartAsync("127.0.0.1", timeout.Token);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{OpenListRuntime.Port}") };
            using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password }, timeout.Token);
            var auth = await login.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.Equal(200, auth.GetProperty("code").GetInt32());
            http.DefaultRequestHeaders.Add("Authorization", auth.GetProperty("data").GetProperty("token").GetString());
            var storage = Path.Combine(root, "storage");
            Directory.CreateDirectory(storage);
            using var mount = await http.PostAsJsonAsync("/api/admin/storage/create", new
            {
                mount_path = "/mpt", driver = "Local", order = 0, cache_expiration = 0,
                addition = JsonSerializer.Serialize(new { root_folder_path = storage, show_hidden = true, mkdir_perm = "700" }),
                webdav_policy = "native_proxy", disabled = false
            }, timeout.Token);
            var mounted = await mount.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            Assert.True(mounted.GetProperty("code").GetInt32() == 200, mounted.GetProperty("message").GetString());
            var serverUrl = new Uri($"http://127.0.0.1:{OpenListRuntime.Port}");
            var account = await OpenListSetup.ConnectAsync(serverUrl, password!, new Uri(serverUrl, "/dav/mpt"), password!, null, timeout.Token);
            Assert.Equal(account, await OpenListSetup.ConnectAsync(serverUrl, password!, new Uri(serverUrl, "/dav/mpt"), password!, account, timeout.Token));
            // A dedicated account owned by another MPT device must survive this device's setup untouched.
            using (var other = await http.PostAsJsonAsync("/api/admin/user/create", new
            {
                username = "mpt-other-device", password = "other-device-password", base_path = "other", role = 0,
                permission = (1 << 3) | (1 << 8) | (1 << 9), disabled = false
            }, timeout.Token))
            {
                Assert.Equal(200, (await other.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("code").GetInt32());
            }
            // With no saved identity the tool creates a new random account instead of claiming an existing one.
            var fresh = await OpenListSetup.ConnectAsync(serverUrl, password!, new Uri(serverUrl, "/dav/mpt"), password!, null, timeout.Token);
            Assert.NotEqual(account.Username, fresh.Username);
            Assert.NotEqual("mpt-other-device", fresh.Username);
            using (var otherLogin = await http.PostAsJsonAsync("/api/auth/login", new { username = "mpt-other-device", password = "other-device-password" }, timeout.Token))
            {
                Assert.Equal(200, (await otherLogin.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("code").GetInt32());
            }
            using var cloud = new OpenListClient(new Uri(serverUrl, "/dav").ToString(), account.Username, password!);
            var path = Path.Combine(root, "网盘 round trip.txt");
            await File.WriteAllTextAsync(path, "文件互传 integration\n", timeout.Token);
            var item = await cloud.UploadAsync(path, "phone", "desktop", null, timeout.Token);
            // An incomplete transfer folder must not become a downloadable inbox entry.
            Directory.CreateDirectory(Path.Combine(storage, "phone", Guid.NewGuid().ToString("N")));
            var items = await cloud.ListAsync("phone", timeout.Token);
            Assert.Single(items);
            Assert.Equal(item, items[0]);
            var saved = await cloud.DownloadAsync("phone", items[0], Path.Combine(root, "received"), null, timeout.Token);
            Assert.Equal(await File.ReadAllBytesAsync(path, timeout.Token), await File.ReadAllBytesAsync(saved, timeout.Token));
            Assert.Null(await server.InitializeAdminAsync(timeout.Token));
            await server.StopAsync();
            Assert.False(server.Running);
        }
        finally { Directory.Delete(root, true); }
    }
}

internal sealed class OfficialOpenListFactAttribute : FactAttribute
{
    public OfficialOpenListFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MPT_OPENLIST_TEST_BINARY")))
            Skip = "Set MPT_OPENLIST_TEST_BINARY to an official OpenList executable for the integration test.";
    }
}
