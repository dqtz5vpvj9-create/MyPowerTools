using System.Diagnostics;
using MyPowerTools.Shell.Avalonia;

namespace MyPowerTools.Tests;

internal static class VisualTestProcess
{
    public static string WriteSnapshotSet(
        string outputDirectory,
        string theme,
        string size,
        string density,
        string surface,
        bool productFoundation = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(ShellRealScreenshotWriter).Assembly.Location);
        startInfo.ArgumentList.Add("shell-snapshot");
        startInfo.ArgumentList.Add("--fixture-only");
        if (productFoundation)
        {
            startInfo.ArgumentList.Add("--product-foundation");
        }

        AddOption(startInfo, "--theme", theme);
        AddOption(startInfo, "--size", size);
        AddOption(startInfo, "--density", density);
        AddOption(startInfo, "--surface", surface);
        AddOption(startInfo, "--out", outputDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the visual test process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
        var output = standardOutput.GetAwaiter().GetResult();
        var error = standardError.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Visual test process exited with code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }

        return Path.Combine(outputDirectory, "shell-real-screenshot-manifest.json");
    }

    private static void AddOption(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }
}
