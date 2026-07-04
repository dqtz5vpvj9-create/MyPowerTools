using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Google.Protobuf.WellKnownTypes;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia;

public sealed class MainWindow : Window
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
    private readonly Services.ShellCommandExecutionService _commandExecutionService = new();
    private readonly Services.ShellHostActionService _hostActions = new();
    private readonly Services.ShellRunnerEventService _runnerEvents = new();
    private readonly Services.ShellSettingsService _settingsService = new();
    private string _currentPage = DashboardPage;

    public MainWindow()
    {
        Title = "MyPowerTools";
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;

        _chromeViewModel = new ShellChromeViewModel(
            [DashboardPage, ModulesPage, SettingsPage, LogsPage, NotificationsPage, PackagesPage, DiagnosticsPage],
            page => ShowPageAsync(page),
            RefreshAsync);
        var chrome = new ShellChromeView
        {
            DataContext = _chromeViewModel
        };
        Content = chrome;
        _searchBox = RequireControl<MptSearchBox>(chrome, "SearchBox");
        _contentHost = RequireControl<ContentControl>(chrome, "ContentHost");
        _commandPanel = RequireControl<ContentControl>(chrome, "CommandPanel");
        _permissionPanel = RequireControl<ContentControl>(chrome, "PermissionPanel");
        _auditPanel = RequireControl<ContentControl>(chrome, "AuditPanel");
        _searchBox.TextChanged += async (_, _) => await LoadCommandsAsync(_searchBox.Text ?? "");

        KeyDown += OnShellKeyDown;
        _runnerEvents.StatusChanged += text => Dispatcher.UIThread.Post(() => SetStatus(text));
        _runnerEvents.RunnerStatusChanged += text => Dispatcher.UIThread.Post(() => SetRunnerStatus(text));
        _runnerEvents.RunnerRecovered += () => Dispatcher.UIThread.Post(async () => await RefreshShellDataAsync());
        _runnerEvents.HostEventReceived += evt => Dispatcher.UIThread.Post(async () => await ApplyHostEventAsync(evt));
        Opened += async (_, _) =>
        {
            _runnerEvents.Start();
            await RefreshAsync();
        };
        Closed += async (_, _) =>
        {
            await _runnerEvents.DisposeAsync();
        };
    }

    private static T RequireControl<T>(Control root, string name)
        where T : Control
    {
        return root.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Shell chrome control '{name}' was not found.");
    }

    private void SetStatus(string text) => _chromeViewModel.StatusText = text;

    private void SetRunnerStatus(string text) => _chromeViewModel.RunnerStatusText = text;

    private async Task RefreshAsync()
    {
        await _runnerEvents.CheckOnceAsync();
        await RefreshShellDataAsync();
    }

    private async Task RefreshShellDataAsync()
    {
        await ShowPageAsync(_currentPage);
        await LoadCommandsAsync(_searchBox.Text ?? "");
        await LoadBrokerAuditAsync();
    }

    private async void OnShellKeyDown(object? sender, KeyEventArgs e)
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
        switch (evt.Type)
        {
            case "notification.created":
                if (_currentPage == NotificationsPage)
                {
                    await LoadNotificationsPageAsync();
                }
                break;
            case "command.executed":
                await LoadBrokerAuditAsync();
                if (_currentPage is DashboardPage or DiagnosticsPage)
                {
                    await ShowPageAsync(_currentPage);
                }
                break;
            case "settings.updated":
                if (_currentPage == SettingsPage)
                {
                    await LoadSettingsPageAsync(evt.SourceId);
                }
                break;
            case "module.enabled":
            case "module.disabled":
            case "registry.loaded":
            case "commands.dynamic.refreshed":
                await LoadCommandsAsync(_searchBox.Text ?? "");
                if (_currentPage is DashboardPage or ModulesPage or PackagesPage or DiagnosticsPage)
                {
                    await ShowPageAsync(_currentPage);
                }
                break;
            case "runtime.process.restart":
            case "runtime.process.policy":
            case "runtime.process.policy.expired":
                if (_currentPage == DiagnosticsPage)
                {
                    await LoadDiagnosticsPageAsync();
                }
                break;
            case "module.health.changed":
                if (_currentPage is DashboardPage or ModulesPage or DiagnosticsPage)
                {
                    await ShowPageAsync(_currentPage);
                }
                break;
        }
    }

    private async Task ShowPageAsync(string page)
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

    private async Task LoadDashboardPageAsync()
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var snapshot = await client.GetDashboardSnapshotAsync();
            var viewModel = ShellPageViewModelFactory.FromDashboard(
                snapshot,
                moduleId => ShowModuleDetailPageAsync(moduleId),
                commandId => ExecuteCommandAsync(commandId));

            _contentHost.Content = new DashboardView
            {
                DataContext = viewModel
            };
            SetStatus(viewModel.Subtitle);
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListModulesAsync();
            var viewModel = ShellPageViewModelFactory.FromModules(
                response,
                moduleId => ShowModuleDetailPageAsync(moduleId),
                moduleId => LoadSettingsPageAsync(moduleId),
                moduleId => LoadLogsPageAsync(moduleId),
                (moduleId, enabled) => SetModuleEnabledAsync(moduleId, enabled));

            _contentHost.Content = new ModulesView
            {
                DataContext = viewModel
            };
            SetStatus($"{viewModel.Modules.Count} modules loaded");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var detail = await client.GetModuleDetailAsync(moduleId);
            var commands = await client.ListCommandsAsync(moduleId);
            var viewModel = ShellPageViewModelFactory.FromModuleDetail(
                detail,
                commands,
                (targetModuleId, enabled) => SetModuleEnabledAsync(targetModuleId, enabled, showDetail: true),
                commandId => ExecuteCommandAsync(commandId));

            _contentHost.Content = new ModuleDetailView
            {
                DataContext = viewModel
            };
            SetStatus($"{detail.DisplayName} detail loaded");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var modules = await client.ListModulesAsync();
            var selected = PickModule(modules, selectedModuleId);
            if (selected is null)
            {
                var emptyViewModel = ShellPageViewModelFactory.FromSettings(
                    modules,
                    null,
                    "",
                    new JsonObject(),
                    "{}",
                    0,
                    DateTimeOffset.MinValue,
                    moduleId => LoadSettingsPageAsync(moduleId),
                    SaveSettingsPageAsync);
                _contentHost.Content = new SettingsCenterView
                {
                    DataContext = emptyViewModel
                };
                SetStatus(emptyViewModel.StatusText);
                return;
            }

            var schema = await client.GetSettingsSchemaAsync(selected.ModuleId);
            var snapshot = await client.GetSettingsAsync(selected.ModuleId);
            var values = JsonStructMapper.ToJsonObject(snapshot.Values);
            var viewModel = ShellPageViewModelFactory.FromSettings(
                modules,
                selected,
                schema.SchemaJson,
                values,
                PrettyJson(snapshot.Values),
                snapshot.Revision,
                snapshot.UpdatedAt.ToDateTimeOffset(),
                moduleId => LoadSettingsPageAsync(moduleId),
                SaveSettingsPageAsync);

            _contentHost.Content = new SettingsCenterView
            {
                DataContext = viewModel
            };
            SetStatus(viewModel.StatusText);
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var modules = await client.ListModulesAsync();
            var selected = PickModule(modules, selectedModuleId);
            IReadOnlyList<HostProto.LogEntry> entries = selected is null
                ? []
                : await client.TailLogsAsync(selected.ModuleId);
            var viewModel = ShellPageViewModelFactory.FromLogs(
                modules,
                selected,
                entries,
                moduleId => LoadLogsPageAsync(moduleId));

            _contentHost.Content = new LogsView
            {
                DataContext = viewModel
            };
            SetStatus(selected is null
                ? "No modules."
                : $"{entries.Count} log entries for {selected.ModuleId}");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListNotificationsAsync(80);
            var viewModel = ShellPageViewModelFactory.FromNotifications(response);

            _contentHost.Content = new NotificationsView
            {
                DataContext = viewModel
            };
            SetStatus($"{viewModel.Notifications.Count} notifications loaded");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListPackagesAsync();
            var viewModel = ShellPageViewModelFactory.FromPackages(
                response,
                sourceDirectory => RunPackageOperationAsync("install", sourceDirectory),
                packageId => RunPackageOperationAsync("rollback", packageId),
                packageId => RunPackageOperationAsync("repair", packageId),
                packageId => RunPackageOperationAsync("uninstall", packageId),
                moduleId => ShowModuleDetailPageAsync(moduleId));

            _contentHost.Content = new PackageManagerView
            {
                DataContext = viewModel
            };
            SetStatus($"{response.Packages.Count} packages loaded");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var diagnostics = await client.GetRuntimeDiagnosticsAsync();
            var audit = await client.ListBrokerAuditAsync(5);
            var viewModel = ShellPageViewModelFactory.FromDiagnostics(
                diagnostics,
                audit,
                (transportKind, poolKey) => RestartRuntimeProcessAsync(transportKind, poolKey),
                (transportKind, poolKey, paused, expiresAt, reason) => SetRuntimeProcessRestartPolicyAsync(
                    transportKind,
                    poolKey,
                    paused,
                    expiresAt,
                    reason));

            _contentHost.Content = new DiagnosticsView
            {
                DataContext = viewModel
            };
            SetStatus($"Diagnostics loaded for {diagnostics.Counts.ModuleCount} modules");
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListCommandsAsync(query);
            if (response.Commands.Count > 30)
            {
                var limited = new HostProto.ListCommandsResponse();
                limited.Commands.AddRange(response.Commands.Take(30));
                response = limited;
            }

            var viewModel = ShellPageViewModelFactory.FromCommands(query, response, commandId => ExecuteCommandAsync(commandId));
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
            using var client = HostControlClient.ForDefaultEndpoint();
            var audit = await client.ListBrokerAuditAsync(6);
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = ShellPageViewModelFactory.FromBrokerAudit(audit)
            };
        }
        catch (Exception ex)
        {
            _auditPanel.Content = new BrokerAuditView
            {
                DataContext = ShellPageViewModelFactory.FromBrokerAuditError(ex.Message)
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

    private static HostProto.ModuleSummary? PickModule(HostProto.ListModulesResponse modules, string? selectedModuleId)
    {
        if (!string.IsNullOrWhiteSpace(selectedModuleId))
        {
            var selected = modules.Modules.FirstOrDefault(module => string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return modules.Modules.OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string PrettyJson(Struct value)
    {
        if (value.Fields.Count == 0)
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value.ToString());
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return value.ToString();
        }
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
