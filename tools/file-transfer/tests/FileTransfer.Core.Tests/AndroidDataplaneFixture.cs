using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;

/// <summary>
/// Opt-in Android dataplane fixture. It does not start or stop the server: tests/android-dataplane-fixture.sh
/// starts an isolated official OpenList on 127.0.0.1 and then runs this test, which
/// mounts a Local driver directory, creates the dedicated relay account through the production
/// OpenListSetup helper, seeds one inbound ready transfer for the phone to download, self-checks the
/// download path through the relay account, and writes the exact handover (URL, connection codes,
/// expected bytes) for the Android device. Skipped unless MPT_FIXTURE_ROOT + MPT_FIXTURE_ADMIN_PASSWORD are set.
/// </summary>
public sealed class AndroidDataplaneFixture
{
    [AndroidFixtureFact]
    public async Task PrepareFixtureAsync()
    {
        var root = Environment.GetEnvironmentVariable("MPT_FIXTURE_ROOT")!;
        var adminPassword = Environment.GetEnvironmentVariable("MPT_FIXTURE_ADMIN_PASSWORD")!;
        var port = int.Parse(Environment.GetEnvironmentVariable("MPT_FIXTURE_PORT") ?? "15244");
        var mount = Environment.GetEnvironmentVariable("MPT_FIXTURE_MOUNT") ?? "mpt";
        var recipient = Environment.GetEnvironmentVariable("MPT_FIXTURE_RECIPIENT") ?? "android";
        var seedName = Environment.GetEnvironmentVariable("MPT_FIXTURE_SEED_NAME") ?? "android-inbound.txt";
        var seedContent = Environment.GetEnvironmentVariable("MPT_FIXTURE_SEED_CONTENT") ?? "MPT android dataplane fixture inbound v1\n";
        const string peerId = "fixture-pc";
        const string peerName = "Fixture PC";
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var serverUrl = new Uri($"http://127.0.0.1:{port}");
        var directoryUrl = new Uri(serverUrl, $"/dav/{mount}").ToString();
        // The relay account is scoped to base_path <mount>, so its WebDAV root is /dav: OpenList maps
        // /dav to the account scope (asking for /dav/<mount> again would resolve <mount>/<mount> -> 404).
        var scopedUrl = new Uri(serverUrl, "/dav").ToString();
        var storageRoot = Path.Combine(root, "storage");
        Directory.CreateDirectory(storageRoot);
        await EnsureMountAsync(serverUrl, adminPassword, storageRoot, mount, token);

        // The relay identity is stable across re-runs so an already imported connection code stays valid.
        var accountPath = Path.Combine(root, "relay-account.json");
        var passwordPath = Path.Combine(root, "relay-password.txt");
        var relayPassword = File.Exists(passwordPath)
            ? (await File.ReadAllTextAsync(passwordPath, token)).Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var saved = File.Exists(accountPath)
            ? JsonSerializer.Deserialize<OpenListSetup.RelayAccount>(await File.ReadAllTextAsync(accountPath, token))
            : null;
        var account = await OpenListSetup.ConnectAsync(serverUrl, adminPassword, new Uri(directoryUrl), relayPassword, saved, token);
        await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(account), token);
        await File.WriteAllTextAsync(passwordPath, relayPassword, token);

        var pairTokenPath = Path.Combine(root, "pair-token.txt");
        var pairToken = File.Exists(pairTokenPath)
            ? (await File.ReadAllTextAsync(pairTokenPath, token)).Trim()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        await File.WriteAllTextAsync(pairTokenPath, pairToken, token);

        // One inbound ready transfer for the phone to list and download, written through the relay account.
        var seedPath = Path.Combine(root, "seed", seedName);
        Directory.CreateDirectory(Path.GetDirectoryName(seedPath)!);
        await File.WriteAllTextAsync(seedPath, seedContent, token);
        long seedBytes = Encoding.UTF8.GetByteCount(seedContent);
        using (var cloud = new OpenListClient(scopedUrl, account.Username, relayPassword))
        {
            await cloud.CheckAsync(token);
            if ((await cloud.ListAsync(recipient, token)).All(item => item.Name != seedName))
                await cloud.UploadAsync(seedPath, recipient, peerId, null, token);
            var seeded = (await cloud.ListAsync(recipient, token)).Single(item => item.Name == seedName);
            Assert.Equal(seedBytes, seeded.Size);
            var roundTrip = await cloud.DownloadAsync(recipient, seeded, Path.Combine(root, "verify"), null, token);
            Assert.Equal(seedContent, await File.ReadAllTextAsync(roundTrip, token));
        }

        var cloudCode = new CloudConnection(scopedUrl, account.Username, relayPassword).Encode();
        var pairCode = new Pairing(peerId, peerName, "100.64.0.1", pairToken).Encode();
        var adminFile = Path.Combine(root, "admin-password.txt");
        var adminUrl = $"{serverUrl}@manage";
        var reverse = $"adb -s <serial> reverse tcp:{port} tcp:{port}";
        var phonePull = Path.Combine(root, "verify", "phone-" + seedName);
        var phoneCompare = $"adb -s <serial> pull /sdcard/Download/MPT/{seedName} {phonePull} && cmp {seedPath} {phonePull}";
        var uploadGlob = Path.Combine(storageRoot, peerId);
        var uploadListing = $"ls -l {uploadGlob}/*/ ; expect payload + ready.json";
        var seedLine = seedContent.TrimEnd('\n');
        var fixture = new JsonObject
        {
            ["fixtureRoot"] = root,
            ["port"] = port,
            ["adminUrl"] = adminUrl,
            ["adminPasswordFile"] = adminFile,
            ["webDavUrl"] = scopedUrl,
            ["scopeDirectory"] = directoryUrl,
            ["relayUser"] = account.Username,
            ["relayPasswordFile"] = passwordPath,
            ["cloudCode"] = cloudCode,
            ["pairCode"] = pairCode,
            ["peerId"] = peerId,
            ["recipient"] = recipient,
            ["seedFile"] = seedPath,
            ["seedBytes"] = seedBytes,
            ["seedContent"] = seedContent,
            ["storageRoot"] = storageRoot,
            ["adbReverse"] = reverse,
            ["fixtureSelfCheck"] = "relay account listed and round-tripped the seeded inbound file"
        };
        await File.WriteAllTextAsync(Path.Combine(root, "fixture.json"),
            fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);

        var handover = $"""
        # MyPowerTools file-transfer · Android dataplane fixture (OpenList Local driver, no cloud, no Tailscale)
        fixture root     : {root}
        server           : {serverUrl}   (@manage {adminUrl}, admin password in {adminFile})
        webdav url (app) : {scopedUrl}   <- relay account scoped to /{mount}, so /dav is the mount root
        setup directory  : {directoryUrl}
        relay account    : {account.Username}   (password in {passwordPath})
        cloud code       : {cloudCode}
        pair code        : {pairCode}
        bridge (host)    : {reverse}
        fixture self-check: OK - the relay account listed and downloaded the seeded inbound file

        Android steps (app: 文件互传)
          1. 更多设置 → 本机收件箱名称 = {recipient} → 保存设置
          2. 粘贴 cloud code → 导入网盘连接（localhost 由 adb reverse 转发，不需要 Tailscale）
          3. 粘贴 pair code → 添加设备（列表出现 “{peerName}”）
          4. 网盘收件箱 → 刷新收件箱 → 下载 {seedName}
          5. 发送：选择文件 → 设备选 “{peerName}” → 方式选「网盘中转」→ 发送

        Host verification
          phone download source : {storageRoot}/{recipient}/<id>/payload
          phone download compare: {phoneCompare}
                                  (若手机 Download 已有同名文件，系统会另存为 “(1)”，请替换为实际文件名)
          android upload (cloud): {uploadListing}
          android upload compare: cmp <从手机取回的原文件> {uploadGlob}/<id>/payload
          expected inbound bytes: {seedBytes} bytes, exact content:
        {seedLine}

        Stop / purge
          bash tools/file-transfer/tests/android-dataplane-fixture.sh stop
          bash tools/file-transfer/tests/android-dataplane-fixture.sh stop --purge
        """;
        await File.WriteAllTextAsync(Path.Combine(root, "handover.txt"), handover, token);
    }

    /// <summary>Mounts the fixture's Local directory; re-runs reuse the existing mount and refuse a mismatch.</summary>
    private static async Task EnsureMountAsync(Uri serverUrl, string adminPassword, string storageRoot, string mount,
        CancellationToken token)
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        { BaseAddress = serverUrl, Timeout = TimeSpan.FromSeconds(30) };
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = adminPassword }, token);
        var auth = await login.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(200, auth.GetProperty("code").GetInt32());
        http.DefaultRequestHeaders.Add("Authorization", auth.GetProperty("data").GetProperty("token").GetString());
        using var list = await http.GetAsync("/api/admin/storage/list?page=1&per_page=0", token);
        var listed = await list.Content.ReadFromJsonAsync<JsonElement>(token);
        var existing = listed.GetProperty("data").GetProperty("content").EnumerateArray()
            .FirstOrDefault(item => item.TryGetProperty("mount_path", out var path) && path.GetString() == "/" + mount);
        if (existing.ValueKind == JsonValueKind.Object)
        {
            var addition = existing.TryGetProperty("addition", out var value) ? value.GetString() : null;
            if (addition is not null && !addition.Contains(storageRoot, StringComparison.Ordinal))
                throw new InvalidOperationException($"挂载 /{mount} 已存在但指向其它目录：{addition}。请更换 MPT_FIXTURE_ROOT 或清理该挂载。");
            return;
        }
        using var created = await http.PostAsJsonAsync("/api/admin/storage/create", new
        {
            mount_path = "/" + mount, driver = "Local", order = 0, cache_expiration = 0,
            addition = JsonSerializer.Serialize(new { root_folder_path = storageRoot, show_hidden = true, mkdir_perm = "700" }),
            webdav_policy = "native_proxy", disabled = false
        }, token);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.True(body.GetProperty("code").GetInt32() == 200, body.GetProperty("message").GetString());
    }
}

internal sealed class AndroidFixtureFactAttribute : FactAttribute
{
    public AndroidFixtureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MPT_FIXTURE_ROOT")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MPT_FIXTURE_ADMIN_PASSWORD")))
            Skip = "Set MPT_FIXTURE_ROOT and MPT_FIXTURE_ADMIN_PASSWORD (run tools/file-transfer/tests/android-dataplane-fixture.sh) to prepare the Android dataplane fixture.";
    }
}
