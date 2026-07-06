using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia;

public sealed class MainWindow : Window
{
    private readonly ShellWorkspaceController _workspace;
    private readonly ShellStartupOptions _startupOptions;

    public MainWindow()
        : this(ShellStartupOptions.Default)
    {
    }

    public MainWindow(ShellStartupOptions startupOptions)
    {
        _startupOptions = startupOptions;
        Title = "MyPowerTools";
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;

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
        Opened += async (_, _) => await OpenShellAsync();
        Closed += async (_, _) => await _workspace.DisposeAsync();
    }

    private async Task OpenShellAsync()
    {
        await _workspace.OpenAsync();
        if (_startupOptions.FocusCommandPalette)
        {
            await _workspace.FocusCommandPaletteAsync();
        }
    }

    private async void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        await _workspace.HandleKeyDownAsync(e);
    }

    private static T RequireControl<T>(Control root, string name)
        where T : Control
    {
        return root.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Shell chrome control '{name}' was not found.");
    }
}
