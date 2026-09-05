using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using AdbForwarder.MyPowerTools;
using AndroidTools.MyPowerTools;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using MyPowerTools.HostControl;
using MyPowerTools.Broker;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;
using MyPowerTools.Platform.Mac;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.SampleModules.DotNet;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI;
using ScreenEase.MyPowerTools;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptOperationConstraints = MyPowerTools.Abstractions.MptOperationConstraints;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;
using SettingsValidationResult = MyPowerTools.Abstractions.SettingsValidationResult;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void Ui_snapshot_writes_contract_manifest()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-ui-snapshot", Guid.NewGuid().ToString("N"));
        var manifestPath = new UiSurfaceGate().WriteSnapshotSet(
            Path.Combine(Root, "modules"),
            output,
            new UiSnapshotRequest("dashboard-card", "light", "1366x768", "normal"));

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var snapshots = manifest["snapshots"]!.AsArray();

        Assert.True(File.Exists(manifestPath));
        Assert.True(manifest["snapshotCount"]!.GetValue<int>() >= 5);
        Assert.Equal(manifest["snapshotCount"]!.GetValue<int>(), manifest["pixelSnapshotCount"]!.GetValue<int>());
        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "adb-forwarder.dashboard");
        Assert.Equal("contract", manifest["artifactKind"]!.GetValue<string>());
        Assert.All(Directory.GetFiles(output, "*.contract.json"), path => Assert.Contains("sourceSha256", File.ReadAllText(path)));
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.contract.png").Length);
        Assert.All(snapshots, item =>
        {
            var pixelName = item!["pixelSnapshot"]!.GetValue<string>();
            var pixelPath = Path.Combine(output, pixelName);
            Assert.True(File.Exists(pixelPath), $"Missing pixel snapshot {pixelPath}");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(pixelPath).Take(8).ToArray());
            Assert.Equal(64, item["pixelSha256"]!.GetValue<string>().Length);
            Assert.Equal(1366, item["pixelWidth"]!.GetValue<int>());
            Assert.Equal(768, item["pixelHeight"]!.GetValue<int>());
            Assert.True(item["pixelUniqueColorCount"]!.GetValue<int>() > 3);
            Assert.True(item["pixelNonBackgroundPixels"]!.GetValue<int>() > 0);
        });
    }

    [Fact]
    public void Shell_keyboard_shortcuts_resolve_navigation_and_command_palette_actions()
    {
        var focus = ShellKeyboardShortcut.Resolve(Key.P, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(ShellKeyboardAction.FocusCommandPalette, focus.Action);

        var oldSearch = ShellKeyboardShortcut.Resolve(Key.F, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.None, oldSearch.Action);

        var clear = ShellKeyboardShortcut.Resolve(Key.Escape, KeyModifiers.None);
        Assert.Equal(ShellKeyboardAction.ClearCommandPalette, clear.Action);

        var refresh = ShellKeyboardShortcut.Resolve(Key.F5, KeyModifiers.None);
        Assert.Equal(ShellKeyboardAction.Refresh, refresh.Action);

        var overlay = ShellKeyboardShortcut.Resolve(Key.Space, KeyModifiers.Control | KeyModifiers.Alt);
        Assert.Equal(ShellKeyboardAction.FocusCommandPalette, overlay.Action);

        var system = ShellKeyboardShortcut.Resolve(Key.D6, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.Navigate, system.Action);
        Assert.Equal("System", system.TargetPage);

        var outOfRange = ShellKeyboardShortcut.Resolve(Key.D8, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.None, outOfRange.Action);

        var ignored = ShellKeyboardShortcut.Resolve(Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(ShellKeyboardAction.None, ignored.Action);
    }

    [Fact]
    public void Command_palette_view_model_ranks_selects_and_exposes_execution_detail()
    {
        var scan = new CommandItemViewModel(
            "adb-forwarder.devices.scan",
            "adb-forwarder",
            "Scan devices",
            "Lists connected devices.",
            "normal",
            false,
            "AdbForwarder",
            "normal",
            "",
            false,
            null);
        var portProxy = new CommandItemViewModel(
            "adb-forwarder.portproxy.apply",
            "adb-forwarder",
            "Apply port proxy",
            "Writes forwarding rules through broker approval.",
            "elevated",
            true,
            "AdbForwarder",
            "broker approval required",
            "",
            true,
            null,
            null,
            [new CommandParameterViewModel("reason", "Reason", "string", true, "test")]);
        var profile = new CommandItemViewModel(
            "screenease.profile.apply",
            "screenease",
            "Apply profile",
            "Applies a display profile.",
            "normal",
            false,
            "ScreenEase",
            "normal",
            "",
            false,
            null);

        var viewModel = new CommandPaletteViewModel("proxy", [scan, profile, portProxy]);

        Assert.Equal("adb-forwarder.portproxy.apply", viewModel.Commands[0].CommandId);
        Assert.Equal("adb-forwarder.portproxy.apply", viewModel.SelectedCommand!.CommandId);
        Assert.Equal(3, viewModel.Results.Count);
        Assert.Equal(3, viewModel.VisibleResults.Count);
        Assert.True(viewModel.VisibleResults[0].IsSelected);
        Assert.Equal("Review", viewModel.VisibleResults[0].ActionHint);
        Assert.True(viewModel.RequiresDangerousConfirmation);
        Assert.Contains("portproxy.apply", viewModel.DangerousConfirmationText);
        Assert.Contains("reason=test", viewModel.SelectionPreview);
    }

    [Fact]
    public async Task Command_palette_keyboard_selection_and_activation_execute_the_highlighted_result()
    {
        var executed = new List<string>();
        var first = new CommandItemViewModel(
            "sample.first",
            "sample",
            "First command",
            "First result",
            "none",
            false,
            "Sample",
            "none",
            "",
            false,
            (commandId, _, _, cancellationToken) => SingleCommandStatus("succeeded", commandId, cancellationToken));
        var second = new CommandItemViewModel(
            "sample.second",
            "sample",
            "Second command",
            "Second result",
            "none",
            false,
            "Sample",
            "none",
            "",
            false,
            (commandId, _, _, cancellationToken) =>
            {
                executed.Add(commandId);
                return SingleCommandStatus("succeeded", commandId, cancellationToken);
            });
        var viewModel = new CommandPaletteViewModel("", [first, second]);

        viewModel.MoveSelection(-1);

        Assert.True(viewModel.VisibleResults[1].IsSelected);
        Assert.Equal("sample.second", viewModel.SelectedCommand!.CommandId);

        viewModel.MoveSelection(1);

        Assert.True(viewModel.VisibleResults[0].IsSelected);
        Assert.Equal("sample.first", viewModel.SelectedCommand!.CommandId);

        viewModel.MoveSelection(1);

        Assert.False(viewModel.VisibleResults[0].IsSelected);
        Assert.True(viewModel.VisibleResults[1].IsSelected);
        Assert.Equal("sample.second", viewModel.SelectedCommand!.CommandId);

        await viewModel.ActivateSelectedAsync();

        Assert.Equal(new[] { "sample.second" }, executed);
        Assert.True(viewModel.IsDetailsOpen);
        Assert.False(viewModel.IsResultsVisible);

        viewModel.BackToResultsCommand.Execute(null);

        Assert.False(viewModel.IsDetailsOpen);
        Assert.True(viewModel.IsResultsVisible);
    }

    [Fact]
    public async Task Command_palette_dangerous_command_requires_explicit_checkbox_confirmation()
    {
        var executeCount = 0;
        var dangerous = new CommandItemViewModel(
            "sample.admin.apply",
            "sample",
            "Apply administrator change",
            "Writes a protected setting",
            "dangerous",
            true,
            "Sample",
            "broker approval required",
            "",
            false,
            (_, _, _, cancellationToken) =>
            {
                executeCount++;
                return SingleCommandStatus("succeeded", "applied", cancellationToken);
            });
        var viewModel = new CommandPaletteViewModel("", [dangerous]);

        Assert.True(dangerous.RequiresDangerousConfirmation);
        Assert.False(dangerous.IsDangerousConfirmed);
        Assert.False(dangerous.ExecuteCommand.CanExecute(null));

        await viewModel.ActivateSelectedAsync();

        Assert.True(viewModel.IsDetailsOpen);
        Assert.Equal(0, executeCount);

        await dangerous.ExecuteAsync();

        Assert.Equal(0, executeCount);
        Assert.Equal("blocked", dangerous.ExecutionState);
        Assert.Equal(dangerous.DangerConfirmationText, dangerous.ExecutionMessage);

        dangerous.IsDangerousConfirmed = true;

        Assert.True(dangerous.ExecuteCommand.CanExecute(null));

        await dangerous.ExecuteAsync();

        Assert.Equal(1, executeCount);
        Assert.Equal("succeeded", dangerous.ExecutionState);
    }

    [Fact]
    public void Hotkey_binding_view_model_supports_editing_states_and_reset_prompt()
    {
        var resetInvoked = false;
        var hotkey = new HotkeyBindingViewModel(
            "screenease.toggle-enabled",
            "Ctrl+Alt+F9",
            "screenease.effect.toggle",
            "conflict",
            "Gesture also maps to command-palette.",
            true,
            new AsyncRelayCommand(() =>
            {
                resetInvoked = true;
                return Task.CompletedTask;
            }));

        Assert.True(hotkey.HasConflict);
        Assert.True(hotkey.CanEdit);
        Assert.Equal("Conflict", hotkey.StateLabel);
        Assert.Contains("Resolve the conflict", hotkey.ResultPrompt);

        hotkey.Gesture = "Ctrl+Alt+F10";

        Assert.True(hotkey.IsDirty);
        Assert.Contains("Ctrl+Alt+F9", hotkey.ResultPrompt);
        Assert.Contains("screenease.effect.toggle", hotkey.CommandArgsPreview);
        hotkey.ResetCommand.Execute(null);
        Assert.True(resetInvoked);
    }

    [Fact]
    public void Shell_ui_colors_are_centralized_in_theme_tokens()
    {
        var files = new[]
            {
                Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs"),
                Path.Combine(Root, "src", "MyPowerTools.UI.Primitives", "MptControls.cs")
            }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services"),
                "ShellWorkspaceController*.cs"));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Brush.Parse(\"#", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brushes.White", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void P_foundation_2_ui_architecture_debt_is_tracked()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var foundationDocPath = Path.Combine(Root, "docs", "P_FOUNDATION_2.md");
        var mainWindowLineCount = File.ReadLines(mainWindowPath).Count();
        var foundationDoc = File.ReadAllText(foundationDocPath);

        Assert.Contains("MainWindow.cs", foundationDoc);
        Assert.Contains($"current: {mainWindowLineCount} lines", foundationDoc);
        Assert.Contains("target <= 250 lines", foundationDoc);
        Assert.Contains("AXAML + MVVM", foundationDoc);
        Assert.True(mainWindowLineCount <= 250, "MainWindow.cs should stay below the P-Foundation-2 thin-window target.");
    }

    [Fact]
    public void Shell_workspace_controller_owns_shell_orchestration()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var startupPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.Startup.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var lifecyclePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.Lifecycle.cs");
        var mainWindow = string.Concat(
            File.ReadAllText(mainWindowPath),
            File.ReadAllText(startupPath),
            File.ReadAllText(lifecyclePath));
        var workspace = ReadShellWorkspaceControllerText();

        Assert.Contains("new ShellWorkspaceController", mainWindow);
        Assert.Contains("ShellWorkspaceController.PageLabels", mainWindow);
        Assert.Contains("workspace.OpenAsync(startupTools)", mainWindow);
        Assert.Contains("workspace.DisposeAsync()", mainWindow);
        Assert.Contains("workspace.HandleKeyDownAsync(e)", File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.Interactions.cs")));
        Assert.Contains("Dispatcher.UIThread.Post(InitializeWorkspace, DispatcherPriority.Background)", mainWindow);
        Assert.Contains("_workspace.ShowStartupPage()", mainWindow);
        Assert.Contains("await _runnerBootstrapTask.ConfigureAwait(true)", mainWindow);
        Assert.Contains("public async Task RefreshAsync()", workspace);
        Assert.Contains("public async Task ShowPageAsync(string page)", workspace);
        Assert.Contains("public async Task HandleKeyDownAsync(KeyEventArgs eventArguments)", workspace);
        Assert.Contains("RefreshShellDataAsync(includeAuxiliaryData: false)", workspace);
        Assert.Contains("Dispatcher.UIThread.Post(StartEventMonitors, DispatcherPriority.Background)", workspace);
        Assert.Contains("ApplyHostEventAsync", workspace);
        Assert.Contains("ShellPageRefreshRouter.Route(_currentPage, evt)", workspace);
        Assert.Contains("_pageData.LoadDashboardAsync", workspace);
        Assert.Contains("_commandExecutionService.ExecuteAsync(invocationId, commandId, args", workspace);
        Assert.DoesNotContain("HostControlClient", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellPageDataService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellCommandExecutionService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellRunnerEventService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellHostActionService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellSettingsService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellPageViewModelFactory.FromPermissionPrompt", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new DashboardView", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new PermissionPromptView", mainWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_axaml_mvvm_migration_scaffold_exists_with_typed_bindings()
    {
        var shellRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia");
        var expectedPages = new Dictionary<string, string>
        {
            ["DashboardView"] = "DashboardViewModel",
            ["ModulesView"] = "ModulesViewModel",
            ["ModuleDetailView"] = "ModuleDetailViewModel",
            ["CommandPaletteView"] = "CommandPaletteViewModel",
            ["SettingsCenterView"] = "SettingsCenterViewModel",
            ["LogsView"] = "LogsViewModel",
            ["NotificationsView"] = "NotificationsViewModel",
            ["PackageManagerView"] = "PackageManagerViewModel",
            ["DiagnosticsView"] = "DiagnosticsViewModel",
            ["UnavailablePageView"] = "UnavailablePageViewModel"
        };

        foreach (var (viewName, viewModelName) in expectedPages)
        {
            var axamlPath = Path.Combine(shellRoot, "Views", $"{viewName}.axaml");
            var codeBehindPath = axamlPath + ".cs";
            var axaml = File.ReadAllText(axamlPath);
            var codeBehind = File.ReadAllText(codeBehindPath);

            Assert.True(File.Exists(axamlPath), $"Missing {axamlPath}");
            Assert.True(File.Exists(codeBehindPath), $"Missing {codeBehindPath}");
            Assert.Contains($"x:DataType=\"vm:{viewModelName}\"", axaml);
            Assert.Contains("DynamicResource", axaml);
            Assert.Contains("AvaloniaXamlLoader.Load(this)", codeBehind);
            Assert.True(File.ReadLines(codeBehindPath).Count() <= 18, $"{codeBehindPath} should stay as thin view loading code.");
            Assert.DoesNotContain("HostControlClient", codeBehind, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Shell_viewmodels_are_control_free_and_map_host_protocol()
    {
        var viewModelRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels");
        foreach (var file in Directory.EnumerateFiles(viewModelRoot, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Avalonia.Controls", text, StringComparison.Ordinal);
            Assert.DoesNotContain("UserControl", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia.Controls.Window", text, StringComparison.Ordinal);
        }

        var dashboard = new HostProto.DashboardSnapshot { EventSeq = 42 };
        var card = new HostProto.ModuleCard
        {
            ModuleId = "sample",
            PackageId = "sample-package",
            Title = "Sample",
            State = "running",
            Summary = "Ready"
        };
        card.Metrics.Add(new HostProto.Metric { Label = "Commands", Value = "3" });
        card.Actions.Add(new HostProto.QuickAction { CommandId = "sample.open", Title = "Open", Style = "primary" });
        dashboard.Cards.Add(card);
        dashboard.Alerts.Add(new HostProto.HostAlert { Id = "a1", Level = "info", Title = "Notice", Body = "All set" });

        var dashboardViewModel = ShellPageViewModelFactory.FromDashboard(dashboard);

        Assert.Equal("Dashboard", dashboardViewModel.Title);
        Assert.Equal("1 modules indexed, event seq 42", dashboardViewModel.Subtitle);
        Assert.Single(dashboardViewModel.Cards);
        Assert.Single(dashboardViewModel.Alerts);

        var commands = new HostProto.ListCommandsResponse();
        commands.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "sample.open",
            ModuleId = "sample",
            Title = "Open",
            Subtitle = "Open Sample",
            DangerLevel = "none"
        });

        var commandViewModel = ShellPageViewModelFactory.FromCommands("open", commands);

        Assert.Equal("Search results", commandViewModel.Title);
        Assert.Equal("open", commandViewModel.Query);
        Assert.Single(commandViewModel.Commands);
    }

    [Fact]
    public void Shell_command_palette_filters_legacy_open_execution_entries()
    {
        var commands = new HostProto.ListCommandsResponse();
        commands.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "sample.legacy.open",
            ModuleId = "sample",
            Title = "Open sample module",
            Subtitle = "Legacy module detail route",
            Execution = new Google.Protobuf.WellKnownTypes.Struct
            {
                Fields =
                {
                    ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("open")
                }
            }
        });
        commands.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "sample.refresh",
            ModuleId = "sample",
            Title = "Refresh sample",
            Subtitle = "Refreshes the product state",
            Execution = new Google.Protobuf.WellKnownTypes.Struct
            {
                Fields =
                {
                    ["type"] = Google.Protobuf.WellKnownTypes.Value.ForString("command")
                }
            }
        });

        var viewModel = ShellPageViewModelFactory.FromCommands("sample", commands);
        var command = Assert.Single(viewModel.Commands);

        Assert.Equal("sample.refresh", command.CommandId);
        Assert.DoesNotContain(viewModel.Results, result => result.Command.CommandId == "sample.legacy.open");
    }

    [Fact]
    public void Shell_command_id_open_suffix_does_not_route_to_module_detail()
    {
        var refreshedModuleId = "";
        var handled = ShellCommandRouter.TryHandleShellCommand(
            "android-tools.notifications.open",
            moduleId =>
            {
                refreshedModuleId = moduleId;
                return Task.CompletedTask;
            },
            out var action);

        Assert.False(handled);
        Assert.True(action.IsCompletedSuccessfully);
        Assert.Equal("", refreshedModuleId);
    }

    [Fact]
    public async Task Shell_router_only_handles_explicit_runtime_refresh_command()
    {
        var refreshed = "";

        var settingsHandled = ShellCommandRouter.TryHandleShellCommand(
            "screenease.settings.open",
            moduleId =>
            {
                refreshed = moduleId;
                return Task.CompletedTask;
            },
            out var settingsAction);

        Assert.False(settingsHandled);
        Assert.True(settingsAction.IsCompletedSuccessfully);
        Assert.Equal("", refreshed);

        var events = new List<CommandExecutionStatus>();
        Assert.True(ShellCommandRouter.TryHandleShellCommandStream(
            "screenease.status.refresh",
            moduleId =>
            {
                refreshed = moduleId;
                return Task.CompletedTask;
            },
            out var stream));

        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        Assert.Equal("screenease", refreshed);
        Assert.Equal("succeeded", events.Last().State);
        Assert.Contains("refreshed: screenease.status.refresh", events.Last().Message, StringComparison.Ordinal);

        Assert.Equal("refreshed: screenease.status.refresh", ShellCommandRouter.SuccessMessage("screenease.status.refresh"));
    }

    [Fact]
    public void Shell_dashboard_actions_do_not_show_duplicate_details_button()
    {
        var dashboardViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "DashboardView.axaml");
        var dashboardView = File.ReadAllText(dashboardViewPath);
        var densityTokens = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptDensity.axaml"));

        Assert.DoesNotContain("Content=\"Details\"", dashboardView, StringComparison.Ordinal);
        Assert.Contains("Content=\"{Binding Title}\"", dashboardView);
        Assert.Contains("Classes.MptPrimaryButton=\"{Binding IsPrimary}\"", dashboardView);
        Assert.Contains("<x:Double x:Key=\"MptLayoutCardMinWidth\">320</x:Double>", densityTokens);
    }

    [Fact]
    public void Shell_modules_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var modulesViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "ModulesView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var modulesView = File.ReadAllText(modulesViewPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadModulesAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromModules", service);
        Assert.Contains("new ModulesView", workspace);
        Assert.DoesNotContain("BuildModuleSummaryCard", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:ModulesViewModel\"", modulesView);
        Assert.Contains("ModuleSummaryItemViewModel", modulesView);
        Assert.Contains("DetailsCommand", modulesView);
        Assert.Contains("SettingsCommand", modulesView);
        Assert.Contains("LogsCommand", modulesView);
        Assert.Contains("ToggleEnabledCommand", modulesView);
    }

    [Fact]
    public void Shell_module_detail_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var moduleDetailViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "ModuleDetailView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var moduleDetailView = File.ReadAllText(moduleDetailViewPath);
        var viewModel = ReadShellViewModelsText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadModuleDetailAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromModuleDetail", service);
        Assert.Contains("new ModuleDetailView", workspace);
        Assert.DoesNotContain("BuildModuleHero", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPermissionList", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildCommand(", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:ModuleDetailViewModel\"", moduleDetailView);
        Assert.Contains("ModulePermissionViewModel", moduleDetailView);
        Assert.Contains("ModuleRequirementViewModel", moduleDetailView);
        Assert.Contains("ModuleDiagnosticItemViewModel", moduleDetailView);
        Assert.Contains("ToggleEnabledCommand", moduleDetailView);
        Assert.Contains("ExecuteCommand", moduleDetailView);
        Assert.Contains("FromModuleDetail", viewModel);
    }

    [Fact]
    public void Shell_dashboard_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var dashboardViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "DashboardView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var dashboardView = File.ReadAllText(dashboardViewPath);
        var viewModel = ReadShellViewModelsText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadDashboardAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromDashboard", service);
        Assert.Contains("new DashboardView", workspace);
        Assert.Contains("ShellCommandRouter.TryHandleShellCommand", workspace);
        Assert.DoesNotContain("BuildDashboardCard", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsCommand", dashboardView);
        Assert.Contains("ExecuteCommand", dashboardView);
        Assert.Contains("System.Windows.Input", viewModel);
    }

    [Fact]
    public void Shell_notifications_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var notificationsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "NotificationsView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var notificationsView = File.ReadAllText(notificationsViewPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadNotificationsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromNotifications", service);
        Assert.Contains("new NotificationsView", workspace);
        Assert.DoesNotContain("MptNotificationItem", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:NotificationsViewModel\"", notificationsView);
        Assert.Contains("NotificationItemViewModel", notificationsView);
        Assert.Contains("IsVisible=\"{Binding IsEmpty}\"", notificationsView);
    }

    [Fact]
    public void Shell_logs_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var logsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "LogsView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var logsView = File.ReadAllText(logsViewPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadLogsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromLogs", service);
        Assert.Contains("new LogsView", workspace);
        Assert.DoesNotContain("FillLogsAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:LogsViewModel\"", logsView);
        Assert.Contains("ModulePickerItemViewModel", logsView);
        Assert.Contains("LogLineViewModel", logsView);
        Assert.Contains("SelectCommand", logsView);
        Assert.Contains("IsVisible=\"{Binding HasNoLogs}\"", logsView);
    }

    [Fact]
    public void Shell_packages_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var packagesViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "PackageManagerView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var packagesView = File.ReadAllText(packagesViewPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadPackagesAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromPackages", service);
        Assert.Contains("new PackageManagerView", workspace);
        Assert.DoesNotContain("BuildPackageOperationsPanel", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildPackageActionRow", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:PackageManagerViewModel\"", packagesView);
        Assert.Contains("InstallSourceDirectory", packagesView);
        Assert.Contains("InstallCommand", packagesView);
        Assert.Contains("PackageModuleLinkViewModel", packagesView);
        Assert.Contains("RepairCommand", packagesView);
        Assert.Contains("UninstallCommand", packagesView);
        Assert.Contains("UpdateVersionText", packagesView);
        Assert.Contains("CanShowApplyButton", packagesView);
        Assert.Contains("IsUpdateConsentVisible", packagesView);
        Assert.Contains("ConfirmUpdateCommand", packagesView);
        Assert.Contains("UpdateConsentConfirmText", packagesView);
        Assert.Contains("HasUpdateConsentCloseItems", packagesView);
        Assert.DoesNotContain("同意并开始升级", packagesView);
        Assert.DoesNotContain("升级后如何恢复", packagesView);
        Assert.Contains("OtaConsentItemViewModel", packagesView);
        Assert.Contains("ReadInstalledVersions", service);
        Assert.Contains("OverlayVersion", service);
        Assert.Contains("ArgumentList.Add(\"--yes\")", workspace);
    }

    [Fact]
    public async Task Package_manager_requires_close_consent_before_ota_apply()
    {
        var applied = 0;
        var stateRoot = Path.Combine(Path.GetTempPath(), "mpt-ota-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateRoot);
        try
        {
            var viewModel = new PackageManagerViewModel(
                [],
                applyUpdate: _ =>
                {
                    applied++;
                    return Task.FromResult<string?>("""{"success":true,"toVersion":"0.3.12","health":{"ok":true}}""");
                },
                createConsent: () => OtaApplyConsent.Create(
                    false,
                    [
                        new OtaCloseTarget("adb", "Android Debug Bridge (adb)"),
                        new OtaCloseTarget("shell", "MyPowerTools")
                    ]),
                otaStateRoot: stateRoot);

            await ((AsyncRelayCommand)viewModel.ApplyUpdateCommand).ExecuteAsync(null);
            Assert.True(viewModel.IsUpdateConsentVisible);
            Assert.Equal(0, applied);
            Assert.Equal("需要关闭以下正在运行的程序", viewModel.UpdateConsentTitle);
            Assert.Contains("以下程序正在使用需要更新的文件", viewModel.UpdateConsentIntro);
            Assert.Equal("关闭并开始升级", viewModel.UpdateConsentConfirmText);
            var listed = string.Join('\n', viewModel.UpdateConsentCloseItems.Select(item => item.Text));
            Assert.Contains("MyPowerTools", listed);
            Assert.Contains("Android Debug Bridge (adb)", listed);
            Assert.DoesNotContain("将自动关闭", listed);
            Assert.DoesNotContain("升级后如何恢复", listed);

            await ((AsyncRelayCommand)viewModel.CancelUpdateConsentCommand).ExecuteAsync(null);
            Assert.False(viewModel.IsUpdateConsentVisible);
            Assert.Equal(0, applied);

            await ((AsyncRelayCommand)viewModel.ApplyUpdateCommand).ExecuteAsync(null);
            await ((AsyncRelayCommand)viewModel.ConfirmUpdateCommand).ExecuteAsync(null);
            Assert.Equal(1, applied);
            Assert.False(viewModel.IsUpdateConsentVisible);
            Assert.Contains("已升级到 0.3.12", viewModel.UpdateStatus);

            var planPath = Path.Combine(stateRoot, OtaCloseTargetScanner.ReopenPlanFileName);
            Assert.True(File.Exists(planPath));
            var plan = File.ReadAllText(planPath);
            Assert.Contains("\"id\": \"shell\"", plan, StringComparison.Ordinal);
            Assert.Contains("\"id\": \"adb\"", plan, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, true);
            }
        }
    }

    [Fact]
    public void Ota_close_target_scanner_classifies_install_lockers()
    {
        var root = @"C:\Users\example\AppData\Local\Programs\MyPowerTools";
        var prefix = root + @"\";

        Assert.True(OtaCloseTargetScanner.TryClassify(
            "adb",
            @"C:\Users\example\AppData\Local\Microsoft\WinGet\Packages\adb.exe",
            "adb -L tcp:5037 fork-server",
            root,
            prefix,
            out var id,
            out var displayName));
        Assert.Equal("adb", id);
        Assert.Equal("Android Debug Bridge (adb)", displayName);

        Assert.True(OtaCloseTargetScanner.TryClassify(
            "pwsh",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"pwsh -File " + prefix + @"service-units\ddns.service\ddns.ps1",
            root,
            prefix,
            out id,
            out displayName));
        Assert.Equal("ddns", id);
        Assert.Equal("MyPowerTools DDNS", displayName);

        Assert.True(OtaCloseTargetScanner.TryClassify(
            "MyPowerTools.Shell.Avalonia",
            prefix + @"Shell\MyPowerTools.Shell.Avalonia.exe",
            "",
            root,
            prefix,
            out id,
            out displayName));
        Assert.Equal("shell", id);
        Assert.Equal("MyPowerTools", displayName);

        Assert.True(OtaCloseTargetScanner.TryClassify(
            "python",
            @"C:\Python\python.exe",
            prefix + @"Runtimes\SmartBird\test_tools\smartbird_thermostat.py",
            root,
            prefix,
            out id,
            out displayName));
        Assert.Equal("smartbird", id);

        Assert.False(OtaCloseTargetScanner.TryClassify(
            "notepad",
            @"C:\Windows\notepad.exe",
            "",
            root,
            prefix,
            out _,
            out _));

        Assert.True(OtaCloseTargetScanner.TryClassify(
            "pwsh",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"pwsh -NoLogo -File " + prefix + @"service-units\custom.service\run.ps1",
            root,
            prefix,
            out id,
            out displayName));
        Assert.Equal("host:pwsh", id);
        Assert.Equal("pwsh", displayName);
    }

    [Fact]
    public void Shell_diagnostics_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var diagnosticsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "DiagnosticsView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var diagnosticsView = File.ReadAllText(diagnosticsViewPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadDiagnosticsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromDiagnostics", service);
        Assert.Contains("new DiagnosticsView", workspace);
        Assert.DoesNotContain("BuildRuntimeProcessDiagnostic", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildRuntimeCommandHistoryEntry", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:DiagnosticsViewModel\"", diagnosticsView);
        Assert.Contains("RuntimeTransportViewModel", diagnosticsView);
        Assert.Contains("RuntimeProcessPolicyHistoryItemViewModel", diagnosticsView);
        Assert.Contains("BrokerAuditEntryViewModel", diagnosticsView);
        Assert.Contains("RestartCommand", diagnosticsView);
        Assert.Contains("ToggleRestartPolicyCommand", diagnosticsView);
        Assert.Contains("StdoutText", diagnosticsView);
        Assert.Contains("StderrText", diagnosticsView);
    }

    [Fact]
    public void Shell_command_palette_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var commandPaletteViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "CommandPaletteView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var commandPaletteView = File.ReadAllText(commandPaletteViewPath);
        var viewModel = ReadShellViewModelsText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadCommandsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromCommands", service);
        Assert.Contains("new CommandPaletteView", workspace);
        Assert.DoesNotContain("_commandPanel.Children", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:CommandPaletteViewModel\"", commandPaletteView);
        Assert.Contains("VisibleResults", commandPaletteView);
        Assert.Contains("CommandSearchResultViewModel", commandPaletteView);
        Assert.Contains("ActivateCommand", commandPaletteView);
        Assert.Contains("IsDetailsOpen", commandPaletteView);
        Assert.Contains("BackToResultsCommand", commandPaletteView);
        Assert.Contains("SelectedCommand.ExecuteCommand", commandPaletteView);
        Assert.Contains("IsVisible=\"{Binding IsEmpty}\"", commandPaletteView);
        Assert.Contains("SelectedCommand.Parameters", commandPaletteView);
        Assert.Contains("SelectedCommand.DangerConfirmationText", commandPaletteView);
        Assert.Contains("SelectedCommand.IsDangerousConfirmed, Mode=TwoWay", commandPaletteView);
        Assert.Contains("CommandParameterViewModel", commandPaletteView);
        Assert.Contains("SelectedCommand.ExecuteLabel", commandPaletteView);
        Assert.Contains("SelectedCommand.CancelCommand", commandPaletteView);
        Assert.Contains("SelectedCommand.CanCancel", commandPaletteView);
        Assert.Contains("SelectedCommand.ProgressEvents", commandPaletteView);
        Assert.Contains("SelectedCommand.HasProgressEvents", commandPaletteView);
        Assert.Contains("SelectedCommand.ExecutionPreview", commandPaletteView);
        Assert.Contains("SelectedCommand.ValidationMessage", commandPaletteView);
        Assert.Contains("SelectedCommand.ExecutionStateLabel", commandPaletteView);
        Assert.DoesNotContain("ProviderGroups", commandPaletteView, StringComparison.Ordinal);
        Assert.DoesNotContain("RecentCommands", commandPaletteView, StringComparison.Ordinal);
        Assert.DoesNotContain("Search commands", commandPaletteView, StringComparison.Ordinal);
        Assert.Contains("ICommand ExecuteCommand", viewModel);
        Assert.Contains("ICommand CancelCommand", viewModel);
        Assert.Contains("CommandExecutionStatus", viewModel);
        Assert.Contains("CommandProgressItemViewModel", viewModel);
        Assert.Contains("CommandCancellationStatus", viewModel);
        Assert.Contains("case Key.Down", workspace);
        Assert.Contains("case Key.Up", workspace);
        Assert.Contains("case Key.Enter", workspace);
        Assert.Contains("MoveSelection(1)", workspace);
        Assert.Contains("MoveSelection(-1)", workspace);
        Assert.Contains("ActivateSelectedAsync", workspace);
    }

    [Fact]
    public void Shell_command_palette_is_an_anchored_flyout_without_a_page_scrim()
    {
        var shellChromePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "ShellChromeView.axaml");
        var themePath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptTheme.axaml");
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var shellChrome = File.ReadAllText(shellChromePath);
        var theme = File.ReadAllText(themePath);
        var mainWindow = string.Concat(
            File.ReadAllText(mainWindowPath),
            File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.Interactions.cs")));
        var commandOverlayStart = shellChrome.IndexOf("x:Name=\"GlobalOverlayHost\"", StringComparison.Ordinal);
        var permissionOverlayStart = commandOverlayStart >= 0
            ? shellChrome.IndexOf("x:Name=\"PermissionOverlayHost\"", commandOverlayStart, StringComparison.Ordinal)
            : -1;
        Assert.True(commandOverlayStart >= 0);
        Assert.True(permissionOverlayStart > commandOverlayStart);

        var commandOverlay = shellChrome[commandOverlayStart..permissionOverlayStart];
        Assert.Contains("x:Name=\"CommandFlyout\"", commandOverlay);
        Assert.Contains("HorizontalAlignment=\"Left\"", commandOverlay);
        Assert.Contains("VerticalAlignment=\"Top\"", commandOverlay);
        Assert.DoesNotContain("MptBrushOverlayScrim", commandOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalAlignment=\"Center\"", commandOverlay, StringComparison.Ordinal);
        Assert.DoesNotContain("PART_TitleTextPanel", theme, StringComparison.Ordinal);
        Assert.Contains("Title = OperatingSystem.IsWindows() ? \"\" : _windowCaption", mainWindow);
        Assert.Contains("SetWindowText(handle, _windowCaption)", mainWindow);
        Assert.Contains("EntryPoint = \"SetWindowTextW\"", mainWindow);
    }

    [Fact]
    public void Shell_command_palette_parameter_form_builds_command_args()
    {
        var commands = new HostProto.ListCommandsResponse();
        var command = new HostProto.CommandItem
        {
            CommandId = "sample.parameterized.run",
            ModuleId = "sample",
            Title = "Run parameterized command",
            Subtitle = "Uses Shell form args",
            DangerLevel = "none"
        };
        command.Parameters.Add(new HostProto.CommandParameter
        {
            Id = "path",
            Label = "Path",
            Type = "text",
            Required = true,
            DefaultValue = "C:\\temp"
        });
        command.Parameters.Add(new HostProto.CommandParameter
        {
            Id = "force",
            Label = "Force",
            Type = "boolean",
            DefaultValue = "true"
        });
        commands.Commands.Add(command);

        var viewModel = ShellPageViewModelFactory.FromCommands(
            "parameterized",
            commands,
            (_, _, _, cancellationToken) => SingleCommandStatus("succeeded", "done", cancellationToken));
        var item = Assert.Single(viewModel.Commands);

        Assert.True(item.HasParameters);
        Assert.Contains("2 parameter(s)", item.ParameterSummary);
        Assert.Equal("Run with parameters", item.ExecuteLabel);
        Assert.Contains("sample.parameterized.run", item.ExecutionPreview);
        Assert.Collection(
            item.Parameters,
            parameter =>
            {
                Assert.Equal("path", parameter.Id);
                Assert.True(parameter.IsText);
                parameter.Value = "C:\\work";
            },
            parameter =>
            {
                Assert.Equal("force", parameter.Id);
                Assert.True(parameter.IsBoolean);
                parameter.BooleanValue = false;
            });

        var args = item.BuildArgs();
        Assert.Equal("C:\\work", args["path"]!.GetValue<string>());
        Assert.False(args["force"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Shell_command_palette_parameter_form_validates_preview_and_execution_state()
    {
        var commands = new HostProto.ListCommandsResponse();
        var command = new HostProto.CommandItem
        {
            CommandId = "sample.validate.run",
            ModuleId = "sample",
            Title = "Validate command",
            Subtitle = "Uses local validation",
            DangerLevel = "none"
        };
        command.Parameters.Add(new HostProto.CommandParameter
        {
            Id = "path",
            Label = "Path",
            Type = "text",
            Required = true
        });
        command.Parameters.Add(new HostProto.CommandParameter
        {
            Id = "count",
            Label = "Count",
            Type = "number",
            DefaultValue = "bad"
        });
        commands.Commands.Add(command);

        JsonObject? capturedArgs = null;
        var viewModel = ShellPageViewModelFactory.FromCommands(
            "validate",
            commands,
            (_, args, _, _) =>
            {
                capturedArgs = args;
                return SingleCommandStatus("succeeded", "succeeded: validated");
            });
        var item = Assert.Single(viewModel.Commands);

        Assert.True(item.HasValidationError);
        Assert.Contains("Path is required.", item.ValidationMessage);
        Assert.Contains("Count must be a number.", item.ValidationMessage);
        Assert.False(item.ExecuteCommand.CanExecute(null));

        item.Parameters[0].Value = "C:\\work";
        item.Parameters[1].Value = "3.5";

        Assert.False(item.HasValidationError);
        Assert.True(item.ExecuteCommand.CanExecute(null));
        Assert.Contains("path=C:\\work", item.ExecutionPreview);
        Assert.Contains("count=3.5", item.ExecutionPreview);

        await item.ExecuteAsync();

        Assert.Equal("succeeded", item.ExecutionState);
        Assert.Equal("Succeeded", item.ExecutionStateLabel);
        Assert.Equal("succeeded: validated", item.ExecutionMessage);
        Assert.NotNull(capturedArgs);
        Assert.Equal("C:\\work", capturedArgs["path"]!.GetValue<string>());
        Assert.Equal(3.5, capturedArgs["count"]!.GetValue<double>());
        Assert.True(item.HasProgressEvents);
        Assert.Contains(item.ProgressEvents, evt => evt.StateLabel == "Succeeded");
    }

    [Fact]
    public async Task Shell_command_palette_progress_stream_records_events()
    {
        var commands = new HostProto.ListCommandsResponse();
        commands.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "sample.progress.run",
            ModuleId = "sample",
            Title = "Progress command",
            Subtitle = "Streams progress",
            DangerLevel = "none"
        });

        var viewModel = ShellPageViewModelFactory.FromCommands(
            "progress",
            commands,
            (_, _, _, cancellationToken) => CommandProgressStatuses(cancellationToken));
        var item = Assert.Single(viewModel.Commands);

        await item.ExecuteAsync();

        Assert.Equal("succeeded", item.ExecutionState);
        Assert.True(item.HasProgressEvents);
        Assert.Collection(
            item.ProgressEvents,
            evt =>
            {
                Assert.Equal(1, evt.Sequence);
                Assert.Equal("Accepted", evt.StateLabel);
                Assert.False(evt.IsTerminal);
            },
            evt =>
            {
                Assert.Equal(2, evt.Sequence);
                Assert.Equal("Running", evt.StateLabel);
                Assert.False(evt.IsTerminal);
            },
            evt =>
            {
                Assert.Equal(3, evt.Sequence);
                Assert.Equal("Succeeded", evt.StateLabel);
                Assert.True(evt.IsTerminal);
            });
    }

    [Fact]
    public async Task Shell_command_palette_cancel_command_updates_running_state()
    {
        var commands = new HostProto.ListCommandsResponse();
        commands.Commands.Add(new HostProto.CommandItem
        {
            CommandId = "sample.cancel.run",
            ModuleId = "sample",
            Title = "Cancelable command",
            Subtitle = "Runs until cancelled",
            DangerLevel = "none"
        });

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = ShellPageViewModelFactory.FromCommands(
            "cancel",
            commands,
            (_, _, _, cancellationToken) => DelayedCommandStatus(started, cancellationToken),
            invocationId =>
            {
                cancelled.SetResult();
                return Task.FromResult(new CommandCancellationStatus(true, invocationId, "cancelling", "cancel requested"));
            });
        var item = Assert.Single(viewModel.Commands);

        var executeTask = item.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(item.CanCancel);
        Assert.Equal("Running", item.ExecutionStateLabel);

        await item.CancelAsync();
        await executeTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("cancelled", item.ExecutionState);
        Assert.Contains("Cancelled", item.ExecutionMessage);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Shell_command_parameter_contract_flows_through_hostcontrol()
    {
        var protoPath = Path.Combine(Root, "proto", "mpt_host_control_v1.proto");
        var moduleProtoPath = Path.Combine(Root, "proto", "mpt_module_v1.proto");
        var abstractionsPath = Path.Combine(Root, "src", "MyPowerTools.Abstractions", "PluginContracts.cs");
        var staticReaderPath = Path.Combine(Root, "src", "MyPowerTools.Runtime", "StaticCommandIndexReader.cs");
        var runtimePath = Path.Combine(Root, "src", "MyPowerTools.Runtime", "MptHostRuntime.cs");
        var grpcHostPath = Path.Combine(Root, "src", "MyPowerTools.ModuleHost.GrpcIpc", "GrpcIpcModuleHost.cs");
        var moduleHostPath = Path.Combine(Root, "tools", "remote-notifications", "current-integration", "src", "AndroidTools.Runtime", "Program.cs");
        var hostServicePath = Path.Combine(Root, "src", "MyPowerTools.HostControl.Server", "HostControlGrpcService.cs");
        var hostClientPath = Path.Combine(Root, "src", "MyPowerTools.HostControl.Client", "HostControlClient.cs");
        var commandServicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellCommandExecutionService.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var proto = File.ReadAllText(protoPath);
        var moduleProto = File.ReadAllText(moduleProtoPath);
        var abstractions = File.ReadAllText(abstractionsPath);
        var staticReader = File.ReadAllText(staticReaderPath);
        var runtime = File.ReadAllText(runtimePath);
        var grpcHost = File.ReadAllText(grpcHostPath);
        var moduleHost = File.ReadAllText(moduleHostPath);
        var hostService = File.ReadAllText(hostServicePath);
        var hostClient = File.ReadAllText(hostClientPath);
        var commandService = File.ReadAllText(commandServicePath);
        var workspace = ReadShellWorkspaceControllerText();

        Assert.Contains("rpc CancelCommand", proto);
        Assert.Contains("rpc ExecuteCommandStream", proto);
        Assert.Contains("rpc ExecuteCommandStream", moduleProto);
        Assert.Contains("message CommandExecutionEvent", moduleProto);
        Assert.Contains("message CancelCommandRequest", proto);
        Assert.Contains("message CommandExecutionEvent", proto);
        Assert.Contains("repeated CommandParameter parameters = 8", proto);
        Assert.Contains("message CommandParameter", proto);
        Assert.Contains("CommandParameterDescriptor", abstractions);
        Assert.Contains("ExecuteCommandStreamAsync(CommandRequest request", abstractions);
        Assert.Contains("ReadParameters(command)", staticReader);
        Assert.Contains("CollectModuleEventsAsync", runtime);
        Assert.Contains("runtime.SubscribeEventsAsync", runtime);
        Assert.Contains("parameter.DefaultValue", grpcHost);
        Assert.Contains("client.ExecuteCommandStream", grpcHost);
        Assert.Contains("client.SubscribeEvents", grpcHost);
        Assert.Contains("ExecuteCommandStream(ExecuteCommandRequest", moduleHost);
        Assert.Contains("item.Parameters.AddRange", hostService);
        Assert.Contains("CancelCommand(HostProto.CancelCommandRequest", hostService);
        Assert.Contains("ExecuteCommandStream(HostProto.ExecuteCommandRequest", hostService);
        Assert.Contains("JsonStructMapper.ToStruct(args)", hostClient);
        Assert.Contains("CancelCommandAsync", hostClient);
        Assert.Contains("ExecuteCommandStreamAsync", hostClient);
        Assert.Contains("ExecuteAsync(string commandId, JsonObject? args", commandService);
        Assert.Contains("ExecuteStreamAsync", commandService);
        Assert.Contains("CancelAsync(string invocationId", commandService);
        Assert.Contains("ExecuteCommandStreamAsync(commandId, args, invocationId", workspace);
        Assert.Contains("CancelCommandAsync(invocationId)", workspace);
    }

    [Fact]
    public void Shell_command_execution_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellCommandExecutionService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellCommandExecutionService", workspace);
        Assert.Contains("_commandExecutionService.ExecuteAsync(invocationId, commandId, args", workspace);
        Assert.Contains("_commandExecutionService.ExecuteStreamAsync(invocationId, commandId, args", workspace);
        Assert.Contains("_commandExecutionService.CancelAsync(invocationId)", workspace);
        Assert.DoesNotContain("ShellCommandExecutionService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("var result = await client.ExecuteCommandAsync(commandId);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("var result = await client.ExecuteCommandAsync(commandId);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("HostControlClient.ForDefaultEndpoint()", service);
        Assert.Contains("ShellCommandExecutionResult", service);
        Assert.Contains("ShellCommandExecutionEvent", service);
        Assert.Contains("RequiresPermissionPrompt", service);
    }

    [Fact]
    public void Shell_runner_events_are_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellRunnerEventService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellRunnerEventService", workspace);
        Assert.Contains("_runnerEvents.CheckOnceAsync()", workspace);
        Assert.Contains("_runnerEvents.HostEventReceived", workspace);
        Assert.DoesNotContain("ShellRunnerEventService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlConnectionMonitor _connectionMonitor", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlEventStreamMonitor _eventStream", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadRunnerStatusAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyConnectionSnapshotAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("HostControlConnectionMonitor", service);
        Assert.Contains("HostControlEventStreamMonitor", service);
        Assert.Contains("RunnerRecovered", service);
        Assert.Contains("HostEventReceived", service);
        Assert.Contains("Publish(StatusChanged", service);
    }

    [Fact]
    public void Shell_host_event_refresh_routing_is_extracted_to_service()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageRefreshRouter.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellPageRefreshRouter.Route(_currentPage, evt)", workspace);
        Assert.DoesNotContain("switch (evt.Type)", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("\"module.enabled\"", workspace, StringComparison.Ordinal);
        Assert.Contains("\"module.enabled\"", service);
        Assert.Contains("ReloadCommands", service);
        Assert.Contains("ReloadCurrentPage", service);

        var commandPlan = ShellPageRefreshRouter.Route("Dashboard", new HostProto.HostEvent { Type = "command.executed" });
        Assert.True(commandPlan.ReloadBrokerAudit);
        Assert.True(commandPlan.ReloadCurrentPage);

        var settingsPlan = ShellPageRefreshRouter.Route(
            "Settings",
            new HostProto.HostEvent { Type = "settings.updated", SourceId = "sample.module" });
        Assert.Equal("sample.module", settingsPlan.ReloadSettingsModuleId);
    }

    [Fact]
    public void Shell_host_actions_are_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellHostActionService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellHostActionService", workspace);
        Assert.Contains("_hostActions.RunPackageOperationAsync", workspace);
        Assert.Contains("_hostActions.RestartRuntimeProcessAsync", workspace);
        Assert.Contains("_hostActions.SetRuntimeProcessRestartPolicyAsync", workspace);
        Assert.Contains("_hostActions.SetModuleEnabledAsync", workspace);
        Assert.DoesNotContain("ShellHostActionService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("client.InstallPackageAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("client.RestartRuntimeProcessAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("client.SetRuntimeProcessRestartPolicyAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("client.SetModuleEnabledAsync", workspace, StringComparison.Ordinal);
        Assert.Contains("HostControlClient.ForDefaultEndpoint()", service);
        Assert.Contains("ShellPackageActionResult", service);
        Assert.Contains("ShellActionResult", service);
    }

    [Fact]
    public void Shell_settings_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var settingsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "SettingsCenterView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var settingsView = File.ReadAllText(settingsViewPath);
        var viewModel = ReadShellViewModelsText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadSettingsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromSettings", service);
        Assert.Contains("new SettingsCenterView", workspace);
        Assert.Contains("SaveSettingsPageAsync", workspace);
        Assert.DoesNotContain("FillSettingsEditorAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildSettingsFieldEditors", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:SettingsCenterViewModel\"", settingsView);
        Assert.Contains("ModulePickerItemViewModel", settingsView);
        Assert.Contains("SettingsFieldViewModel", settingsView);
        Assert.Contains("SaveCommand", settingsView);
        Assert.Contains("RawJson", settingsView);
        Assert.Contains("ChangeSummary", settingsView);
        Assert.Contains("PatchPreview", settingsView);
        Assert.Contains("DirtySummary", settingsView);
        Assert.Contains("HasSaveResult", settingsView);
        Assert.Contains("SaveResultState", settingsView);
        Assert.Contains("SaveResultMessage", settingsView);
        Assert.Contains("HasValidationErrors", settingsView);
        Assert.Contains("HasValidationError", settingsView);
        Assert.Contains("BuildSettingsPatch", viewModel);
        Assert.Contains("RefreshStagedChanges", viewModel);
        Assert.Contains("ApplySaveResult", viewModel);
        Assert.Contains("CanSave", viewModel);
    }

    [Fact]
    public void Shell_settings_page_tracks_staged_diff_before_save()
    {
        var modules = new HostProto.ListModulesResponse();
        var selected = new HostProto.ModuleSummary
        {
            ModuleId = "sample",
            DisplayName = "Sample"
        };
        modules.Modules.Add(selected);
        var schemaJson = """
            {
              "properties": {
                "enabled": { "type": "boolean", "title": "Enabled" },
                "mode": { "type": "string", "title": "Mode", "enum": [ "normal", "compact" ] },
                "port": { "type": "integer", "title": "Port" }
              }
            }
            """;
        var values = new JsonObject
        {
            ["enabled"] = true,
            ["mode"] = "normal",
            ["port"] = 38189
        };
        var viewModel = ShellPageViewModelFactory.FromSettings(
            modules,
            selected,
            schemaJson,
            values,
            values.ToJsonString(),
            7,
            DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        Assert.False(viewModel.HasChanges);
        Assert.Equal(0, viewModel.DirtyCount);
        Assert.Equal("No staged changes.", viewModel.ChangeSummary);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        var port = Assert.Single(viewModel.Fields, field => field.Key == "port");
        var mode = Assert.Single(viewModel.Fields, field => field.Key == "mode");
        port.Value = "invalid";
        mode.SelectedOption = "compact";

        Assert.True(viewModel.HasValidationErrors);
        Assert.Contains("Port must be an integer.", viewModel.ValidationMessage);
        Assert.False(viewModel.SaveCommand.CanExecute(null));

        port.Value = "38200";

        Assert.True(viewModel.HasChanges);
        Assert.False(viewModel.HasValidationErrors);
        Assert.Equal(2, viewModel.DirtyCount);
        Assert.Equal("2 staged change(s)", viewModel.ChangeSummary);
        Assert.Contains("port: 38189 -> 38200", viewModel.PatchPreview);
        Assert.Contains("mode: normal -> compact", viewModel.PatchPreview);
        Assert.True(viewModel.SaveCommand.CanExecute(null));

        var patch = ShellPageViewModelFactory.BuildSettingsPatch(viewModel);
        Assert.Equal(38200, patch["port"]!.GetValue<long>());
        Assert.Equal("compact", patch["mode"]!.GetValue<string>());
        Assert.False(patch.ContainsKey("enabled"));

        viewModel.ApplySaveResult("applied", "Settings applied", "Settings applied to sample.", 8, saved: true);

        Assert.True(viewModel.HasSaveResult);
        Assert.Equal("applied", viewModel.SaveResultState);
        Assert.Equal("Settings applied", viewModel.SaveResultTitle);
        Assert.Equal("Settings applied to sample.", viewModel.SaveResultMessage);
        Assert.Equal("Revision 8", viewModel.SaveResultRevision);
        Assert.Equal((ulong)8, viewModel.Revision);
        Assert.False(viewModel.HasChanges);
        Assert.Equal("No staged changes.", viewModel.ChangeSummary);
        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void Shell_settings_save_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellSettingsService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellSettingsService", workspace);
        Assert.Contains("_settingsService.SaveAsync(viewModel)", workspace);
        Assert.DoesNotContain("ShellSettingsService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("client.UpdateSettingsAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("client.UpdateSettingsAsync", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("RpcException", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("RpcException", mainWindow, StringComparison.Ordinal);
        Assert.Contains("client.UpdateSettingsAsync", service);
        Assert.Contains("RpcException", service);
        Assert.Contains("BuildSettingsPatch", service);
        Assert.Contains("ShellSettingsSaveResult", service);
        Assert.Contains("ApplySaveResult", workspace);
        Assert.Contains("ApplyState", service);
        Assert.Contains("ApplyTitle", service);
    }

    [Fact]
    public void Shell_read_only_page_data_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = ReadShellWorkspaceControllerText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellPageDataService", workspace);
        Assert.DoesNotContain("ShellPageDataService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlClient", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlClient", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDashboardSnapshotAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ListModulesAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("GetModuleDetailAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ListNotificationsAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ListPackagesAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRuntimeDiagnosticsAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ListBrokerAuditAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("PickModule", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("PrettyJson", workspace, StringComparison.Ordinal);
        Assert.Contains("HostControlClient.ForDefaultEndpoint()", service);
        Assert.Contains("GetDashboardSnapshotAsync", service);
        Assert.Contains("ListModulesAsync", service);
        Assert.Contains("GetModuleDetailAsync", service);
        Assert.Contains("TailLogsAsync", service);
        Assert.Contains("ListNotificationsAsync", service);
        Assert.Contains("ListPackagesAsync", service);
        Assert.Contains("GetRuntimeDiagnosticsAsync", service);
        Assert.Contains("ListBrokerAuditAsync", service);
        Assert.Contains("ShellPageDataResult", service);
        var toolEventService = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellToolEventService.cs"));
        Assert.Contains("HostControlClient.ForDefaultEndpoint()", toolEventService);
        Assert.Contains("PublishToolEventAsync", toolEventService);
    }

    [Fact]
    public void Shell_permission_and_audit_sidebars_are_wired_to_axaml_view_models()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var permissionViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "PermissionPromptView.axaml");
        var auditViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "BrokerAuditView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = ReadShellWorkspaceControllerText();
        var permissionView = File.ReadAllText(permissionViewPath);
        var auditView = File.ReadAllText(auditViewPath);
        var viewModel = ReadShellViewModelsText();
        var service = File.ReadAllText(servicePath);

        Assert.Contains("new PermissionPromptView", workspace);
        Assert.Contains("new BrokerAuditView", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromPermissionPrompt", workspace);
        Assert.Contains("_pageData.LoadBrokerAuditAsync", workspace);
        Assert.Contains("_pageData.CreateBrokerAuditError", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromBrokerAudit", service);
        Assert.DoesNotContain("BuildPermissionPrompt", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildAuditEntry", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("_auditPanel.Children", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:PermissionPromptViewModel\"", permissionView);
        Assert.Contains("AuditCommand", permissionView);
        Assert.Contains("x:DataType=\"vm:BrokerAuditViewModel\"", auditView);
        Assert.Contains("BrokerAuditSidebarEntryViewModel", auditView);
        Assert.Contains("FromPermissionPrompt", viewModel);
        Assert.Contains("FromBrokerAuditError", viewModel);
    }

    [Fact]
    public void Shell_unavailable_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var unavailableViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "UnavailablePageView.axaml");
        var workspace = ReadShellWorkspaceControllerText();
        var unavailableView = File.ReadAllText(unavailableViewPath);
        var viewModel = ReadShellViewModelsText();

        Assert.Contains("new UnavailablePageView", workspace);
        Assert.Contains("new UnavailablePageViewModel", workspace);
        Assert.DoesNotContain("BuildPage(", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:UnavailablePageViewModel\"", unavailableView);
        Assert.Contains("Message", unavailableView);
        Assert.Contains("class UnavailablePageViewModel", viewModel);
    }

    [Fact]
    public void Shell_chrome_layout_is_wired_to_axaml_view()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var shellChromeViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "ShellChromeView.axaml");
        var codeBehindPath = shellChromeViewPath + ".cs";
        // MainWindow is split across partial files; the chrome construction lives in
        // MainWindow.cs and the workspace construction in MainWindow.Startup.cs.
        var mainWindow = string.Concat(
            File.ReadAllText(mainWindowPath),
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(mainWindowPath)!, "MainWindow.Startup.cs")),
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(mainWindowPath)!, "MainWindow.Lifecycle.cs")));
        var shellChromeView = File.ReadAllText(shellChromeViewPath);
        var codeBehind = File.ReadAllText(codeBehindPath);
        var viewModel = ReadShellViewModelsText();

        Assert.Contains("new ShellChromeView", mainWindow);
        Assert.Contains("new ShellChromeViewModel", mainWindow);
        Assert.DoesNotContain("BuildLayout", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new Grid", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("NavButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateNavigationState", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavigationHost\"", shellChromeView);
        Assert.Contains("ItemsSource=\"{Binding TopNavigationItems}\"", shellChromeView);
        Assert.Contains("ItemsSource=\"{Binding ToolNavigationItems}\"", shellChromeView);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\"", shellChromeView);
        Assert.Contains("x:Name=\"FooterNavigationStack\"", shellChromeView);
        Assert.Contains("Grid.Row=\"2\"", shellChromeView);
        Assert.Contains("ItemsSource=\"{Binding FooterNavigationItems}\"", shellChromeView);
        Assert.Contains("Command=\"{Binding RefreshCommand}\"", shellChromeView);
        Assert.Contains("ShellNavigationItemViewModel", shellChromeView);
        Assert.Contains("x:Name=\"TitleContentHost\"", shellChromeView);
        Assert.Contains("x:Name=\"NavigationModeButton\"", shellChromeView);
        Assert.Contains("x:Name=\"PageHeaderHost\"", shellChromeView);
        Assert.Contains("Text=\"{Binding CommandPaletteShortcutHint}\"", shellChromeView);
        Assert.Contains("IsVisible=\"{Binding HasCommandPaletteShortcutHint}\"", shellChromeView);
        Assert.Contains("WindowDecorationProperties.ElementRole=\"TitleBar\"", shellChromeView);
        Assert.Contains("Classes.selected=\"{Binding IsSelected}\"", shellChromeView);
        Assert.Contains("x:Name=\"SearchBox\"", shellChromeView);
        Assert.Contains("x:Name=\"ContentHost\"", shellChromeView);
        Assert.Contains("x:Name=\"CommandPanel\"", shellChromeView);
        Assert.Contains("x:Name=\"StatusBar\"", shellChromeView);
        Assert.Contains("Text=\"{Binding StatusText}\"", shellChromeView);
        Assert.Contains("Text=\"{Binding RunnerStatusText}\"", shellChromeView);
        Assert.Contains("AvaloniaXamlLoader.Load(this)", codeBehind);
        Assert.Contains("public ShellNavigationMode NavigationMode", codeBehind);
        Assert.Contains("ShellNavigationMode.Hidden", codeBehind);
        Assert.Contains("ShellNavigationMode.Compact", codeBehind);
        Assert.Contains("ShellNavigationMode.Expanded", codeBehind);
        Assert.Contains("class ShellChromeViewModel", viewModel);
        Assert.Contains("class ShellNavigationItemViewModel", viewModel);
        Assert.Contains("public string StatusText", viewModel);
        Assert.Contains("public string RunnerStatusText", viewModel);
        Assert.Contains("public void SetNavigationCompact", viewModel);
        Assert.Contains("public string DisplayLabel", viewModel);
        Assert.DoesNotContain("_statusBar", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_runnerStatus", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ShellWorkspaceController", mainWindow);
    }

    [Fact]
    public void Shell_theme_resource_dictionary_is_loaded_and_defines_design_tokens()
    {
        var appPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "App.cs");
        var programPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Program.cs");
        var themePath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptTheme.axaml");
        var criticalThemePath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptThemeCritical.axaml");
        var deferredThemePath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptThemeDeferred.axaml");
        var colorsPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptColors.axaml");
        var spacingPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptSpacing.axaml");
        var radiiPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptRadii.axaml");
        var typographyPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptTypography.axaml");
        var markdownPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Controls", "MptMarkdownView.cs");
        var densityPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptDensity.axaml");
        var controlsPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Controls", "MptControls.axaml");
        var app = File.ReadAllText(appPath);
        var program = File.ReadAllText(programPath);
        var theme = string.Concat(
            File.ReadAllText(themePath),
            File.ReadAllText(criticalThemePath),
            File.ReadAllText(deferredThemePath));
        var colors = File.ReadAllText(colorsPath);
        var spacing = File.ReadAllText(spacingPath);
        var radii = File.ReadAllText(radiiPath);
        var typography = File.ReadAllText(typographyPath);
        var markdown = File.ReadAllText(markdownPath);
        var density = File.ReadAllText(densityPath);
        var controls = File.ReadAllText(controlsPath);

        Assert.Contains("avares://MyPowerTools.UI/Themes/MptThemeCritical.axaml", app);
        Assert.Contains("avares://MyPowerTools.UI/Themes/MptThemeDeferred.axaml", app);
        Assert.Contains("ScheduleDeferredStyles", app);
        Assert.Contains("MptColors.axaml", theme);
        Assert.Contains("MptSpacing.axaml", theme);
        Assert.Contains("MptRadii.axaml", theme);
        Assert.Contains("MptTypography.axaml", theme);
        Assert.Contains("MptDensity.axaml", theme);
        Assert.Contains("Controls/MptControls.axaml", theme);
        Assert.Contains("x:Key=\"MptBrushAppBackground\"", colors);
        Assert.Contains("x:Key=\"MptBrushWarningBackground\"", colors);
        Assert.Contains("x:Key=\"MptPagePadding\"", spacing);
        Assert.Contains("x:Key=\"MptRadiusCard\"", radii);
        Assert.Contains("x:Key=\"MptFontSizeTitle\"", typography);
        Assert.Contains("TextBlock.MptPageTitle", typography);
        Assert.Contains("<Style Selector=\"SelectableTextBlock\">", typography);
        Assert.Contains("Microsoft YaHei UI", typography);
        Assert.Contains(">Microsoft YaHei UI, Segoe UI Variable, Segoe UI, Segoe UI Emoji, Segoe UI Symbol</FontFamily>", typography);
        Assert.DoesNotContain("WithInterFont", program, StringComparison.Ordinal);
        Assert.Contains("Microsoft YaHei UI", markdown);
        Assert.Contains("font-family: \"Microsoft YaHei UI\", \"Segoe UI Variable\"", markdown);
        Assert.Contains("x:Key=\"MptDensityControlHeight\"", density);
        Assert.Contains("Border.MptCard", controls);
        Assert.All(new[] { theme, spacing, radii, typography, density, controls }, text => Assert.DoesNotContain("#", text, StringComparison.Ordinal));
    }

    [Fact]
    public void Shell_ui_component_styles_cover_foundation_controls()
    {
        var controlsPath = Path.Combine(Root, "src", "MyPowerTools.UI.Primitives", "MptControls.cs");
        var controlsAxamlPath = Path.Combine(Root, "src", "MyPowerTools.UI", "Controls", "MptControls.axaml");
        var themeTokensPath = Path.Combine(Root, "src", "MyPowerTools.UI.Primitives", "MptThemeTokens.cs");
        var controlsCode = File.ReadAllText(controlsPath);
        var controlsStyles = File.ReadAllText(controlsAxamlPath);
        var themeTokens = File.ReadAllText(themeTokensPath);

        foreach (var component in new[]
        {
            "MptModuleCard",
            "MptStatusBadge",
            "MptMetricTile",
            "MptCommandItem",
            "MptSettingsSection",
            "MptSettingsField",
            "MptLogViewer",
            "MptLogRow",
            "MptNotificationItem",
            "MptPermissionPrompt",
            "MptEmptyState",
            "MptErrorState",
            "MptLoadingSkeleton",
            "MptPageHeader",
            "MptActionBar",
            "MptActionButton"
        })
        {
            Assert.Contains($"class {component}", controlsCode);
            Assert.Contains($".{component}", controlsStyles);
        }

        foreach (var component in new[] { "MptButton", "MptTextBox", "MptCheckBox", "MptComboBox" })
        {
            Assert.Contains($"class {component}", controlsCode);
        }

        Assert.Contains("ControlHeight", themeTokens);
    }

    [Fact]
    public void Shell_ui_component_style_files_cover_p_ui_foundation_list()
    {
        var controlsRoot = Path.Combine(Root, "src", "MyPowerTools.UI", "Controls");
        var themeRoot = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes");
        var theme = string.Concat(
            File.ReadAllText(Path.Combine(themeRoot, "MptTheme.axaml")),
            File.ReadAllText(Path.Combine(themeRoot, "MptThemeCritical.axaml")),
            File.ReadAllText(Path.Combine(themeRoot, "MptThemeDeferred.axaml")));
        foreach (var componentFile in new[]
        {
            "MptButton.axaml",
            "MptIconButton.axaml",
            "MptSidebar.axaml",
            "MptTopBar.axaml",
            "MptSearchBox.axaml",
            "MptModuleCard.axaml",
            "MptStatusBadge.axaml",
            "MptMetricTile.axaml",
            "MptCommandPalette.axaml",
            "MptCommandListItem.axaml",
            "MptCommandParameterForm.axaml",
            "MptSettingsSection.axaml",
            "MptSettingsField.axaml",
            "MptLogViewer.axaml",
            "MptNotificationItem.axaml",
            "MptPackageCard.axaml",
            "MptPermissionPrompt.axaml",
            "MptEmptyState.axaml",
            "MptErrorState.axaml",
            "MptLoadingSkeleton.axaml",
            "MptPageHeader.axaml",
            "MptToolbar.axaml",
            "MptTabStrip.axaml",
            "MptSplitView.axaml"
        })
        {
            var path = Path.Combine(controlsRoot, componentFile);
            Assert.True(File.Exists(path), $"Missing {componentFile}.");
            Assert.Contains($"Controls/{componentFile}", theme);
        }
    }

    [Fact]
    public void Shell_axaml_views_use_foundation_component_classes()
    {
        var viewRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views");
        var productNativeViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "HomeView.axaml",
            "RemoteNotificationsView.axaml",
            "RemoteNotificationDetailWindow.axaml"
        };
        var dashboard = File.ReadAllText(Path.Combine(viewRoot, "DashboardView.axaml"));
        var modules = File.ReadAllText(Path.Combine(viewRoot, "ModulesView.axaml"));
        var moduleDetail = File.ReadAllText(Path.Combine(viewRoot, "ModuleDetailView.axaml"));
        var settings = File.ReadAllText(Path.Combine(viewRoot, "SettingsCenterView.axaml"));
        var notifications = File.ReadAllText(Path.Combine(viewRoot, "NotificationsView.axaml"));
        var logs = File.ReadAllText(Path.Combine(viewRoot, "LogsView.axaml"));
        var permissions = File.ReadAllText(Path.Combine(viewRoot, "PermissionPromptView.axaml"));
        var packages = File.ReadAllText(Path.Combine(viewRoot, "PackageManagerView.axaml"));
        var unavailable = File.ReadAllText(Path.Combine(viewRoot, "UnavailablePageView.axaml"));

        Assert.Contains("MptModuleCard", dashboard);
        Assert.Contains("MptMetricTile", dashboard);
        Assert.Contains("MptModuleCard", modules);
        Assert.Contains("MptSettingsSection", moduleDetail);
        Assert.Contains("MptSettingsField", moduleDetail);
        Assert.Contains("MptCommandItem", moduleDetail);
        Assert.Contains("MptSettingsSection", settings);
        Assert.Contains("MptSettingsField", settings);
        Assert.Contains("MptNotificationItem", notifications);
        Assert.Contains("MptLogRow", logs);
        Assert.Contains("MptPermissionPrompt", permissions);
        Assert.Contains("MptPackageCard", packages);
        Assert.Contains("MptErrorState", unavailable);

        foreach (var file in Directory.EnumerateFiles(viewRoot, "*.axaml"))
        {
            var view = File.ReadAllText(file);
            Assert.DoesNotContain("Classes=\"MptCard\"", view, StringComparison.Ordinal);
            if (!productNativeViews.Contains(Path.GetFileName(file)))
            {
                Assert.False(
                    System.Text.RegularExpressions.Regex.IsMatch(view, "</?(Button|TextBox|ComboBox|CheckBox)\\b"),
                    $"{file} should use Mpt input/action controls.");
            }
            if (view.Contains("controls:Mpt", StringComparison.Ordinal))
            {
                Assert.Contains("xmlns:controls=\"using:MyPowerTools.UI.Controls\"", view);
            }
        }
    }

    [Fact]
    public void Shell_axaml_views_use_theme_tokens_without_inline_colors()
    {
        var viewRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views");
        foreach (var file in Directory.EnumerateFiles(viewRoot, "*.axaml"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains("DynamicResource", text);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "#[0-9A-Fa-f]{3,8}"),
                $"{file} should not contain inline hex colors.");
            Assert.DoesNotContain("Brush.Parse", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brushes.", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Shell_static_style_lint_rejects_raw_axaml_and_csharp_ui_literals()
    {
        var axamlFiles = Directory
            .EnumerateFiles(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views"), "*.axaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "src", "MyPowerTools.UI", "Controls"), "*.axaml"));

        foreach (var file in axamlFiles)
        {
            var text = File.ReadAllText(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "#[0-9A-Fa-f]{3,8}"),
                $"{file} should not contain inline hex colors.");
            Assert.DoesNotContain("Brush.Parse", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brushes.", text, StringComparison.Ordinal);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "FontSize=\"[0-9]"),
                $"{file} should use typography tokens instead of raw FontSize values.");
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "\\b(Margin|Padding|Spacing)=\"[0-9]"),
                $"{file} should use spacing tokens instead of raw spacing values.");
        }

        var csharpFiles = new[]
            {
                Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs"),
                Path.Combine(Root, "src", "MyPowerTools.UI.Primitives", "MptControls.cs")
            }
            .Concat(Directory.EnumerateFiles(
                Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services"),
                "ShellWorkspaceController*.cs"));
        foreach (var file in csharpFiles)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Brush.Parse(\"#", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brushes.", text, StringComparison.Ordinal);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(text, "FontSize = [0-9]"),
                $"{file} should use typography constants instead of raw FontSize values.");
        }

        Assert.Empty(new UiSurfaceGate().CheckShellSource(Root).Where(issue => issue.Severity == "error"));
    }

    [Fact]
    public void Shell_code_behind_files_stay_thin_and_hostcontrol_free()
    {
        var viewRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views");
        foreach (var file in Directory.EnumerateFiles(viewRoot, "*.axaml.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.True(
                text.Contains("AvaloniaXamlLoader.Load(this)", StringComparison.Ordinal) ||
                text.Contains("InitializeComponent()", StringComparison.Ordinal),
                $"{file} should load its AXAML-defined layout.");
            Assert.DoesNotContain("HostControlClient", text, StringComparison.Ordinal);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    text,
                    "new\\s+(Grid|Button|TextBox|ComboBox|CheckBox|StackPanel|Border|ScrollViewer|ContentControl)\\b"),
                $"{file} should keep production layout in AXAML.");
            Assert.True(File.ReadLines(file).Count() <= 350, $"{file} should remain a bounded interaction adapter.");
        }
    }

    [Fact]
    public void Shell_viewmodel_files_stay_split_by_page()
    {
        var viewModelRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels");
        var files = Directory.EnumerateFiles(viewModelRoot, "*.cs").ToArray();
        var shellPageFile = Path.Combine(viewModelRoot, "ShellPageViewModels.cs");

        Assert.Contains(files, file => Path.GetFileName(file) == "ShellPageViewModelFactory.DashboardCommands.cs");
        Assert.Contains(files, file => Path.GetFileName(file) == "SettingsCenterViewModel.cs");
        Assert.Contains("View model definitions are split", File.ReadAllText(shellPageFile));
        Assert.All(files, file => Assert.True(File.ReadLines(file).Count() <= 350, $"{file} must stay <= 350 lines."));
    }

    [Fact]
    public void Ui_shell_snapshot_writes_key_surface_matrix()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-shell-ui-snapshot", Guid.NewGuid().ToString("N"));
        var manifestPath = new UiSurfaceGate().WriteShellSnapshotSet(
            output,
            new UiSnapshotRequest("*", "light", "1366x768", "normal"));

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var snapshots = manifest["snapshots"]!.AsArray();
        var required = manifest["requiredSurfaces"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.True(File.Exists(manifestPath));
        Assert.Equal("1.0", manifest["schemaVersion"]!.GetValue<string>());
        Assert.Equal("contract", manifest["artifactKind"]!.GetValue<string>());
        Assert.Equal(8, manifest["requiredSurfaceCount"]!.GetValue<int>());
        Assert.True(manifest["snapshotCount"]!.GetValue<int>() >= required.Length);
        Assert.Equal(manifest["snapshotCount"]!.GetValue<int>(), manifest["pixelSnapshotCount"]!.GetValue<int>());

        var keyboard = manifest["keyboardNavigation"]!.AsObject();
        var shortcuts = keyboard["shortcuts"]!.AsArray();
        var focusStates = keyboard["focusStates"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Contains(shortcuts, item =>
            item!["keys"]!.GetValue<string>() == "Ctrl+Alt+Space" &&
            item["action"]!.GetValue<string>() == "focus-command-palette" &&
            item["surfaceId"]!.GetValue<string>() == "shell.command-palette");
        Assert.Contains(shortcuts, item =>
            item!["keys"]!.GetValue<string>() == "Ctrl+8" &&
            item["surfaceId"]!.GetValue<string>() == "shell.runtime-diagnostics");
        Assert.Contains("command-search-focus-visible", focusStates);
        Assert.Contains("permission-audit-action-focus-visible", focusStates);

        foreach (var surfaceId in required)
        {
            Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == surfaceId);
        }

        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "shell.package-manager");
        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "shell.runtime-diagnostics");
        var commandPalette = snapshots
            .First(item => item!["surfaceId"]!.GetValue<string>() == "shell.command-palette")!
            .AsObject();
        Assert.Contains(commandPalette["keyboardShortcuts"]!.AsArray(), item => item!.GetValue<string>() == "Ctrl+Shift+P");
        Assert.Contains(commandPalette["focusStates"]!.AsArray(), item => item!.GetValue<string>() == "command-item-focus-visible");
        Assert.Contains(commandPalette["focusStates"]!.AsArray(), item => item!.GetValue<string>() == "command-parameter-validation-readable");
        Assert.Contains(commandPalette["states"]!.AsArray(), item => item!.GetValue<string>() == "permission-required");
        Assert.Contains(commandPalette["states"]!.AsArray(), item => item!.GetValue<string>() == "validation-error");
        Assert.Contains(commandPalette["states"]!.AsArray(), item => item!.GetValue<string>() == "executing");
        var settingsCenter = snapshots
            .First(item => item!["surfaceId"]!.GetValue<string>() == "shell.settings-center")!
            .AsObject();
        Assert.Contains(settingsCenter["states"]!.AsArray(), item => item!.GetValue<string>() == "conflict");
        Assert.Contains(settingsCenter["states"]!.AsArray(), item => item!.GetValue<string>() == "staged-diff");
        Assert.Contains(settingsCenter["states"]!.AsArray(), item => item!.GetValue<string>() == "apply-failed");
        Assert.Contains(settingsCenter["focusStates"]!.AsArray(), item => item!.GetValue<string>() == "patch-preview-readable");
        var logsViewer = snapshots
            .First(item => item!["surfaceId"]!.GetValue<string>() == "shell.logs-viewer")!
            .AsObject();
        Assert.Contains(logsViewer["states"]!.AsArray(), item => item!.GetValue<string>() == "streaming");
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.contract.png").Length);
        Assert.All(snapshots, item =>
        {
            var pixelName = item!["pixelSnapshot"]!.GetValue<string>();
            var pixelPath = Path.Combine(output, pixelName);
            Assert.True(File.Exists(pixelPath), $"Missing pixel snapshot {pixelPath}");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(pixelPath).Take(8).ToArray());
            Assert.Equal(64, item["pixelSha256"]!.GetValue<string>().Length);
            Assert.Equal(1366, item["pixelWidth"]!.GetValue<int>());
            Assert.Equal(768, item["pixelHeight"]!.GetValue<int>());
            Assert.True(item["pixelUniqueColorCount"]!.GetValue<int>() > 3);
            Assert.True(item["pixelNonBackgroundPixels"]!.GetValue<int>() > 0);
        });
    }

    [Fact]
    public async Task Ui_shell_real_screenshot_renders_actual_avalonia_pngs()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-shell-real-screenshot", Guid.NewGuid().ToString("N"));
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "ui",
            "screenshot",
            "--mode",
            "fixture",
            "--full-shell",
            "--theme",
            "light",
            "--size",
            "1366x768",
            "--density",
            "normal",
            "--out",
            output);
        Assert.Equal(0, result.ExitCode);

        var manifestPath = Path.Combine(output, "shell-real-screenshot-manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var screenshots = manifest["screenshots"]!.AsArray();
        var requiredScreens = new[]
        {
            "dashboard",
            "command-palette-with-params",
            "settings-dirty-state",
            "module-detail-degraded",
            "logs-long-lines",
            "notifications-list",
            "packages",
            "diagnostics-wide"
        };

        Assert.Equal("real-avalonia-screenshot", manifest["artifactKind"]!.GetValue<string>());
        Assert.Equal(requiredScreens.Length, manifest["screenshotCount"]!.GetValue<int>());
        foreach (var screenId in requiredScreens)
        {
            Assert.Contains(screenshots, item => item!["screenId"]!.GetValue<string>() == screenId);
        }

        Assert.All(screenshots, item =>
        {
            var fileName = item!["fileName"]!.GetValue<string>();
            var path = Path.Combine(output, fileName);
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 1000, $"Real screenshot {path} should be non-empty.");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
            Assert.Equal(64, item["sha256"]!.GetValue<string>().Length);
            Assert.Equal(1366, item["width"]!.GetValue<int>());
            Assert.Equal(768, item["height"]!.GetValue<int>());
            Assert.Equal("Avalonia.Headless", item["renderer"]!.GetValue<string>());
        });

        var writerSource = File.ReadAllText(Path.Combine(Root, "src", "Mpt.Cli.VisualTesting", "ShellRealScreenshotWriter.cs"));
        Assert.Contains("CreateShellChrome(", writerSource);
        Assert.Contains("new ShellChromeView", writerSource);
        Assert.Contains("\"ContentHost\"", writerSource);
        Assert.Contains("\"CommandPanel\"", writerSource);
        Assert.Contains("\"PermissionPanel\"", writerSource);
        Assert.Contains("\"AuditPanel\"", writerSource);

        var cliSource = File.ReadAllText(Path.Combine(Root, "src", "Mpt.Cli.VisualTesting", "Program.cs"));
        Assert.Contains("\"--live-runner\"", cliSource);
        Assert.Contains("\"screenshot\" => UiScreenshot", cliSource);
        Assert.Contains("\"shell-snapshot\" => UiShellSnapshot", cliSource);
    }

    [Fact]
    public void Ui_shell_real_screenshot_filters_page_and_records_acceptance_manifest_fields()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-shell-real-page-screenshot", Guid.NewGuid().ToString("N"));
        var manifestPath = VisualTestProcess.WriteSnapshotSet(
            output,
            "dark",
            "1280x720",
            "compact",
            "shell.command-palette");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var screenshots = manifest["screenshots"]!.AsArray();
        var screenshot = screenshots.Single()!.AsObject();

        Assert.Equal("shell.command-palette", manifest["surface"]!.GetValue<string>());
        Assert.Equal("fixture", manifest["mode"]!.GetValue<string>());
        Assert.False(manifest["runnerConnected"]!.GetValue<bool>());
        Assert.Equal(2, manifest["moduleCount"]!.GetValue<int>());
        Assert.Equal(5, manifest["commandCount"]!.GetValue<int>());
        Assert.Equal(1, manifest["screenshotCount"]!.GetValue<int>());

        Assert.Equal("command-palette-with-params", screenshot["screenId"]!.GetValue<string>());
        Assert.Equal("command-palette", screenshot["page"]!.GetValue<string>());
        Assert.Equal("shell.command-palette", screenshot["surfaceId"]!.GetValue<string>());
        Assert.Equal("fixture", screenshot["mode"]!.GetValue<string>());
        Assert.Equal("dark", screenshot["theme"]!.GetValue<string>());
        Assert.Equal("compact", screenshot["density"]!.GetValue<string>());
        Assert.Equal("1280x720", screenshot["size"]!.GetValue<string>());
        Assert.False(screenshot["runnerConnected"]!.GetValue<bool>());
        Assert.Equal(2, screenshot["moduleCount"]!.GetValue<int>());
        Assert.Equal(5, screenshot["commandCount"]!.GetValue<int>());

        var imagePath = screenshot["imagePath"]!.GetValue<string>();
        Assert.True(File.Exists(imagePath), $"Missing filtered real screenshot {imagePath}.");
        Assert.DoesNotContain(screenshots, item => item!["screenId"]!.GetValue<string>() == "dashboard");
    }

    [Fact]
    public async Task Shell_connection_monitor_reports_offline_then_restored()
    {
        var probe = new SequenceHostControlProbe(
            new InvalidOperationException("pipe is unavailable"),
            new HostControlConnectionProbeResult("0.2.0", "running"));
        await using var monitor = new HostControlConnectionMonitor(
            probe,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1));
        var observed = new List<HostControlConnectionSnapshot>();
        monitor.StateChanged += (_, snapshot) => observed.Add(snapshot);

        var offline = await monitor.CheckOnceAsync();
        var restored = await monitor.CheckOnceAsync();

        Assert.False(offline.Online);
        Assert.Equal("offline", offline.State);
        Assert.Equal(1, offline.ConsecutiveFailures);
        Assert.Contains("pipe is unavailable", offline.Message, StringComparison.Ordinal);
        Assert.True(restored.Online);
        Assert.True(restored.Recovered);
        Assert.Equal("running", restored.State);
        Assert.Equal("0.2.0", restored.RunnerVersion);
        Assert.Equal(0, restored.ConsecutiveFailures);
        Assert.Collection(
            observed,
            first => Assert.False(first.Online),
            second => Assert.True(second.Recovered));
    }

    [Fact]
    public async Task Shell_event_stream_monitor_resumes_after_fault_and_tracks_seq()
    {
        var source = new SequenceHostEventSource(
            [
                HostEvent(1, "runner", "registry.loaded"),
                new IOException("event stream dropped")
            ],
            [
                HostEvent(1, "runner", "duplicate"),
                HostEvent(2, "doubao-agent", "notification.created")
            ]);
        await using var monitor = new HostControlEventStreamMonitor(source, TimeSpan.FromMilliseconds(10));
        var seen = new List<HostProto.HostEvent>();
        var faults = new List<Exception>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.EventReceived += (_, evt) =>
        {
            seen.Add(evt);
            if (seen.Count == 2)
            {
                completed.TrySetResult();
            }
        };
        monitor.StreamFaulted += (_, ex) => faults.Add(ex);

        monitor.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1UL, 2UL], seen.Select(evt => evt.Seq).ToArray());
        Assert.Equal(2UL, monitor.LastEventSeq);
        Assert.Single(faults);
        Assert.Equal([0UL, 1UL], source.RequestedSeqs.Take(2).ToArray());
    }
}
