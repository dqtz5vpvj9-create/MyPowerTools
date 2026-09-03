using System.Diagnostics;
using System.Text.Json.Nodes;
using MyPowerTools.Packaging.Ota;
using MyPowerTools.Platform.Abstractions;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.ViewModels;

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
        var bundleRoot = OperatingSystem.IsMacOS()
            ? OtaUpdaterLocator.FindMacBundleRoot(AppContext.BaseDirectory)
            : null;
        var cliFileName = OtaUpdaterLocator.CliFileName();
        var cliPath = OtaUpdaterLocator.ResolveFirstExisting(
            OtaUpdaterLocator.CliCandidates(
                AppContext.BaseDirectory,
                bundleRoot,
                OperatingSystem.IsWindows()));
        if (cliPath is null)
        {
            return $"OTA 更新器不可用：未找到 {cliFileName}。请先安装或修复 MyPowerTools 最新版本。";
        }

        var otaState = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "ota-state");
        Directory.CreateDirectory(otaState);

        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = otaState
        };
        startInfo.ArgumentList.Add("ota");
        startInfo.ArgumentList.Add(command);
        if (string.Equals(command, "apply", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("--yes");
        }

        // Contents/MacOS inside the bundle, the install root on Windows. The Shell runs from a
        // nested helper bundle on macOS, so its parent directory is not the product root.
        var installRoot = OtaUpdaterLocator.ProductRoot(AppContext.BaseDirectory, bundleRoot);
        DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, installRoot);
        using var cts = new CancellationTokenSource(
            TimeSpan.FromMinutes(string.Equals(command, "apply", StringComparison.OrdinalIgnoreCase) ? 30 : 2));

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return $"OTA 更新器启动失败：无法启动 {cliFileName}。";
        }

        try
        {
            var standardOutputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var standardErrorTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(cts.Token)) != null)
                {
                    if (onProgress is not null && TryParseOtaProgress(line, out var progress))
                    {
                        Dispatcher.UIThread.Post(() => onProgress(progress));
                    }
                }
            }, cts.Token);
            var standardOutput = await standardOutputTask;
            await standardErrorTask;
            await process.WaitForExitAsync(cts.Token);
            var output = ExtractOtaJson(standardOutput);
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return $"OTA 更新器退出码 {process.ExitCode}，未返回错误详情。";
            }

            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have already exited.
            }

            return $"OTA 更新器超时：\"{command}\" 命令在规定时间内未完成，进程已终止。";
        }
    }

    internal async Task CheckLastOtaUpdateAsync()
    {
        try
        {
            var otaState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools",
                "ota-state");
            var lastUpdatePath = Path.Combine(otaState, "last-update.json");
            var acknowledgedPath = Path.Combine(otaState, "last-acknowledged-update.json");

            if (!File.Exists(lastUpdatePath))
            {
                return;
            }

            var lastUpdateContent = await File.ReadAllTextAsync(lastUpdatePath);
            if (string.IsNullOrWhiteSpace(lastUpdateContent))
            {
                return;
            }

            if (File.Exists(acknowledgedPath))
            {
                var acknowledgedContent = await File.ReadAllTextAsync(acknowledgedPath);
                if (string.Equals(lastUpdateContent.Trim(), acknowledgedContent.Trim(), StringComparison.Ordinal))
                {
                    return;
                }
            }

            var node = JsonNode.Parse(lastUpdateContent);
            var success = node?["success"]?.GetValue<bool>() ?? false;
            var toVersion = node?["toVersion"]?.GetValue<string>();
            var healthOk = node?["health"]?["ok"]?.GetValue<bool>() ?? false;

            string message;
            InfoBarSeverity severity;
            if (success && !string.IsNullOrWhiteSpace(toVersion))
            {
                severity = healthOk ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                message = healthOk
                    ? $"OTA update to {toVersion} completed successfully."
                    : $"OTA update to {toVersion} completed, but health check did not fully pass.";
            }
            else
            {
                severity = InfoBarSeverity.Error;
                var error = node?["error"]?.GetValue<string>();
                message = !string.IsNullOrWhiteSpace(error)
                    ? $"OTA update failed: {error}"
                    : "OTA update failed. Check ota-state/last-update.json for details.";
            }

            ShowInfoBar(severity, message, autoDismissMs: success ? 8000 : null);
            await File.WriteAllTextAsync(acknowledgedPath, lastUpdateContent);
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Check last OTA update result", ex, "startup");
        }
    }

    internal static string? ExtractOtaJson(string output)
    {
        var trimmed = output.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        try
        {
            JsonNode.Parse(trimmed);
            return trimmed;
        }
        catch (System.Text.Json.JsonException)
        {
        }

        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] != '{')
            {
                continue;
            }

            var candidate = trimmed[index..];
            try
            {
                JsonNode.Parse(candidate);
                return candidate;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return trimmed;
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
