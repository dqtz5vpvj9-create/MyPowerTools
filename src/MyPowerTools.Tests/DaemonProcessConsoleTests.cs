using MyPowerTools.Ipc;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class DaemonProcessConsoleTests
{
    [Fact]
    public void Jsonl_host_log_is_readable_by_the_logs_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-daemon-log", Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logsDir);

        using (var writer = new JsonlLogWriter(Path.Combine(logsDir, "runner.jsonl"), "runner"))
        {
            writer.Append("info", "started");
            writer.Append("error", "boom");
            writer.Append("warn", "careful");
        }

        var snapshot = LocalLogFileReader.Read(root);

        Assert.Contains("runner", snapshot.ModuleIds);
        Assert.Contains(snapshot.Lines, line => line.Message == "started" && line.Level == "info");
        Assert.Contains(snapshot.Lines, line => line.Message == "boom" && line.Level == "error");
        Assert.Contains(snapshot.Lines, line => line.Message == "careful" && line.Level == "warning");
    }

    [Theory]
    [InlineData("fail: Microsoft.Hosting.Lifetime[0] crashed", "error")]
    [InlineData("MyPowerTools.Runner tray failed: boom", "error")]
    [InlineData("warn: Microsoft.AspNetCore[0] slow", "warning")]
    [InlineData("MyPowerTools.Runner serving HostControl", "info")]
    public void DetectLevel_maps_host_and_framework_lines(string message, string expected)
    {
        Assert.Equal(expected, JsonlLogWriter.DetectLevel(message));
    }
}
