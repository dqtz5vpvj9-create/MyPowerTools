using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia;

public sealed partial class MainWindow : Window
{
    private const string WindowCaption = "MyPowerTools";
    private readonly ShellWorkspaceController _workspace;
    private readonly ShellStartupOptions _startupOptions;
    private readonly TaskCompletionSource<bool> _workspaceOpened = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow()
        : this(ShellStartupOptions.Default)
    {
    }

    public MainWindow(ShellStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        Title = OperatingSystem.IsWindows() ? "" : WindowCaption;
        Width = 1280;
        Height = 800;
        MinWidth = 640;
        MinHeight = 480;
        Background = MptThemeTokens.TransparentBrush;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MyPowerTools.Shell.Avalonia/Assets/MyPowerTools.ico")));
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = MptThemeTokens.LayoutTopBarHeight;
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,
            WindowTransparencyLevel.None
        ];

        ShellWorkspaceController? workspace = null;
        var chromeViewModel = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            page => workspace?.ShowPageAsync(page) ?? Task.CompletedTask,
            () => workspace?.RefreshAsync() ?? Task.CompletedTask,
            () => workspace?.OpenCommandPaletteAsync() ?? Task.CompletedTask,
            () => workspace?.CloseCommandPaletteAsync() ?? Task.CompletedTask,
            () => workspace?.DismissPermissionPromptAsync() ?? Task.CompletedTask);
        var chrome = new ShellChromeView
        {
            DataContext = chromeViewModel
        };
        Content = chrome;

        _workspace = new ShellWorkspaceController(
            chromeViewModel,
            RequireControl<MptSearchBox>(chrome, "SearchBox"),
            RequireControl<ContentControl>(chrome, "ContentHost"),
            RequireControl<ContentControl>(chrome, "CommandPanel"),
            RequireControl<ContentControl>(chrome, "PermissionPanel"),
            RequireControl<ContentControl>(chrome, "AuditPanel"));
        workspace = _workspace;

        KeyDown += OnShellKeyDown;
        Opened += OnWindowOpened;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        Closed += OnWindowClosed;
    }

    private async Task OpenShellAsync()
    {
        try
        {
            await _workspace.OpenAsync();
            if (_startupOptions.FocusCommandPalette)
            {
                await _workspace.FocusCommandPaletteAsync();
            }
        }
        finally
        {
            // Protocol activation can arrive with the first window Opened event.
            // Let initial shell navigation finish before selecting the notification
            // workspace, otherwise the normal Home refresh can overwrite the deep link.
            _workspaceOpened.TrySetResult(true);
        }
    }

    private void OnWindowOpened(object? sender, EventArgs args)
    {
        ApplyWindowsChrome();
        RunWindowUiEvent(OpenShellAsync, "Open Shell workspace");
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs args)
    {
        ApplyWindowsChrome();
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        KeyDown -= OnShellKeyDown;
        Opened -= OnWindowOpened;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        Closed -= OnWindowClosed;
        RunWindowUiEvent(
            async () => await _workspace.DisposeAsync(),
            "Dispose Shell workspace");
    }

}
