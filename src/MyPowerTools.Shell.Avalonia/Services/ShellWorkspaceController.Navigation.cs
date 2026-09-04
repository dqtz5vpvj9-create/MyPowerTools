using MyPowerTools.Shell.Avalonia.Navigation;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private static readonly TimeSpan PendingPageIndicatorDelay = TimeSpan.FromMilliseconds(180);

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

        var previousPage = _currentPage;
        BeginWorkspace();
        _currentPage = page;
        _currentToolId = "";
        _currentToolRouteId = "";
        var navigationPage = IsSystemDestination(page) ? SystemPage : page;
        _chromeViewModel.SelectPage(navigationPage);
        var route = string.Equals(page, NotificationsPage, StringComparison.OrdinalIgnoreCase)
            ? ShellRoute.Notifications
            : ToShellRoute(page);
        _navigation.Navigate(route, addToHistory: true);
        if (!string.Equals(page, CommandsPage, StringComparison.OrdinalIgnoreCase))
        {
            _chromeViewModel.IsCommandPaletteOpen = false;
        }

        SetStatus($"Loading {page}");
        if (!string.Equals(previousPage, page, StringComparison.OrdinalIgnoreCase))
        {
            SchedulePendingPageIndicator(page);
        }

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
            case NotificationsPage:
                await LoadNotificationsPageAsync();
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

    /// <summary>
    /// Page loads are remote calls with multi-second deadlines, and until one returns the
    /// workspace still shows the page the user just left, which reads as a click that did
    /// nothing. After a short grace period -- long enough that a page loading from cache never
    /// flashes a placeholder -- the content host switches to the loading message for the page
    /// being opened. Only navigations to a different page schedule one, so refreshing a page in
    /// place keeps its current content on screen.
    /// </summary>
    private void SchedulePendingPageIndicator(string page)
    {
        var identity = _workspaceIdentity.Capture();
        var contentAtNavigation = _contentHost.Content;
        PostUiEvent(
            async () =>
            {
                await Task.Delay(PendingPageIndicatorDelay);
                if (IsDisposed ||
                    !_workspaceIdentity.IsCurrent(identity) ||
                    !ReferenceEquals(_contentHost.Content, contentAtNavigation))
                {
                    return;
                }

                SetOwnedContent(_contentHost, BuildPageMessage($"Loading {page}"));
            },
            $"Show {page} loading state");
    }
}
