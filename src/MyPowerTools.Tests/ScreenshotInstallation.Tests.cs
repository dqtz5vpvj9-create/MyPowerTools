using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Screenshot.MyPowerTools;

namespace MyPowerTools.Tests;

// All responses are synthetic and destinations are disposable. No publisher download,
// existing installation, external process or user profile is touched by these tests.
public sealed class ScreenshotInstallationTests
{
    [Fact]
    public async Task Unsupported_platform_never_creates_http_client()
    {
        var installer = new ScreenshotSnowShotInstaller(ScreenshotPlatform.Linux, null, null,
            () => throw new InvalidOperationException("HTTP must remain unused"));
        Assert.False(installer.TryStartInstall(out var task));
        Assert.Equal("unsupported", (await task).State);
        Assert.False(installer.Status.InProgress);
    }

    [Fact]
    public async Task Missing_windows_destination_rejects_install_before_network()
    {
        var installer = new ScreenshotSnowShotInstaller(ScreenshotPlatform.Windows, null, " ",
            () => throw new InvalidOperationException("HTTP must remain unused"));
        Assert.False(installer.TryStartInstall(out var task));
        Assert.Equal("unsupported", (await task).State);
        Assert.Contains("程序目录", installer.Status.Message);
    }

    [Fact]
    public void Checksum_selects_exact_asset_and_rejects_missing_or_invalid_hash()
    {
        var hash = new string('A', 64);
        Assert.Equal(hash.ToLowerInvariant(), ScreenshotSnowShotInstaller.ParseChecksum(
            $"{new string('b', 64)}  other.zip\n{hash} *./{ScreenshotSnowShotInstaller.WindowsPortableArchive}\r\n",
            ScreenshotSnowShotInstaller.WindowsPortableArchive));
        Assert.Throws<InvalidOperationException>(() => ScreenshotSnowShotInstaller.ParseChecksum(
            $"{hash} other.zip", ScreenshotSnowShotInstaller.WindowsPortableArchive));
        Assert.Throws<InvalidOperationException>(() => ScreenshotSnowShotInstaller.ParseChecksum(
            "invalid " + ScreenshotSnowShotInstaller.WindowsPortableArchive, ScreenshotSnowShotInstaller.WindowsPortableArchive));
    }

    [Fact]
    public async Task Verified_portable_payload_installs_with_config_and_replaces_old_payload()
    {
        using var fixture = new InstallationFixture(Archive(("bundle/bin/snow_shot.exe", "synthetic executable"),
            ("bundle/portable/config.json", "{\"global_shortcuts\":{\"screenshot\":\"Alt+A\"}}")));
        fixture.CreateExisting();
        Assert.True(fixture.Installer.TryStartInstall(out var task));
        Assert.Equal("installed", (await task).State);
        Assert.Equal("synthetic executable", File.ReadAllText(Path.Combine(fixture.Target, "bin", "snow_shot.exe")));
        Assert.Contains("Alt+A", File.ReadAllText(Path.Combine(fixture.Target, "portable", "config.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "old.txt")));
        fixture.AssertNoStaging();
    }

    [Fact]
    public async Task Checksum_mismatch_preserves_existing_install_and_does_not_extract()
    {
        using var fixture = new InstallationFixture(Archive(("snow_shot.exe", "synthetic")), new string('0', 64));
        fixture.CreateExisting();
        Assert.True(fixture.Installer.TryStartInstall(out var task));
        Assert.Equal("failed", (await task).State);
        Assert.Contains("SHA-256", fixture.Installer.Status.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Target, "old.txt")));
        fixture.AssertNoStaging();
    }

    [Fact]
    public async Task Missing_executable_preserves_existing_install_and_cleans_extraction()
    {
        using var fixture = new InstallationFixture(Archive(("readme.txt", "no executable")));
        fixture.CreateExisting();
        Assert.True(fixture.Installer.TryStartInstall(out var task));
        Assert.Equal("failed", (await task).State);
        Assert.Contains("snow_shot.exe", fixture.Installer.Status.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Target, "old.txt")));
        fixture.AssertNoStaging();
    }

    [Fact]
    public async Task Archive_traversal_is_rejected_and_partial_extraction_is_removed()
    {
        using var fixture = new InstallationFixture(Archive(("snow_shot.exe", "synthetic"), ("../escaped.txt", "reject")));
        fixture.CreateExisting();
        Assert.True(fixture.Installer.TryStartInstall(out var task));
        Assert.Equal("failed", (await task).State);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "Programs", "escaped.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Target, "old.txt")));
        fixture.AssertNoStaging();
    }

    [Fact]
    public async Task Http_failure_is_reported_and_user_can_retry_successfully()
    {
        using var fixture = new InstallationFixture(Archive(("snow_shot.exe", "synthetic")));
        fixture.Handler.FailDownload = true;
        Assert.True(fixture.Installer.TryStartInstall(out var failed));
        Assert.Equal("failed", (await failed).State);
        Assert.False(Directory.Exists(fixture.Target));
        fixture.Handler.FailDownload = false;
        Assert.True(fixture.Installer.TryStartInstall(out var retried));
        Assert.Equal("installed", (await retried).State);
        Assert.True(File.Exists(Path.Combine(fixture.Target, "snow_shot.exe")));
        fixture.AssertNoStaging();
    }

    [Fact]
    public async Task Concurrent_requests_share_one_install_task_and_download()
    {
        using var fixture = new InstallationFixture(Archive(("snow_shot.exe", "synthetic")));
        fixture.Handler.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(fixture.Installer.TryStartInstall(out var first));
        Assert.False(fixture.Installer.TryStartInstall(out var second));
        Assert.Same(first, second);
        Assert.True(fixture.Installer.Status.InProgress);
        fixture.Handler.DownloadGate.SetResult();
        Assert.Equal("installed", (await first).State);
        Assert.Equal(1, fixture.Handler.Downloads);
    }

    private static byte[] Archive(params (string Name, string Content)[] entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Name).Open());
                writer.Write(entry.Content);
            }
        }
        return bytes.ToArray();
    }

    private sealed class InstallationFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mpt-screenshot-install-test-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "Programs", "Snow Shot");
        public ResponseHandler Handler { get; }
        public ScreenshotSnowShotInstaller Installer { get; }
        public InstallationFixture(byte[] archive, string? expectedHash = null)
        {
            Handler = new ResponseHandler(archive, expectedHash ?? Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant());
            Installer = new ScreenshotSnowShotInstaller(ScreenshotPlatform.Windows, Root, Root,
                () => new HttpClient(Handler, disposeHandler: false));
        }
        public void CreateExisting()
        {
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, "old.txt"), "keep");
        }
        public void AssertNoStaging()
        {
            var programs = Path.Combine(Root, "Programs");
            if (Directory.Exists(programs))
                Assert.Empty(Directory.EnumerateDirectories(programs, ".snow-shot-mpt-install*"));
        }
        public void Dispose()
        {
            Handler.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class ResponseHandler(byte[] archive, string hash) : HttpMessageHandler
    {
        public bool FailDownload { get; set; }
        public int Downloads { get; private set; }
        public TaskCompletionSource? DownloadGate { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("snowshot.top", request.RequestUri.Host);
            if (request.RequestUri.AbsolutePath.EndsWith(ScreenshotSnowShotInstaller.WindowsPortableArchive, StringComparison.Ordinal))
            {
                Downloads++;
                if (DownloadGate is not null) await DownloadGate.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(FailDownload ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new ByteArrayContent(archive) };
            }
            Assert.EndsWith(ScreenshotSnowShotInstaller.WindowsChecksums, request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(hash + "  " + ScreenshotSnowShotInstaller.WindowsPortableArchive) };
        }
    }
}
