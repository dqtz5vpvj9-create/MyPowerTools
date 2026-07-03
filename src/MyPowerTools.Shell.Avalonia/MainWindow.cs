using System.Globalization;
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
    private readonly StackPanel _commandPanel = new();
    private readonly StackPanel _permissionPanel = new();
    private readonly StackPanel _auditPanel = new();
    private readonly TextBlock _runnerStatus = new();
    private readonly TextBlock _statusBar = new();
    private readonly Dictionary<string, Button> _navButtons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HostControlConnectionMonitor _connectionMonitor = new(new HostControlRunnerConnectionProbe());
    private readonly HostControlEventStreamMonitor _eventStream = new(new HostControlClientEventSource());
    private readonly DesignTokens _tokens;
    private string _currentPage = DashboardPage;

    public MainWindow()
    {
        Title = "MyPowerTools";
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;

        _tokens = TryLoadTokens();
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

        _commandPanel.Spacing = 8;
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
            var panel = new WrapPanel
            {
                ItemWidth = Math.Min(_tokens.Layout.DashboardCardMaxWidth, 380),
                ItemHeight = 190
            };

            foreach (var alert in snapshot.Alerts)
            {
                panel.Children.Add(new MptErrorState($"{alert.Title}: {alert.Body}"));
            }

            foreach (var card in snapshot.Cards)
            {
                panel.Children.Add(BuildDashboardCard(card));
            }

            _contentHost.Content = BuildPage(DashboardPage, $"{snapshot.Cards.Count} modules indexed, event seq {snapshot.EventSeq}", panel);
            _statusBar.Text = $"{snapshot.Cards.Count} modules indexed, event seq {snapshot.EventSeq}";
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
            var list = new StackPanel { Spacing = 12 };
            foreach (var module in response.Modules.OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                list.Children.Add(BuildModuleSummaryCard(module));
            }

            _contentHost.Content = BuildPage(ModulesPage, $"{response.Modules.Count} modules", list);
            _statusBar.Text = $"{response.Modules.Count} modules loaded";
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
                _contentHost.Content = BuildPage(SettingsPage, "", new MptEmptyState("No modules."));
                return;
            }

            var editorHost = new StackPanel { Spacing = 12 };
            var body = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    BuildModulePicker(modules, selected.ModuleId, moduleId => LoadSettingsPageAsync(moduleId)),
                    editorHost
                }
            };
            _contentHost.Content = BuildPage(SettingsPage, selected.DisplayName, body);
            await FillSettingsEditorAsync(selected.ModuleId, editorHost);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(SettingsPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private async Task FillSettingsEditorAsync(string moduleId, StackPanel editorHost)
    {
        editorHost.Children.Clear();
        using var client = HostControlClient.ForDefaultEndpoint();
        var schema = await client.GetSettingsSchemaAsync(moduleId);
        var snapshot = await client.GetSettingsAsync(moduleId);
        var values = JsonStructMapper.ToJsonObject(snapshot.Values);
        var schemaObject = TryParseSettingsSchema(schema.SchemaJson);
        IReadOnlyList<SettingsFieldEditor> fields = schemaObject is null
            ? []
            : BuildSettingsFieldEditors(schemaObject, values);
        TextBox? rawEditor = null;

        var state = new TextBlock
        {
            Text = $"Revision {snapshot.Revision} · {snapshot.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss} · Schema fields {fields.Count}",
            Foreground = MptTheme.TextSecondary
        };
        editorHost.Children.Add(state);

        if (fields.Count > 0)
        {
            var section = new MptSettingsSection("Settings");
            foreach (var field in fields)
            {
                section.Children.Add(field.View);
            }

            editorHost.Children.Add(new MptModuleCard(section));
        }
        else
        {
            rawEditor = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = FontFamily.Parse("Consolas"),
                FontSize = 12,
                MinHeight = 280,
                Text = PrettyJson(snapshot.Values)
            };
            editorHost.Children.Add(rawEditor);
        }

        var save = new MptActionButton("Save settings");
        save.Click += async (_, _) =>
        {
            try
            {
                var patch = rawEditor is null
                    ? JsonStructMapper.ToStruct(BuildSettingsPatch(fields))
                    : Struct.Parser.ParseJson(string.IsNullOrWhiteSpace(rawEditor.Text) ? "{}" : rawEditor.Text);
                var updated = await client.UpdateSettingsAsync(moduleId, snapshot.Revision, patch);
                _statusBar.Text = $"{moduleId} settings saved at revision {updated.Revision}";
                await FillSettingsEditorAsync(moduleId, editorHost);
            }
            catch (RpcException ex)
            {
                _statusBar.Text = ex.Status.Detail;
            }
            catch (Exception ex)
            {
                _statusBar.Text = ex.Message;
            }
        };

        editorHost.Children.Add(save);
        _statusBar.Text = $"{moduleId} settings revision {snapshot.Revision}, schema fields {fields.Count}";
    }

    private async Task LoadLogsPageAsync(string? selectedModuleId = null)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var modules = await client.ListModulesAsync();
            var selected = PickModule(modules, selectedModuleId);
            if (selected is null)
            {
                _contentHost.Content = BuildPage(LogsPage, "", new MptEmptyState("No modules."));
                return;
            }

            var logHost = new StackPanel { Spacing = 12 };
            var body = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    BuildModulePicker(modules, selected.ModuleId, moduleId => LoadLogsPageAsync(moduleId)),
                    logHost
                }
            };
            _contentHost.Content = BuildPage(LogsPage, selected.DisplayName, body);
            await FillLogsAsync(selected.ModuleId, logHost);
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(LogsPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private async Task FillLogsAsync(string moduleId, StackPanel logHost)
    {
        logHost.Children.Clear();
        using var client = HostControlClient.ForDefaultEndpoint();
        var entries = await client.TailLogsAsync(moduleId);
        if (entries.Count == 0)
        {
            logHost.Children.Add(new MptEmptyState("No logs."));
        }
        else
        {
            logHost.Children.Add(new MptLogViewer
            {
                MinHeight = 420,
                ItemsSource = entries.Select(entry => $"{entry.Time.ToDateTimeOffset():HH:mm:ss} {entry.Level,-5} {entry.Message}").ToArray()
            });
        }

        _statusBar.Text = $"{entries.Count} log entries for {moduleId}";
    }

    private async Task LoadNotificationsPageAsync()
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListNotificationsAsync(80);
            var list = new StackPanel { Spacing = 10 };
            foreach (var item in response.Notifications)
            {
                list.Children.Add(new MptNotificationItem(
                    $"{item.Level} · {item.Title}",
                    $"{item.ModuleId} · {item.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}\n{item.Body}"));
            }

            _contentHost.Content = BuildPage(
                NotificationsPage,
                $"{response.Notifications.Count} notifications",
                list.Children.Count == 0 ? new MptEmptyState("No notifications.") : list);
            _statusBar.Text = $"{response.Notifications.Count} notifications loaded";
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
            var list = new StackPanel { Spacing = 12 };
            list.Children.Add(BuildPackageOperationsPanel());
            foreach (var package in response.Packages.OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(HeaderWithBadge(package.DisplayName, package.TrustState));
                body.Children.Add(BuildMetricRow([
                    ("Version", package.Version),
                    ("Modules", package.ModuleCount.ToString()),
                    ("Runtimes", package.SharedRuntimeCount.ToString()),
                    ("Trust", string.IsNullOrWhiteSpace(package.TrustPolicy) ? "-" : package.TrustPolicy),
                    ("Issues", package.TrustIssueCount.ToString()),
                    ("Hashes", string.IsNullOrWhiteSpace(package.Hashes) ? "-" : package.Hashes),
                    ("Signature", string.IsNullOrWhiteSpace(package.SignaturePath) ? "-" : package.SignaturePath)
                ]));
                body.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", package.ModuleIds),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = MptTheme.TextSecondary
                });
                body.Children.Add(new TextBlock
                {
                    Text = package.Directory,
                    FontSize = 12,
                    Foreground = MptTheme.TextMuted,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var moduleId in package.ModuleIds.Take(3))
                {
                    var open = new Button
                    {
                        Content = new TextBlock
                        {
                            Text = moduleId,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = 150
                        },
                        Tag = moduleId,
                        MinHeight = 32,
                        MaxWidth = 180
                    };
                    open.Click += async (_, _) => await ShowModuleDetailPageAsync((string)open.Tag!);
                    actions.Children.Add(open);
                }
                body.Children.Add(actions);
                body.Children.Add(BuildPackageActionRow(package.PackageId));
                list.Children.Add(new MptModuleCard(body));
            }

            _contentHost.Content = BuildPage(PackagesPage, $"{response.Packages.Count} packages", list);
            _statusBar.Text = $"{response.Packages.Count} packages loaded";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(PackagesPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private Control BuildPackageOperationsPanel()
    {
        var installPath = new TextBox
        {
            PlaceholderText = "Package source directory",
            MinWidth = 360,
            MinHeight = 34
        };
        var install = new MptActionButton("Install");
        install.Click += async (_, _) => await RunPackageOperationAsync(
            "install",
            installPath.Text ?? "",
            client => client.InstallPackageAsync(installPath.Text ?? ""));

        var rollbackId = new TextBox
        {
            PlaceholderText = "Package id",
            MinWidth = 220,
            MinHeight = 34
        };
        var rollback = new MptActionButton("Rollback");
        rollback.Click += async (_, _) => await RunPackageOperationAsync(
            "rollback",
            rollbackId.Text ?? "",
            client => client.RollbackPackageAsync(rollbackId.Text ?? ""));

        var installRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { installPath, install }
        };
        var rollbackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { rollbackId, rollback }
        };

        return Section("Package Operations", new StackPanel
        {
            Spacing = 10,
            Children = { installRow, rollbackRow }
        });
    }

    private Control BuildPackageActionRow(string packageId)
    {
        var repair = new MptActionButton("Repair") { Tag = packageId };
        repair.Click += async (_, _) => await RunPackageOperationAsync(
            "repair",
            packageId,
            client => client.RepairPackageAsync(packageId));

        var uninstall = new MptActionButton("Uninstall") { Tag = packageId };
        uninstall.Click += async (_, _) => await RunPackageOperationAsync(
            "uninstall",
            packageId,
            client => client.UninstallPackageAsync(packageId));

        var rollback = new MptActionButton("Rollback") { Tag = packageId };
        rollback.Click += async (_, _) => await RunPackageOperationAsync(
            "rollback",
            packageId,
            client => client.RollbackPackageAsync(packageId));

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { repair, uninstall, rollback }
        };
    }

    private async Task LoadDiagnosticsPageAsync()
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var diagnostics = await client.GetRuntimeDiagnosticsAsync();
            var audit = await client.ListBrokerAuditAsync(5);
            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(BuildMetricRow([
                ("Runner", diagnostics.RunnerVersion),
                ("Host IPC", diagnostics.HostControlProtocolVersion),
                ("Module IPC", diagnostics.ModuleProtocolVersion),
                ("Platform", diagnostics.PlatformRid)
            ]));
            body.Children.Add(BuildMetricRow([
                ("Packages", diagnostics.Counts.PackageCount.ToString()),
                ("Modules", diagnostics.Counts.ModuleCount.ToString()),
                ("Enabled", diagnostics.Counts.EnabledModuleCount.ToString()),
                ("Commands", diagnostics.Counts.CommandCount.ToString())
            ]));
            body.Children.Add(BuildMetricRow([
                ("Running", diagnostics.Counts.RunningModuleCount.ToString()),
                ("Degraded", diagnostics.Counts.DegradedModuleCount.ToString()),
                ("Errors", diagnostics.Counts.ErrorModuleCount.ToString()),
                ("Event Seq", diagnostics.CurrentEventSeq.ToString())
            ]));

            body.Children.Add(Section("Runtime Paths", BuildPathDiagnostics(diagnostics.Paths)));

            var transportList = new StackPanel { Spacing = 8 };
            foreach (var transport in diagnostics.Transports)
            {
                transportList.Children.Add(BuildRuntimeTransportDiagnostic(transport));
            }
            body.Children.Add(Section("Transports", transportList.Children.Count == 0 ? new MptEmptyState("No transports.") : transportList));

            var processList = new StackPanel { Spacing = 8 };
            foreach (var process in diagnostics.Processes)
            {
                processList.Children.Add(BuildRuntimeProcessDiagnostic(process));
            }
            body.Children.Add(Section("Runtime Processes", processList.Children.Count == 0 ? new MptEmptyState("No transport processes.") : processList));

            var policyHistory = new StackPanel { Spacing = 8 };
            foreach (var entry in diagnostics.ProcessPolicyHistory)
            {
                policyHistory.Children.Add(BuildRuntimeProcessPolicyHistoryEntry(entry));
            }
            body.Children.Add(Section("Process Policy History", policyHistory.Children.Count == 0 ? new MptEmptyState("No process policy history.") : policyHistory));

            var moduleList = new StackPanel { Spacing = 8 };
            foreach (var module in diagnostics.Modules)
            {
                moduleList.Children.Add(BuildRuntimeModuleDiagnostic(module));
            }
            body.Children.Add(Section("Modules", moduleList.Children.Count == 0 ? new MptEmptyState("No modules.") : moduleList));

            var commandHistory = new StackPanel { Spacing = 8 };
            foreach (var command in diagnostics.RecentCommands)
            {
                commandHistory.Children.Add(BuildRuntimeCommandHistoryEntry(command));
            }
            body.Children.Add(Section("Command History", commandHistory.Children.Count == 0 ? new MptEmptyState("No command history.") : commandHistory));

            var auditList = new StackPanel { Spacing = 8 };
            foreach (var entry in audit.Entries)
            {
                auditList.Children.Add(BuildAuditEntry(entry));
            }
            body.Children.Add(Section("Broker Audit", auditList.Children.Count == 0 ? new MptEmptyState("No broker audit entries.") : auditList));

            _contentHost.Content = BuildPage(DiagnosticsPage, $"Collected {diagnostics.CollectedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}", body);
            _statusBar.Text = $"Diagnostics loaded for {diagnostics.Counts.ModuleCount} modules";
        }
        catch (Exception ex)
        {
            _contentHost.Content = BuildUnavailablePage(DiagnosticsPage, ex.Message);
            _statusBar.Text = ex.Message;
        }
    }

    private static Control BuildPathDiagnostics(HostProto.RuntimePathDiagnostics paths)
    {
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                DetailLine("Root", paths.Root),
                DetailLine("Settings", paths.Settings),
                DetailLine("Logs", paths.Logs),
                DetailLine("State", paths.State),
                DetailLine("Packages", paths.Packages),
                DetailLine("Package Root", paths.PackageRoot)
            }
        };
    }

    private Control BuildRuntimeTransportDiagnostic(HostProto.RuntimeTransportDiagnostics transport)
    {
        var state = transport.RuntimeRegistered ? "registered" : "manifest";
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(HeaderWithBadge(transport.Kind, state));
        panel.Children.Add(DetailLine("Modules", transport.ModuleCount.ToString()));
        return new MptModuleCard(panel);
    }

    private Control BuildRuntimeProcessDiagnostic(HostProto.RuntimeProcessDiagnostics process)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(HeaderWithBadge(process.PoolKey, process.State));
        panel.Children.Add(DetailLine("Transport", process.TransportKind));
        panel.Children.Add(DetailLine("Process", process.ProcessId == 0 ? "external" : process.ProcessId.ToString()));
        panel.Children.Add(DetailLine("Endpoint", process.Endpoint));
        panel.Children.Add(DetailLine("Starts", $"{process.StartCount}/{process.RestartLimit}"));
        panel.Children.Add(DetailLine("Policy", string.IsNullOrWhiteSpace(process.PolicyReason) ? process.RestartPolicy : $"{process.RestartPolicy} · {process.PolicyReason}"));
        if (process.PolicyExpiresAt is not null)
        {
            panel.Children.Add(DetailLine("Expires", process.PolicyExpiresAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        panel.Children.Add(DetailLine("Modules", process.ModuleIds.Count == 0 ? "none" : string.Join(", ", process.ModuleIds)));
        if (process.LastStartedAt is not null)
        {
            panel.Children.Add(DetailLine("Started", process.LastStartedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var restart = new MptActionButton("Restart");
        restart.Click += async (_, _) => await RestartRuntimeProcessAsync(process.TransportKind, process.PoolKey);
        actions.Children.Add(restart);
        var paused = string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase);
        var policy = new MptActionButton(paused ? "Resume" : "Pause");
        policy.Click += async (_, _) => await SetRuntimeProcessRestartPolicyAsync(process.TransportKind, process.PoolKey, !paused);
        actions.Children.Add(policy);
        if (!paused)
        {
            var maintenance = new MptActionButton("Pause 1h");
            maintenance.Click += async (_, _) => await SetRuntimeProcessRestartPolicyAsync(
                process.TransportKind,
                process.PoolKey,
                true,
                DateTimeOffset.UtcNow.AddHours(1),
                "Shell Diagnostics maintenance window");
            actions.Children.Add(maintenance);
        }

        panel.Children.Add(actions);

        return new MptModuleCard(panel);
    }

    private static Control BuildRuntimeProcessPolicyHistoryEntry(HostProto.RuntimeProcessPolicyHistoryEntry entry)
    {
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = $"{entry.RestartPolicy} · {entry.PoolKey}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = $"{entry.Source} · {entry.TransportKind} · rev {entry.Revision} · {entry.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
                    FontSize = 11,
                    Foreground = MptTheme.TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = entry.ExpiresAt is null
                        ? (string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)
                        : $"{(string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)} · expires {entry.ExpiresAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
                    FontSize = 11,
                    Foreground = MptTheme.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
    }

    private Control BuildRuntimeModuleDiagnostic(HostProto.RuntimeModuleDiagnostics module)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(HeaderWithBadge(module.DisplayName, module.State));
        panel.Children.Add(DetailLine("Module", module.ModuleId));
        panel.Children.Add(DetailLine("Package", module.PackageId));
        panel.Children.Add(DetailLine("Transport", module.TransportKind));
        panel.Children.Add(DetailLine("Diagnostics", module.DiagnosticCount.ToString()));
        panel.Children.Add(DetailLine("Updated", module.UpdatedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")));
        return new MptModuleCard(panel);
    }

    private static Control BuildRuntimeCommandHistoryEntry(HostProto.RuntimeCommandHistoryEntry command)
    {
        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = $"{command.State} · {command.CommandId}",
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = $"{command.ModuleId} · {command.StartedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
                    FontSize = 12,
                    Foreground = MptTheme.TextSecondary,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
    }

    private async Task LoadCommandsAsync(string query)
    {
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListCommandsAsync(query);
            _commandPanel.Children.Clear();

            foreach (var command in response.Commands.Take(30))
            {
                _commandPanel.Children.Add(BuildCommand(command));
            }

            if (response.Commands.Count == 0)
            {
                _commandPanel.Children.Add(new MptEmptyState("No commands found."));
            }
        }
        catch (Exception ex)
        {
            _commandPanel.Children.Clear();
            _commandPanel.Children.Add(new MptErrorState(ex.Message));
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

    private Control BuildDashboardCard(HostProto.ModuleCard card)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(HeaderWithBadge(card.Title, card.State));
        panel.Children.Add(new TextBlock
        {
            Text = card.Summary,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MptTheme.TextSecondary,
            MaxHeight = 42
        });

        panel.Children.Add(BuildMetricRow(card.Metrics.Select(metric => (metric.Label, metric.Value)).ToArray()));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var details = new MptActionButton("Details");
        details.Click += async (_, _) => await ShowModuleDetailPageAsync(card.ModuleId);
        actions.Children.Add(details);
        foreach (var action in card.Actions.Take(2))
        {
            var button = new MptActionButton(action.Title) { Tag = action.CommandId };
            button.Click += async (_, _) => await ExecuteCommandAsync((string)button.Tag!);
            actions.Children.Add(button);
        }
        panel.Children.Add(actions);

        return new MptModuleCard(panel);
    }

    private Control BuildModuleSummaryCard(HostProto.ModuleSummary module)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(HeaderWithBadge(module.DisplayName, module.State));
        panel.Children.Add(new TextBlock
        {
            Text = module.Summary,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MptTheme.TextSecondary
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{module.PackageId} · {module.ModuleId}",
            FontSize = 12,
            Foreground = MptTheme.TextMuted
        });
        var permissionSummary = module.Permissions.Count == 0
            ? "Permissions: none"
            : $"Permissions: {module.Permissions.Count} declared";
        panel.Children.Add(new TextBlock
        {
            Text = $"{permissionSummary} · Requirements: {module.Requirements.Count}",
            FontSize = 12,
            Foreground = module.Permissions.Any(permission => permission.Level is "broker" or "elevated" or "service")
                ? MptTheme.Warning
                : MptTheme.TextMuted,
            TextWrapping = TextWrapping.Wrap
        });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var details = new MptActionButton("Details");
        details.Click += async (_, _) => await ShowModuleDetailPageAsync(module.ModuleId);
        var settings = new MptActionButton("Settings");
        settings.Click += async (_, _) => await LoadSettingsPageAsync(module.ModuleId);
        var logs = new MptActionButton("Logs");
        logs.Click += async (_, _) => await LoadLogsPageAsync(module.ModuleId);
        var toggle = new MptActionButton(module.Enabled ? "Disable" : "Enable");
        toggle.Click += async (_, _) => await SetModuleEnabledAsync(module.ModuleId, !module.Enabled);
        actions.Children.Add(details);
        actions.Children.Add(settings);
        actions.Children.Add(logs);
        actions.Children.Add(toggle);
        panel.Children.Add(actions);

        return new MptModuleCard(panel);
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

    private Control BuildModulePicker(HostProto.ListModulesResponse modules, string selectedModuleId, Func<string, Task> handler)
    {
        var picker = new WrapPanel();
        foreach (var module in modules.Modules.OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var button = new Button
            {
                Content = module.DisplayName,
                Tag = module.ModuleId,
                Margin = new Thickness(0, 0, 8, 8),
                MinHeight = 32,
                BorderBrush = module.ModuleId == selectedModuleId ? MptTheme.Accent : MptTheme.Border
            };
            button.Click += async (_, _) => await handler((string)button.Tag!);
            picker.Children.Add(button);
        }

        return picker;
    }

    private static IReadOnlyList<SettingsFieldEditor> BuildSettingsFieldEditors(JsonObject schema, JsonObject values)
    {
        if (!schema.TryGetPropertyValue("properties", out var propertiesNode) ||
            propertiesNode is not JsonObject properties)
        {
            return [];
        }

        var fields = new List<SettingsFieldEditor>();
        foreach (var propertyPair in properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (propertyPair.Value is not JsonObject property)
            {
                continue;
            }

            var key = propertyPair.Key;
            var label = GetSchemaString(property, "title", key);
            var description = GetSchemaString(property, "description", "");
            var type = GetSchemaString(property, "type", "string").ToLowerInvariant();
            values.TryGetPropertyValue(key, out var currentValue);
            property.TryGetPropertyValue("default", out var defaultValue);
            var effectiveValue = currentValue ?? defaultValue;

            Control input;
            var editorType = type;
            if (property.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumValues)
            {
                editorType = "enum";
                var options = enumValues.Select(NodeToEditorText).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                var selected = NodeToEditorText(effectiveValue);
                input = new ComboBox
                {
                    ItemsSource = options,
                    SelectedItem = options.Contains(selected, StringComparer.OrdinalIgnoreCase)
                        ? selected
                        : options.FirstOrDefault(),
                    MinWidth = 260,
                    MinHeight = 34
                };
            }
            else if (type == "boolean")
            {
                input = new CheckBox
                {
                    IsChecked = NodeToBool(effectiveValue),
                    MinHeight = 32
                };
            }
            else
            {
                input = new TextBox
                {
                    Text = NodeToEditorText(effectiveValue),
                    MinWidth = 360,
                    MinHeight = type is "object" or "array" ? 96 : 34,
                    AcceptsReturn = type is "object" or "array",
                    TextWrapping = type is "object" or "array" ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    FontFamily = type is "object" or "array" ? FontFamily.Parse("Consolas") : FontFamily.Default,
                    FontSize = type is "object" or "array" ? 12 : 14
                };
            }

            fields.Add(new SettingsFieldEditor(
                key,
                editorType,
                input,
                BuildSettingsFieldView(label, key, editorType, description, input)));
        }

        return fields;
    }

    private static Control BuildSettingsFieldView(string label, string key, string type, string description, Control input)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(description) ? $"{key} · {type}" : $"{key} · {type} · {description}",
            FontSize = 12,
            Foreground = MptTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(input);
        return panel;
    }

    private static JsonObject BuildSettingsPatch(IReadOnlyList<SettingsFieldEditor> fields)
    {
        var patch = new JsonObject();
        foreach (var field in fields)
        {
            patch[field.Key] = field.Type switch
            {
                "boolean" => JsonValue.Create(((CheckBox)field.Input).IsChecked == true),
                "integer" => JsonValue.Create(ParseLong(((TextBox)field.Input).Text, field.Key)),
                "number" => JsonValue.Create(ParseDouble(((TextBox)field.Input).Text, field.Key)),
                "object" => ParseCompositeSetting(((TextBox)field.Input).Text, field.Key, "{}"),
                "array" => ParseCompositeSetting(((TextBox)field.Input).Text, field.Key, "[]"),
                "enum" => JsonValue.Create(((ComboBox)field.Input).SelectedItem?.ToString() ?? ""),
                _ => JsonValue.Create(((TextBox)field.Input).Text ?? "")
            };
        }

        return patch;
    }

    private static JsonObject? TryParseSettingsSchema(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(schemaJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetSchemaString(JsonObject schema, string key, string fallback)
    {
        return schema.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;
    }

    private static bool NodeToBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static string NodeToEditorText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = node is JsonObject or JsonArray });
    }

    private static long ParseLong(string? value, string key)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be an integer.");
    }

    private static double ParseDouble(string? value, string key)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be a number.");
    }

    private static JsonNode ParseCompositeSetting(string? value, string key, string emptyValue)
    {
        var text = string.IsNullOrWhiteSpace(value) ? emptyValue : value;
        return JsonNode.Parse(text)
            ?? throw new FormatException($"{key} must be valid JSON.");
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

    private sealed record SettingsFieldEditor(string Key, string Type, Control Input, Control View);

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

    private static DesignTokens TryLoadTokens()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory);
        var tokenPath = Path.Combine(root, "ui", "design-tokens.json");
        return File.Exists(tokenPath) ? DesignTokens.Load(tokenPath) : new DesignTokens();
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
