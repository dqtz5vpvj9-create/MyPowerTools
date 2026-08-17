using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed record OtaDownloadProgress(
    string File,
    long ReceivedBytes,
    long? TotalBytes,
    int? Percent)
{
    public double PercentValue => TotalBytes is > 0
        ? Math.Clamp(ReceivedBytes * 100.0 / TotalBytes.Value, 0, 100)
        : 0;

    public string Text => TotalBytes is > 0
        ? $"{FormatBytes(ReceivedBytes)} / {FormatBytes(TotalBytes.Value)}"
        : FormatBytes(ReceivedBytes);

    private static string FormatBytes(long bytes)
    {
        const double mb = 1024.0 * 1024.0;
        return bytes >= mb
            ? $"{bytes / mb:0.0} MB"
            : $"{bytes / 1024.0:0} KB";
    }
}

public sealed partial class ShellWorkspaceController
{
    private async Task<string?> RunOtaCliAsync(
        string command,
        Action<OtaDownloadProgress>? onProgress = null)
    {
        var cliPath = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Cli",
            "MyPowerTools.Cli.exe");
        if (!File.Exists(cliPath))
        {
            return "OTA 更新器不可用：未找到 Cli\\MyPowerTools.Cli.exe。请先安装或修复 MyPowerTools 最新版本。";
        }

        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("ota");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return "OTA 更新器启动失败：无法启动 MyPowerTools.Cli.exe。";
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                if (onProgress is not null && TryParseOtaProgress(line, out var progress))
                {
                    onProgress(progress);
                }
            }
        });
        var standardOutput = await standardOutputTask;
        await standardErrorTask;
        await process.WaitForExitAsync();
        var output = standardOutput;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            return $"OTA 更新器退出码 {process.ExitCode}，未返回错误详情。";
        }

        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    private static bool TryParseOtaProgress(
        string line,
        out OtaDownloadProgress progress)
    {
        progress = null!;
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            var node = JsonNode.Parse(line);
            if (node is null
                || node["event"]?.GetValue<string>() != "download-progress")
            {
                return false;
            }

            progress = new OtaDownloadProgress(
                node["file"]?.GetValue<string>() ?? "",
                node["received"]?.GetValue<long>() ?? 0,
                node["total"]?.GetValue<long>(),
                node["percent"]?.GetValue<int>());
            return true;
        }
        catch
        {
            return false;
        }
    }
}
