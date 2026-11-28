using System.Globalization;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>Current state of the user's authorized external download folder on Android.</summary>
public sealed record MobileExternalDirectoryState(
    string Mode,
    string DisplayPath,
    bool PermissionGranted,
    bool SetupCompleted,
    int ExternalFileCount,
    long ExternalBytes,
    string StatusSummary)
{
    public bool NeedsReauthorization =>
        string.Equals(Mode, "saf", StringComparison.OrdinalIgnoreCase) && !PermissionGranted;
}

/// <summary>One manageable local file inside app storage, displayed in the Storage Manager.</summary>
public sealed record MobileStorageFileEntry(
    string Id,
    string FilePath,
    string Name,
    string Category,
    string CategoryLabel,
    long Bytes,
    DateTimeOffset ModifiedAt,
    bool IsProtected,
    string ProtectedReason);

/// <summary>Full WeChat-style storage space breakdown for the phone host.</summary>
public sealed record MobileStorageBreakdown(
    long TotalAppBytes,
    long DeviceFreeBytes,
    long DeviceTotalBytes,
    long TransferFilesBytes,
    int TransferFilesCount,
    long CleanableTransferBytes,
    long CacheBytes,
    int CacheFileCount,
    long LogsAndShadowBytes,
    int LogsAndShadowFileCount,
    long SystemDataBytes,
    MobileExternalDirectoryState ExternalDirectory,
    IReadOnlyList<MobileStorageFileEntry> ManagedFiles)
{
    public long TotalCleanableCacheBytes => CacheBytes + LogsAndShadowBytes;
}

public sealed record MobileStorageCleanupResult(
    bool Succeeded,
    long FreedBytes,
    int RemovedFiles,
    int SkippedProtectedFiles,
    string Message);

public sealed record MobileExternalDirectoryPickResult(
    bool Succeeded,
    bool Cancelled,
    MobileExternalDirectoryState State,
    string Message);

public interface IMobileStorageManager
{
    bool NeedsInitialSetup { get; }
    MobileExternalDirectoryState GetExternalDirectoryState();
    Task<MobileStorageBreakdown> InspectAsync(CancellationToken cancellationToken = default);
    Task<MobileExternalDirectoryPickResult> PickExternalDirectoryAsync(CancellationToken cancellationToken = default);
    MobileExternalDirectoryState UseSystemDownloadsFallback();
    Task<MobileStorageCleanupResult> ClearCacheAsync(CancellationToken cancellationToken = default);
    Task<MobileStorageCleanupResult> ClearManagedTransferFilesAsync(CancellationToken cancellationToken = default);
    Task<MobileStorageCleanupResult> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>Host-registered bridge so MobileShell stays free of Android SDK references.</summary>
public static class MobileStorageBridge
{
    public static IMobileStorageManager? Current { get; set; }
}

public static class MobileByteFormatter
{
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "0 B";
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;
        if (bytes >= gb) return (bytes / gb).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= mb) return (bytes / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= kb) return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
