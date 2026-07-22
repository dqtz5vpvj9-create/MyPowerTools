using Avalonia;
using MyPowerTools.Abstractions;
using MyPowerTools.HostControl;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ShellStartupDiagnostics.Mark("managed-entry");
        var startupOptions = ShellStartupOptions.FromArgs(args);
        if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            return RunHostControlSmokeAsync(args, startupOptions).GetAwaiter().GetResult();
        }

        var toolActivation = ToolActivationProtocol.Parse(args);
        var prewarmShell = args.Contains("--prewarm", StringComparer.OrdinalIgnoreCase);
        var shutdownShell = args.Contains("--shutdown-shell", StringComparer.OrdinalIgnoreCase);
        using var instanceLock = ShellInstanceLock.Acquire();
        if (!instanceLock.Acquired)
        {
            var request = shutdownShell
                ? ShellActivationRequest.Shutdown
                : toolActivation is not null
                    ? ShellActivationRequest.ForTool(toolActivation)
                    : prewarmShell
                        ? ShellActivationRequest.PrewarmShell
                        : ShellActivationRequest.FocusShell;
            return ShellActivationPipe.TryForwardAsync(request).GetAwaiter().GetResult() ? 0 : 2;
        }

        if (shutdownShell)
        {
            return 0;
        }

        App.StartupActivationRequest = toolActivation is not null
            ? ShellActivationRequest.ForTool(toolActivation)
            : prewarmShell
                ? ShellActivationRequest.PrewarmShell
                : null;
        var opensHome = toolActivation is null && !startupOptions.FocusCommandPalette;
        var cachedHomeSnapshotTask = opensHome
            ? ShellHomeSnapshotCache.TryReadAsync(startupOptions.DataRoot)
            : Task.FromResult<ShellHomeSnapshot?>(null);
        App.CachedHomeSnapshotTask = cachedHomeSnapshotTask;
        App.RunnerBootstrapTask = ShellRunnerBootstrapper.EnsureStartedAsync(
            startupOptions,
            loadHomeTools: opensHome);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect();
    }

    private static async Task<int> RunHostControlSmokeAsync(string[] args, ShellStartupOptions startupOptions)
    {
        if (!string.IsNullOrWhiteSpace(startupOptions.DataRoot))
        {
            Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, startupOptions.DataRoot);
        }

        var timeoutMs = GetIntOption(args, "--timeout-ms", 30000);
        var endpointAddress = GetOption(args, "--endpoint-address");
        var endpoint = string.IsNullOrWhiteSpace(endpointAddress)
            ? IpcEndpoint.RunnerDefault(PlatformId.Current())
            : new IpcEndpoint(
                OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
                endpointAddress);
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var client = HostControlClient.ForEndpoint(endpoint);
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

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
