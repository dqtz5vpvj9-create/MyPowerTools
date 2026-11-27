using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPowerTools.Android.Updates;

namespace MyPowerTools.Android.Tests;

public sealed class AndroidModuleHotUpdaterTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _packageRoot;

    public AndroidModuleHotUpdaterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mpt-hotupdate-test-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_tempRoot, "bundled-modules");
        Directory.CreateDirectory(Path.Combine(_packageRoot, "remote-commands-android"));
        File.WriteAllText(Path.Combine(_packageRoot, "remote-commands-android", "BouncyCastle.Cryptography.dll"), "ORIGINAL_CRYPTO");
        File.WriteAllText(Path.Combine(_packageRoot, "remote-commands-android", "RemoteCommands.Android.dll"), "OLD_DLL");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task TailscaleReachable_DownloadsDirectlyFromTailscale_AndPreservesSharedCrypto()
    {
        var (zipBytes, sha) = BuildModuleZip(("RemoteCommands.Android.dll", "NEW_TAILSCALE_DLL"), ("module.json", "{}"));
        var manifest = JsonSerializer.Serialize(new ModuleFeedManifest(
            1, "2026-11-27T08:00:00Z", 19,
            [new ModuleFeedEntry("remote-commands-android", "0.2.17-ts1", "remote-commands-android.zip", sha, zipBytes.Length)]));

        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://tail.test/manifest.json") return JsonResponse(manifest);
            if (url == "http://tail.test/remote-commands-android.zip") return BytesResponse(zipBytes);
            throw new InvalidOperationException("Unexpected fallback call: " + url);
        });

        var reloaded = false;
        var updater = new AndroidModuleHotUpdaterCore(
            _tempRoot, _packageRoot, hostVersionCode: 19,
            httpClient: new HttpClient(handler),
            onAppliedReload: _ => { reloaded = true; return Task.CompletedTask; });
        updater.SaveConfig(new FeedConfig("http://tail.test", "https://proxy.test", "https://direct.test"));

        var outcome = await updater.CheckAndApplyAsync();
        Assert.True(outcome.Succeeded);
        Assert.Equal("Tailscale 内网直连", outcome.SourceLabel);
        Assert.Contains("remote-commands-android", outcome.UpdatedModules);
        Assert.True(reloaded);
        Assert.Equal("NEW_TAILSCALE_DLL", File.ReadAllText(Path.Combine(_packageRoot, "remote-commands-android", "RemoteCommands.Android.dll")));
        Assert.Equal("ORIGINAL_CRYPTO", File.ReadAllText(Path.Combine(_packageRoot, "remote-commands-android", "BouncyCastle.Cryptography.dll")));
    }

    [Fact]
    public async Task TailscaleUnreachable_AutomaticallyFallsBackToPublicProxy()
    {
        var (zipBytes, sha) = BuildModuleZip(("RemoteCommands.Android.dll", "NEW_PROXY_DLL"));
        var manifest = JsonSerializer.Serialize(new ModuleFeedManifest(
            1, "2026-11-27T08:00:00Z", 19,
            [new ModuleFeedEntry("remote-commands-android", "0.2.17-px1", "remote-commands-android.zip", sha, zipBytes.Length)]));

        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.StartsWith("http://tail.test")) throw new HttpRequestException("No route to Tailnet");
            if (url == "https://proxy.test/manifest.json") return JsonResponse(manifest);
            if (url == "https://proxy.test/remote-commands-android.zip") return BytesResponse(zipBytes);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var updater = new AndroidModuleHotUpdaterCore(
            _tempRoot, _packageRoot, hostVersionCode: 19,
            httpClient: new HttpClient(handler),
            tailscaleTimeout: TimeSpan.FromMilliseconds(200));
        updater.SaveConfig(new FeedConfig("http://tail.test", "https://proxy.test", "https://direct.test"));

        var outcome = await updater.CheckAndApplyAsync();
        Assert.True(outcome.Succeeded);
        Assert.Equal("公网代理回退", outcome.SourceLabel);
        Assert.Equal("NEW_PROXY_DLL", File.ReadAllText(Path.Combine(_packageRoot, "remote-commands-android", "RemoteCommands.Android.dll")));
    }

    [Fact]
    public async Task TamperedZip_RejectedBySha256_LeavesExistingModuleUntouched()
    {
        var (zipBytes, _) = BuildModuleZip(("RemoteCommands.Android.dll", "TAMPERED"));
        var manifest = JsonSerializer.Serialize(new ModuleFeedManifest(
            1, "2026-11-27T08:00:00Z", 19,
            [new ModuleFeedEntry("remote-commands-android", "0.2.17-bad", "remote-commands-android.zip", new string('0', 64), zipBytes.Length)]));

        var handler = new StubHandler(req =>
            req.RequestUri!.ToString().EndsWith("manifest.json") ? JsonResponse(manifest) : BytesResponse(zipBytes));

        var updater = new AndroidModuleHotUpdaterCore(_tempRoot, _packageRoot, hostVersionCode: 19, httpClient: new HttpClient(handler));
        updater.SaveConfig(new FeedConfig("http://tail.test", "", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => updater.CheckAndApplyAsync());
        Assert.Equal("OLD_DLL", File.ReadAllText(Path.Combine(_packageRoot, "remote-commands-android", "RemoteCommands.Android.dll")));
    }

    [Fact]
    public void NewerApkVersion_PrunesStaleOverlayAutomatically()
    {
        var overlayMod = Path.Combine(_tempRoot, "modules-overlay", "remote-commands-android");
        Directory.CreateDirectory(overlayMod);
        File.WriteAllText(Path.Combine(overlayMod, "RemoteCommands.Android.dll"), "STALE_OVERLAY");
        File.WriteAllText(Path.Combine(overlayMod, AndroidModuleHotUpdaterCore.OverlayMetaFileName),
            JsonSerializer.Serialize(new OverlayModuleMeta("remote-commands-android", "0.2.16", "abc", 18, "Tailscale", "http://tail", "2026-11-27T00:00:00Z")));

        var applied = AndroidModuleHotUpdaterCore.ApplyOverlaysToCatalog(Path.Combine(_tempRoot, "modules-overlay"), _packageRoot, currentHostVersionCode: 19);
        Assert.Equal(0, applied);
        Assert.False(Directory.Exists(overlayMod));
        Assert.Equal("OLD_DLL", File.ReadAllText(Path.Combine(_packageRoot, "remote-commands-android", "RemoteCommands.Android.dll")));
    }

    private static (byte[] Bytes, string Sha256) BuildModuleZip(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
                w.Write(content);
            }
        }
        var bytes = ms.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, sha);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BytesResponse(byte[] bytes) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
