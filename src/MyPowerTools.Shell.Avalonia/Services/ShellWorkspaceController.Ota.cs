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
            return null;
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
            return null;
        }

        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(standardOutput)
            ? (string.IsNullOrWhiteSpace(standardError) ? null : standardError)
            : standardOutput;
    }
}
