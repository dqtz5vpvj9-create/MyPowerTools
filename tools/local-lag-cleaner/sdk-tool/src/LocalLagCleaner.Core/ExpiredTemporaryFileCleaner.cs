namespace LocalLagCleaner.MyPowerTools;

public sealed record TemporaryCleanupResult(int DeletedFiles, long ReleasedBytes, int SkippedFiles);

public static class ExpiredTemporaryFileCleaner
{
    public static TemporaryCleanupResult Clean(string rootDirectory, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (root.Length < 4 || !Directory.Exists(root) || IsReparsePoint(root))
            throw new InvalidOperationException("临时目录不可用。");
        var prefix = root + Path.DirectorySeparatorChar;
        var directories = new Stack<string>();
        directories.Push(root);
        var visited = 0;
        var removed = 0;
        var skipped = 0;
        long bytes = 0;
        while (directories.Count > 0 && visited < 20_000 && bytes < 2L * 1024 * 1024 * 1024)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (++visited > 20_000 || bytes >= 2L * 1024 * 1024 * 1024) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var fullPath = Path.GetFullPath(entry);
                        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || IsReparsePoint(fullPath)) continue;
                        if (Directory.Exists(fullPath)) { directories.Push(fullPath); continue; }
                        var extension = Path.GetExtension(fullPath);
                        if (!extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".temp", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(fullPath);
                        if (info.LastWriteTimeUtc >= now.UtcDateTime.AddDays(-7) || info.CreationTimeUtc >= now.UtcDateTime.AddDays(-7) || info.Length > 512L * 1024 * 1024) continue;
                        // Exclusive access rejects in-use files; DeleteOnClose keeps deletion tied to this handle.
                        long length;
                        using (var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose))
                        { length = file.Length; }
                        if (!File.Exists(fullPath)) { bytes += length; removed++; }
                        else skipped++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new(removed, bytes, skipped);
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
