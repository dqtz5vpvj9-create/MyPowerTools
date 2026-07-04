using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
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

    private readonly MptSidebar _navigation = new();
    private readonly MptSearchBox _searchBox = new();
    private readonly ContentControl _contentHost = new();
    private readonly ContentControl _commandPanel = new();
    private readonly StackPanel _permissionPanel = new();
    private readonly StackPanel _auditPanel = new();
    private readonly TextBlock _runnerStatus = new();
    private readonly TextBlock _statusBar = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostControlConnectionMonitor _connectionMonitor = new(new HostControlRunnerConnectionProbe());
    private readonly HostControlEventStreamMonitor _eventStream = new(new HostControlClientEventSource());
    private string _currentPage = DashboardPage;

    public MainWindow()
    {
        Title = "MyPowerTools";
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;

        Content = BuildLayout();
        KeyDown += OnShellKeyDown;
        _connectionMonitor.StateChanged += (_, snapshot) =>
        {
            Dispatcher.UIThread.Post(async () => await ApplyConnectionSnapshotAsync(snapshot, refreshOnRecovery: true));
        };
        _eventStream.EventReceived += (_, evt) =>
        {
            Dispatcher.UIThread.Post(async () => await ApplyHostEventAsync(evt));
        };
        _eventStream.StreamFaulted += (_, ex) =>
        {
            Dispatcher.UIThread.Post(() => _statusBar.Text = $"Host event stream reconnecting: {ex.Message}");
        };
        Opened += async (_, _) =>
        {
            _connectionMonitor.Start();
            _eventStream.Start();
            await RefreshAsync();
        };
        Closed += async (_, _) =>
        {
            await _eventStream.DisposeAsync();
            await _connectionMonitor.DisposeAsync();
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("220,*,360"),
            RowDefinitions = new RowDefinitions("64,*,32"),
            Background = MptTheme.AppBackground
        };

        _navigation.Children.Add(new TextBlock
        {
            Text = "MyPowerTools",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        });
        foreach (var label in new[] { DashboardPage, ModulesPage, SettingsPage, LogsPage, NotificationsPage, PackagesPage, DiagnosticsPage })
        {
            _navigation.Children.Add(NavButton(label));
        }

        Grid.SetRowSpan(_navigation, 3);
        root.Children.Add(_navigation);

        var topBar = new MptTopBar();
        _searchBox.TextChanged += async (_, _) => await LoadCommandsAsync(_searchBox.Text ?? "");
        topBar.Children.Add(_searchBox);

        var refresh = new MptActionButton("Refresh")
        {
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        refresh.Click += async (_, _) => await RefreshAsync();
        Grid.SetColumn(refresh, 1);
        topBar.Children.Add(refresh);
        Grid.SetColumn(topBar, 1);
        Grid.SetColumnSpan(topBar, 2);
        root.Children.Add(topBar);

        var content = new ScrollViewer
        {
            Content = _contentHost,
            Margin = new Thickness(16, 4, 16, 12)
        };
        Grid.SetColumn(content, 1);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        _permissionPanel.Spacing = 8;
        _auditPanel.Spacing = 6;
        var commandHost = new Border
        {
            Margin = new Thickness(0, 4, 16, 12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(8),
            BorderBrush = MptTheme.Border,
            BorderThickness = new Thickness(1),
            Background = MptTheme.CardBackground,
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    Header("Command Palette"),
                    DockedTop(_permissionPanel),
                    DockedBottom(_auditPanel),
                    new ScrollViewer
                    {
                        Content = _commandPanel
                    }
                }
            }
        };
        Grid.SetColumn(commandHost, 2);
        Grid.SetRow(commandHost, 1);
        root.Children.Add(commandHost);

        var status = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(16, 0, 16, 8)
        };
        _statusBar.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(_statusBar);
        _runnerStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_runnerStatus, 1);
        status.Children.Add(_runnerStatus);
        Grid.SetColumn(status, 1);
        Grid.SetColumnSpan(status, 2);
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        return root;
    }

    private async Task RefreshAsync()
    {
        await LoadRunnerStatusAsync();
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
            _statusBar.Text = ex.Message;
        }
    }

    private async Task ApplyKeyboardShortcutAsync(ShellKeyboardShortcutResult shortcut)
    {
        switch (shortcut.Action)
        {
            case ShellKeyboardAction.FocusCommandPalette:
                _searchBox.Focus();
                _searchBox.SelectAll();
                _statusBar.Text = "Command Palette focused.";
                await LoadCommandsAsync(_searchBox.Text ?? "");
                break;
            case ShellKeyboardAction.ClearCommandPalette:
                _searchBox.Text = "";
                _contentHost.Focus();
                _statusBar.Text = "Command Palette cleared.";
                await LoadCommandsAsync("");
                break;
            case ShellKeyboardAction.Refresh:
                await RefreshAsync();
                _statusBar.Text = $"{_currentPage} refreshed.";
                break;
            case ShellKeyboardAction.Navigate when shortcut.TargetPage is not null:
                await ShowPageAsync(shortcut.TargetPage);
                break;
        }
    }

    private async Task LoadRunnerStatusAsync()
    {
        var snapshot = await _connectionMonitor.CheckOnceAsync(notify: false);
        ApplyRunnerStatus(snapshot);
        if (!snapshot.Online)
        {
            _statusBar.Text = $"Runner offline: {snapshot.Message}";
        }
    }

    private async Task ApplyConnectionSnapshotAsync(HostControlConnectionSnapshot snapshot, bool refreshOnRecovery)
    {
        ApplyRunnerStatus(snapshot);
        if (!snapshot.Online)
        {
            _statusBar.Text = $"Runner offline: {snapshot.Message}";
            return;
        }

        if (snapshot.Recovered && refreshOnRecovery)
        {
            _statusBar.Text = "Runner connection restored.";
            await ShowPageAsync(_currentPage);
            await LoadCommandsAsync(_searchBox.Text ?? "");
            await LoadBrokerAuditAsync();
        }
    }

    private void ApplyRunnerStatus(HostControlConnectionSnapshot snapshot)
    {
        _runnerStatus.Text = snapshot.Online ? $"Runner {snapshot.State}" : "Runner offline";
    }

    private async Task ApplyHostEventAsync(HostProto.HostEvent evt)
    {
        _statusBar.Text = $"Event {evt.Seq}: {evt.Type}";
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
        UpdateNavigationState();
        _statusBar.Text = $"Loading {page}";

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
            _statusBar.Text = viewModel.Subtitle;
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(DashboardPage, ex.Message);
            _statusBar.Text = ex.Message;
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
            _statusBar.Text = $"{viewModel.Modules.Count} modules loaded";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(ModulesPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private async Task ShowModuleDetailPageAsync(string moduleId)
    {
        _currentPage = ModulesPage;
        UpdateNavigationState();

        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var detail = await client.GetModuleDetailAsync(moduleId);
            var commands = await client.ListCommandsAsync(moduleId);
            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(BuildModuleHero(detail));
            body.Children.Add(Section(
                "Declared Permissions",
                detail.Permissions.Count == 0 ? new MptEmptyState("No broker permissions declared.") : BuildPermissionList(detail.Permissions)));
            body.Children.Add(Section(
                "Capability Requirements",
                detail.Requirements.Count == 0 ? new MptEmptyState("No capability requirements declared.") : BuildRequirementList(detail.Requirements)));

            var diagnostics = new StackPanel { Spacing = 8 };
            foreach (var item in detail.Diagnostics)
            {
                diagnostics.Children.Add(BuildDiagnostic(item));
            }
            body.Children.Add(Section("Diagnostics", diagnostics.Children.Count == 0 ? new MptEmptyState("No diagnostics.") : diagnostics));

            var commandList = new StackPanel { Spacing = 8 };
            foreach (var command in commands.Commands.Where(command => command.ModuleId == moduleId).Take(12))
            {
                commandList.Children.Add(BuildCommand(command));
            }
            body.Children.Add(Section("Commands", commandList.Children.Count == 0 ? new MptEmptyState("No commands.") : commandList));

            _contentHost.Content = BuildPage(detail.DisplayName, detail.PackageId, body);
            _statusBar.Text = $"{detail.DisplayName} detail loaded";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage("Module Detail", ex.Message);
            _statusBar.Text = ex.Message;
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
                _statusBar.Text = emptyViewModel.StatusText;
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
            _statusBar.Text = viewModel.StatusText;
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(SettingsPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private async Task SaveSettingsPageAsync(SettingsCenterViewModel viewModel)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var patch = JsonStructMapper.ToStruct(ShellPageViewModelFactory.BuildSettingsPatch(viewModel));
            var updated = await client.UpdateSettingsAsync(viewModel.SelectedModuleId, viewModel.Revision, patch);
            _statusBar.Text = $"{viewModel.SelectedModuleId} settings saved at revision {updated.Revision}";
            await LoadSettingsPageAsync(viewModel.SelectedModuleId);
        }
        catch (RpcException ex)
        {
            viewModel.StatusText = ex.Status.Detail;
            _statusBar.Text = ex.Status.Detail;
        }
        catch (Exception ex)
        {
            viewModel.StatusText = ex.Message;
            _statusBar.Text = ex.Message;
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
            _statusBar.Text = selected is null
                ? "No modules."
                : $"{entries.Count} log entries for {selected.ModuleId}";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(LogsPage, ex.Message);
            _statusBar.Text = ex.Message;
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
            _statusBar.Text = $"{viewModel.Notifications.Count} notifications loaded";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(NotificationsPage, ex.Message);
            _statusBar.Text = ex.Message;
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
                sourceDirectory => RunPackageOperationAsync(
                    "install",
                    sourceDirectory,
                    client => client.InstallPackageAsync(sourceDirectory)),
                packageId => RunPackageOperationAsync(
                    "rollback",
                    packageId,
                    client => client.RollbackPackageAsync(packageId)),
                packageId => RunPackageOperationAsync(
                    "repair",
                    packageId,
                    client => client.RepairPackageAsync(packageId)),
                packageId => RunPackageOperationAsync(
                    "uninstall",
                    packageId,
                    client => client.UninstallPackageAsync(packageId)),
                moduleId => ShowModuleDetailPageAsync(moduleId));

            _contentHost.Content = new PackageManagerView
            {
                DataContext = viewModel
            };
            _statusBar.Text = $"{response.Packages.Count} packages loaded";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(PackagesPage, ex.Message);
            _statusBar.Text = ex.Message;
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
            _statusBar.Text = $"Diagnostics loaded for {diagnostics.Counts.ModuleCount} modules";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(DiagnosticsPage, ex.Message);
            _statusBar.Text = ex.Message;
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
            _auditPanel.Children.Clear();
            _auditPanel.Children.Add(new TextBlock
            {
                Text = "Broker Audit",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 8, 0, 2)
            });

            foreach (var entry in audit.Entries)
            {
                _auditPanel.Children.Add(BuildAuditEntry(entry));
            }

            if (audit.Entries.Count == 0)
            {
                _auditPanel.Children.Add(new TextBlock
                {
                    Text = "No broker audit entries.",
                    FontSize = 12,
                    Foreground = MptTheme.TextSecondary
                });
            }
        }
        catch (Exception ex)
        {
            _auditPanel.Children.Clear();
            _auditPanel.Children.Add(new TextBlock
            {
                Text = $"Audit unavailable: {ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                    Foreground = MptTheme.Warning,
                FontSize = 12
            });
        }
    }

    private Control BuildModuleHero(HostProto.ModuleDetail detail)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(HeaderWithBadge(detail.DisplayName, detail.State));
        panel.Children.Add(new TextBlock
        {
            Text = detail.Summary,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MptTheme.TextSecondary
        });
        panel.Children.Add(BuildMetricRow([
            ("Package", detail.PackageId),
            ("Module", detail.ModuleId),
            ("Diagnostics", detail.Diagnostics.Count.ToString()),
            ("Permissions", detail.Permissions.Count.ToString())
        ]));
        var enabled = detail.State != "disabled";
        var toggle = new MptActionButton(enabled ? "Disable" : "Enable");
        toggle.Click += async (_, _) => await SetModuleEnabledAsync(detail.ModuleId, !enabled, showDetail: true);
        panel.Children.Add(toggle);

        return new MptModuleCard(panel);
    }

    private Control BuildPermissionList(IEnumerable<HostProto.ModulePermission> permissions)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var permission in permissions.OrderBy(permission => permission.Level, StringComparer.OrdinalIgnoreCase).ThenBy(permission => permission.Id, StringComparer.OrdinalIgnoreCase))
        {
            var capability = string.IsNullOrWhiteSpace(permission.Capability) ? "No capability" : permission.Capability;
            list.Children.Add(new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    HeaderWithBadge(permission.Id, permission.Level),
                    DetailLine("Capability", capability),
                    DetailLine("Reason", permission.Reason)
                }
            });
        }

        return list;
    }

    private Control BuildRequirementList(IEnumerable<HostProto.ModuleRequirement> requirements)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var requirement in requirements.OrderByDescending(requirement => requirement.Required).ThenBy(requirement => requirement.Capability, StringComparer.OrdinalIgnoreCase))
        {
            list.Children.Add(new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    HeaderWithBadge(requirement.Capability, requirement.Required ? "required" : "optional"),
                    DetailLine("Reason", requirement.Reason)
                }
            });
        }

        return list;
    }

    private Control BuildDiagnostic(HostProto.Diagnostic diagnostic)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(HeaderWithBadge(diagnostic.Label, diagnostic.State));
        panel.Children.Add(new TextBlock
        {
            Text = diagnostic.Detail,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MptTheme.TextSecondary
        });
        return new MptModuleCard(panel);
    }

    private Control BuildCommand(HostProto.CommandItem command)
    {
        var item = new MptCommandItem(command.Title, command.Subtitle);
        var button = new Button
        {
            Content = item,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Tag = command.CommandId,
            MinHeight = 58
        };
        button.Click += async (_, _) => await ExecuteCommandAsync((string)button.Tag!);
        return button;
    }

    private Control BuildPermissionPrompt(HostProto.CommandExecutionResponse result)
    {
        var details = result.ErrorDetails;
        var actionId = ReadString(details, "actionId", result.ErrorCode);
        var scope = ReadString(details, "scope", "");
        var reason = ReadString(details, "reason", result.ErrorMessage);
        var applyCount = CountNestedList(details, "expectedChange", "apply");
        var removeCount = CountNestedList(details, "expectedChange", "remove");
        var rollbackCount = CountList(details, "rollback");

        var rows = new StackPanel { Spacing = 5 };
        rows.Children.Add(new TextBlock
        {
            Text = "Permission Required",
            FontWeight = FontWeight.SemiBold,
            Foreground = MptTheme.Warning
        });
        rows.Children.Add(DetailLine("Action", actionId));
        rows.Children.Add(DetailLine("Scope", scope));
        rows.Children.Add(DetailLine("Reason", reason));
        rows.Children.Add(DetailLine("Expected change", $"{applyCount} apply, {removeCount} remove"));
        rows.Children.Add(DetailLine("Rollback", $"{rollbackCount} step(s)"));

        var auditButton = new MptActionButton("Audit");
        auditButton.Click += async (_, _) => await LoadBrokerAuditAsync();
        rows.Children.Add(auditButton);

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(8),
            BorderBrush = MptTheme.Warning,
            BorderThickness = new Thickness(1),
            Background = MptTheme.WarningBackground,
            Child = rows
        };
    }

    private Control BuildAuditEntry(HostProto.BrokerAuditEntry entry)
    {
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = $"{entry.Result} · {entry.ActionId}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = $"{entry.ModuleId} · {entry.Scope}",
                    FontSize = 12,
                    Foreground = MptTheme.TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
    }

    private Control BuildPage(string title, string subtitle, Control body)
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 28,
            FontWeight = FontWeight.SemiBold
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = MptTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap
            });
        }
        panel.Children.Add(body);
        return panel;
    }

    private Control BuildUnavailablePage(string title, string message)
    {
        return BuildPage(title, "", new MptErrorState(message));
    }

    private Control Section(string title, Control content)
    {
        return new MptModuleCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                },
                content
            }
        });
    }

    private Control HeaderWithBadge(string title, string state)
    {
        var header = new DockPanel { LastChildFill = true };
        var badge = new MptStatusBadge(state);
        DockPanel.SetDock(badge, Dock.Right);
        header.Children.Add(badge);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        return header;
    }

    private Control BuildMetricRow(IReadOnlyList<(string Label, string Value)> metrics)
    {
        var row = new WrapPanel { ItemWidth = 140 };
        foreach (var metric in metrics)
        {
            row.Children.Add(new MptMetricTile(metric.Label, metric.Value)
            {
                Margin = new Thickness(0, 0, 8, 8)
            });
        }

        return row;
    }

    private Button NavButton(string label)
    {
        var button = new Button
        {
            Content = label,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            MinHeight = 36
        };
        button.Click += async (_, _) => await ShowPageAsync(label);
        _navButtons[label] = button;
        return button;
    }

    private void UpdateNavigationState()
    {
        foreach (var pair in _navButtons)
        {
            pair.Value.BorderBrush = pair.Key == _currentPage ? MptTheme.Accent : MptTheme.Border;
        }
    }

    private static TextBlock Header(string text)
    {
        var header = new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(header, Dock.Top);
        return header;
    }

    private static Control DockedTop(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private static Control DockedBottom(Control control)
    {
        DockPanel.SetDock(control, Dock.Bottom);
        return control;
    }

    private async Task ExecuteCommandAsync(string commandId)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var result = await client.ExecuteCommandAsync(commandId);
            _statusBar.Text = $"{result.State}: {result.Summary}";
            _permissionPanel.Children.Clear();
            if (result.State == "permission-required")
            {
                _permissionPanel.Children.Add(BuildPermissionPrompt(result));
            }

            await LoadBrokerAuditAsync();
            if (_currentPage == NotificationsPage)
            {
                await LoadNotificationsPageAsync();
            }
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    private async Task RunPackageOperationAsync(string operation, string target, Func<HostControlClient, Task<HostProto.PackageOperationResult>> action)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            _statusBar.Text = $"{operation}: target is required.";
            return;
        }

        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var result = await action(client);
            var status = $"{result.Operation} {result.PackageId}: {result.Message}";
            if (result.Issues.Count > 0)
            {
                status = $"{result.Operation} {result.PackageId}: {result.Issues[0].Severity}: {result.Issues[0].Message}";
            }

            await LoadPackagesPageAsync();
            await LoadCommandsAsync(_searchBox.Text ?? "");
            _statusBar.Text = status;
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    private async Task RestartRuntimeProcessAsync(string transportKind, string poolKey)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var result = await client.RestartRuntimeProcessAsync(transportKind, poolKey);
            _statusBar.Text = $"{result.State}: {result.Message}";
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    private async Task SetRuntimeProcessRestartPolicyAsync(string transportKind, string poolKey, bool paused, DateTimeOffset? expiresAt = null, string? reason = null)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var result = await client.SetRuntimeProcessRestartPolicyAsync(transportKind, poolKey, paused, reason ?? "Shell Diagnostics action", source: "shell", expiresAt: expiresAt);
            _statusBar.Text = $"{result.State}: {result.Message}";
            await LoadDiagnosticsPageAsync();
        }
        catch (Exception ex)
        {
            _statusBar.Text = ex.Message;
        }
    }

    private async Task SetModuleEnabledAsync(string moduleId, bool enabled, bool showDetail = false)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var detail = await client.SetModuleEnabledAsync(moduleId, enabled);
            _statusBar.Text = $"{detail.DisplayName} {(enabled ? "enabled" : "disabled")}";
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
            _statusBar.Text = ex.Message;
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

    private static TextBlock DetailLine(string label, string value)
    {
        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? $"{label}: -" : $"{label}: {value}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = MptTheme.TextPrimary
        };
    }

    private static string ReadString(Struct details, string key, string fallback)
    {
        if (details.Fields.TryGetValue(key, out var value))
        {
            return ValueToText(value);
        }

        return fallback;
    }

    private static int CountNestedList(Struct details, string objectKey, string listKey)
    {
        if (details.Fields.TryGetValue(objectKey, out var outer) &&
            outer.KindCase == Value.KindOneofCase.StructValue &&
            outer.StructValue.Fields.TryGetValue(listKey, out var inner) &&
            inner.KindCase == Value.KindOneofCase.ListValue)
        {
            return inner.ListValue.Values.Count;
        }

        return 0;
    }

    private static int CountList(Struct details, string key)
    {
        if (details.Fields.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.ListValue)
        {
            return value.ListValue.Values.Count;
        }

        return 0;
    }

    private static string ValueToText(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString("0.##"),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.ListValue => $"{value.ListValue.Values.Count} item(s)",
            Value.KindOneofCase.StructValue => $"{value.StructValue.Fields.Count} field(s)",
            _ => ""
        };
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
