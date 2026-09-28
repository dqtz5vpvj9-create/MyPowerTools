using System.Diagnostics;
using LocalLagCleaner.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerAutomaticCareTests
{
    [Fact]
    public void Native_hard_fault_capture_matches_live_process_identity()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return;
        var capture = ProcessHardFaultProbe.Capture();
        Assert.Equal("", capture.Error);
        using var process = Process.GetCurrentProcess();
        var counter = capture.Counters[process.Id];
        Assert.InRange(Math.Abs(DateTime.FromFileTimeUtc(counter.CreateTime).Ticks - process.StartTime.ToUniversalTime().Ticks), 0, TimeSpan.TicksPerMillisecond);
        Assert.True(capture.Counters.Count > 10);
    }

    [Fact]
    public void Hard_fault_rates_reject_recycled_pids_and_counter_resets()
    {
        var start = DateTimeOffset.UtcNow;
        var before = new ProcessHardFaultCapture(start, new Dictionary<int, ProcessHardFaultCounter>
        { [11] = new(11, 100, 10), [12] = new(12, 100, 10), [13] = new(13, 100, 100) }, "");
        var after = new ProcessHardFaultCapture(start.AddSeconds(5), new Dictionary<int, ProcessHardFaultCounter>
        { [11] = new(11, 100, 35), [12] = new(12, 200, 1000), [13] = new(13, 100, 5) }, "");
        var row = Assert.Single(ProcessHardFaultProbe.Compare(before, after, new Dictionary<int, string> { [11] = "sample" }));
        Assert.Equal(11, row.ProcessId);
        Assert.Equal(5, row.FaultsPerSecond);
        Assert.Equal(25U, row.Faults);
    }

    [Fact]
    public void Temporary_cleanup_only_deletes_old_unlocked_temporary_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-cleaner-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "old.tmp", "old.temp", "old.txt", "new.tmp", "locked.tmp" })
            {
                var path = Path.Combine(root, name);
                File.WriteAllText(path, "12345");
                if (name != "new.tmp")
                {
                    File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
                }
            }
            using var locked = new FileStream(Path.Combine(root, "locked.tmp"), FileMode.Open, FileAccess.Read, FileShare.None);
            var result = ExpiredTemporaryFileCleaner.Clean(root, DateTimeOffset.UtcNow);
            Assert.Equal(2, result.DeletedFiles);
            Assert.Equal(10, result.ReleasedBytes);
            Assert.False(File.Exists(Path.Combine(root, "old.tmp")));
            Assert.True(File.Exists(Path.Combine(root, "old.txt")));
            Assert.True(File.Exists(Path.Combine(root, "new.tmp")));
            Assert.True(File.Exists(Path.Combine(root, "locked.tmp")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
