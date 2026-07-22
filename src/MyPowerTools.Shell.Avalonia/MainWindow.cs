using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI;
using MyPowerTools.UI.Controls;
using MyPowerTools.WebSurface.Avalonia;

namespace MyPowerTools.Shell.Avalonia;

public sealed partial class MainWindow : Window
{
    private const string WindowCaption = "MyPowerTools";
    private readonly ShellStartupOptions _startupOptions;
    private readonly Task<ShellRunnerBootstrapResult> _runnerBootstrapTask;
    private readonly Task<ShellHomeSnapshot?> _cachedHomeSnapshotTask;
    private readonly WebSurfaceOcclusionState _webSurfaceOcclusion;
    private readonly ShellChromeViewModel _chromeViewModel;
    private readonly ShellChromeView _chrome;
    private readonly TaskCompletionSource<bool> _workspaceOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ShellWorkspaceController? _workspace;
    private ShellActivationPipe? _activationPipe;
    private ShellActivationRequest? _startupActivation;
    private int _workspaceInitializationStarted;
    private int _windowClosed;
    private int _suppressInitialPresentation;

    public MainWindow()
        : this(ShellStartupOptions.Default)
    {
    }

    public MainWindow(ShellStartupOptions startupOptions)
        : this(
            startupOptions,
            Task.FromResult(new ShellRunnerBootstrapResult("already-running", "Runner bootstrap already completed.")),
            Task.FromResult<ShellHomeSnapshot?>(null))
    {
    }

    internal MainWindow(
        ShellStartupOptions startupOptions,
        Task<ShellRunnerBootstrapResult> runnerBootstrapTask,
        Task<ShellHomeSnapshot?> cachedHomeSnapshotTask)
    {
        _startupOptions = startupOptions;
        _runnerBootstrapTask = runnerBootstrapTask;
        _cachedHomeSnapshotTask = cachedHomeSnapshotTask;
        Title = OperatingSystem.IsWindows() ? "" : WindowCaption;
        Width = 1280;
        Height = 800;
        MinWidth = 640;
        MinHeight = 480;
        Background = MptThemeTokens.TransparentBrush;
        if (!OperatingSystem.IsWindows())
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MyPowerTools.Shell.Avalonia/Assets/MyPowerTools.ico")));
        }
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = MptThemeTokens.LayoutTopBarHeight;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.None
        ];

        _webSurfaceOcclusion = new WebSurfaceOcclusionState();
        _chromeViewModel = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            page => _workspace?.ShowPageAsync(page) ?? Task.CompletedTask,
            () => _workspace?.RefreshAsync() ?? Task.CompletedTask,
            () => _workspace?.OpenCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.CloseCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.DismissPermissionPromptAsync() ?? Task.CompletedTask);
        _chrome = new ShellChromeView
        {
            DataContext = _chromeViewModel,
            WebSurfaceOcclusion = _webSurfaceOcclusion
        };
        Content = _chrome;

        KeyDown += OnShellKeyDown;
        Opened += OnWindowOpened;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Closed += OnWindowClosed;
        ShellStartupDiagnostics.Mark("window-constructed");
    }



}
