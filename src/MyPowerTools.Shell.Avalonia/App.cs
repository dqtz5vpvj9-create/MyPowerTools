using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Platform;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia;

public sealed class App : Application
{
    private static readonly Uri ThemeBaseUri = new("avares://MyPowerTools.Shell.Avalonia/App.cs");
    private int _deferredStylesScheduled;
    private int _deferredStylesLoaded;
    private ITrayService? _platformTray;

    internal static ShellActivationRequest? StartupActivationRequest { get; set; }
    internal static Task<ShellHomeSnapshot?> CachedHomeSnapshotTask { get; set; } =
        Task.FromResult<ShellHomeSnapshot?>(null);
    internal static Task<ShellRunnerBootstrapResult> RunnerBootstrapTask { get; set; } =
        Task.FromResult(new ShellRunnerBootstrapResult("not-requested", "Runner bootstrap was not requested."));

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(ThemeBaseUri)
        {
            Source = new Uri("avares://MyPowerTools.UI/Themes/MptThemeCritical.axaml")
        });
        ShellAppearanceService.ApplySavedTheme(this);
        ApplyProductPalette();
        ActualThemeVariantChanged += (_, _) => ApplyProductPalette();
        ShellStartupDiagnostics.Mark("app-initialized");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var runnerBootstrapTask = RunnerBootstrapTask;
            var cachedHomeSnapshotTask = CachedHomeSnapshotTask;
            RunnerBootstrapTask = Task.FromResult(
                new ShellRunnerBootstrapResult("not-requested", "Runner bootstrap was not requested."));
            CachedHomeSnapshotTask = Task.FromResult<ShellHomeSnapshot?>(null);
            var mainWindow = new MainWindow(
                ShellStartupOptions.FromArgs(desktop.Args),
                runnerBootstrapTask,
                cachedHomeSnapshotTask);
            desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => mainWindow.AllowPermanentClose();
            mainWindow.StartActivationListener(StartupActivationRequest);
            StartupActivationRequest = null;
            desktop.MainWindow = mainWindow;
            StartPlatformTray(desktop, mainWindow);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartPlatformTray(
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow mainWindow)
    {
        var platform = PlatformPackFactory.Create();
        if (platform.TrayHost != PlatformTrayHost.Shell ||
            !platform.Capabilities.Resolve("tray").Supported)
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
            iconPath = "";
        }
        var tray = platform.Tray;
        var result = tray.StartAsync(
            new TrayOptions(
                "com.mypowertools.desktop",
                "MyPowerTools",
                iconPath,
                [
                    new TrayMenuItem("open-shell", "Open MyPowerTools", IsDefault: true),
                    new TrayMenuItem("exit-application", "Exit MyPowerTools", SeparatorBefore: true)
                ]),
            async (invocation, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (invocation.ActionId == "open-shell")
                {
                    await mainWindow.PresentFromPlatformTrayAsync();
                }
                else if (invocation.ActionId == "exit-application")
                {
                    mainWindow.ShutdownFromPlatformTray();
                }
            },
            CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Success)
        {
            tray.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return;
        }

        _platformTray = tray;
        desktop.Exit += async (_, _) =>
        {
            var activeTray = Interlocked.Exchange(ref _platformTray, null);
            if (activeTray is not null)
            {
                await activeTray.DisposeAsync();
            }
        };
    }

    internal void ScheduleDeferredStyles()
    {
        if (Interlocked.Exchange(ref _deferredStylesScheduled, 1) != 0)
        {
            return;
        }

        DispatcherTimer.RunOnce(
            EnsureDeferredStyles,
            TimeSpan.FromMilliseconds(500),
            DispatcherPriority.ApplicationIdle);
    }

    internal void EnsureDeferredStyles()
    {
        Interlocked.Exchange(ref _deferredStylesScheduled, 1);
        if (Interlocked.Exchange(ref _deferredStylesLoaded, 1) != 0)
        {
            return;
        }

        try
        {
            Styles.Add(new StyleInclude(ThemeBaseUri)
            {
                Source = new Uri("avares://MyPowerTools.UI/Themes/MptThemeDeferred.axaml")
            });
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _deferredStylesLoaded, 0);
            Volatile.Write(ref _deferredStylesScheduled, 0);
            ShellCommandFaultLog.Write("Load deferred Shell styles", ex, "startup-prefetch");
        }
    }

    private void ApplyProductPalette()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        MptTheme.ApplyPalette(this, dark);
    }
}
