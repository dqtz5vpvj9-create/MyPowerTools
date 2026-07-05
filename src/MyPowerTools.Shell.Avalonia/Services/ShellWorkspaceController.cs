using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController : IAsyncDisposable
{
    private const string DashboardPage = "Dashboard";
    private const string ModulesPage = "Modules";
    private const string SettingsPage = "Settings";
    private const string LogsPage = "Logs";
    private const string NotificationsPage = "Notifications";
    private const string PackagesPage = "Packages";
    private const string DiagnosticsPage = "Diagnostics";

    private readonly MptSearchBox _searchBox;
    private readonly ContentControl _contentHost;
    private readonly ContentControl _commandPanel;
    private readonly ContentControl _permissionPanel;
    private readonly ContentControl _auditPanel;
    private readonly ShellChromeViewModel _chromeViewModel;
    private readonly ShellCommandExecutionService _commandExecutionService = new();
    private readonly ShellHostActionService _hostActions = new();
    private readonly ShellPageDataService _pageData = new();
    private readonly ShellRunnerEventService _runnerEvents = new();
    private readonly ShellSettingsService _settingsService = new();
    private string _currentPage = DashboardPage;

    public ShellWorkspaceController(
        ShellChromeViewModel chromeViewModel,
        MptSearchBox searchBox,
        ContentControl contentHost,
        ContentControl commandPanel,
        ContentControl permissionPanel,
        ContentControl auditPanel)
    {
        _chromeViewModel = chromeViewModel;
        _searchBox = searchBox;
        _contentHost = contentHost;
        _commandPanel = commandPanel;
        _permissionPanel = permissionPanel;
        _auditPanel = auditPanel;

        _searchBox.TextChanged += async (_, _) => await LoadCommandsAsync(_searchBox.Text ?? "");
        _runnerEvents.StatusChanged += text => Dispatcher.UIThread.Post(() => SetStatus(text));
        _runnerEvents.RunnerStatusChanged += text => Dispatcher.UIThread.Post(() => SetRunnerStatus(text));
        _runnerEvents.RunnerRecovered += () => Dispatcher.UIThread.Post(async () => await RefreshShellDataAsync());
        _runnerEvents.HostEventReceived += evt => Dispatcher.UIThread.Post(async () => await ApplyHostEventAsync(evt));
    }

    public static IReadOnlyList<string> PageLabels { get; } =
        [DashboardPage, ModulesPage, SettingsPage, LogsPage, NotificationsPage, PackagesPage, DiagnosticsPage];

    public async Task OpenAsync()
    {
        _runnerEvents.Start();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        await _runnerEvents.CheckOnceAsync();
        await RefreshShellDataAsync();
    }

    public async Task ShowPageAsync(string page)
    {
        _currentPage = page;
        _chromeViewModel.SelectPage(page);
        SetStatus($"Loading {page}");

        switch (page)
        {
            case DashboardPage:
                await LoadDashboardPageAsync();
                break;
            case ModulesPage:
                await LoadModulesPageAsync();
                break;
            case SettingsPage:
                await LoadSettingsPageAsync();
                break;
            case LogsPage:
                await LoadLogsPageAsync();
                break;
            case NotificationsPage:
                await LoadNotificationsPageAsync();
                break;
            case PackagesPage:
                await LoadPackagesPageAsync();
                break;
            case DiagnosticsPage:
                await LoadDiagnosticsPageAsync();
                break;
        }
    }

    public async Task HandleKeyDownAsync(KeyEventArgs e)
    {
        var shortcut = ShellKeyboardShortcut.Resolve(e.Key, e.KeyModifiers);
        if (shortcut.Action == ShellKeyboardAction.None)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await ApplyKeyboardShortcutAsync(shortcut);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    public async Task FocusCommandPaletteAsync()
    {
        _searchBox.Focus();
        _searchBox.SelectAll();
        SetStatus("Command Palette focused.");
        await LoadCommandsAsync(_searchBox.Text ?? "");
    }

    public async ValueTask DisposeAsync()
    {
        await _runnerEvents.DisposeAsync();
    }

    private void SetStatus(string text) => _chromeViewModel.StatusText = text;

    private void SetRunnerStatus(string text) => _chromeViewModel.RunnerStatusText = text;

    private async Task RefreshShellDataAsync()
    {
        await ShowPageAsync(_currentPage);
        await LoadCommandsAsync(_searchBox.Text ?? "");
        await LoadBrokerAuditAsync();
    }

    private async Task ApplyKeyboardShortcutAsync(ShellKeyboardShortcutResult shortcut)
    {
        switch (shortcut.Action)
        {
            case ShellKeyboardAction.FocusCommandPalette:
                await FocusCommandPaletteAsync();
                break;
            case ShellKeyboardAction.ClearCommandPalette:
                _searchBox.Text = "";
                _contentHost.Focus();
                SetStatus("Command Palette cleared.");
                await LoadCommandsAsync("");
                break;
            case ShellKeyboardAction.Refresh:
                await RefreshAsync();
                SetStatus($"{_currentPage} refreshed.");
                break;
            case ShellKeyboardAction.Navigate when shortcut.TargetPage is not null:
                await ShowPageAsync(shortcut.TargetPage);
                break;
        }
    }

    private async Task ApplyHostEventAsync(HostProto.HostEvent evt)
    {
        var plan = ShellPageRefreshRouter.Route(_currentPage, evt);
        if (plan.ReloadBrokerAudit)
        {
            await LoadBrokerAuditAsync();
        }

        if (plan.ReloadCommands)
        {
            await LoadCommandsAsync(_searchBox.Text ?? "");
        }

        if (plan.ReloadNotifications)
        {
            await LoadNotificationsPageAsync();
        }

        if (plan.ReloadSettingsModuleId is not null)
        {
            await LoadSettingsPageAsync(plan.ReloadSettingsModuleId);
        }

        if (plan.ReloadDiagnostics)
        {
            await LoadDiagnosticsPageAsync();
        }

        if (plan.ReloadCurrentPage)
        {
            await ShowPageAsync(_currentPage);
        }
    }

}
