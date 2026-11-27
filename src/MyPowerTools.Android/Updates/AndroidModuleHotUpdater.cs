using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPowerTools.Android.Updates;

/// <summary>
/// Pure-.NET multi-endpoint module hot-updater for Android.
/// Probes Tailscale first (short timeout), then automatically falls back to Public Proxy and Direct GitHub.
/// All downloads are verified with SHA-256 and staged atomically before replacing the active module overlay.
/// </summary>
public sealed class AndroidModuleHotUpdaterCore
{
    public const string ConfigFileName = "module-update-sources.json";
    public const string OverlayMetaFileName = ".overlay-meta.json";
    public const string SharedCryptoDll = "BouncyCastle.Cryptography.dll";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _rootDir;
    private readonly string _overlayDir;
    private readonly string _packageRoot;
    private readonly long _hostVersionCode;
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task>? _onAppliedReload;
    private readonly TimeSpan _tailscaleTimeout;
    private readonly TimeSpan _publicTimeout;

    public AndroidModuleHotUpdaterCore(
        string rootDir,
        string packageRoot,
        long hostVersionCode,
        HttpClient? httpClient = null,
        Func<CancellationToken, Task>? onAppliedReload = null,
        TimeSpan? tailscaleTimeout = null,
        TimeSpan? publicTimeout = null)
    {
        _rootDir = rootDir;
        _overlayDir = Path.Combine(rootDir, "modules-overlay");
        _packageRoot = packageRoot;
        _hostVersionCode = hostVersionCode;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _onAppliedReload = onAppliedReload;
        _tailscaleTimeout = tailscaleTimeout ?? TimeSpan.FromMilliseconds(2500);
        _publicTimeout = publicTimeout ?? TimeSpan.FromSeconds(10);
    }

    public string ConfigPath => Path.Combine(_rootDir, ConfigFileName);
    public string OverlayDir => _overlayDir;

    public FeedConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var parsed = JsonSerializer.Deserialize<FeedConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (parsed is not null)
                {
                    return parsed.Normalize();
                }
            }
        }
        catch
        {
            // Fall back to defaults on corrupt config.
        }

        return FeedConfig.CreateDefault();
    }

    public void SaveConfig(FeedConfig config)
    {
        Directory.CreateDirectory(_rootDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config.Normalize(), JsonOptions));
    }

    public IReadOnlyList<OverlayModuleMeta> ListInstalledOverlays()
    {
        var results = new List<OverlayModuleMeta>();
        if (!Directory.Exists(_overlayDir))
        {
            return results;
        }

        foreach (var dir in Directory.EnumerateDirectories(_overlayDir))
        {
            var metaPath = Path.Combine(dir, OverlayMetaFileName);
            if (!File.Exists(metaPath))
            {
                continue;
            }

            try
            {
                var meta = JsonSerializer.Deserialize<OverlayModuleMeta>(File.ReadAllText(metaPath), JsonOptions);
                if (meta is not null && !string.IsNullOrWhiteSpace(meta.Id))
                {
                    results.Add(meta);
                }
            }
            catch
            {
                // Ignore malformed metadata; startup overlay merge skips it too.
            }
        }

        return results.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Resolves the first reachable feed endpoint in priority order:
    /// 1) Tailscale private URL (fast 2.5s timeout), 2) Public proxy URL, 3) Direct GitHub fallback.
    /// </summary>
    public async Task<ResolvedFeed> ResolveFeedAsync(CancellationToken cancellationToken = default)
    {
        var cfg = LoadConfig();
        var candidates = new List<(string Kind, string Label, string BaseUrl, TimeSpan Timeout)>();
        if (!string.IsNullOrWhiteSpace(cfg.TailscaleBaseUrl))
            candidates.Add(("tailscale", "Tailscale 内网直连", cfg.TailscaleBaseUrl.TrimEnd('/'), _tailscaleTimeout));
        if (!string.IsNullOrWhiteSpace(cfg.PublicProxyBaseUrl))
            candidates.Add(("proxy", "公网代理回退", cfg.PublicProxyBaseUrl.TrimEnd('/'), _publicTimeout));
        if (!string.IsNullOrWhiteSpace(cfg.DirectFallbackBaseUrl))
            candidates.Add(("direct", "GitHub 直连回退", cfg.DirectFallbackBaseUrl.TrimEnd('/'), _publicTimeout));

        var errors = new List<string>();
        foreach (var (kind, label, baseUrl, timeout) in candidates)
        {
            var manifestUrl = $"{baseUrl}/manifest.json";
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(timeout);
            try
            {
                using var response = await _http.GetAsync(manifestUrl, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{label}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);
                var manifest = JsonSerializer.Deserialize<ModuleFeedManifest>(json, JsonOptions);
                if (manifest is null || manifest.SchemaVersion != 1 || manifest.Modules is null)
                {
                    errors.Add($"{label}: manifest 格式无效");
                    continue;
                }

                return new ResolvedFeed(kind, label, baseUrl, manifest);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{label}: 连接超时");
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
            }
        }

        throw new InvalidOperationException("所有更新源均不可达：" + string.Join("；", errors));
    }

    public async Task<UpdateApplyOutcome> CheckAndApplyAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveFeedAsync(cancellationToken).ConfigureAwait(false);
        var manifest = resolved.Manifest;

        if (manifest.MinHostVersionCode > 0 && _hostVersionCode > 0 && _hostVersionCode < manifest.MinHostVersionCode)
        {
            return new UpdateApplyOutcome(
                false,
                resolved.Label,
                resolved.BaseUrl,
                Array.Empty<string>(),
                0,
                $"远端模块要求宿主版本 >= {manifest.MinHostVersionCode}（当前 {_hostVersionCode}），请先更新基础 APK。");
        }

        var existing = ListInstalledOverlays().ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
        var toUpdate = new List<ModuleFeedEntry>();
        foreach (var entry in manifest.Modules ?? Enumerable.Empty<ModuleFeedEntry>())
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.File) || string.IsNullOrWhiteSpace(entry.Sha256))
            {
                continue;
            }

            if (existing.TryGetValue(entry.Id, out var installed) &&
                string.Equals(installed.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            toUpdate.Add(entry);
        }

        if (toUpdate.Count == 0)
        {
            return new UpdateApplyOutcome(
                true,
                resolved.Label,
                resolved.BaseUrl,
                Array.Empty<string>(),
                0,
                $"已通过 {resolved.Label} 检查：全部模块已是最新。");
        }

        Directory.CreateDirectory(_overlayDir);
        var updatedIds = new List<string>();
        long totalBytes = 0;

        foreach (var entry in toUpdate)
        {
            ValidateSafeModuleId(entry.Id);
            var zipUrl = entry.File.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         entry.File.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? entry.File
                : $"{resolved.BaseUrl}/{entry.File.TrimStart('/')}";

            var tempZip = Path.Combine(_overlayDir, $".dl-{entry.Id}-{Guid.NewGuid():N}.zip");
            var stagingDir = Path.Combine(_overlayDir, $".stage-{entry.Id}-{Guid.NewGuid():N}");
            var targetDir = Path.Combine(_overlayDir, entry.Id);
            var backupDir = Path.Combine(_overlayDir, $".bak-{entry.Id}-{Guid.NewGuid():N}");

            try
            {
                using (var response = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var dst = File.Create(tempZip);
                    await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                }

                var zipBytes = new FileInfo(tempZip).Length;
                var actualHash = ComputeSha256Hex(tempZip);
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"模块 {entry.Id} SHA-256 校验失败（预期 {entry.Sha256[..8]}，实际 {actualHash[..8]}）。");
                }

                Directory.CreateDirectory(stagingDir);
                ExtractZipSafely(tempZip, stagingDir);

                var meta = new OverlayModuleMeta(
                    entry.Id,
                    entry.Version ?? "",
                    actualHash,
                    _hostVersionCode,
                    resolved.Label,
                    resolved.BaseUrl,
                    DateTimeOffset.UtcNow.ToString("O"));
                File.WriteAllText(Path.Combine(stagingDir, OverlayMetaFileName), JsonSerializer.Serialize(meta, JsonOptions));

                if (Directory.Exists(targetDir))
                {
                    Directory.Move(targetDir, backupDir);
                }

                try
                {
                    Directory.Move(stagingDir, targetDir);
                    if (Directory.Exists(backupDir))
                    {
                        Directory.Delete(backupDir, recursive: true);
                    }
                }
                catch
                {
                    if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                    {
                        Directory.Move(backupDir, targetDir);
                    }
                    throw;
                }

                updatedIds.Add(entry.Id);
                totalBytes += zipBytes;
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
                if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            }
        }

        ApplyOverlaysToCatalog(_overlayDir, _packageRoot, _hostVersionCode);

        if (_onAppliedReload is not null)
        {
            await _onAppliedReload(cancellationToken).ConfigureAwait(false);
        }

        var kb = totalBytes / 1024.0;
        return new UpdateApplyOutcome(
            true,
            resolved.Label,
            resolved.BaseUrl,
            updatedIds,
            totalBytes,
            $"已通过 {resolved.Label} 热更新 {updatedIds.Count} 个模块（{kb:F0} KB）：{string.Join(", ", updatedIds)}");
    }

    public async Task<UpdateApplyOutcome> ResetToBundledAsync(CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(_overlayDir))
        {
            Directory.Delete(_overlayDir, recursive: true);
        }

        var stampPath = Path.Combine(_packageRoot, ".catalog-stamp");
        if (File.Exists(stampPath))
        {
            File.Delete(stampPath);
        }

        if (_onAppliedReload is not null)
        {
            await _onAppliedReload(cancellationToken).ConfigureAwait(false);
        }

        return new UpdateApplyOutcome(true, "本地内置", "", Array.Empty<string>(), 0, "已还原为 APK 内置模块并重新加载。");
    }

    /// <summary>
    /// Merges valid overlays on top of the extracted APK catalog while preserving shared BouncyCastle.Cryptography.dll
    /// (so delta zips do not need to re-ship the 2.4 MiB crypto assembly). Stale overlays from older APK builds are pruned.
    /// </summary>
    public static int ApplyOverlaysToCatalog(string overlayDir, string packageRoot, long currentHostVersionCode)
    {
        if (!Directory.Exists(overlayDir) || !Directory.Exists(packageRoot))
        {
            return 0;
        }

        var applied = 0;
        foreach (var dir in Directory.EnumerateDirectories(overlayDir))
        {
            var moduleId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(moduleId) || moduleId.StartsWith('.'))
            {
                continue;
            }

            var metaPath = Path.Combine(dir, OverlayMetaFileName);
            if (!File.Exists(metaPath))
            {
                continue;
            }

            OverlayModuleMeta? meta;
            try
            {
                meta = JsonSerializer.Deserialize<OverlayModuleMeta>(File.ReadAllText(metaPath), JsonOptions);
            }
            catch
            {
                continue;
            }

            if (meta is null)
            {
                continue;
            }

            // If the user installed a newer base APK than the overlay was built for, drop stale overlay automatically.
            if (currentHostVersionCode > 0 && meta.HostVersionCode > 0 && meta.HostVersionCode < currentHostVersionCode)
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
                continue;
            }

            var targetModuleDir = Path.Combine(packageRoot, moduleId);
            Directory.CreateDirectory(targetModuleDir);

            // Preserve shared BouncyCastle.Cryptography.dll if the delta overlay omits it.
            var existingCrypto = Path.Combine(targetModuleDir, SharedCryptoDll);
            byte[]? preservedCrypto = File.Exists(existingCrypto) && !File.Exists(Path.Combine(dir, SharedCryptoDll))
                ? File.ReadAllBytes(existingCrypto)
                : null;

            foreach (var srcFile in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, srcFile);
                if (string.Equals(rel, OverlayMetaFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var dstFile = Path.Combine(targetModuleDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                File.Copy(srcFile, dstFile, overwrite: true);
            }

            if (preservedCrypto is not null && !File.Exists(existingCrypto))
            {
                File.WriteAllBytes(existingCrypto, preservedCrypto);
            }

            applied++;
        }

        return applied;
    }

    public static void QuarantineOverlays(string overlayDir)
    {
        if (!Directory.Exists(overlayDir))
        {
            return;
        }

        var quarantine = overlayDir + ".quarantine-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        try
        {
            Directory.Move(overlayDir, quarantine);
        }
        catch
        {
            try { Directory.Delete(overlayDir, recursive: true); } catch { }
        }
    }

    private static void ValidateSafeModuleId(string moduleId)
    {
        if (string.IsNullOrWhiteSpace(moduleId) ||
            moduleId.Contains("..", StringComparison.Ordinal) ||
            moduleId.Contains('/') ||
            moduleId.Contains((char)92))
        {
            throw new InvalidOperationException($"非法模块名：{moduleId}");
        }
    }

    private static void ExtractZipSafely(string zipPath, string destinationDir)
    {
        var fullDest = Path.GetFullPath(destinationDir);
        if (!fullDest.EndsWith(Path.DirectorySeparatorChar))
        {
            fullDest += Path.DirectorySeparatorChar;
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var targetPath = Path.GetFullPath(Path.Combine(fullDest, entry.FullName));
            if (!targetPath.StartsWith(fullDest, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Zip 路径越界：{entry.FullName}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static string ComputeSha256Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed record FeedConfig(
    string TailscaleBaseUrl,
    string PublicProxyBaseUrl,
    string DirectFallbackBaseUrl)
{
    public const string DefaultTailscaleBaseUrl = "http://mpt-relay.tail.lixinrui000.cn/mpt/android-modules";
    public const string DefaultPublicProxyBaseUrl = "https://gh-proxy.org/https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/android-modules-latest";
    public const string DefaultDirectFallbackBaseUrl = "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/android-modules-latest";

    public static FeedConfig CreateDefault() => new(
        DefaultTailscaleBaseUrl,
        DefaultPublicProxyBaseUrl,
        DefaultDirectFallbackBaseUrl);

    public FeedConfig Normalize() => new(
        (TailscaleBaseUrl ?? "").Trim(),
        (PublicProxyBaseUrl ?? "").Trim(),
        (DirectFallbackBaseUrl ?? "").Trim());
}

public sealed record ModuleFeedManifest(
    int SchemaVersion,
    string? GeneratedAtUtc,
    long MinHostVersionCode,
    List<ModuleFeedEntry>? Modules);

public sealed record ModuleFeedEntry(
    string Id,
    string? Version,
    string File,
    string Sha256,
    long SizeBytes);

public sealed record OverlayModuleMeta(
    string Id,
    string Version,
    string Sha256,
    long HostVersionCode,
    string SourceLabel,
    string SourceUrl,
    string UpdatedAtUtc);

public sealed record ResolvedFeed(
    string Kind,
    string Label,
    string BaseUrl,
    ModuleFeedManifest Manifest);

public sealed record UpdateApplyOutcome(
    bool Succeeded,
    string SourceLabel,
    string SourceUrl,
    IReadOnlyList<string> UpdatedModules,
    long DownloadedBytes,
    string Message);
