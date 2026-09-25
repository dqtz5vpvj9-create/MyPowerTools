using Avalonia;
using Avalonia.Media;
using System.Runtime.InteropServices;
using MyPowerTools.Abstractions;
using MyPowerTools.HostControl;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Shell.Avalonia.Services;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => ShellDiagnostics.Run(
        () => MainCore(args),
        captureNativeStderr: !args.Contains("--smoke", StringComparer.OrdinalIgnoreCase));

    private static int MainCore(string[] args)
    {
        var installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        DotNetRuntimeEnvironment.ConfigureCurrentProcess(installRoot);
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

        // Only the primary Shell owns a running-session marker. Forwarding activations
        // and HostControl smoke processes must not produce false crash-recovery notices.
        ShellDiagnostics.BeginSession();
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
        SetMacProcessName();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args,
            global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();
        if (OperatingSystem.IsMacOS())
        {
            builder = builder.With(new FontManagerOptions
            {
                DefaultFamilyName = "PingFang SC",
                FontFamilyMappings = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Microsoft YaHei UI"] = new FontFamily("PingFang SC"),
                    ["Segoe UI Variable"] = new FontFamily("PingFang SC"),
                    ["Segoe UI"] = new FontFamily("PingFang SC"),
                    ["Segoe UI Emoji"] = new FontFamily("Apple Color Emoji"),
                    ["Segoe UI Symbol"] = new FontFamily("Apple Symbols"),
                    ["Cascadia Mono"] = new FontFamily("Menlo"),
                    ["Consolas"] = new FontFamily("Menlo")
                }
            });
        }

        return builder.With(new MacOSPlatformOptions
        {
            // The native host sets the product name before Avalonia creates
            // the default application menu.
            DisableSetProcessName = true
        }).LogToTrace().AfterSetup(_ => ShellDiagnostics.AttachToUi());
    }

    internal static void SetMacProcessName()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            MptSetProcessName("MyPowerTools");
        }
        catch (DllNotFoundException)
        {
            // Managed-only validation does not include the native macOS library.
        }
        catch (EntryPointNotFoundException)
        {
            // Allow an older installed native host to launch during an in-place update.
        }
    }

    internal static void SetMacApplicationIcon()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var iconPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "Resources",
            "MyPowerTools.icns"));
        if (!File.Exists(iconPath))
        {
            return;
        }

        try
        {
            _ = MptSetApplicationIcon(iconPath);
        }
        catch (DllNotFoundException)
        {
            // Managed-only validation does not include the native macOS library.
        }
        catch (EntryPointNotFoundException)
        {
            // Allow an older installed native host to launch during an in-place update.
        }
    }

    [DllImport("MptMacNative", EntryPoint = "mpt_set_process_name")]
    private static extern void MptSetProcessName(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("MptMacNative", EntryPoint = "mpt_set_application_icon")]
    private static extern int MptSetApplicationIcon(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconPath);

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
        var quitRunner = args.Contains("--quit-runner", StringComparer.OrdinalIgnoreCase);
        // Phase 1: wait for the Runner to serve HostControl. A cold Runner only starts
        // listening after it has probed every enabled module, so retrying here is expected.
        // Only Ping is retried: it touches no module, so abandoning an attempt is harmless.
        // A fresh client per attempt re-reads the auth token, which a Runner that is still
        // starting may not have written yet.
        HostControlClient? client = null;
        HostProto.PingResponse? ping = null;
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var attempt = HostControlClient.ForEndpoint(endpoint);
            try
            {
                ping = await attempt.PingAsync(attemptTimeout.Token);
                client = attempt;
                break;
            }
            catch (Exception ex)
            {
                attempt.Dispose();
                lastError = ex;
                await Task.Delay(500);
            }
        }

        using var connectedClient = client;
        if (client is null || ping is null)
        {
            Console.Error.WriteLine($"Shell HostControl smoke failed: Runner did not answer Ping within {timeoutMs} ms: {lastError?.Message ?? "timeout"}");
            return 1;
        }

        // Phase 2: exercise the module-facing RPCs exactly once, within the remaining budget.
        // They are not retried under a short per-attempt timeout: cancelling a dashboard
        // refresh mid-flight cancels module callbacks, and a callback that does not stop in
        // time is quarantined by the in-proc host, which would turn a slow machine into a
        // degraded Runner instead of a slow smoke.
        var remaining = deadline - DateTimeOffset.UtcNow;
        var verificationBudget = remaining > TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
        using var verificationTimeout = new CancellationTokenSource(verificationBudget);
        var exitCode = 0;
        try
        {
            var dashboard = await client.GetDashboardSnapshotAsync(verificationBudget, verificationTimeout.Token);
            var modules = await client.ListModulesAsync(verificationTimeout.Token);
            var commands = await client.ListCommandsAsync(cancellationToken: verificationTimeout.Token);

            Console.WriteLine($"Shell HostControl smoke connected: runner={ping.State} version={ping.RunnerVersion}");
            Console.WriteLine($"Shell HostControl smoke modules={modules.Modules.Count} dashboardCards={dashboard.Cards.Count} commands={commands.Commands.Count}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Shell HostControl smoke failed: {ex.Message}");
            exitCode = 1;
        }

        if (quitRunner)
        {
            // Also on failure, so a smoke-owned Runner is not left behind.
            try
            {
                using var quitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.QuitRunnerAsync(quitTimeout.Token);
                Console.WriteLine("Shell HostControl smoke requested Runner shutdown.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Shell HostControl smoke could not request Runner shutdown: {ex.Message}");
                exitCode = 1;
            }
        }

        return exitCode;
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
