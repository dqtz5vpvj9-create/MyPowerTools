using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPowerTools.Packaging.Ota;

/// <summary>Options of the native macOS install/update engine.</summary>
public sealed record MacInstallOptions
{
    /// <summary>The app bundle to install or update. Default: <c>~/Applications/MyPowerTools.app</c>.</summary>
    public string AppBundlePath { get; init; } = Path.Combine(UserProfile(), "Applications", "MyPowerTools.app");

    /// <summary>The product data root. Default: <c>~/Library/Application Support/MyPowerTools</c>.</summary>
    public string DataRoot { get; init; } = Path.Combine(UserProfile(), "Library", "Application Support", "MyPowerTools");

    /// <summary><c>stable</c> or <c>nightly</c> (<c>local</c> only together with <see cref="FeedUrl"/>).</summary>
    public string Channel { get; init; } = "stable";

    /// <summary>Reinstall even when the installed version is current or newer.</summary>
    public bool Force { get; init; }

    /// <summary>Accept an unsigned feed (local/nightly validation only).</summary>
    public bool AllowUnsigned { get; init; }

    /// <summary>Explicit feed URL or absolute local feed path; assets are read from the same directory.</summary>
    public string? FeedUrl { get; init; }

    /// <summary>Run <c>/usr/bin/open</c> on the app after a successful install/update (or when already current).</summary>
    public bool Relaunch { get; init; } = true;

    /// <summary>
    /// Additional process ids that must survive the bundle stop, besides the current process.
    /// The CLI uses it for its in-bundle parent while the actual work runs from a copy.
    /// </summary>
    public IReadOnlyCollection<int> ExcludedProcessIds { get; init; } = [];

    public static MacInstallOptions CreateDefault() => new();

    private static string UserProfile() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>
/// One progress event. <see cref="Stage"/> is one of check, download, verify, stage, stop, swap,
/// services, health, rollback, done. <see cref="Message"/> is short Chinese UI text.
/// <see cref="Asset"/> names the file being downloaded for <c>download</c> events.
/// </summary>
public sealed record MacInstallProgress(
    string Stage,
    string Message,
    long? ReceivedBytes = null,
    long? TotalBytes = null,
    int? Percent = null,
    string? Asset = null);

/// <summary>
/// Native macOS installer/updater: a C# port of ota-update-macos.ps1, ota-apply-macos.ps1 and
/// install-macos-base.ps1 so that end users need no PowerShell. It verifies the signed channel
/// feed, downloads the full bundle, validates and stages it on the destination volume, swaps it
/// in transactionally (LaunchAgents, Launch Services, health check) and rolls back on failure.
/// The state files it writes keep the pwsh updater and the Shell compatible.
/// </summary>
public sealed partial class MacNativeInstaller
{
    private static readonly Lazy<HttpClient> SharedHttpClient = new(() => new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private const string UserAgent = "MyPowerTools-OTA";

    private readonly MacInstallOptions _options;
    private readonly Action<MacInstallProgress>? _progress;
    private readonly HttpClient _http;

    public MacNativeInstaller(
        MacInstallOptions options,
        Action<MacInstallProgress>? progress = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _progress = progress;
        _http = httpClient ?? SharedHttpClient.Value;
    }

    /// <summary>Test hook: bypasses lipo/sysctl detection.</summary>
    internal string? RuntimeIdentifierOverride { get; set; }

    private string AppBundleFull => Path.GetFullPath(_options.AppBundlePath).TrimEnd('/');

    private string DataRootFull => Path.GetFullPath(_options.DataRoot).TrimEnd('/');

    private string StateRoot => Path.Combine(DataRootFull, "ota-state");

    /// <summary>CFBundleShortVersionString of <see cref="MacInstallOptions.AppBundlePath"/>, or null when not installed.</summary>
    public string? ReadInstalledVersion() => ReadBundleVersion(AppBundleFull);

    /// <summary>
    /// Checks the channel feed. Throws with a Chinese message on network, feed or signature
    /// errors; writes <c>ota-state/last-check.json</c> on success.
    /// </summary>
    /// <remarks>Runs on the thread pool; progress callbacks arrive on pool threads.</remarks>
    public Task<JsonObject> CheckAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => CheckCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task<JsonObject> CheckCoreAsync(CancellationToken cancellationToken)
    {
        RequireNotRoot();
        var context = await ResolveCheckAsync(cancellationToken).ConfigureAwait(false);
        return (JsonObject)context.Check.DeepClone();
    }

    /// <summary>
    /// The <c>mpt ota status</c> document, same shape as the pwsh updater's Status command.
    /// </summary>
    public JsonObject ReadStatus()
    {
        var installedRelease = ReadJsonFile(Path.Combine(StateRoot, "installed-release.json"));
        string platform;
        try
        {
            platform = ResolveRuntimeIdentifier(ReadInstalledVersion() is not null, installedRelease as JsonObject);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            platform = OtaFeedLayout.CurrentRuntimeIdentifier();
        }

        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["platform"] = platform,
            ["installed"] = installedRelease,
            ["lastCheck"] = ReadJsonFile(Path.Combine(StateRoot, "last-check.json")),
            ["lastUpdate"] = ReadJsonFile(Path.Combine(StateRoot, "last-update.json")),
            ["health"] = ReadJsonFile(Path.Combine(StateRoot, "health-check.json"))
        };
    }

    /// <summary>
    /// Installs (when missing), updates (when newer) or does nothing (when current). Never throws
    /// except <see cref="OperationCanceledException"/> before the bundle swap starts.
    /// </summary>
    /// <remarks>Runs on the thread pool; progress callbacks arrive on pool threads.</remarks>
    public Task<JsonObject> ApplyAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => ApplyCoreAsync(cancellationToken), cancellationToken);
    }

    private async Task<JsonObject> ApplyCoreAsync(CancellationToken cancellationToken)
    {
        var channel = _options.Channel;
        var platform = string.Empty;
        var fromVersion = "0.0.0";
        var latestVersion = string.Empty;
        var rolledBack = false;
        FileStream? stateLock = null;
        FileStream? installLock = null;
        try
        {
            if (!OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("原生安装器仅支持 macOS。");
            }

            RequireNotRoot();
            fromVersion = ReadInstalledVersion() is { } installed && MacInstallLogic.IsValidVersion(installed)
                ? installed
                : "0.0.0";
            var layout = PrepareLayout();
            Directory.CreateDirectory(StateRoot);
            stateLock = AcquireLock(
                Path.Combine(StateRoot, "ota-update.lock"),
                "另一个 MyPowerTools 更新正在进行，请稍后再试。");
            installLock = AcquireLock(
                layout.InstallLockPath,
                "另一个 MyPowerTools 安装程序正在运行，请稍后再试。");
            TrySetUnixMode(layout.InstallLockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var context = await ResolveCheckAsync(cancellationToken).ConfigureAwait(false);
            platform = context.RuntimeIdentifier;
            fromVersion = context.CurrentVersion;
            latestVersion = context.Package.Version;
            if (!context.Available)
            {
                Report("done", context.Reason == "up-to-date"
                    ? $"已是最新版本 {context.CurrentVersion}。"
                    : $"已安装版本 {context.CurrentVersion} 高于更新源版本 {latestVersion}，未降级。");
                var relaunchedCurrent = _options.Relaunch && context.Installed && Relaunch(layout.TargetApp);
                return new JsonObject
                {
                    ["success"] = true,
                    ["upToDate"] = true,
                    ["channel"] = channel,
                    ["platform"] = platform,
                    ["fromVersion"] = fromVersion,
                    ["toVersion"] = fromVersion,
                    ["latestVersion"] = latestVersion,
                    ["reason"] = context.Reason,
                    ["relaunched"] = relaunchedCurrent,
                    ["completedAtUtc"] = UtcNow()
                };
            }

            var package = context.Package;
            var packagePath = await DownloadAssetAsync(
                context.Source, package.Asset, package.Sha256, package.Size, cancellationToken).ConfigureAwait(false);
            var manifestPath = await DownloadAssetAsync(
                context.Source, package.ManifestAsset, package.ManifestSha256, null, cancellationToken).ConfigureAwait(false);
            Report("verify", "正在校验文件清单…");
            MacInstallLogic.ValidateManifest(ReadJsonFile(manifestPath), package.Version);

            var outcome = await InstallBundleAsync(layout, packagePath, package.Version, cancellationToken)
                .ConfigureAwait(false);
            rolledBack = outcome.RolledBack;
            if (outcome.Failure is not null)
            {
                throw outcome.Failure;
            }

            // State first, relaunch last: the relaunched Shell reads last-update.json on start.
            var installedManifest = Path.Combine(StateRoot, "installed-files.manifest.json");
            CopyFileAtomic(manifestPath, installedManifest);
            var publicKey = ResolvePublicKey();
            if (MacInstallLogic.IsPublicKeyHex(publicKey))
            {
                WriteTextAtomic(Path.Combine(StateRoot, "ota-signing-public-key.txt"), publicKey);
            }

            WriteJsonAtomic(
                Path.Combine(StateRoot, "installed-release.json"),
                MacInstallLogic.BuildInstalledRelease(
                    package.Version,
                    channel,
                    layout.TargetApp,
                    layout.DataRoot,
                    MacInstallLogic.Sha256File(installedManifest),
                    platform,
                    DateTimeOffset.UtcNow));
            WriteJsonAtomic(Path.Combine(StateRoot, "health-check.json"), outcome.Health!.DeepClone());

            var stopped = new JsonArray();
            foreach (var process in outcome.StoppedProcesses)
            {
                stopped.Add(new JsonObject { ["processId"] = process.ProcessId, ["path"] = process.ExecutablePath });
            }

            var result = new JsonObject
            {
                ["success"] = true,
                ["channel"] = channel,
                ["platform"] = platform,
                ["fromVersion"] = fromVersion,
                ["toVersion"] = package.Version,
                ["freshInstall"] = !context.Installed,
                ["packageKind"] = "full",
                ["packageSha256"] = package.Sha256,
                ["manifestSha256"] = package.ManifestSha256,
                ["appBundlePath"] = layout.TargetApp,
                ["stoppedProcesses"] = stopped,
                ["health"] = outcome.Health!.DeepClone(),
                ["relaunched"] = _options.Relaunch,
                ["completedAtUtc"] = UtcNow()
            };
            WriteJsonAtomic(Path.Combine(StateRoot, "last-update.json"), result);
            ReleaseLocks(ref stateLock, ref installLock);

            if (_options.Relaunch)
            {
                result["relaunched"] = Relaunch(layout.TargetApp);
            }

            Report("done", context.Installed
                ? $"已更新到 {package.Version}。"
                : $"已安装 {package.Version}。");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = new JsonObject
            {
                ["success"] = false,
                ["channel"] = channel,
                ["platform"] = platform,
                ["fromVersion"] = fromVersion,
                ["latestVersion"] = latestVersion,
                ["error"] = DescribeError(exception),
                ["rolledBack"] = rolledBack,
                ["completedAtUtc"] = UtcNow()
            };
            try
            {
                Directory.CreateDirectory(StateRoot);
                WriteJsonAtomic(Path.Combine(StateRoot, "last-update.json"), failure);
            }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
            }

            return failure;
        }
        finally
        {
            ReleaseLocks(ref stateLock, ref installLock);
        }
    }

    private sealed record CheckContext(
        JsonObject Check,
        MacFeedPackage Package,
        FeedSource Source,
        string RuntimeIdentifier,
        bool Installed,
        string CurrentVersion,
        bool Available,
        string Reason);

    /// <summary>Where the feed came from: an HTTP base URL or a local directory.</summary>
    private sealed record FeedSource(string BaseLocation, bool IsLocalDirectory);

    private async Task<CheckContext> ResolveCheckAsync(CancellationToken cancellationToken)
    {
        Report("check", "正在检查更新…");
        var channel = _options.Channel?.Trim() ?? string.Empty;
        if (channel is not ("stable" or "nightly") &&
            !(channel == "local" && !string.IsNullOrWhiteSpace(_options.FeedUrl)))
        {
            throw new ArgumentException($"不支持的更新渠道 '{channel}'，只能是 stable 或 nightly。");
        }

        Directory.CreateDirectory(StateRoot);
        var installedRelease = ReadJsonFile(Path.Combine(StateRoot, "installed-release.json")) as JsonObject;
        var bundleVersion = ReadInstalledVersion();
        var installed = bundleVersion is not null;
        var currentVersion = "0.0.0";
        if (installed)
        {
            if (MacInstallLogic.IsValidVersion(bundleVersion))
            {
                currentVersion = bundleVersion!;
            }
            else if (MacInstallLogic.ReadString(installedRelease?["version"]) is var recorded &&
                     MacInstallLogic.IsValidVersion(recorded))
            {
                currentVersion = recorded;
            }
        }

        var runtimeIdentifier = ResolveRuntimeIdentifier(installed, installedRelease);
        var (feedBytes, signature, source) = await FetchFeedAsync(channel, runtimeIdentifier, cancellationToken)
            .ConfigureAwait(false);

        JsonNode? feedNode;
        try
        {
            feedNode = JsonNode.Parse(MacInstallLogic.StripUtf8Bom(feedBytes));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"更新源不是有效的 JSON：{exception.Message}");
        }

        Report("verify", "正在校验更新源签名…");
        var package = MacInstallLogic.ValidateFeed(feedNode, channel, runtimeIdentifier);
        MacInstallLogic.VerifyFeedSignature(
            feedBytes,
            signature,
            ResolvePublicKey(),
            package.Signed,
            _options.AllowUnsigned);

        var (available, reason) = MacInstallLogic.DecideUpdate(installed, currentVersion, package.Version, _options.Force);
        var check = new JsonObject
        {
            ["checkedAtUtc"] = UtcNow(),
            ["channel"] = channel,
            ["platform"] = runtimeIdentifier,
            ["installed"] = installed,
            ["currentVersion"] = currentVersion,
            ["latestVersion"] = package.Version,
            ["available"] = available,
            ["reason"] = reason,
            ["signed"] = package.Signed,
            ["package"] = available
                ? new JsonObject
                {
                    ["kind"] = "full",
                    ["asset"] = package.Asset,
                    ["sha256"] = package.Sha256,
                    ["size"] = package.Size,
                    ["manifestAsset"] = package.ManifestAsset,
                    ["manifestSha256"] = package.ManifestSha256
                }
                : null
        };
        WriteJsonAtomic(Path.Combine(StateRoot, "last-check.json"), check);
        Report("check", available
            ? (installed ? $"发现新版本 {package.Version}。" : $"可安装版本 {package.Version}。")
            : $"已是最新版本 {currentVersion}。");
        return new CheckContext(check, package, source, runtimeIdentifier, installed, currentVersion, available, reason);
    }

    private async Task<(byte[] Feed, string Signature, FeedSource Source)> FetchFeedAsync(
        string channel,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var explicitFeed = _options.FeedUrl?.Trim();
        if (!string.IsNullOrEmpty(explicitFeed) && TryGetLocalPath(explicitFeed, out var localFeed))
        {
            if (!File.Exists(localFeed))
            {
                throw new FileNotFoundException($"本地更新源不存在：{localFeed}");
            }

            var localSignature = localFeed + ".sig";
            return (
                await File.ReadAllBytesAsync(localFeed, cancellationToken).ConfigureAwait(false),
                File.Exists(localSignature)
                    ? (await File.ReadAllTextAsync(localSignature, cancellationToken).ConfigureAwait(false)).Trim()
                    : string.Empty,
                new FeedSource(Path.GetDirectoryName(localFeed)!, true));
        }

        var feedUrl = !string.IsNullOrEmpty(explicitFeed)
            ? explicitFeed
            : channel == "stable"
                ? MacInstallLogic.StableFeedUrl(runtimeIdentifier)
                : await ResolveGitHubFeedUrlAsync(channel, runtimeIdentifier, cancellationToken).ConfigureAwait(false);

        var feedBytes = await GetBytesAsync(feedUrl, "更新源", cancellationToken).ConfigureAwait(false);
        var downloads = Path.Combine(StateRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var feedCopy = Path.Combine(downloads, OtaFeedLayout.ChannelFeedAsset(channel, runtimeIdentifier));
        await File.WriteAllBytesAsync(feedCopy, feedBytes, cancellationToken).ConfigureAwait(false);

        // Like the pwsh updater, a missing signature is not an error here; an unsigned feed is
        // rejected (or allowed) by the signature policy, a signed one without .sig fails there.
        var signature = string.Empty;
        try
        {
            var signatureBytes = await GetBytesAsync(feedUrl + ".sig", "更新源签名", cancellationToken)
                .ConfigureAwait(false);
            signature = Encoding.UTF8.GetString(MacInstallLogic.StripUtf8Bom(signatureBytes)).Trim();
            await File.WriteAllTextAsync(feedCopy + ".sig", signature, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            signature = string.Empty;
        }

        return (feedBytes, signature, new FeedSource(MacInstallLogic.FeedBaseUrl(feedUrl), false));
    }

    private async Task<string> ResolveGitHubFeedUrlAsync(
        string channel,
        string runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        var assetName = OtaFeedLayout.ChannelFeedAsset(channel, runtimeIdentifier);
        var apiUrl = $"https://api.github.com/repos/{MacInstallLogic.Repository}/releases?per_page=30";
        var bytes = await GetBytesAsync(apiUrl, "GitHub 发布列表", cancellationToken, "application/vnd.github+json")
            .ConfigureAwait(false);
        JsonNode? releases;
        try
        {
            releases = JsonNode.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"GitHub 发布列表不是有效的 JSON：{exception.Message}");
        }

        return MacInstallLogic.FindReleaseAssetUrl(releases, assetName)
            ?? throw new InvalidOperationException($"GitHub 上当前没有发布 {assetName}（{channel} 渠道）。");
    }

    private async Task<byte[]> GetBytesAsync(
        string url,
        string what,
        CancellationToken cancellationToken,
        string? accept = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FeedTimeout);
        try
        {
            using var request = CreateRequest(url, accept);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"下载{what}失败：服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}（{url}）。");
            }

            return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"下载{what}超时（{FeedTimeout.TotalSeconds:0} 秒）：{url}");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"无法连接更新服务器，下载{what}失败：{exception.Message}（{url}）");
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string? accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (accept is not null)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        return request;
    }

    /// <summary>
    /// Resolve-OtaArtifact: downloads (or copies) an asset into <c>ota-state/downloads</c> and
    /// verifies its SHA-256 and, when known, its size. A cached file that already verifies is reused.
    /// </summary>
    private async Task<string> DownloadAssetAsync(
        FeedSource source,
        string asset,
        string expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(asset) != asset || asset.Length == 0)
        {
            throw new InvalidDataException($"更新文件名不能包含路径：{asset}");
        }

        if (!MacInstallLogic.IsSha256(expectedSha256))
        {
            throw new InvalidDataException($"更新文件的 SHA-256 无效：{asset}");
        }

        var downloads = Path.Combine(StateRoot, "downloads");
        Directory.CreateDirectory(downloads);
        var destination = Path.Combine(downloads, asset);
        if (File.Exists(destination) &&
            (expectedSize is null || new FileInfo(destination).Length == expectedSize) &&
            string.Equals(MacInstallLogic.Sha256File(destination), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            var length = new FileInfo(destination).Length;
            Report(new MacInstallProgress("download", $"已使用缓存的 {asset}", length, length, 100, asset));
            return destination;
        }

        var partial = destination + ".partial";
        TryDeleteFile(partial);
        string actualSha256;
        long received = 0;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            if (source.IsLocalDirectory)
            {
                var sourceFile = Path.Combine(source.BaseLocation, asset);
                if (!File.Exists(sourceFile))
                {
                    throw new FileNotFoundException($"本地更新文件不存在：{sourceFile}");
                }

                await using var input = File.OpenRead(sourceFile);
                received = await CopyWithProgressAsync(input, partial, hash, asset, expectedSize ?? input.Length, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var url = source.BaseLocation.TrimEnd('/') + "/" + Uri.EscapeDataString(asset);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DownloadTimeout);
                try
                {
                    using var request = CreateRequest(url, null);
                    using var response = await _http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"下载 {asset} 失败：服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
                    }

                    var total = response.Content.Headers.ContentLength ?? expectedSize;
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    received = await CopyWithProgressAsync(input, partial, hash, asset, total, timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    TryDeleteFile(partial);
                    throw new InvalidOperationException($"下载 {asset} 超时。");
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    TryDeleteFile(partial);
                    throw new InvalidOperationException($"下载 {asset} 失败：{exception.Message}");
                }
            }

            actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        Report("verify", $"正在校验 {asset}…");
        if (expectedSize is not null && received != expectedSize)
        {
            TryDeleteFile(partial);
            throw new InvalidDataException($"{asset} 大小不符：应为 {expectedSize} 字节，实际为 {received} 字节。");
        }

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partial);
            throw new InvalidDataException($"{asset} 的 SHA-256 校验失败：应为 {expectedSha256}，实际为 {actualSha256}。");
        }

        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    private async Task<long> CopyWithProgressAsync(
        Stream input,
        string destination,
        IncrementalHash hash,
        string asset,
        long? total,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long received = 0;
        var lastReport = DateTime.MinValue;
        Report(new MacInstallProgress("download", $"正在下载 {asset}", 0, total, MacInstallLogic.Percent(0, total), asset));
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, true))
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                var now = DateTime.UtcNow;
                if (now - lastReport >= ProgressInterval)
                {
                    lastReport = now;
                    Report(new MacInstallProgress(
                        "download", $"正在下载 {asset}", received, total, MacInstallLogic.Percent(received, total), asset));
                }
            }
        }

        Report(new MacInstallProgress(
            "download", $"已下载 {asset}", received, total ?? received, MacInstallLogic.Percent(received, total ?? received), asset));
        return received;
    }

    private static bool TryGetLocalPath(string feed, out string path)
    {
        if (feed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(feed, UriKind.Absolute, out var uri))
        {
            path = uri.LocalPath;
            return true;
        }

        if (!feed.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(feed))
        {
            path = Path.GetFullPath(feed);
            return true;
        }

        path = string.Empty;
        return false;
    }

    private string ResolvePublicKey()
    {
        var stateKey = Path.Combine(StateRoot, "ota-signing-public-key.txt");
        try
        {
            if (File.Exists(stateKey))
            {
                var value = File.ReadAllText(stateKey, new UTF8Encoding(false)).Trim();
                if (MacInstallLogic.IsPublicKeyHex(value))
                {
                    return value.ToLowerInvariant();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return MacInstallLogic.EmbeddedPublicKeyHex;
    }

    /// <summary>
    /// Resolve-MacRuntimeIdentifier: the installed launcher's architecture wins (an x64 install
    /// on Apple silicon keeps receiving x64), then the recorded release, then the hardware.
    /// </summary>
    private string ResolveRuntimeIdentifier(bool installed, JsonObject? installedRelease)
    {
        if (!string.IsNullOrWhiteSpace(RuntimeIdentifierOverride))
        {
            return RuntimeIdentifierOverride;
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("原生安装器仅支持 macOS。");
        }

        if (installed)
        {
            var launcher = Path.Combine(AppBundleFull, "Contents", "MacOS", "MyPowerTools");
            if (File.Exists(launcher))
            {
                var lipo = MacProcessRunner.Run("/usr/bin/lipo", ["-archs", launcher], TimeSpan.FromSeconds(15));
                if (lipo.Succeeded && MacInstallLogic.RuntimeIdentifierFromLipo(lipo.StandardOutput) is { } fromLipo)
                {
                    return fromLipo;
                }
            }

            var recorded = MacInstallLogic.ReadString(installedRelease?["runtimeIdentifier"]);
            if (recorded is OtaFeedLayout.OsxArm64 or OtaFeedLayout.OsxX64)
            {
                return recorded;
            }
        }

        var sysctl = MacProcessRunner.Run("/usr/sbin/sysctl", ["-n", "hw.optional.arm64"], TimeSpan.FromSeconds(10));
        return MacInstallLogic.RuntimeIdentifierFromSysctl(sysctl.ExitCode, sysctl.StandardOutput);
    }

    private static string? ReadBundleVersion(string bundlePath)
    {
        var plist = Path.Combine(bundlePath, "Contents", "Info.plist");
        if (!File.Exists(plist))
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var result = MacProcessRunner.Run(
                "/usr/bin/plutil",
                ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", plist],
                TimeSpan.FromSeconds(30));
            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return result.StandardOutput.Trim();
            }
        }

        try
        {
            return MacInstallLogic.ReadPlistString(File.ReadAllText(plist), "CFBundleShortVersionString") ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void RequireNotRoot()
    {
        if (OperatingSystem.IsMacOS() && MacNative.CurrentUserId() == 0)
        {
            throw new InvalidOperationException("请以当前登录用户身份运行 MyPowerTools 安装/更新，不要使用 sudo 或 root。");
        }
    }

    private static FileStream AcquireLock(string path, string busyMessage)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            // Keep the lock inode after closing: unlinking it would let two later runs lock
            // different inodes under the same name. Only the open handle represents ownership.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(busyMessage);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"无法获取安装锁 {path}：{exception.Message}");
        }
    }

    private static void ReleaseLocks(ref FileStream? first, ref FileStream? second)
    {
        second?.Dispose();
        second = null;
        first?.Dispose();
        first = null;
    }

    private void Report(string stage, string message) => Report(new MacInstallProgress(stage, message));

    private void Report(MacInstallProgress progress)
    {
        if (_progress is null)
        {
            return;
        }

        try
        {
            _progress(progress);
        }
        catch (Exception)
        {
            // A broken UI callback (or a closed console pipe) must never abort an install.
        }
    }

    private static string DescribeError(Exception exception)
    {
        return exception is AggregateException { InnerException: { } inner }
            ? inner.Message
            : exception.Message;
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static JsonNode? ReadJsonFile(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(MacInstallLogic.StripUtf8Bom(File.ReadAllBytes(path))) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static void WriteJsonAtomic(string path, JsonNode node)
    {
        WriteTextAtomic(path, node.ToJsonString(JsonOptions));
    }

    internal static void WriteTextAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void CopyFileAtomic(string source, string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
