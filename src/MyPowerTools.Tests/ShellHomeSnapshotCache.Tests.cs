using MyPowerTools.Protocol.HostControl.V1;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellHomeSnapshotCacheTests
{
    [Fact]
    public async Task Snapshot_round_trips_tool_descriptors_and_fingerprint()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-home-snapshot-test", Guid.NewGuid().ToString("N"));
        try
        {
            var tool = new ToolDescriptor
            {
                ToolId = "sample.tool",
                Title = "Sample tool",
                State = "ready",
                Availability = "available"
            };
            var snapshot = ShellHomeSnapshotCache.Create([tool]);

            await ShellHomeSnapshotCache.WriteAsync(snapshot, dataRoot);
            var restored = await ShellHomeSnapshotCache.TryReadAsync(dataRoot);

            Assert.NotNull(restored);
            Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
            Assert.Collection(restored.Tools, item => Assert.Equal("sample.tool", item.ToolId));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Corrupt_snapshot_is_treated_as_a_cache_miss()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-home-snapshot-test", Guid.NewGuid().ToString("N"));
        try
        {
            var path = ShellHomeSnapshotCache.SnapshotPath(dataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [0xFF, 0x01, 0x02]);

            Assert.Null(await ShellHomeSnapshotCache.TryReadAsync(dataRoot));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Empty_tool_catalog_is_a_valid_snapshot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-home-snapshot-test", Guid.NewGuid().ToString("N"));
        try
        {
            var snapshot = ShellHomeSnapshotCache.Create([]);

            await ShellHomeSnapshotCache.WriteAsync(snapshot, dataRoot);
            var restored = await ShellHomeSnapshotCache.TryReadAsync(dataRoot);

            Assert.NotNull(restored);
            Assert.Empty(restored.Tools);
            Assert.Equal(snapshot.Fingerprint, restored.Fingerprint);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }
}
