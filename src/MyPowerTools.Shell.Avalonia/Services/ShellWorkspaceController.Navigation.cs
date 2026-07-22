using MyPowerTools.Shell.Avalonia.Navigation;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    public async Task ShowPageAsync(string page)
    {
        if (IsDisposed)
        {
            return;
        }

        if (string.Equals(page, DashboardPage, StringComparison.OrdinalIgnoreCase))
        {
            page = HomePage;
        }

        if (string.Equals(page, CommandsPage, StringComparison.OrdinalIgnoreCase))
        {
            _chromeViewModel.SelectPage(CommandsPage);
            await OpenCommandPaletteAsync();
            return;
        }

        BeginWorkspace();
        _currentPage = page;
        _currentToolId = "";
        _currentToolRouteId = "";
        var navigationPage = IsSystemDestination(page) ? SystemPage : page;
        _chromeViewModel.SelectPage(navigationPage);
        _navigation.Navigate(ToShellRoute(page), addToHistory: true);
        if (!string.Equals(page, CommandsPage, StringComparison.OrdinalIgnoreCase))
        {
            _chromeViewModel.IsCommandPaletteOpen = false;
        }

        SetStatus($"Loading {page}");

        switch (page)
        {
            case HomePage:
                await LoadHomePageAsync();
                break;
            case ToolsPage:
                await LoadToolsPageAsync();
                break;
            case ActivityPage:
                LoadActivityPage();
                break;
            case SystemPage:
                LoadSystemPage();
                break;
            case DashboardPage:
                await LoadDashboardPageAsync();
                break;
            case ModulesPage:
                await LoadModulesPageAsync();
                break;
            case CommandsPage:
                await OpenCommandPaletteAsync();
                break;
            case SettingsPage:
                await LoadGeneralSettingsPage();
                break;
            case LogsPage:
                await LoadLogsPageAsync();
                break;
            case PackagesPage:
                await LoadPackagesPageAsync();
                break;
            case DiagnosticsPage:
                await LoadDiagnosticsPageAsync();
                break;
            case ServicesPage:
                await LoadServicesPageAsync();
                break;
        }
    }
}
