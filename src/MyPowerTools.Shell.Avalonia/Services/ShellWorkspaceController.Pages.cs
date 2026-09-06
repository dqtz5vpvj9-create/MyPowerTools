using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.ServiceManager.Client;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private async Task LoadDashboardPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadDashboardAsync(
                moduleId => ShowModuleDetailPageAsync(moduleId),
                commandId => ExecuteCommandAsync(commandId));

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new DashboardView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadDashboardPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadDashboardPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(DashboardPage, failure.Message, retry: () => LoadDashboardPageAsync()));
        }
    }

    private async Task LoadModulesPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadModulesAsync(
                moduleId => ShowModuleDetailPageAsync(moduleId),
                moduleId => ShowModuleSettingsPageAsync(moduleId),
                moduleId => ShowModuleLogsPageAsync(moduleId),
                (moduleId, enabled) => SetModuleEnabledAsync(moduleId, enabled));

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new ModulesView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadModulesPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadModulesPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(ModulesPage, failure.Message, retry: () => LoadModulesPageAsync()));
        }
    }

    private async Task ShowModuleDetailPageAsync(string moduleId)
    {
        BeginWorkspace();
        _currentPage = ModulesPage;
        _chromeViewModel.SelectPage(ModulesPage);

        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadModuleDetailAsync(
                moduleId,
                (targetModuleId, enabled) => SetModuleEnabledAsync(targetModuleId, enabled, showDetail: true),
                commandId => ExecuteCommandAsync(commandId));

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new ModuleDetailView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(ShowModuleDetailPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(ShowModuleDetailPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Module Detail", failure.Message, retry: () => ShowModuleDetailPageAsync(moduleId)));
        }
    }

    private Task LoadGeneralSettingsPage()
    {
        SetOwnedContent(_contentHost, new GeneralSettingsView
        {
            DataContext = new GeneralSettingsViewModel(_appearance.CurrentTheme, _appearance.SetThemeAsync,
                () => ShowPageAsync(SystemPage), _devSource, openShortcuts: () => ShowPageAsync(ShortcutsPage))
        });
        SetStatus("Application preferences opened.");
        return Task.CompletedTask;
    }

    private async Task LoadModuleSettingsPageAsync(string selectedModuleId)
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadSettingsAsync(
                selectedModuleId,
                moduleId => LoadModuleSettingsPageAsync(moduleId),
                SaveSettingsPageAsync);

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            result.ViewModel.OpenShortcutsCommand = new AsyncRelayCommand(() => OpenShortcutsForToolAsync(selectedModuleId));
            SetOwnedContent(_contentHost, new SettingsCenterView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadModuleSettingsPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadModuleSettingsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(SettingsPage, failure.Message, retry: () => LoadModuleSettingsPageAsync(selectedModuleId)));
        }
    }

    private async Task ShowModuleSettingsPageAsync(string moduleId)
    {
        BeginWorkspace();
        _currentPage = ModulesPage;
        _chromeViewModel.SelectPage(SystemPage);
        await LoadModuleSettingsPageAsync(moduleId);
    }

    private async Task SaveSettingsPageAsync(SettingsCenterViewModel viewModel)
    {
        var result = await _settingsService.SaveAsync(viewModel);
        viewModel.ApplySaveResult(
            result.ApplyState,
            result.ApplyTitle,
            result.ApplyMessage,
            result.Revision,
            result.Saved);
        SetStatus(result.StatusText);
    }

    private long _logLoadVersion;

    private async Task LoadLogsPageAsync(string? selectedModuleId = null)
    {
        var identity = _workspaceIdentity.Capture();
        var requestVersion = Interlocked.Increment(ref _logLoadVersion);
        try
        {
            _logPageModuleId = selectedModuleId;
            var result = await _pageData.LoadLogsAsync(
                selectedModuleId,
                moduleId => LoadLogsPageAsync(moduleId),
                refresh: () => LoadLogsPageAsync(_logPageModuleId));

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            if (requestVersion != Volatile.Read(ref _logLoadVersion)) return;
            if (_contentHost.Content is LogsView { DataContext: LogsViewModel current })
            {
                current.RefreshFrom(result.ViewModel);
            }
            else
            {
                SetOwnedContent(_contentHost, new LogsView { DataContext = result.ViewModel });
            }
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (requestVersion != Volatile.Read(ref _logLoadVersion) ||
                IsStalePageFailure(nameof(LoadLogsPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadLogsPageAsync), ex);
            if (_contentHost.Content is LogsView { DataContext: LogsViewModel current })
            {
                current.ReportRefreshFailure(failure.Message);
            }
            else
            {
                SetOwnedContent(_contentHost, BuildUnavailablePage(LogsPage, failure.Message, retry: () => LoadLogsPageAsync(_logPageModuleId)));
            }
        }
    }

    private async Task ShowModuleLogsPageAsync(string moduleId)
    {
        BeginWorkspace();
        _currentPage = LogsPage;
        _chromeViewModel.SelectPage(LogsPage);
        _logPageModuleId = moduleId;
        await LoadLogsPageAsync(moduleId);
    }

    private async Task LoadNotificationsPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadNotificationsAsync();

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new NotificationsView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadNotificationsPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadNotificationsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Notifications", failure.Message, retry: () => LoadNotificationsPageAsync()));
        }
    }

    private async Task LoadPackagesPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadPackagesAsync(
                sourceDirectory => RunPackageOperationAsync("install", sourceDirectory),
                packageId => RunPackageOperationAsync("rollback", packageId),
                packageId => RunPackageOperationAsync("repair", packageId),
                packageId => RunPackageOperationAsync("uninstall", packageId),
                moduleId => ShowModuleDetailPageAsync(moduleId),
                () => RunOtaCliAsync("check"),
                progress => RunOtaCliAsync("apply", progress));

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new PackageManagerView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadPackagesPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadPackagesPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(PackagesPage, failure.Message, retry: () => LoadPackagesPageAsync()));
        }
    }

    private async Task LoadDiagnosticsPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
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

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new DiagnosticsView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadDiagnosticsPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(nameof(LoadDiagnosticsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(DiagnosticsPage, failure.Message, retry: () => LoadDiagnosticsPageAsync()));
        }
    }

    private async Task LoadServicesPageAsync()
    {
        var identity = _workspaceIdentity.Capture();
        try
        {
            var result = await _pageData.LoadServicesAsync(
                startUnit: unitId => InvokeServiceUnitActionAsync(unitId, ServiceUnitAction.Start),
                stopUnit: unitId => InvokeServiceUnitActionAsync(unitId, ServiceUnitAction.Stop),
                restartUnit: unitId => InvokeServiceUnitActionAsync(unitId, ServiceUnitAction.Restart),
                tailLogs: TailServiceUnitLogsAsync,
                openTool: OpenToolFromServicesAsync,
                toggleAutostart: ToggleServiceUnitAutostartAsync,
                refresh: () => { return LoadServicesPageAsync(); },
                reloadManifests: ReloadServiceUnitsAsync);

            if (!_workspaceIdentity.IsCurrent(identity)) return;

            SetOwnedContent(_contentHost, new ServicesView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            if (IsStalePageFailure(nameof(LoadServicesPageAsync), ex, identity)) return;

            var failure = ReportPageFailure(
                nameof(LoadServicesPageAsync),
                ex,
                ShellFailureSource.ServiceManager);
            SetOwnedContent(_contentHost, BuildUnavailablePage(
                ServicesPage,
                failure.Message,
                retry: TryStartServiceManagerThenLoadAsync));
        }
    }
}
