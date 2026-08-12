using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Tests;

public sealed class LogsPageTests
{
    [Fact]
    public void LocalLogFileReader_reads_jsonl_and_text_logs()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-logs-test", Guid.NewGuid().ToString("N"));
        var logsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(
            Path.Combine(logsDir, "runner.jsonl"),
            "{\"Time\":\"2026-08-11T10:35:10.4798478+00:00\",\"ModuleId\":\"runner\",\"Level\":\"info\",\"Message\":\"started\"}\n" +
            "{\"Time\":\"2026-08-11T10:35:11.0000000+00:00\",\"ModuleId\":\"runner\",\"Level\":\"error\",\"Message\":\"boom\"}\n");
        File.WriteAllText(
            Path.Combine(logsDir, "shell-faults.log"),
            "2026-08-12T03:04:38.1585965+00:00\tpage-load\tLoadPackagesPageAsync [unexpected-page-failure]\tRpcException\tboom\n");

        var snapshot = LocalLogFileReader.Read(root);

        Assert.Contains("runner", snapshot.ModuleIds);
        Assert.Contains("shell-faults", snapshot.ModuleIds);
        Assert.Contains(snapshot.Lines, line => line.Message.Contains("started"));
        Assert.Contains(snapshot.Lines, line => line.Message.Contains("boom"));
        Assert.Contains(snapshot.Lines, line => line.Message.Contains("LoadPackagesPageAsync"));
        Assert.All(snapshot.Lines, line => Assert.False(string.IsNullOrWhiteSpace(line.ModuleId)));
    }

    [Fact]
    public void LocalLogFileReader_returns_empty_snapshot_when_directory_missing()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-logs-test", Guid.NewGuid().ToString("N"));

        var snapshot = LocalLogFileReader.Read(root);

        Assert.Empty(snapshot.ModuleIds);
        Assert.Empty(snapshot.Lines);
    }

    [Fact]
    public void LogsViewModel_filters_by_level_and_search_text()
    {
        var lines = new[]
        {
            new LogLineViewModel("12:00:00", "info", "hello world"),
            new LogLineViewModel("12:00:01", "error", "boom detail"),
            new LogLineViewModel("12:00:02", "warning", "careful now")
        };
        var viewModel = new LogsViewModel("runner", [], lines);

        Assert.Equal(3, viewModel.Lines.Count);

        viewModel.LevelFilter = "Error";
        Assert.Single(viewModel.Lines);
        Assert.Equal("boom detail", viewModel.Lines[0].Message);

        viewModel.LevelFilter = "All";
        viewModel.SearchText = "careful";
        Assert.Single(viewModel.Lines);
        Assert.Equal("warning", viewModel.Lines[0].Level);
    }
}
