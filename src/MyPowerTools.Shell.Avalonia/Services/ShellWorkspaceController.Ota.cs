using System.Diagnostics;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private async Task<string?> RunOtaCliAsync(string command)
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

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = string.IsNullOrWhiteSpace(standardOutput) ? standardError : standardOutput;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            return $"OTA 更新器退出码 {process.ExitCode}，未返回错误详情。";
        }

        return string.IsNullOrWhiteSpace(output) ? null : output;
    }
}
