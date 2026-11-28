using MyPowerTools.Platform.Android;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using A = global::Android;

namespace MyPowerTools.Android.Storage;

/// <summary>
/// Android host implementation of <see cref="IMobileStorageManager"/>. Bridges SAF directory
/// authorization (<see cref="MainActivity"/>), external directory inspection, device free-space
/// measurement (<see cref="StatFs"/>), and <see cref="AndroidStorageCore"/> cleanup into the
/// platform-neutral mobile shell.
/// </summary>
internal sealed class AndroidStorageManager : IMobileStorageManager
{
    private readonly A.Content.Context _context;
    private readonly AndroidStorageCore _core;
    private readonly Func<CancellationToken, Task<string?>> _pickTreeUriAsync;

    public AndroidStorageManager(
        A.Content.Context context,
        string runtimeRoot,
        string cacheDir,
        Func<CancellationToken, Task<string?>> pickTreeUriAsync)
    {
        _context = context.ApplicationContext ?? context;
        _core = new AndroidStorageCore(runtimeRoot, cacheDir);
        _pickTreeUriAsync = pickTreeUriAsync;
    }

    public bool NeedsInitialSetup => !AndroidExternalStoragePreferences.Load().SetupCompleted;

    public MobileExternalDirectoryState GetExternalDirectoryState()
    {
        var config = AndroidExternalStoragePreferences.Load();
        if (config.Mode == AndroidExternalStorageConfig.ModeSaf && config.TreeUri.Length > 0)
        {
            var granted = AndroidDownloadsService.HasPersistedWritePermission(_context, config.TreeUri);
            var (count, bytes, accessible) = granted
                ? AndroidDownloadsService.InspectSafTree(_context, config.TreeUri)
                : (0, 0L, false);
            return new MobileExternalDirectoryState(
                Mode: config.Mode,
                DisplayPath: config.DisplayPath,
                PermissionGranted: granted && accessible,
                SetupCompleted: config.SetupCompleted,
                ExternalFileCount: count,
                ExternalBytes: bytes,
                StatusSummary: granted && accessible
                    ? "已授权外部目录 · 系统文件管理器直接可见"
                    : "外部目录授权已失效 · 请重新选择目录");
        }

        var (msCount, msBytes) = AndroidDownloadsService.InspectMediaStoreDownloads(_context);
        return new MobileExternalDirectoryState(
            Mode: config.SetupCompleted ? AndroidExternalStorageConfig.ModeMediaStore : AndroidExternalStorageConfig.ModeUnset,
            DisplayPath: AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath,
            PermissionGranted: true,
            SetupCompleted: config.SetupCompleted,
            ExternalFileCount: msCount,
            ExternalBytes: msBytes,
            StatusSummary: config.SetupCompleted
                ? "使用系统下载目录（Download/MPT）"
                : "尚未授权外部目录 · 建议选择常用下载文件夹");
    }

    public Task<MobileStorageBreakdown> InspectAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var external = GetExternalDirectoryState();
            var (freeBytes, totalBytes) = ReadDeviceSpace();
            return _core.Inspect(external, freeBytes, totalBytes);
        }, cancellationToken);
    }

    public async Task<MobileExternalDirectoryPickResult> PickExternalDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var pickedUri = await _pickTreeUriAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(pickedUri))
            {
                var current = GetExternalDirectoryState();
                return new MobileExternalDirectoryPickResult(false, Cancelled: true, current, "已取消目录选择。");
            }

            var display = AndroidExternalStoragePreferences.FormatTreeUriDisplayPath(pickedUri);
            AndroidExternalStoragePreferences.Save(new AndroidExternalStorageConfig(
                Mode: AndroidExternalStorageConfig.ModeSaf,
                TreeUri: pickedUri,
                DisplayPath: display,
                SetupCompleted: true,
                UpdatedAtUtc: DateTimeOffset.UtcNow));

            var updated = GetExternalDirectoryState();
            AndroidStartupLog.Info("storage", $"Authorized external directory: {display}");
            return new MobileExternalDirectoryPickResult(true, Cancelled: false, updated, $"已授权保存目录：{display}");
        }
        catch (OperationCanceledException)
        {
            return new MobileExternalDirectoryPickResult(false, Cancelled: true, GetExternalDirectoryState(), "已取消目录选择。");
        }
        catch (Exception ex)
        {
            AndroidStartupLog.Error("storage-pick", ex);
            return new MobileExternalDirectoryPickResult(false, Cancelled: false, GetExternalDirectoryState(), "目录授权失败：" + ex.Message);
        }
    }

    public MobileExternalDirectoryState UseSystemDownloadsFallback()
    {
        AndroidExternalStoragePreferences.Save(new AndroidExternalStorageConfig(
            Mode: AndroidExternalStorageConfig.ModeMediaStore,
            TreeUri: "",
            DisplayPath: AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath,
            SetupCompleted: true,
            UpdatedAtUtc: DateTimeOffset.UtcNow));
        AndroidStartupLog.Info("storage", "Selected system Downloads/MPT fallback");
        return GetExternalDirectoryState();
    }

    public Task<MobileStorageCleanupResult> ClearCacheAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _core.ClearCacheAndLogs();
        }, cancellationToken);

    public Task<MobileStorageCleanupResult> ClearManagedTransferFilesAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _core.ClearManagedTransferFiles();
        }, cancellationToken);

    public Task<MobileStorageCleanupResult> DeleteFileAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _core.DeleteManagedFile(filePath);
        }, cancellationToken);

    private (long FreeBytes, long TotalBytes) ReadDeviceSpace()
    {
        try
        {
            var path = _context.FilesDir?.AbsolutePath ?? "/data";
            using var stat = new A.OS.StatFs(path);
            return (stat.AvailableBytes, stat.TotalBytes);
        }
        catch
        {
            return (0, 0);
        }
    }
}
