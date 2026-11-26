using A = global::Android;

namespace MyPowerTools.Android.Files;

/// <summary>
/// Resolves the concrete directories the phone host may share. The list is derived from the app's
/// own private directories plus <see cref="SharedFileRootSpec"/>, never from a caller-supplied path,
/// and the runtime data layout is verified once so a relocated module data root is reported as a
/// configuration failure instead of every open quietly missing the file.
/// </summary>
internal static class MptSharedFileRoots
{
    private static readonly object Gate = new();
    private static SharedFileAccessPolicy? _policy;

    /// <summary>The process-wide policy; the directories cannot change while the process lives.</summary>
    internal static SharedFileAccessPolicy Policy(A.Content.Context context)
    {
        var cached = Volatile.Read(ref _policy);
        if (cached is not null)
        {
            return cached;
        }

        lock (Gate)
        {
            return _policy ??= Create(context);
        }
    }

    internal static SharedFileAccessPolicy Create(A.Content.Context context)
    {
        var files = FilesDirectory(context);
        var cache = CacheDirectory(context);
        VerifyRuntimeLayout(files);
        return new SharedFileAccessPolicy(SharedFileRootSpec.ResolveAll(files, cache));
    }

    /// <summary>Provider authority for the installed package; must match the merged manifest entry.</summary>
    internal static string Authority(A.Content.Context context)
    {
        try
        {
            return MptSharedFileContract.AuthorityFor(context.PackageName);
        }
        catch (ArgumentException ex)
        {
            throw MptFileOpenErrors.ProviderMisconfigured("包名不可用。", ex);
        }
    }

    /// <summary>Root name that contains an accepted file, for logs; never the path itself.</summary>
    internal static string? RootName(A.Content.Context context, string file)
    {
        var roots = SharedFileRootSpec.All;
        var directories = SharedFileRootSpec.ResolveAll(FilesDirectory(context), CacheDirectory(context));
        for (var index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            if (string.Equals(file, directory, StringComparison.Ordinal) ||
                (file.Length > directory.Length &&
                 file.StartsWith(directory, StringComparison.Ordinal) &&
                 file[directory.Length] == '/'))
            {
                return roots[index].Name;
            }
        }

        return null;
    }

    private static string FilesDirectory(A.Content.Context context) =>
        context.FilesDir?.AbsolutePath is { Length: > 0 } files
            ? files
            : throw MptFileOpenErrors.ProviderMisconfigured("应用私有文件目录不可用。");

    private static string CacheDirectory(A.Content.Context context) =>
        context.CacheDir?.AbsolutePath is { Length: > 0 } cache
            ? cache
            : throw MptFileOpenErrors.ProviderMisconfigured("应用私有缓存目录不可用。");

    private static void VerifyRuntimeLayout(string filesDirectory)
    {
        string runtimeRoot;
        try
        {
            // RuntimePaths composes its root from this folder, and the paths resource is relative to
            // Context.FilesDir. Both must describe the same directory.
            runtimeRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                SharedFileRootSpec.RuntimeFolderName);
        }
        catch (Exception ex)
        {
            throw MptFileOpenErrors.ProviderMisconfigured("无法确定运行数据目录。", ex);
        }

        var verdict = SharedFileRootSpec.VerifyRuntimeLayout(filesDirectory, runtimeRoot, out var relativePath);
        switch (verdict)
        {
            case SharedFileLayoutVerdict.Aligned:
                return;
            case SharedFileLayoutVerdict.RuntimeRootOutsideFilesDirectory:
                throw MptFileOpenErrors.ProviderMisconfigured("模块数据目录不在应用私有文件目录内。");
            case SharedFileLayoutVerdict.RuntimeFolderRenamed:
                throw MptFileOpenErrors.ProviderMisconfigured(
                    $"模块数据目录名为 {relativePath}，与路径资源声明的 {SharedFileRootSpec.RuntimeFolderName} 不一致。");
            default:
                throw MptFileOpenErrors.ProviderMisconfigured("应用私有目录不可用。");
        }
    }
}
