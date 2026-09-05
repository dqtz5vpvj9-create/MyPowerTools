using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private async Task OpenShortcutsForToolAsync(string? toolId)
    {
        await ShowPageAsync(ShortcutsPage);
        if ((_contentHost.Content as ShortcutCenterView)?.DataContext is ShortcutCenterViewModel model)
            model.Query = toolId ?? "";
    }

    private async Task LoadShortcutCenterAsync()
    {
        var model = new ShortcutCenterViewModel(_shortcuts);
        SetOwnedContent(_contentHost, new ShortcutCenterView { DataContext = model });
        await model.RefreshAsync();
        SetStatus("Keyboard shortcuts opened.");
    }

    private async Task GoBackWithShortcutsAsync()
    {
        if (!_navigation.TryGoBack()) return;
        var route = _navigation.Current;
        if (route.Kind == ShellRouteKind.Tool)
        {
            await ShowToolPageAsync(route.ToolId, route.ToolRouteId);
            return;
        }
        await ShowPageAsync(route.Kind switch
        {
            ShellRouteKind.Home => HomePage,
            ShellRouteKind.Tools => ToolsPage,
            ShellRouteKind.Activity => ActivityPage,
            ShellRouteKind.Settings => SettingsPage,
            ShellRouteKind.KeyboardShortcuts => ShortcutsPage,
            ShellRouteKind.Modules => ModulesPage,
            ShellRouteKind.Packages => PackagesPage,
            ShellRouteKind.Logs => LogsPage,
            ShellRouteKind.RuntimeHealth => DiagnosticsPage,
            _ => SystemPage
        });
    }
}
