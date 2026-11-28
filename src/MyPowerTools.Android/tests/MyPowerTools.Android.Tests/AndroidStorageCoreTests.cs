using MyPowerTools.Android.Storage;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Android.Tests;

public sealed class AndroidStorageCoreTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mpt-storage-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _runtimeRoot;
    private readonly string _cacheDir;

    public AndroidStorageCoreTests()
    {
        _runtimeRoot = Path.Combine(_tempRoot, "files", "MyPowerTools");
        _cacheDir = Path.Combine(_tempRoot, "cache");
        Directory.CreateDirectory(_runtimeRoot);
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private static MobileExternalDirectoryState DefaultExternal() => new(
        "saf", "内部存储 / Download / MPT", true, true, 2, 4096, "已授权外部目录");

    private string WriteBytes(string relativeUnderTemp, int size)
    {
        var full = Path.Combine(_tempRoot, relativeUnderTemp);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[size]);
        return full;
    }

    [Fact]
    public void Inspect_accounts_for_all_buckets_and_protects_inflight_transfers()
    {
        // Completed received file (cleanable)
        WriteBytes("files/MyPowerTools/state/modules/file-transfer/data/assistant/inbox/item-done/photo.jpg", 1200);
        // Outgoing file still sending (protected)
        WriteBytes("files/MyPowerTools/state/modules/file-transfer/data/assistant/payload/item-sending/video.mp4", 3000);
        // Assistant state marking item-sending as sending and item-done as available
        var statePath = Path.Combine(_runtimeRoot, "state", "modules", "file-transfer", "data", "assistant", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, """
        {
          "items": [
            { "id": "item-done", "state": "available", "receipts": [] },
            { "id": "item-sending", "state": "sending", "receipts": [] }
          ]
        }
        """);

        // Temp caches
        WriteBytes("cache/shares/s1/shared.pdf", 500);
        WriteBytes("files/MyPowerTools/state/modules/file-transfer/data/outbox/temp.bin", 400);
        // Logs
        WriteBytes("files/MyPowerTools/logs/startup.log", 250);
        // Module shadow copies: older (stale) + newer (active, must stay in SystemData)
        var oldShadow = WriteBytes("files/MyPowerTools/cache/file-transfer/inproc-shadow/file-transfer/file-transfer/v1/mod.dll", 700);
        var oldMarker = WriteBytes("files/MyPowerTools/cache/file-transfer/inproc-shadow/file-transfer/file-transfer/v1/.complete", 10);
        File.SetLastWriteTimeUtc(oldMarker, DateTime.UtcNow.AddHours(-2));
        var newShadow = WriteBytes("files/MyPowerTools/cache/file-transfer/inproc-shadow/file-transfer/file-transfer/v2/mod.dll", 900);
        var newMarker = WriteBytes("files/MyPowerTools/cache/file-transfer/inproc-shadow/file-transfer/file-transfer/v2/.complete", 10);
        File.SetLastWriteTimeUtc(newMarker, DateTime.UtcNow);
        // Bundled module (system data)
        WriteBytes("files/MyPowerTools/bundled-modules/file-transfer/module.json", 150);

        var core = new AndroidStorageCore(_runtimeRoot, _cacheDir);
        var breakdown = core.Inspect(DefaultExternal(), 10_000_000, 64_000_000);

        Assert.Equal(4200, breakdown.TransferFilesBytes);
        Assert.Equal(2, breakdown.TransferFilesCount);
        Assert.Equal(1200, breakdown.CleanableTransferBytes);
        Assert.Equal(900, breakdown.CacheBytes);
        Assert.Equal(2, breakdown.CacheFileCount);
        Assert.Equal(250 + 710, breakdown.LogsAndShadowBytes);
        Assert.True(breakdown.SystemDataBytes >= 910 + 150);
        Assert.Equal(2, breakdown.ManagedFiles.Count);
        Assert.True(breakdown.ManagedFiles[0].IsProtected);
        Assert.Equal("video.mp4", breakdown.ManagedFiles[0].Name);
        Assert.False(breakdown.ManagedFiles[1].IsProtected);
        Assert.Equal("photo.jpg", breakdown.ManagedFiles[1].Name);

        // Cleaning cache removes temp + logs + stale v1 shadow, but preserves active v2 shadow & state.json.
        var cacheCleanup = core.ClearCacheAndLogs();
        Assert.True(cacheCleanup.Succeeded);
        Assert.False(File.Exists(oldShadow));
        Assert.True(File.Exists(newShadow));
        Assert.True(File.Exists(statePath));

        // Cleaning transfer files removes photo.jpg but protects video.mp4.
        var transferCleanup = core.ClearManagedTransferFiles();
        Assert.True(transferCleanup.Succeeded);
        Assert.Equal(1, transferCleanup.RemovedFiles);
        Assert.Equal(1, transferCleanup.SkippedProtectedFiles);
        Assert.Equal(1200, transferCleanup.FreedBytes);
        Assert.True(File.Exists(Path.Combine(_runtimeRoot, "state", "modules", "file-transfer", "data", "assistant", "payload", "item-sending", "video.mp4")));
    }

    [Fact]
    public void DeleteManagedFile_rejects_system_files_and_protected_inflight_files()
    {
        var secretFile = WriteBytes("files/MyPowerTools/state/secrets.json", 64);
        var activeFile = WriteBytes("files/MyPowerTools/state/modules/file-transfer/data/assistant/payload/item-q/a.bin", 512);
        var statePath = Path.Combine(_runtimeRoot, "state", "modules", "file-transfer", "data", "assistant", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, """{ "items": [ { "id": "item-q", "state": "queued" } ] }""");

        var core = new AndroidStorageCore(_runtimeRoot, _cacheDir);

        var sysResult = core.DeleteManagedFile(secretFile);
        Assert.False(sysResult.Succeeded);
        Assert.True(File.Exists(secretFile));

        var protectedResult = core.DeleteManagedFile(activeFile);
        Assert.False(protectedResult.Succeeded);
        Assert.Equal(1, protectedResult.SkippedProtectedFiles);
        Assert.True(File.Exists(activeFile));
    }
}
