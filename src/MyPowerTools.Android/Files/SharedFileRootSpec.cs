namespace MyPowerTools.Android.Files;

/// <summary>Which app-private directory a shared root is declared against.</summary>
public enum SharedFileRootKind
{
    /// <summary>Relative to <c>Context.FilesDir</c>; declared as <c>&lt;files-path&gt;</c>.</summary>
    Files,

    /// <summary>Relative to <c>Context.CacheDir</c>; declared as <c>&lt;cache-path&gt;</c>.</summary>
    Cache,
}

/// <summary>
/// One directory tree the read-only provider may serve. <see cref="RelativePath"/> is relative to
/// <see cref="SharedFileRootKind"/>'s base directory and is written verbatim into
/// <c>Resources/xml/mpt_shared_files.xml</c>; the provider cannot serve anything outside these
/// trees, and neither can the launcher's own path policy.
/// </summary>
public sealed record SharedFileRoot(string Name, SharedFileRootKind Kind, string RelativePath)
{
    /// <summary>Element name used inside the provider paths resource.</summary>
    public string XmlElementName => Kind == SharedFileRootKind.Files ? "files-path" : "cache-path";

    /// <summary>Value of the resource's <c>path</c> attribute (a directory, so it ends with a separator).</summary>
    public string XmlPath => RelativePath + "/";

    /// <summary>Absolute directory for this root under the given app-private base directory.</summary>
    public string ResolveUnder(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.GetFullPath(Path.Combine(baseDirectory, RelativePath));
    }
}

/// <summary>Result of checking the runtime data layout against the paths resource.</summary>
public enum SharedFileLayoutVerdict
{
    /// <summary>The runtime data root is exactly the folder the paths resource declares.</summary>
    Aligned,

    /// <summary>The app-private directories could not be read.</summary>
    DirectoryUnavailable,

    /// <summary>The runtime data root is not below the app's files directory, so the provider cannot reach it.</summary>
    RuntimeRootOutsideFilesDirectory,

    /// <summary>The runtime data root is below the files directory but not at the declared folder name.</summary>
    RuntimeFolderRenamed,
}

/// <summary>
/// The complete list of directory trees the phone host may hand to a viewer. Deliberately narrow:
/// only the File Transfer receive/staging folders and the host's own share staging area, never the
/// app-private root (which holds settings, secrets, logs and module state) and never external
/// storage. Opening never copies or deletes the file, so these roots are the whole boundary.
/// </summary>
public static class SharedFileRootSpec
{
    /// <summary>Folder <c>RuntimePaths</c> uses below the app-private data directory.</summary>
    public const string RuntimeFolderName = "MyPowerTools";

    /// <summary>Module id whose data directory holds user files on the phone.</summary>
    public const string FileTransferModuleId = "file-transfer";

    private const string ModuleData = RuntimeFolderName + "/state/modules/" + FileTransferModuleId + "/data";

    /// <summary>Durable copies of attachments the user sent; the session keeps them for reopening.</summary>
    public static SharedFileRoot OutgoingAttachments { get; } =
        new("mpt_payload", SharedFileRootKind.Files, ModuleData + "/assistant/payload");

    /// <summary>Attachments received through the assistant conversation.</summary>
    public static SharedFileRoot ReceivedAttachments { get; } =
        new("mpt_inbox", SharedFileRootKind.Files, ModuleData + "/assistant/inbox");

    /// <summary>Files received from a paired device or the relay on this phone.</summary>
    public static SharedFileRoot ReceivedTransfers { get; } =
        new("mpt_incoming", SharedFileRootKind.Files, ModuleData + "/incoming");

    /// <summary>Short-lived copies staged from the system picker before a send.</summary>
    public static SharedFileRoot PickerStaging { get; } =
        new("mpt_outbox", SharedFileRootKind.Files, ModuleData + "/outbox");

    /// <summary>Files the host copied out of a system share intent before activating a tool.</summary>
    public static SharedFileRoot HostShareStaging { get; } =
        new("mpt_shares", SharedFileRootKind.Cache, "shares");

    /// <summary>Every root, in the order the paths resource declares them.</summary>
    public static IReadOnlyList<SharedFileRoot> All { get; } =
    [
        OutgoingAttachments,
        ReceivedAttachments,
        ReceivedTransfers,
        PickerStaging,
        HostShareStaging,
    ];

    /// <summary>Absolute directories for every root under the given app-private base directories.</summary>
    public static IReadOnlyList<string> ResolveAll(string filesDirectory, string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        return All
            .Select(root => root.ResolveUnder(root.Kind == SharedFileRootKind.Files ? filesDirectory : cacheDirectory))
            .ToArray();
    }

    /// <summary>
    /// Checks that the runtime data root (<c>RuntimePaths</c>, derived from the platform's local
    /// application data folder) is exactly <c>&lt;files directory&gt;/MyPowerTools</c>. The paths
    /// resource is a static file and cannot follow a relocated data root, so a mismatch is reported
    /// as a configuration failure instead of every open silently missing.
    /// </summary>
    public static SharedFileLayoutVerdict VerifyRuntimeLayout(string? filesDirectory, string? runtimeRoot, out string relativePath)
    {
        relativePath = "";
        if (string.IsNullOrWhiteSpace(filesDirectory) || string.IsNullOrWhiteSpace(runtimeRoot))
        {
            return SharedFileLayoutVerdict.DirectoryUnavailable;
        }

        string files;
        string runtime;
        try
        {
            files = TrimSeparator(Path.GetFullPath(filesDirectory.Trim()));
            runtime = TrimSeparator(Path.GetFullPath(runtimeRoot.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return SharedFileLayoutVerdict.DirectoryUnavailable;
        }

        var relative = Path.GetRelativePath(files, runtime);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return SharedFileLayoutVerdict.RuntimeRootOutsideFilesDirectory;
        }

        relativePath = relative.Replace('\\', '/');
        return string.Equals(relativePath, RuntimeFolderName, StringComparison.Ordinal)
            ? SharedFileLayoutVerdict.Aligned
            : SharedFileLayoutVerdict.RuntimeFolderRenamed;
    }

    private static string TrimSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
