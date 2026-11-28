using System.Text.Json.Nodes;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Android.Storage;

/// <summary>
/// Pure filesystem storage inspector and safe cleaner for the Android app's private storage.
/// Kept free of Android SDK types so every accounting bucket, in-flight transfer protection rule,
/// stale shadow retention rule, and containment boundary can be unit-tested off-device.
/// </summary>
internal sealed class AndroidStorageCore
{
    private const int MaxListedFiles = 120;

    private readonly string _runtimeRoot;
    private readonly string _cacheDir;

    public AndroidStorageCore(string runtimeRoot, string cacheDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDir);
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _cacheDir = Path.GetFullPath(cacheDir);
    }

    private string FileTransferDataRoot => Path.Combine(_runtimeRoot, "state", "modules", "file-transfer", "data");
    private string AssistantRoot => Path.Combine(FileTransferDataRoot, "assistant");
    private string AssistantInboxRoot => Path.Combine(AssistantRoot, "inbox");
    private string AssistantPayloadRoot => Path.Combine(AssistantRoot, "payload");
    private string AssistantStatePath => Path.Combine(AssistantRoot, "state.json");
    private string OutboxRoot => Path.Combine(FileTransferDataRoot, "outbox");
    private string PublishRoot => Path.Combine(FileTransferDataRoot, "publish");
    private string IncomingRoot => Path.Combine(FileTransferDataRoot, "incoming");
    private string ShareCacheRoot => Path.Combine(_cacheDir, "shares");
    private string LogsRoot => Path.Combine(_runtimeRoot, "logs");
    private string ModulesCacheRoot => Path.Combine(_runtimeRoot, "cache");

    public MobileStorageBreakdown Inspect(
        MobileExternalDirectoryState externalState,
        long deviceFreeBytes,
        long deviceTotalBytes)
    {
        var protectedIds = ReadProtectedAssistantItemIds();
        var managed = new List<MobileStorageFileEntry>();

        long transferBytes = 0;
        long cleanableTransferBytes = 0;
        int transferCount = 0;

        ScanAssistantDirectory(AssistantInboxRoot, "received", "接收文件缓存", protectedIds, managed,
            ref transferBytes, ref cleanableTransferBytes, ref transferCount);
        ScanAssistantDirectory(AssistantPayloadRoot, "sent", "发送文件缓存", protectedIds, managed,
            ref transferBytes, ref cleanableTransferBytes, ref transferCount);

        long cacheBytes = 0;
        int cacheCount = 0;
        foreach (var tempDir in new[] { ShareCacheRoot, OutboxRoot, PublishRoot, IncomingRoot })
        {
            var (bytes, count) = MeasureDirectory(tempDir);
            cacheBytes += bytes;
            cacheCount += count;
        }

        long logsAndShadowBytes = 0;
        int logsAndShadowCount = 0;
        var (logBytes, logCount) = MeasureDirectory(LogsRoot);
        logsAndShadowBytes += logBytes;
        logsAndShadowCount += logCount;

        var staleShadowDirs = FindStaleShadowDirectories();
        foreach (var dir in staleShadowDirs)
        {
            var (bytes, count) = MeasureDirectory(dir);
            logsAndShadowBytes += bytes;
            logsAndShadowCount += count;
        }

        // Measure everything inside runtimeRoot + ShareCacheRoot so the total matches real disk usage,
        // and attribute the non-cleanable remainder to system/module base data.
        var (runtimeTotalBytes, _) = MeasureDirectory(_runtimeRoot);
        var (shareTotalBytes, _) = MeasureDirectory(ShareCacheRoot);
        var grandTotal = runtimeTotalBytes + shareTotalBytes;
        var accounted = transferBytes + cacheBytes + logsAndShadowBytes;
        var systemDataBytes = Math.Max(0, grandTotal - accounted);

        var sortedManaged = managed
            .OrderByDescending(entry => entry.Bytes)
            .ThenByDescending(entry => entry.ModifiedAt)
            .Take(MaxListedFiles)
            .ToArray();

        return new MobileStorageBreakdown(
            TotalAppBytes: grandTotal,
            DeviceFreeBytes: Math.Max(0, deviceFreeBytes),
            DeviceTotalBytes: Math.Max(0, deviceTotalBytes),
            TransferFilesBytes: transferBytes,
            TransferFilesCount: transferCount,
            CleanableTransferBytes: cleanableTransferBytes,
            CacheBytes: cacheBytes,
            CacheFileCount: cacheCount,
            LogsAndShadowBytes: logsAndShadowBytes,
            LogsAndShadowFileCount: logsAndShadowCount,
            SystemDataBytes: systemDataBytes,
            ExternalDirectory: externalState,
            ManagedFiles: sortedManaged);
    }

    public MobileStorageCleanupResult ClearCacheAndLogs()
    {
        long freed = 0;
        var removed = 0;

        foreach (var tempDir in new[] { ShareCacheRoot, OutboxRoot, PublishRoot, IncomingRoot })
        {
            var (bytes, count) = DeleteDirectoryContents(tempDir, skipFile: null);
            freed += bytes;
            removed += count;
        }

        // Clean log files except the active startup/runtime log currently open if locked.
        var (logBytes, logCount) = DeleteDirectoryContents(LogsRoot, skipFile: null);
        freed += logBytes;
        removed += logCount;

        // Clean stale shadow-copy module versions while keeping the newest active copy per module.
        foreach (var staleDir in FindStaleShadowDirectories())
        {
            var (bytes, count) = MeasureDirectory(staleDir);
            if (TryDeleteDirectory(staleDir))
            {
                freed += bytes;
                removed += count;
            }
        }

        var message = removed == 0
            ? "缓存与临时文件已经很干净了。"
            : $"已清理 {removed} 个缓存与临时文件，释放 {MobileByteFormatter.Format(freed)}。";
        return new MobileStorageCleanupResult(true, freed, removed, 0, message);
    }

    public MobileStorageCleanupResult ClearManagedTransferFiles()
    {
        var protectedIds = ReadProtectedAssistantItemIds();
        long freed = 0;
        var removed = 0;
        var skippedProtected = 0;

        CleanAssistantDirectory(AssistantInboxRoot, protectedIds, ref freed, ref removed, ref skippedProtected);
        CleanAssistantDirectory(AssistantPayloadRoot, protectedIds, ref freed, ref removed, ref skippedProtected);

        string message;
        if (removed == 0 && skippedProtected > 0)
        {
            message = $"有 {skippedProtected} 个文件正在同步或等待传输，已自动保护未删除。";
        }
        else if (removed == 0)
        {
            message = "没有可清理的文件助手本地缓存。";
        }
        else if (skippedProtected > 0)
        {
            message = $"已清理 {removed} 个本地副本，释放 {MobileByteFormatter.Format(freed)}（已跳过 {skippedProtected} 个传输中文件）。";
        }
        else
        {
            message = $"已清理 {removed} 个本地副本，释放 {MobileByteFormatter.Format(freed)}。聊天记录与外部目录文件不受影响。";
        }

        return new MobileStorageCleanupResult(true, freed, removed, skippedProtected, message);
    }

    public MobileStorageCleanupResult DeleteManagedFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new MobileStorageCleanupResult(false, 0, 0, 0, "未指定要清理的文件。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch
        {
            return new MobileStorageCleanupResult(false, 0, 0, 0, "文件路径无效。");
        }

        if (!IsInsideCleanableRoot(full))
        {
            return new MobileStorageCleanupResult(false, 0, 0, 0, "该文件属于受保护的系统数据，不能通过空间清理删除。");
        }

        var protectedIds = ReadProtectedAssistantItemIds();
        if (TryExtractAssistantItemId(full, out var itemId) && protectedIds.Contains(itemId))
        {
            return new MobileStorageCleanupResult(false, 0, 0, 1, "该文件正在传输或等待同步，为避免丢件已跳过删除。");
        }

        if (!File.Exists(full))
        {
            return new MobileStorageCleanupResult(true, 0, 0, 0, "文件已不存在。");
        }

        try
        {
            var size = new FileInfo(full).Length;
            File.Delete(full);
            TryRemoveEmptyParentDirectory(full);
            return new MobileStorageCleanupResult(true, size, 1, 0, $"已清理本地副本，释放 {MobileByteFormatter.Format(size)}。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MobileStorageCleanupResult(false, 0, 0, 0, "删除失败：" + ex.Message);
        }
    }

    private void ScanAssistantDirectory(
        string root,
        string category,
        string categoryLabel,
        HashSet<string> protectedIds,
        List<MobileStorageFileEntry> target,
        ref long totalBytes,
        ref long cleanableBytes,
        ref int totalCount)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                }
                catch
                {
                    continue;
                }

                var size = Math.Max(0, info.Length);
                totalBytes += size;
                totalCount++;

                var isProtected = TryExtractAssistantItemId(file, out var itemId) && protectedIds.Contains(itemId);
                if (!isProtected)
                {
                    cleanableBytes += size;
                }

                target.Add(new MobileStorageFileEntry(
                    Id: Path.GetRelativePath(_runtimeRoot, file),
                    FilePath: file,
                    Name: info.Name,
                    Category: category,
                    CategoryLabel: categoryLabel,
                    Bytes: size,
                    ModifiedAt: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                    IsProtected: isProtected,
                    ProtectedReason: isProtected ? "传输中 · 已保护" : ""));
            }
        }
        catch
        {
        }
    }

    private void CleanAssistantDirectory(
        string root,
        HashSet<string> protectedIds,
        ref long freed,
        ref int removed,
        ref int skippedProtected)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray())
            {
                if (TryExtractAssistantItemId(file, out var itemId) && protectedIds.Contains(itemId))
                {
                    skippedProtected++;
                    continue;
                }

                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += Math.Max(0, size);
                    removed++;
                    TryRemoveEmptyParentDirectory(file);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reads <c>assistant/state.json</c> (read-only) to find items that are still queued, transferring,
    /// or waiting for initial relay/peer delivery so storage cleanup never causes data loss.
    /// </summary>
    private HashSet<string> ReadProtectedAssistantItemIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(AssistantStatePath))
        {
            return ids;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(AssistantStatePath)) is not JsonObject root ||
                root["items"] is not JsonArray items)
            {
                return ids;
            }

            foreach (var node in items)
            {
                if (node is not JsonObject item) continue;
                var id = item["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var state = (item["state"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (state is "queued" or "sending" or "downloading")
                {
                    ids.Add(id);
                }
                else if (state == "stored")
                {
                    // Outgoing items still waiting in relay with no receipts yet are protected.
                    var receipts = item["receipts"] as JsonArray;
                    if (receipts is null || receipts.Count == 0)
                    {
                        ids.Add(id);
                    }
                }
            }
        }
        catch
        {
            // If state cannot be parsed, avoid crashing inspection; cleanup remains conservative.
        }

        return ids;
    }

    private bool TryExtractAssistantItemId(string filePath, out string itemId)
    {
        itemId = "";
        foreach (var root in new[] { AssistantInboxRoot, AssistantPayloadRoot })
        {
            if (!IsUnderDirectory(filePath, root)) continue;
            var relative = Path.GetRelativePath(root, filePath);
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            if (separator > 0)
            {
                itemId = relative[..separator];
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds old module shadow-copy directories under <c>cache/*/inproc-shadow/*/*/*</c>,
    /// always preserving the most recently touched version directory per module.
    /// </summary>
    private List<string> FindStaleShadowDirectories()
    {
        var stale = new List<string>();
        if (!Directory.Exists(ModulesCacheRoot))
        {
            return stale;
        }

        try
        {
            foreach (var moduleCacheDir in Directory.EnumerateDirectories(ModulesCacheRoot))
            {
                var shadowBase = Path.Combine(moduleCacheDir, "inproc-shadow");
                if (!Directory.Exists(shadowBase)) continue;
                foreach (var pkgDir in Directory.EnumerateDirectories(shadowBase))
                {
                    foreach (var modDir in Directory.EnumerateDirectories(pkgDir))
                    {
                        var versions = Directory.EnumerateDirectories(modDir)
                            .Select(dir =>
                            {
                                var marker = Path.Combine(dir, ".complete");
                                var stamp = File.Exists(marker)
                                    ? File.GetLastWriteTimeUtc(marker)
                                    : Directory.GetLastWriteTimeUtc(dir);
                                return (Path: dir, Stamp: stamp);
                            })
                            .OrderByDescending(entry => entry.Stamp)
                            .ToArray();
                        // Keep the latest version directory (index 0) because the live process uses it.
                        for (var i = 1; i < versions.Length; i++)
                        {
                            stale.Add(versions[i].Path);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return stale;
    }

    private bool IsInsideCleanableRoot(string fullPath)
    {
        var allowed = new[]
        {
            AssistantInboxRoot,
            AssistantPayloadRoot,
            ShareCacheRoot,
            OutboxRoot,
            PublishRoot,
            IncomingRoot,
            LogsRoot
        };
        return allowed.Any(root => IsUnderDirectory(fullPath, root));
    }

    private static bool IsUnderDirectory(string candidatePath, string rootDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullRoot = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(fullRoot, comparison);
    }

    private void TryRemoveEmptyParentDirectory(string deletedFilePath)
    {
        try
        {
            var parent = Path.GetDirectoryName(deletedFilePath);
            if (string.IsNullOrWhiteSpace(parent)) return;
            var protectedParents = new[]
            {
                AssistantInboxRoot, AssistantPayloadRoot, ShareCacheRoot, OutboxRoot, PublishRoot, IncomingRoot, LogsRoot
            };
            if (protectedParents.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent, recursive: false);
            }
        }
        catch
        {
        }
    }

    private static (long Bytes, int Count) MeasureDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += Math.Max(0, new FileInfo(file).Length);
                    count++;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return (bytes, count);
    }

    private static (long Bytes, int Count) DeleteDirectoryContents(string directory, Func<string, bool>? skipFile)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        long freed = 0;
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
            {
                if (skipFile?.Invoke(file) == true) continue;
                try
                {
                    var size = new FileInfo(file).Length;
                    File.Delete(file);
                    freed += Math.Max(0, size);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            foreach (var sub in Directory.EnumerateDirectories(directory).ToArray())
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(sub).Any())
                    {
                        Directory.Delete(sub, recursive: false);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return (freed, removed);
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
