using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
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
        viewModel.ApplySaveResult(
            result.ApplyState,
            result.ApplyTitle,
            result.ApplyMessage,
            result.Revision,
            result.Saved);
        SetStatus(result.StatusText);
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
}
