using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>
/// The concrete remediation behind each recovery rung. Every method reports progress in plain
/// language and never throws for an expected failure; the caller always reloads afterwards.
/// </summary>
internal static class ShellRecoveryActions
{
    private const string RunnerProcessName = "MyPowerTools.Runner";
    private const string ServiceManagerProcessName = "MyPowerTools.ServiceManager";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(6);

    public static async Task StartServiceAsync(
        ShellFailureSource source,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        progress.Report($"Checking the {ShellRecoveryPlan.ServiceName(source)}…");
        if (source == ShellFailureSource.ServiceManager)
        {
            await ShellServiceManagerBootstrapper.EnsureStartedAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        // The user explicitly asked to recover, so start the Runner even if this Shell was launched
        // with --no-runner-bootstrap; module and data roots still come from the launch arguments.
        var options = ShellStartupOptions.FromArgs(Environment.GetCommandLineArgs()) with { RunnerBootstrap = true };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await ShellRunnerBootstrapper.EnsureStartedAsync(options, cancellationToken).ConfigureAwait(true);
            if (result.State is "already-running" or "started" or "unauthenticated" or "missing")
            {
                return;
            }

            progress.Report("Waiting for the background service to start…");
        }
    }

    public static async Task RestartServiceAsync(
        ShellFailureSource source,
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        progress.Report($"Restarting the {ShellRecoveryPlan.ServiceName(source)}…");
        await StopInstalledProcessesAsync(
            source == ShellFailureSource.ServiceManager ? ServiceManagerProcessName : RunnerProcessName,
            cancellationToken).ConfigureAwait(true);
        await StartServiceAsync(source, progress, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Starts a fresh Shell that waits for this process to exit, then shuts this one down. The
    /// background service is untouched, so tools hosted by it keep running.
    /// </summary>
    public static void RestartApplication()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Shell executable path is unavailable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(ShellRelaunch.ArgumentName);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        foreach (var option in new[] { "--data-root", "--modules" })
        {
            var value = FindOption(Environment.GetCommandLineArgs(), option);
            if (value is not null)
            {
                startInfo.ArgumentList.Add(option);
                startInfo.ArgumentList.Add(value);
            }
        }

        Process.Start(startInfo);
        ShellApplicationLifecycle.RequestPermanentShutdown();
    }

    private static async Task StopInstalledProcessesAsync(string processName, CancellationToken cancellationToken)
    {
        var appRoot = ShellRunnerBootstrapper.FindApplicationRoot(AppContext.BaseDirectory);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    process.Kill();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(StopTimeout);
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
                }
                catch (Exception ex) when (
                    ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or NotSupportedException ||
                    (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // A process owned by another account or elevation level cannot be stopped from
                    // here; the next rung (restart MyPowerTools) still applies.
                    ShellCommandFaultLog.Write($"Recovery: stop {processName}", ex, "recovery");
                }
            }
        }
    }

    private static string? FindOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal static class ShellRelaunch
{
    public const string ArgumentName = "--relaunch-after";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    /// <summary>Blocks until the Shell that requested the relaunch has exited and released the instance lock.</summary>
    public static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (!string.Equals(args[index], ArgumentName, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var processId))
            {
                continue;
            }

            try
            {
                using var previous = Process.GetProcessById(processId);
                previous.WaitForExit(WaitLimit);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Already gone.
            }

            return;
        }
    }
}

internal static class ShellApplicationLifecycle
{
    private static Action? _permanentShutdown;

    public static void RegisterPermanentShutdown(Action shutdown) => _permanentShutdown = shutdown;

    public static void RequestPermanentShutdown()
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_permanentShutdown is { } shutdown)
                {
                    shutdown();
                }
                else if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            },
            DispatcherPriority.Send);
    }
}
