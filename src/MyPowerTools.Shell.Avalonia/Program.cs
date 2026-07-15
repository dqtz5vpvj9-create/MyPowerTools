using Avalonia;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var startupOptions = ShellStartupOptions.FromArgs(args);
        if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            return RunHostControlSmokeAsync(args, startupOptions).GetAwaiter().GetResult();
        }

        // Remote Notifications is now a Service Unit; the Shell no longer owns single-instance
        // activation forwarding or toast-activation pipes.
        ShellRunnerBootstrapper.EnsureStartedAsync(startupOptions).GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();
    }

    private static async Task<int> RunHostControlSmokeAsync(string[] args, ShellStartupOptions startupOptions)
    {
        if (!string.IsNullOrWhiteSpace(startupOptions.DataRoot))
        {
            Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, startupOptions.DataRoot);
        }

        var timeoutMs = GetIntOption(args, "--timeout-ms", 30000);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var client = HostControlClient.ForDefaultEndpoint();
                var ping = await client.PingAsync(attemptTimeout.Token);
                var dashboard = await client.GetDashboardSnapshotAsync(attemptTimeout.Token);
                var modules = await client.ListModulesAsync(attemptTimeout.Token);
                var commands = await client.ListCommandsAsync(cancellationToken: attemptTimeout.Token);

                Console.WriteLine($"Shell HostControl smoke connected: runner={ping.State} version={ping.RunnerVersion}");
                Console.WriteLine($"Shell HostControl smoke modules={modules.Modules.Count} dashboardCards={dashboard.Cards.Count} commands={commands.Commands.Count}");
                if (args.Contains("--quit-runner", StringComparer.OrdinalIgnoreCase))
                {
                    await client.QuitRunnerAsync(attemptTimeout.Token);
                    Console.WriteLine("Shell HostControl smoke requested Runner shutdown.");
                }

                return 0;
            }
            catch (Exception ex)
            {
                lastError = ex;
                await Task.Delay(500);
            }
        }

        Console.Error.WriteLine($"Shell HostControl smoke failed: {lastError?.Message ?? "timeout"}");
        return 1;
    }

    private static int GetIntOption(string[] args, string name, int defaultValue)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var value))
            {
                return value;
            }
        }

        return defaultValue;
    }
}
