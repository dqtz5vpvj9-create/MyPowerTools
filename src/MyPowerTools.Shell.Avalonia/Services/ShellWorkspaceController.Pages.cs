using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.ServiceManager.Client;
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

            SetOwnedContent(_contentHost, new DashboardView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadDashboardPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(DashboardPage, failure.Message));
        }
    }

    private async Task LoadModulesPageAsync()
    {
        try
        {
            var result = await _pageData.LoadModulesAsync(
                moduleId => ShowModuleDetailPageAsync(moduleId),
                moduleId => ShowModuleSettingsPageAsync(moduleId),
                moduleId => ShowModuleLogsPageAsync(moduleId),
                (moduleId, enabled) => SetModuleEnabledAsync(moduleId, enabled));

            SetOwnedContent(_contentHost, new ModulesView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadModulesPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(ModulesPage, failure.Message));
        }
    }

    private async Task ShowModuleDetailPageAsync(string moduleId)
    {
        BeginWorkspace();
        _currentPage = ModulesPage;
        _chromeViewModel.SelectPage(ModulesPage);

        try
        {
            var result = await _pageData.LoadModuleDetailAsync(
                moduleId,
                (targetModuleId, enabled) => SetModuleEnabledAsync(targetModuleId, enabled, showDetail: true),
                commandId => ExecuteCommandAsync(commandId));

            SetOwnedContent(_contentHost, new ModuleDetailView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(ShowModuleDetailPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Module Detail", failure.Message));
        }
    }

    private async Task LoadGeneralSettingsPage()
    {
        IReadOnlyList<MyPowerTools.Shell.Avalonia.ViewModels.GlobalHotkeyViewModel> hotkeys = [];
        try
        {
            hotkeys = await _pageData.LoadGlobalHotkeysAsync();
        }
        catch (Exception ex)
        {
            // The shortcuts overview is best-effort; settings must open even when
            // the Runner diagnostics are unavailable.
            ShellCommandFaultLog.Write("Load global hotkeys", ex, "settings");
        }

        var viewModel = new GeneralSettingsViewModel(
            _appearance.CurrentTheme,
            _appearance.SetThemeAsync,
            () => ShowPageAsync(SystemPage),
            _devSource,
            hotkeys);
        SetOwnedContent(_contentHost, new GeneralSettingsView
        {
            DataContext = viewModel
        });
        SetStatus("Application preferences opened.");
    }

    private async Task LoadModuleSettingsPageAsync(string selectedModuleId)
    {
        try
        {
            var result = await _pageData.LoadSettingsAsync(
                selectedModuleId,
                moduleId => LoadModuleSettingsPageAsync(moduleId),
                SaveSettingsPageAsync);

            SetOwnedContent(_contentHost, new SettingsCenterView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadModuleSettingsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(SettingsPage, failure.Message));
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

    private async Task LoadLogsPageAsync(string? selectedModuleId = null)
    {
        try
        {
            _logPageModuleId = selectedModuleId;
            var result = await _pageData.LoadLogsAsync(
                selectedModuleId,
                moduleId => LoadLogsPageAsync(moduleId),
                refresh: () => LoadLogsPageAsync(_logPageModuleId));

            SetOwnedContent(_contentHost, new LogsView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadLogsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(LogsPage, failure.Message));
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
        try
        {
            var result = await _pageData.LoadNotificationsAsync();

            SetOwnedContent(_contentHost, new NotificationsView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadNotificationsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage("Notifications", failure.Message));
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
                moduleId => ShowModuleDetailPageAsync(moduleId),
                () => RunOtaCliAsync("check"),
                () => RunOtaCliAsync("apply"));

            SetOwnedContent(_contentHost, new PackageManagerView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadPackagesPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(PackagesPage, failure.Message));
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

            SetOwnedContent(_contentHost, new DiagnosticsView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
            var failure = ReportPageFailure(nameof(LoadDiagnosticsPageAsync), ex);
            SetOwnedContent(_contentHost, BuildUnavailablePage(DiagnosticsPage, failure.Message));
        }
    }

    private async Task LoadServicesPageAsync()
    {
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

            SetOwnedContent(_contentHost, new ServicesView
            {
                DataContext = result.ViewModel
            });
            SetStatus(result.StatusText);
        }
        catch (Exception ex)
        {
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

    private async Task TryStartServiceManagerThenLoadAsync()
    {
        // Spawns the ServiceManager process if it is not already reachable, then reloads the page.
        // If startup still fails, LoadServicesPageAsync re-enters its catch and re-renders the
        // unavailable page with the same Try-again affordance, so the user can retry repeatedly.
        var result = await ShellServiceManagerBootstrapper.EnsureStartedAsync();
        SetStatus(result.Message);
        await LoadServicesPageAsync();
    }

    private async Task TailServiceUnitLogsAsync(string unitId)
    {
        // Tail logs is surfaced as a status update for now; a dedicated logs flyout can layer on later.
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        var entries = await client.TailLogsAsync(unitId, 50);
        var summary = entries.Count == 0 ? "No recent log lines." : string.Join("\n", entries.Take(20).Select(e => $"[{e.Level}] {e.Message}"));
        SetStatus($"Logs for {unitId}:\n{summary}");
    }

    private async Task OpenToolFromServicesAsync(string toolId)
    {
        // Navigate to the owning tool's page if it is a known first-party tool.
        if (!string.IsNullOrEmpty(toolId))
        {
            await ShowToolPageAsync(toolId, "");
        }
    }

    private async Task ToggleServiceUnitAutostartAsync(string unitId)
    {
        // Autostart is a property of the unit manifest; toggling rewrites the deployed manifest and reloads.
        // For units whose manifest is managed by the ServiceManager deploy root, we update the file in place.
        try
        {
            await ToggleDeployedUnitAutostartAsync(unitId);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not toggle autostart for {unitId}: {ex.Message}");
        }

        await LoadServicesPageAsync();
    }

    private async Task ToggleDeployedUnitAutostartAsync(string unitId)
    {
        var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
        var deployRoot = Path.Combine(dataRoot, "ServiceManager");
        var manifestPath = Path.Combine(deployRoot, "units", $"{unitId}.json");
        if (!File.Exists(manifestPath))
        {
            SetStatus($"Manifest for {unitId} not found in deploy root; cannot toggle autostart.");
            return;
        }

        var json = await File.ReadAllTextAsync(manifestPath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            var current = obj["autostart"]?.GetValue<bool>() ?? false;
            obj["autostart"] = !current;
            await File.WriteAllTextAsync(manifestPath, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private async Task InvokeServiceUnitActionAsync(string unitId, ServiceUnitAction action)
    {
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        switch (action)
        {
            case ServiceUnitAction.Start:
                await client.StartAsync(unitId);
                break;
            case ServiceUnitAction.Stop:
                await client.StopAsync(unitId);
                break;
            case ServiceUnitAction.Restart:
                await client.RestartAsync(unitId);
                break;
        }

        await LoadServicesPageAsync();
    }

    private async Task ReloadServiceUnitsAsync()
    {
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        await client.ReloadAsync();
        await LoadServicesPageAsync();
    }

    private enum ServiceUnitAction { Start, Stop, Restart }

}
