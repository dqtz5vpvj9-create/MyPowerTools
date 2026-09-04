using MyPowerTools.Shell.Avalonia.Navigation;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private async Task GoBackAsync()
    {
        if (!_navigation.TryGoBack())
        {
            SetStatus("No previous page.");
            return;
        }

        var route = _navigation.Current;
        if (route.Kind == ShellRouteKind.Tool)
        {
            await ShowToolPageAsync(route.ToolId, route.ToolRouteId);
            return;
        }

        await ShowPageAsync(PageForRoute(route.Kind));
    }

    private static string PageForRoute(ShellRouteKind kind)
    {
        return kind switch
        {
            ShellRouteKind.Home => HomePage,
            ShellRouteKind.Tools => ToolsPage,
            ShellRouteKind.Activity => ActivityPage,
            ShellRouteKind.Notifications => NotificationsPage,
            ShellRouteKind.Settings => SettingsPage,
            ShellRouteKind.System => SystemPage,
            ShellRouteKind.Modules => ModulesPage,
            ShellRouteKind.Packages => PackagesPage,
            ShellRouteKind.Logs => LogsPage,
            ShellRouteKind.RuntimeHealth => DiagnosticsPage,
            _ => SystemPage
        };
    }
}
