using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellWorkspaceController : IAsyncDisposable
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
                _searchBox.Focus();
                _searchBox.SelectAll();
                SetStatus("Command Palette focused.");
                await LoadCommandsAsync(_searchBox.Text ?? "");
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

    private async Task LoadDashboardPageAsync()
    {
        try
        {
            var result = await _pageData.LoadDashboardAsync(
                moduleId => ShowModuleDetailPageAsync(moduleId),
                commandId => ExecuteCommandAsync(commandId));

            _contentHost.Content = new DashboardView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(DashboardPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadModulesPageAsync()
    {
        try
        {
            var result = await _pageData.LoadModulesAsync(
                moduleId => ShowModuleDetailPageAsync(moduleId),
                moduleId => LoadSettingsPageAsync(moduleId),
                moduleId => LoadLogsPageAsync(moduleId),
                (moduleId, enabled) => SetModuleEnabledAsync(moduleId, enabled));

            _contentHost.Content = new ModulesView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(ModulesPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task ShowModuleDetailPageAsync(string moduleId)
    {
        _currentPage = ModulesPage;
        _chromeViewModel.SelectPage(ModulesPage);

        try
        {
            var result = await _pageData.LoadModuleDetailAsync(
                moduleId,
                (targetModuleId, enabled) => SetModuleEnabledAsync(targetModuleId, enabled, showDetail: true),
                commandId => ExecuteCommandAsync(commandId));

            _contentHost.Content = new ModuleDetailView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage("Module Detail", ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadSettingsPageAsync(string? selectedModuleId = null)
    {
        try
        {
            var result = await _pageData.LoadSettingsAsync(
                selectedModuleId,
                moduleId => LoadSettingsPageAsync(moduleId),
                SaveSettingsPageAsync);

            _contentHost.Content = new SettingsCenterView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(SettingsPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task SaveSettingsPageAsync(SettingsCenterViewModel viewModel)
    {
        var result = await _settingsService.SaveAsync(viewModel);
        viewModel.StatusText = result.StatusText;
        SetStatus(result.StatusText);
        if (result.Saved)
        {
            await LoadSettingsPageAsync(viewModel.SelectedModuleId);
        }
    }

    private async Task LoadLogsPageAsync(string? selectedModuleId = null)
    {
        try
        {
            var result = await _pageData.LoadLogsAsync(
                selectedModuleId,
                moduleId => LoadLogsPageAsync(moduleId));

            _contentHost.Content = new LogsView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(LogsPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadNotificationsPageAsync()
    {
        try
        {
            var result = await _pageData.LoadNotificationsAsync();

            _contentHost.Content = new NotificationsView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(NotificationsPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadPackagesPageAsync()
    {
        try
        {
            var result = await _pageData.LoadPackagesAsync(
                sourceDirectory => RunPackageOperationAsync("install", sourceDirectory),
                packageId => RunPackageOperationAsync("rollback", packageId),
                packageId => RunPackageOperationAsync("repair", packageId),
                packageId => RunPackageOperationAsync("uninstall", packageId),
                moduleId => ShowModuleDetailPageAsync(moduleId));

            _contentHost.Content = new PackageManagerView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(PackagesPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadDiagnosticsPageAsync()
    {
        try
        {
            var result = await _pageData.LoadDiagnosticsAsync(
                (transportKind, poolKey) => RestartRuntimeProcessAsync(transportKind, poolKey),
                (transportKind, poolKey, paused, expiresAt, reason) => SetRuntimeProcessRestartPolicyAsync(
                    transportKind,
                    poolKey,
                    paused,
                    expiresAt,
                    reason));

            _contentHost.Content = new DiagnosticsView
            {
                DataContext = result.ViewModel
            };
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(DiagnosticsPage, ex.Message);
            SetStatus(ex.Message);
        }
    }

    private async Task LoadCommandsAsync(string query)
    {
        try
        {
            var viewModel = await _pageData.LoadCommandsAsync(query, commandId => ExecuteCommandAsync(commandId));
            _commandPanel.Content = new CommandPaletteView
            {
                DataContext = viewModel
            };
        }
        catch (Exception ex)
        {
            _commandPanel.Content = new MptErrorState(ex.Message);
        }
    }

    private async Task LoadBrokerAuditAsync()
    {
        try
        {
            var viewModel = await _pageData.LoadBrokerAuditAsync();
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = viewModel
            };
        }
        catch (Exception ex)
        {
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = _pageData.CreateBrokerAuditError(ex.Message)
            };
        }
    }

    private Control BuildUnavailablePage(string title, string message)
    {
        return new UnavailablePageView
        {
            DataContext = new UnavailablePageViewModel(title, message)
        };
    }

    private async Task ExecuteCommandAsync(string commandId)
    {
        try
        {
            var result = await _commandExecutionService.ExecuteAsync(commandId);
            SetStatus(result.StatusText);
            _permissionPanel.Content = null;
            if (result.RequiresPermissionPrompt)
            {
                _permissionPanel.Content = new PermissionPromptView
                {
                    DataContext = ShellPageViewModelFactory.FromPermissionPrompt(result.Response, LoadBrokerAuditAsync)
                };
            }

            await LoadBrokerAuditAsync();
            if (_currentPage == NotificationsPage)
            {
                await LoadNotificationsPageAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task RunPackageOperationAsync(string operation, string target)
    {
        try
        {
            var result = await _hostActions.RunPackageOperationAsync(operation, target);
            if (result.ShouldRefresh)
            {
                await LoadPackagesPageAsync();
                await LoadCommandsAsync(_searchBox.Text ?? "");
            }

            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task RestartRuntimeProcessAsync(string transportKind, string poolKey)
    {
        try
        {
            var result = await _hostActions.RestartRuntimeProcessAsync(transportKind, poolKey);
            SetStatus(result.StatusText);
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task SetRuntimeProcessRestartPolicyAsync(string transportKind, string poolKey, bool paused, DateTimeOffset? expiresAt = null, string? reason = null)
    {
        try
        {
            var result = await _hostActions.SetRuntimeProcessRestartPolicyAsync(transportKind, poolKey, paused, expiresAt, reason);
            SetStatus(result.StatusText);
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task SetModuleEnabledAsync(string moduleId, bool enabled, bool showDetail = false)
    {
        try
        {
            var result = await _hostActions.SetModuleEnabledAsync(moduleId, enabled);
            SetStatus(result.StatusText);
            await LoadCommandsAsync(_searchBox.Text ?? "");
            await LoadBrokerAuditAsync();
            if (showDetail)
            {
                await ShowModuleDetailPageAsync(moduleId);
            }
            else if (_currentPage == DashboardPage)
            {
                await LoadDashboardPageAsync();
            }
            else
            {
                await LoadModulesPageAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }
}
