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
        var packages = new PackageReader().DiscoverPackages(Path.Combine(Root, "modules"));
        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.Equal(
            new UiSurfaceContractValidator().CheckPackage(package),
            new UiSurfaceGate().CheckPackage(package)));

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
        var snapshot = new ShortcutCatalogSnapshot(new(0, []), ShortcutCatalog.ShellCommands, [], "windows");
        EffectiveShortcut? Resolve(Key key, KeyModifiers modifiers) => ShortcutCatalog.Resolve(
            snapshot, ShortcutKeyAdapter.Format(key, modifiers)!, "", "", false, false);

        Assert.Equal("shell.command-palette.open", Resolve(Key.P, KeyModifiers.Control | KeyModifiers.Shift)?.Definition.Id);
        Assert.Null(Resolve(Key.F, KeyModifiers.Control));

        // Escape is intercepted by the live controller before configured shortcut dispatch.
        Assert.Null(Resolve(Key.Escape, KeyModifiers.None));
        var keyboard = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.Keyboard.cs"));
        Assert.Contains("if (_chromeViewModel.IsCommandPaletteOpen && eventArguments.Key == Key.Escape)", keyboard);
        Assert.Contains("await CloseCommandPaletteAsync();", keyboard);

        Assert.Equal("shell.refresh", Resolve(Key.F5, KeyModifiers.None)?.Definition.Id);

        // Background activation belongs to Runner, not application-local dispatch.
        var overlay = Assert.Single(ShortcutCatalog.Effective(snapshot), item => item.Gesture == "Ctrl+Alt+Space");
        Assert.Equal("system", overlay.Definition.Scope);
        Assert.Equal("shell.command-palette.open", overlay.Definition.CommandId);
        Assert.Null(Resolve(Key.Space, KeyModifiers.Control | KeyModifiers.Alt));

        Assert.Equal("shell.navigate.system", Resolve(Key.D6, KeyModifiers.Control)?.Definition.Id);
        Assert.Null(Resolve(Key.D8, KeyModifiers.Control));
        Assert.Equal("shell.shortcuts.open", Resolve(Key.K, KeyModifiers.Control | KeyModifiers.Shift)?.Definition.Id);
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
    public void Shell_host_event_refresh_routing_is_extracted_to_service()
    {
        var commandPlan = ShellPageRefreshRouter.Route("Dashboard", new HostProto.HostEvent { Type = "command.executed" });
        Assert.True(commandPlan.ReloadBrokerAudit);
        Assert.True(commandPlan.ReloadCurrentPage);

        var settingsPlan = ShellPageRefreshRouter.Route(
            "Settings",
            new HostProto.HostEvent { Type = "settings.updated", SourceId = "sample.module" });
        Assert.Equal("sample.module", settingsPlan.ReloadSettingsModuleId);
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
