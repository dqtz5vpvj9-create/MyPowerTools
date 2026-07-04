using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class RuntimeAcceptanceTests
{
    private static readonly string Root = FindRepositoryRoot(AppContext.BaseDirectory);
    private const string PortProxySample = """
Listen on ipv4:             Connect to ipv4:

Address         Port        Address         Port
--------------- ----------  --------------- ----------
0.0.0.0         5555        127.0.0.1       7555
""";

    [Fact]
    public void Examples_validate_against_schemas()
    {
        var validator = new SchemaPackageValidator(Path.Combine(Root, "schemas"));
        var reports = validator.ValidatePackageRoot(Path.Combine(Root, "modules"));
        Assert.All(reports, report => Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message))));
    }

    [Fact]
    public void Module_schema_accepts_planned_permission_levels()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-permission-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var permissions = new JsonArray();
        foreach (var level in new[] { "user", "elevated", "service", "serviceUser", "serviceSystem", "sensitive", "broker" })
        {
            permissions.Add(new JsonObject
            {
                ["id"] = $"permission-{level.ToLowerInvariant()}",
                ["level"] = level,
                ["capability"] = $"capability.{level}",
                ["reason"] = $"validate {level} permission"
            });
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "permission-schema-sample",
            ["packageId"] = "permission-schema-sample",
            ["displayName"] = "Permission Schema Sample",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = "Permission.Schema.Sample.dll",
                    ["type"] = "Permission.Schema.Sample.Module"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands"),
            ["permissions"] = permissions
        };
        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var report = new SchemaPackageValidator(Path.Combine(Root, "schemas")).ValidatePackageDirectory(packageRoot);

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Module_schema_accepts_runtime_policy_and_reader_maps_fields()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var manifest = CreateRuntimePolicyManifest(new JsonObject
        {
            ["preferred"] = "sidecar",
            ["allowInProc"] = true,
            ["inProcRules"] = new JsonObject
            {
                ["maxCallMs"] = 3000,
                ["allowNativeDll"] = false,
                ["allowWindow"] = false,
                ["allowBackgroundThreads"] = false,
                ["loadContext"] = "collectible",
                ["shadowCopy"] = true,
                ["sharedAssemblies"] = new JsonArray(
                    "MyPowerTools.Abstractions",
                    "Microsoft.Extensions.Logging.Abstractions")
            },
            ["sidecarRules"] = new JsonObject
            {
                ["readyTimeoutMs"] = 8000,
                ["restartLimit"] = 4,
                ["restartWindowSeconds"] = 30,
                ["killProcessTree"] = true
            }
        });
        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var report = new SchemaPackageValidator(Path.Combine(Root, "schemas")).ValidatePackageDirectory(packageRoot);
        var module = new PackageReader().ReadPackageDirectory(packageRoot).Modules.Single().Manifest;

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
        Assert.NotNull(module.RuntimePolicy);
        Assert.Equal("sidecar", module.RuntimePolicy!.Preferred);
        Assert.True(module.RuntimePolicy.AllowInProc);
        Assert.Equal(3000, module.RuntimePolicy.InProcRules!.MaxCallMs);
        Assert.False(module.RuntimePolicy.InProcRules.AllowNativeDll);
        Assert.Equal("collectible", module.RuntimePolicy.InProcRules.LoadContext);
        Assert.True(module.RuntimePolicy.InProcRules.ShadowCopy);
        Assert.Contains("MyPowerTools.Abstractions", module.RuntimePolicy.InProcRules.SharedAssemblies);
        Assert.Equal(8000, module.RuntimePolicy.SidecarRules!.ReadyTimeoutMs);
        Assert.Equal(4, module.RuntimePolicy.SidecarRules.RestartLimit);
        Assert.True(module.RuntimePolicy.SidecarRules.KillProcessTree);
    }

    [Fact]
    public void Module_schema_rejects_invalid_runtime_policy()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var manifest = CreateRuntimePolicyManifest(new JsonObject
        {
            ["preferred"] = "load-everything-in-runner",
            ["allowInProc"] = true,
            ["inProcRules"] = new JsonObject
            {
                ["maxCallMs"] = 1,
                ["allowNativeDll"] = true,
                ["allowWindow"] = false,
                ["allowBackgroundThreads"] = false,
                ["loadContext"] = "default"
            }
        });
        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var report = new SchemaPackageValidator(Path.Combine(Root, "schemas")).ValidatePackageDirectory(packageRoot);

        Assert.False(report.IsValid);
        Assert.Contains(report.Issues, issue => issue.Severity == "error" && issue.Message.Contains("module.schema.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cli_validate_modules_returns_successful_exit_code()
    {
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "validate",
            "modules");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("adb-forwarder: valid", result.Output);
        Assert.DoesNotContain(": invalid", result.Output);
    }

    [Fact]
    public async Task Cli_module_enable_disable_persists_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-module-state", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");

        var disable = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "disable",
            "doubao-agent",
            "--data-root",
            dataRoot);

        var visibleOnly = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "list",
            "--data-root",
            dataRoot);

        var includeDisabled = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "list",
            "--include-disabled",
            "--data-root",
            dataRoot);

        var enable = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "enable",
            "doubao-agent",
            "--data-root",
            dataRoot);

        Assert.Equal(0, disable.ExitCode);
        Assert.Contains("doubao-agent: disabled (disabled)", disable.Output);
        Assert.Equal(0, visibleOnly.ExitCode);
        Assert.DoesNotContain("doubao-agent ", visibleOnly.Output);
        Assert.Equal(0, includeDisabled.ExitCode);
        Assert.Contains("doubao-agent disabled disabled", includeDisabled.Output);
        Assert.Equal(0, enable.ExitCode);
        Assert.Contains("doubao-agent: enabled", enable.Output);
    }

    [Fact]
    public async Task Cli_diagnostics_reports_runtime_snapshot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-diagnostics", Guid.NewGuid().ToString("N"));
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "diagnostics",
            "--data-root",
            dataRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("runner: 0.2.0", result.Output);
        Assert.Contains("modules: 7 enabled=7 disabled=0", result.Output);
        Assert.Contains("transport: inproc-dotnet", result.Output);
        Assert.Contains("module: screenease", result.Output);
        Assert.Contains("supervisor=", result.Output);
    }

    [Fact]
    public async Task Cli_diagnostics_reports_grpc_process_snapshot()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.cli." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-grpc-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "diagnostics",
            "--modules",
            packageRoot,
            "--data-root",
            Path.Combine(Path.GetTempPath(), "mpt-cli-grpc-diagnostics-data", Guid.NewGuid().ToString("N")));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("process: grpc-ipc pool=module:sample.grpc state=running", result.Output);
        Assert.Contains("starts=1/4", result.Output);
        Assert.Contains(pipeName, result.Output);
        Assert.Contains("modules=sample.grpc", result.Output);
    }

    [Fact]
    public async Task Cli_validate_contracts_reports_all_module_contracts()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-contracts", Guid.NewGuid().ToString("N"));
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "validate",
            "contracts",
            "--data-root",
            dataRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Module contract validation passed: 5 packages, 7 modules.", result.Output);
        Assert.Contains("contract: adb-forwarder", result.Output);
        Assert.Contains("contract: android-tools.remote-commands", result.Output);
        Assert.Contains("contract: screenease", result.Output);
        Assert.Contains("settings=runtime-schema", result.Output);
        Assert.Contains("logs=ok", result.Output);
    }

    [Fact]
    public async Task Release_metadata_script_writes_update_and_scoop_manifests()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "mpt-release-metadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactsRoot);
        await File.WriteAllTextAsync(Path.Combine(artifactsRoot, "MyPowerTools-win-x64.zip"), "portable zip bytes");

        var result = await RunPwshAsync(
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-File",
            Path.Combine(Root, "scripts", "release-metadata.ps1"),
            "-RepoRoot",
            Root,
            "-ArtifactsRoot",
            artifactsRoot,
            "-Version",
            "0.2.0");
        var metadataPath = Path.Combine(artifactsRoot, "release-metadata.json");
        var scoopPath = Path.Combine(artifactsRoot, "package-managers", "scoop", "mypowertools.json");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(metadataPath))!.AsObject();
        var scoop = JsonNode.Parse(await File.ReadAllTextAsync(scoopPath))!.AsObject();
        var artifact = metadata["artifacts"]!.AsArray().Single()!.AsObject();
        var scoop64 = scoop["architecture"]!["64bit"]!.AsObject();

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(metadataPath));
        Assert.True(File.Exists(scoopPath));
        Assert.Equal("MyPowerTools", metadata["product"]!.GetValue<string>());
        Assert.Equal("local-portable", metadata["channel"]!.GetValue<string>());
        Assert.Equal(64, artifact["sha256"]!.GetValue<string>().Length);
        Assert.Equal(artifact["sha256"]!.GetValue<string>(), scoop64["hash"]!.GetValue<string>());
        Assert.Equal("MyPowerTools-win-x64.zip", artifact["url"]!.GetValue<string>());
        Assert.Equal(artifact["url"]!.GetValue<string>(), scoop64["url"]!.GetValue<string>());
        Assert.Equal("package-managers/scoop/mypowertools.json", metadata["packageManagers"]!["scoop"]!.GetValue<string>());
        Assert.Equal("mpt", scoop["bin"]!.AsArray()[0]!.AsArray()[1]!.GetValue<string>());
    }

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
        Assert.All(Directory.GetFiles(output, "*.snapshot.json"), path => Assert.Contains("sourceSha256", File.ReadAllText(path)));
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.snapshot.png").Length);
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
        var focus = ShellKeyboardShortcut.Resolve(Key.K, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.FocusCommandPalette, focus.Action);

        var search = ShellKeyboardShortcut.Resolve(Key.F, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.FocusCommandPalette, search.Action);

        var clear = ShellKeyboardShortcut.Resolve(Key.Escape, KeyModifiers.None);
        Assert.Equal(ShellKeyboardAction.ClearCommandPalette, clear.Action);

        var refresh = ShellKeyboardShortcut.Resolve(Key.F5, KeyModifiers.None);
        Assert.Equal(ShellKeyboardAction.Refresh, refresh.Action);

        var diagnostics = ShellKeyboardShortcut.Resolve(Key.D7, KeyModifiers.Control);
        Assert.Equal(ShellKeyboardAction.Navigate, diagnostics.Action);
        Assert.Equal("Diagnostics", diagnostics.TargetPage);

        var ignored = ShellKeyboardShortcut.Resolve(Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        Assert.Equal(ShellKeyboardAction.None, ignored.Action);
    }

    [Fact]
    public void Shell_ui_colors_are_centralized_in_theme_tokens()
    {
        foreach (var file in new[]
        {
            Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs"),
            Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs"),
            Path.Combine(Root, "src", "MyPowerTools.UI", "Controls", "MptControls.cs")
        })
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
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = File.ReadAllText(workspacePath);

        Assert.Contains("new ShellWorkspaceController", mainWindow);
        Assert.Contains("ShellWorkspaceController.PageLabels", mainWindow);
        Assert.Contains("_workspace.OpenAsync()", mainWindow);
        Assert.Contains("_workspace.DisposeAsync()", mainWindow);
        Assert.Contains("_workspace.HandleKeyDownAsync(e)", mainWindow);
        Assert.Contains("public async Task RefreshAsync()", workspace);
        Assert.Contains("public async Task ShowPageAsync(string page)", workspace);
        Assert.Contains("public async Task HandleKeyDownAsync(KeyEventArgs e)", workspace);
        Assert.Contains("ApplyHostEventAsync", workspace);
        Assert.Contains("ShellPageRefreshRouter.Route(_currentPage, evt)", workspace);
        Assert.Contains("_pageData.LoadDashboardAsync", workspace);
        Assert.Contains("_commandExecutionService.ExecuteAsync(commandId)", workspace);
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
            Assert.Contains("DynamicResource MptPagePadding", axaml);
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

        Assert.Equal("Command Palette", commandViewModel.Title);
        Assert.Equal("open", commandViewModel.Query);
        Assert.Single(commandViewModel.Commands);
    }

    [Fact]
    public void Shell_modules_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var modulesViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "ModulesView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
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
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
        var moduleDetailView = File.ReadAllText(moduleDetailViewPath);
        var viewModel = File.ReadAllText(viewModelPath);
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
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
        var dashboardView = File.ReadAllText(dashboardViewPath);
        var viewModel = File.ReadAllText(viewModelPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadDashboardAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromDashboard", service);
        Assert.Contains("new DashboardView", workspace);
        Assert.DoesNotContain("BuildDashboardCard", workspace, StringComparison.Ordinal);
        Assert.Contains("DetailsCommand", dashboardView);
        Assert.Contains("ExecuteCommand", dashboardView);
        Assert.Contains("System.Windows.Input", viewModel);
    }

    [Fact]
    public void Shell_notifications_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var notificationsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "NotificationsView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
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
        var workspace = File.ReadAllText(workspacePath);
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
        var workspace = File.ReadAllText(workspacePath);
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
    }

    [Fact]
    public void Shell_diagnostics_page_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var diagnosticsViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "DiagnosticsView.axaml");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
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
    }

    [Fact]
    public void Shell_command_palette_is_wired_to_axaml_view_model()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var commandPaletteViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "CommandPaletteView.axaml");
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
        var commandPaletteView = File.ReadAllText(commandPaletteViewPath);
        var viewModel = File.ReadAllText(viewModelPath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("_pageData.LoadCommandsAsync", workspace);
        Assert.Contains("ShellPageViewModelFactory.FromCommands", service);
        Assert.Contains("new CommandPaletteView", workspace);
        Assert.DoesNotContain("_commandPanel.Children", workspace, StringComparison.Ordinal);
        Assert.Contains("x:DataType=\"vm:CommandPaletteViewModel\"", commandPaletteView);
        Assert.Contains("CommandItemViewModel", commandPaletteView);
        Assert.Contains("ExecuteCommand", commandPaletteView);
        Assert.Contains("IsVisible=\"{Binding IsEmpty}\"", commandPaletteView);
        Assert.Contains("ParameterSummary", commandPaletteView);
        Assert.Contains("ICommand ExecuteCommand", viewModel);
    }

    [Fact]
    public void Shell_command_execution_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellCommandExecutionService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = File.ReadAllText(workspacePath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellCommandExecutionService", workspace);
        Assert.Contains("_commandExecutionService.ExecuteAsync(commandId)", workspace);
        Assert.DoesNotContain("ShellCommandExecutionService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("var result = await client.ExecuteCommandAsync(commandId);", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("var result = await client.ExecuteCommandAsync(commandId);", mainWindow, StringComparison.Ordinal);
        Assert.Contains("HostControlClient.ForDefaultEndpoint()", service);
        Assert.Contains("ShellCommandExecutionResult", service);
        Assert.Contains("RequiresPermissionPrompt", service);
    }

    [Fact]
    public void Shell_runner_events_are_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellRunnerEventService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = File.ReadAllText(workspacePath);
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
        Assert.Contains("StatusChanged?.Invoke", service);
    }

    [Fact]
    public void Shell_host_event_refresh_routing_is_extracted_to_service()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageRefreshRouter.cs");
        var workspace = File.ReadAllText(workspacePath);
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
        var workspace = File.ReadAllText(workspacePath);
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
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
        var settingsView = File.ReadAllText(settingsViewPath);
        var viewModel = File.ReadAllText(viewModelPath);
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
        Assert.Contains("BuildSettingsPatch", viewModel);
    }

    [Fact]
    public void Shell_settings_save_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellSettingsService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = File.ReadAllText(workspacePath);
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
    }

    [Fact]
    public void Shell_read_only_page_data_is_extracted_to_service()
    {
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var workspace = File.ReadAllText(workspacePath);
        var service = File.ReadAllText(servicePath);

        Assert.Contains("ShellPageDataService", workspace);
        Assert.DoesNotContain("ShellPageDataService", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlClient", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("HostControlClient", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDashboardSnapshotAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ListModulesAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("GetModuleDetailAsync", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("TailLogsAsync", workspace, StringComparison.Ordinal);
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
    }

    [Fact]
    public void Shell_permission_and_audit_sidebars_are_wired_to_axaml_view_models()
    {
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var permissionViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "PermissionPromptView.axaml");
        var auditViewPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views", "BrokerAuditView.axaml");
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var servicePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellPageDataService.cs");
        var workspace = File.ReadAllText(workspacePath);
        var permissionView = File.ReadAllText(permissionViewPath);
        var auditView = File.ReadAllText(auditViewPath);
        var viewModel = File.ReadAllText(viewModelPath);
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
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var workspace = File.ReadAllText(workspacePath);
        var unavailableView = File.ReadAllText(unavailableViewPath);
        var viewModel = File.ReadAllText(viewModelPath);

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
        var viewModelPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels", "ShellPageViewModels.cs");
        var mainWindow = File.ReadAllText(mainWindowPath);
        var shellChromeView = File.ReadAllText(shellChromeViewPath);
        var codeBehind = File.ReadAllText(codeBehindPath);
        var viewModel = File.ReadAllText(viewModelPath);

        Assert.Contains("new ShellChromeView", mainWindow);
        Assert.Contains("new ShellChromeViewModel", mainWindow);
        Assert.DoesNotContain("BuildLayout", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("new Grid", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("NavButton", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateNavigationState", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NavigationHost\"", shellChromeView);
        Assert.Contains("ItemsSource=\"{Binding NavigationItems}\"", shellChromeView);
        Assert.Contains("Command=\"{Binding RefreshCommand}\"", shellChromeView);
        Assert.Contains("ShellNavigationItemViewModel", shellChromeView);
        Assert.Contains("x:Name=\"SearchBox\"", shellChromeView);
        Assert.Contains("x:Name=\"ContentHost\"", shellChromeView);
        Assert.Contains("x:Name=\"CommandPanel\"", shellChromeView);
        Assert.Contains("x:Name=\"StatusBar\"", shellChromeView);
        Assert.Contains("Text=\"{Binding StatusText}\"", shellChromeView);
        Assert.Contains("Text=\"{Binding RunnerStatusText}\"", shellChromeView);
        Assert.Contains("AvaloniaXamlLoader.Load(this)", codeBehind);
        Assert.Contains("class ShellChromeViewModel", viewModel);
        Assert.Contains("class ShellNavigationItemViewModel", viewModel);
        Assert.Contains("public string StatusText", viewModel);
        Assert.Contains("public string RunnerStatusText", viewModel);
        Assert.DoesNotContain("_statusBar", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("_runnerStatus", mainWindow, StringComparison.Ordinal);
        Assert.Contains("new ShellWorkspaceController", mainWindow);
    }

    [Fact]
    public void Shell_theme_resource_dictionary_is_loaded_and_defines_design_tokens()
    {
        var appPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "App.cs");
        var themePath = Path.Combine(Root, "src", "MyPowerTools.UI", "Themes", "MptTheme.axaml");
        var app = File.ReadAllText(appPath);
        var theme = File.ReadAllText(themePath);

        Assert.Contains("avares://MyPowerTools.UI/Themes/MptTheme.axaml", app);
        Assert.Contains("x:Key=\"MptBrushAppBackground\"", theme);
        Assert.Contains("x:Key=\"MptPagePadding\"", theme);
        Assert.Contains("x:Key=\"MptRadiusCard\"", theme);
        Assert.Contains("TextBlock.MptPageTitle", theme);
        Assert.Contains("Border.MptCard", theme);
    }

    [Fact]
    public void Shell_axaml_views_use_theme_tokens_without_inline_colors()
    {
        var viewRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views");
        foreach (var file in Directory.EnumerateFiles(viewRoot, "*.axaml"))
        {
            var text = File.ReadAllText(file);
            Assert.Contains("DynamicResource", text);
            Assert.DoesNotContain("#", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brush.Parse", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Brushes.", text, StringComparison.Ordinal);
        }
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
        Assert.Equal(8, manifest["requiredSurfaceCount"]!.GetValue<int>());
        Assert.True(manifest["snapshotCount"]!.GetValue<int>() >= required.Length);
        Assert.Equal(manifest["snapshotCount"]!.GetValue<int>(), manifest["pixelSnapshotCount"]!.GetValue<int>());

        var keyboard = manifest["keyboardNavigation"]!.AsObject();
        var shortcuts = keyboard["shortcuts"]!.AsArray();
        var focusStates = keyboard["focusStates"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Contains(shortcuts, item =>
            item!["keys"]!.GetValue<string>() == "Ctrl+K" &&
            item["action"]!.GetValue<string>() == "focus-command-palette" &&
            item["surfaceId"]!.GetValue<string>() == "shell.command-palette");
        Assert.Contains(shortcuts, item =>
            item!["keys"]!.GetValue<string>() == "Ctrl+7" &&
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
        Assert.Contains(commandPalette["keyboardShortcuts"]!.AsArray(), item => item!.GetValue<string>() == "Ctrl+K");
        Assert.Contains(commandPalette["focusStates"]!.AsArray(), item => item!.GetValue<string>() == "command-item-focus-visible");
        Assert.Contains(commandPalette["states"]!.AsArray(), item => item!.GetValue<string>() == "permission-required");
        var settingsCenter = snapshots
            .First(item => item!["surfaceId"]!.GetValue<string>() == "shell.settings-center")!
            .AsObject();
        Assert.Contains(settingsCenter["states"]!.AsArray(), item => item!.GetValue<string>() == "conflict");
        var logsViewer = snapshots
            .First(item => item!["surfaceId"]!.GetValue<string>() == "shell.logs-viewer")!
            .AsObject();
        Assert.Contains(logsViewer["states"]!.AsArray(), item => item!.GetValue<string>() == "streaming");
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.snapshot.png").Length);
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
    public void Runtime_indexes_modules_without_starting_sidecars()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.Contains(snapshot.Cards, card => card.ModuleId == "doubao-agent");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "android-tools.remote-commands");
    }

    [Fact]
    public void Runtime_persists_module_enable_disable_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-module-state", Guid.NewGuid().ToString("N"));
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(dataRoot));
        runtime.Load(Path.Combine(Root, "modules"));

        var disabled = runtime.SetModuleEnabled("doubao-agent", enabled: false);

        Assert.Equal("disabled", disabled.State);
        Assert.DoesNotContain(runtime.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
        Assert.DoesNotContain(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.open");
        Assert.DoesNotContain(runtime.ListModules(includeDisabled: false), module => module.Module.Manifest.Id == "doubao-agent");
        Assert.Contains(runtime.ListModules(includeDisabled: true), module =>
            module.Module.Manifest.Id == "doubao-agent" &&
            module.Status.State == "disabled");

        var reloaded = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(dataRoot));
        reloaded.Load(Path.Combine(Root, "modules"));

        Assert.DoesNotContain(reloaded.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
        Assert.Contains(reloaded.ListModules(includeDisabled: true), module =>
            module.Module.Manifest.Id == "doubao-agent" &&
            module.Status.State == "disabled");

        var enabled = reloaded.SetModuleEnabled("doubao-agent", enabled: true);

        Assert.NotEqual("disabled", enabled.State);
        Assert.Contains(reloaded.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
    }

    [Fact]
    public void Command_execution_is_idempotent_by_invocation_id()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var request = new CommandRequest("same-invocation", "doubao-agent.open", new JsonObject());

        var first = runtime.ExecuteCommand(request);
        var second = runtime.ExecuteCommand(request);

        Assert.Equal(first.Output, second.Output);
        Assert.True(second.Success);
    }

    [Fact]
    public void Command_index_reads_declared_static_commands()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var commands = runtime.ListCommands("Doubao");

        Assert.Contains(commands, command => command.Id == "doubao-agent.health.check");
    }

    [Fact]
    public void Broker_declared_command_returns_permission_required()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var result = runtime.ExecuteCommand(new CommandRequest("broker-test", "adb-forwarder.portproxy.apply", new JsonObject()));

        Assert.False(result.Success);
        Assert.Equal("permission-required", result.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", result.Error!.Code);
    }

    [Fact]
    public void Settings_command_reads_revision()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var result = runtime.ExecuteCommand(new CommandRequest("settings-read", "screenease.settings.read", new JsonObject()));

        Assert.True(result.Success);
        Assert.Contains("Settings revision", result.Output);
    }

    [Fact]
    public void Settings_revision_conflicts_are_detected()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var current = runtime.GetSettings("doubao-agent");
        runtime.UpdateSettings(new SettingsPatch("doubao-agent", current.Revision, new JsonObject { ["enabled"] = true }));

        Assert.Throws<SettingsConflictException>(() =>
            runtime.UpdateSettings(new SettingsPatch("doubao-agent", current.Revision, new JsonObject { ["enabled"] = false })));
    }

    [Fact]
    public void Settings_persist_and_rollback()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-settings-test", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);
        var current = store.Get("sample.dotnet");
        var updated = store.Update(new SettingsPatch("sample.dotnet", current.Revision, new JsonObject { ["enabled"] = true }));
        store.Update(new SettingsPatch("sample.dotnet", updated.Revision, new JsonObject { ["enabled"] = false }));

        var rolledBack = store.Rollback("sample.dotnet");

        Assert.True(rolledBack.Values["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Broker_audit_redacts_sensitive_values()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new PrivilegedBroker(audit);

        broker.Evaluate("network.apply", "elevated", "token=abc123", "adb-forwarder", "listen=5556");

        var entry = audit.ReadAll().Single();
        Assert.Contains("token=****", entry.Reason);
    }

    [Fact]
    public void PrivilegedBroker_requires_broker_for_planned_privilege_levels()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-privileged-levels-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new PrivilegedBroker(audit);
        var brokered = new[] { "elevated", "service", "serviceUser", "serviceSystem", "sensitive", "broker" };

        foreach (var level in brokered)
        {
            var decision = broker.Evaluate($"test.{level}", level, $"reason for {level}", "test-module", $"scope:{level}");
            Assert.True(decision.RequiresBroker);
            Assert.Equal("MPT_PERMISSION_REQUIRED", decision.ErrorCode);
        }

        var userDecision = broker.Evaluate("test.user", "user", "ordinary user action", "test-module", "scope:user");
        var entries = audit.ReadAll();

        Assert.False(userDecision.RequiresBroker);
        Assert.Equal("", userDecision.ErrorCode);
        Assert.All(brokered, level => Assert.Contains(entries, entry => entry.PermissionLevel == level && entry.RequiresBroker));
        Assert.Contains(entries, entry => entry.PermissionLevel == "user" && !entry.RequiresBroker);
    }

    [Fact]
    public async Task ServiceBroker_restart_audits_service_user_level()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-service-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var services = new RecordingServiceManager();
        var broker = new ServiceBroker(services, audit);

        var result = await broker.RestartAsync(
            "smartbird-thermostat",
            "smartbird-user-service",
            "restart after token=abc123",
            CancellationToken.None);
        var entries = audit.ReadAll();
        var auditText = File.ReadAllText(path);

        Assert.True(result.Success);
        Assert.Equal(["stop:smartbird-user-service", "start:smartbird-user-service"], services.Operations);
        Assert.All(entries, entry => Assert.Equal("serviceUser", entry.PermissionLevel));
        Assert.Contains(entries, entry => entry.ActionId == "service.restart" && entry.Result == "requested" && entry.Rollback.Contains("start smartbird-user-service", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.ActionId == "service.restart" && entry.Result == "started");
        Assert.Contains("token=****", auditText);
        Assert.DoesNotContain("abc123", auditText);
    }

    [Fact]
    public async Task SecretBroker_stores_reads_deletes_and_redacts_audit()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-secret-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new SecretBroker(new InMemorySecretStore(), audit);

        var reference = await broker.SaveAsync("sample.dotnet", "api-token", "super-secret-value", "save token=abc123", CancellationToken.None);
        var value = await broker.ReadAsync("sample.dotnet", reference, "read password=hunter2", CancellationToken.None);
        await broker.DeleteAsync("sample.dotnet", reference, "delete secret=gone", CancellationToken.None);
        var missing = await broker.ReadAsync("sample.dotnet", reference, "read after delete", CancellationToken.None);

        Assert.Equal("secret://sample.dotnet/api-token", reference.Uri);
        Assert.Equal("super-secret-value", value);
        Assert.Null(missing);

        var auditText = File.ReadAllText(path);
        Assert.Contains("secret.save", auditText);
        Assert.Contains("secret.read", auditText);
        Assert.Contains("secret.delete", auditText);
        Assert.Contains("token=****", auditText);
        Assert.Contains("password=****", auditText);
        Assert.Contains("secret=****", auditText);
        Assert.DoesNotContain("super-secret-value", auditText);
        Assert.DoesNotContain("abc123", auditText);
        Assert.DoesNotContain("hunter2", auditText);
    }

    [Fact]
    public async Task InMemorySecretStore_rejects_unsafe_secret_references()
    {
        var store = new InMemorySecretStore();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("sample.dotnet", "../api-token", "value", CancellationToken.None));

        var invalidRead = await store.ReadAsync(new SecretReference("secret://sample.dotnet/../api-token"), CancellationToken.None);
        Assert.Null(invalidRead);
    }

    [Fact]
    public async Task AutostartBroker_enable_disable_status_audits_user_actions()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-autostart-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var autostart = new RecordingAutostartService();
        var broker = new AutostartBroker(autostart, audit);

        var before = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);
        var enable = await broker.EnableAsync("runner", "MyPowerTools.Runner", "\"runner.exe\"", "test enable", CancellationToken.None);
        var afterEnable = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);
        var disable = await broker.DisableAsync("runner", "MyPowerTools.Runner", "test disable", CancellationToken.None);
        var afterDisable = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);

        Assert.Equal("disabled", before.State);
        Assert.True(enable.Success);
        Assert.Equal("enabled", afterEnable.State);
        Assert.Equal("\"runner.exe\"", afterEnable.Detail);
        Assert.True(disable.Success);
        Assert.Equal("disabled", afterDisable.State);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.enable" && entry.Result == "enabled");
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.disable" && entry.Result == "disabled");
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.status" && entry.Result == "enabled");
    }

    [Fact]
    public async Task Cli_runner_autostart_status_reports_missing_entry()
    {
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "runner",
            "autostart",
            "status",
            "--id",
            $"mpt-test-{Guid.NewGuid():N}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("disabled", result.Output);
        Assert.Contains("No HKCU Run entry exists", result.Output);
    }

    [Fact]
    public async Task NetworkBroker_change_set_executes_remove_then_apply_and_audits()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-network-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var network = new RecordingNetworkBroker();
        var broker = new NetworkBroker(network, audit);
        var oldRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 7555);
        var newRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 5555);

        var result = await broker.ApplyChangeSetAsync(
            "adb-forwarder",
            new PortProxyChangeSet([newRule], [oldRule]),
            "test broker changeset",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["remove:0.0.0.0:5555", "apply:0.0.0.0:5555->127.0.0.1:5555"], network.Operations);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "network.portproxy.changeset" && entry.Result == "success");
    }

    [Fact]
    public async Task NetworkBroker_change_set_rolls_back_after_partial_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-network-broker-rollback-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var network = new RecordingNetworkBroker
        {
            FailNextApply = true
        };
        var broker = new NetworkBroker(network, audit);
        var oldRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 7555);
        var newRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 5555);

        var result = await broker.ApplyChangeSetAsync(
            "adb-forwarder",
            new PortProxyChangeSet([newRule], [oldRule]),
            "test broker rollback",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("rolled-back", result.State);
        Assert.Equal(
            ["remove:0.0.0.0:5555", "apply:0.0.0.0:5555->127.0.0.1:5555", "apply:0.0.0.0:5555->127.0.0.1:7555"],
            network.Operations);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "network.portproxy.rollback" && entry.Result == "rolled-back");
    }

    [Fact]
    public void Package_store_installs_and_repairs_hashes()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-package-store", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));

        var result = store.Install(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var repairIssues = store.Repair("sample-dotnet");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.True(File.Exists(Path.Combine(result.TargetPath, "shared", "package.signature.json")));
        Assert.Empty(repairIssues);
    }

    [Fact]
    public void Package_trust_strict_policy_requires_signature_hook()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-package-trust", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        var trust = new PackageTrustVerifier();

        var local = trust.Verify(packageCopy, PackageTrustPolicy.LocalDevelopment);
        var strictBefore = trust.Verify(packageCopy, PackageTrustPolicy.StrictSigned);
        var signaturePath = trust.WriteLocalSignatureHook(packageCopy);
        var strictAfter = trust.Verify(packageCopy, PackageTrustPolicy.StrictSigned);

        Assert.True(local.IsTrusted, string.Join(Environment.NewLine, local.Issues.Select(issue => issue.Message)));
        Assert.Equal("local-trust", local.State);
        Assert.False(strictBefore.IsTrusted);
        Assert.Contains(strictBefore.Issues, issue => issue.Severity == "error" && issue.Message.Contains("signature hook", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(signaturePath));
        Assert.True(strictAfter.IsTrusted, string.Join(Environment.NewLine, strictAfter.Issues.Select(issue => issue.Message)));
        Assert.Equal("signature-hook", strictAfter.State);
    }

    [Fact]
    public void Package_trust_rejects_hash_manifest_path_escape()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-package-trust-escape", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        File.WriteAllText(
            Path.Combine(packageCopy, "package.json"),
            """
            {
              "schemaVersion": "1.0",
              "id": "sample-dotnet",
              "displayName": "Sample .NET Module",
              "version": "0.2.0",
              "modules": [
                "module.json"
              ],
              "hashes": "../outside.hashes.json"
            }
            """);

        var report = new PackageTrustVerifier().Verify(packageCopy, PackageTrustPolicy.StrictSigned);

        Assert.False(report.IsTrusted);
        Assert.Equal("invalid-trust-manifest", report.State);
        Assert.Contains(report.Issues, issue => issue.Message.Contains("escapes package directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_store_uninstall_and_rollback_restores_installed_package()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-package-store-rollback", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));

        var install = store.Install(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        Assert.True(install.Success, string.Join(Environment.NewLine, install.Issues.Select(issue => issue.Message)));

        var markerPath = Path.Combine(install.TargetPath, "shared", "rollback-marker.txt");
        File.WriteAllText(markerPath, "restore me");
        var uninstall = store.Uninstall("sample-dotnet");
        var rollback = store.Rollback("sample-dotnet");

        Assert.True(uninstall.Success, string.Join(Environment.NewLine, uninstall.Issues.Select(issue => issue.Message)));
        Assert.True(rollback.Success, string.Join(Environment.NewLine, rollback.Issues.Select(issue => issue.Message)));
        Assert.True(Directory.Exists(rollback.TargetPath));
        Assert.True(File.Exists(markerPath));
        Assert.Empty(store.Repair("sample-dotnet"));
    }

    [Fact]
    public async Task Cli_package_install_uninstall_rollback_uses_store_root()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-package-store", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");
        var fixture = Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet");
        var target = Path.Combine(storeRoot, "sample-dotnet");
        var rollbackTarget = target + ".rollback";

        var install = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "install",
            fixture,
            "--store-root",
            storeRoot);

        var uninstall = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "uninstall",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        Assert.Equal(0, install.ExitCode);
        Assert.Contains("sample-dotnet: success", install.Output);
        Assert.Equal(0, uninstall.ExitCode);
        Assert.Contains("sample-dotnet: success", uninstall.Output);
        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(rollbackTarget));

        var rollback = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "rollback",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        var repair = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "repair",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Contains("sample-dotnet: success", rollback.Output);
        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(rollbackTarget));
        Assert.Equal(0, repair.ExitCode);
        Assert.Contains("repair check passed.", repair.Output);
    }

    [Fact]
    public async Task Cli_inspect_package_hash_and_doctor_cover_package_commands()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-package-commands", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");
        var hashPath = Path.Combine(packageCopy, "shared", "package.hashes.json");
        var signaturePath = Path.Combine(packageCopy, "shared", "package.signature.json");

        var inspect = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "inspect",
            "modules");

        var hash = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "hash",
            packageCopy);

        var sign = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "sign-local",
            packageCopy);

        var trust = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "trust",
            packageCopy,
            "--strict");

        var doctor = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "doctor");

        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("android-tools-suite", inspect.Output);
        Assert.Contains("android-tools.remote-commands", inspect.Output);
        Assert.Contains("permission: apply-portproxy level=broker capability=network.portForwarding", inspect.Output);
        Assert.Contains("requires: network.portForwarding required", inspect.Output);
        Assert.Equal(0, hash.ExitCode);
        Assert.Contains("package.hashes.json", hash.Output);
        Assert.True(File.Exists(hashPath));
        Assert.Contains("module.json", File.ReadAllText(hashPath));
        Assert.Equal(0, sign.ExitCode);
        Assert.Contains("package.signature.json", sign.Output);
        Assert.True(File.Exists(signaturePath));
        Assert.Equal(0, trust.ExitCode);
        Assert.Contains("sample-dotnet: signature-hook", trust.Output);
        Assert.Equal(0, doctor.ExitCode);
        Assert.Contains("packages: 5 checked, errors: 0", doctor.Output);
        Assert.Contains("modules: 7", doctor.Output);
    }

    [Fact]
    public void Platform_capability_registry_degrades_missing_capabilities()
    {
        var registry = new CapabilityRegistry([
            new CapabilityDescriptor("ipc.local", "user", true, "test", "ok")
        ]);

        var resolution = registry.ResolveForModule("module", [
            new CapabilityRequest("module", "ipc.local", true, "ipc"),
            new CapabilityRequest("module", "network.portForwarding", false, "optional")
        ]);

        Assert.Equal("degraded", resolution.State);
    }

    [Fact]
    public void Platform_capability_registry_marks_missing_required_capability_unsupported()
    {
        var registry = new CapabilityRegistry([
            new CapabilityDescriptor("ipc.local", "user", true, "test", "ok")
        ]);

        var resolution = registry.ResolveForModule("module", [
            new CapabilityRequest("module", "ipc.local", true, "ipc"),
            new CapabilityRequest("module", "network.portForwarding", true, "required")
        ]);

        Assert.Equal("unsupported", resolution.State);
        Assert.False(resolution.IsUsable);
        Assert.Contains(resolution.Messages, message => message.Contains("network.portForwarding", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_ipc_service_selects_platform_native_endpoint_shape()
    {
        var windows = new LocalIpcService(new PlatformId("windows", "x64"), Path.Combine(Path.GetTempPath(), "ignored"));
        var linux = new LocalIpcService(new PlatformId("linux", "x64"), Path.Combine(Path.GetTempPath(), "mpt-ipc-test"));
        var mac = new LocalIpcService(new PlatformId("macos", "arm64"), Path.Combine(Path.GetTempPath(), "mpt-ipc-test"));

        Assert.Equal(IpcTransport.NamedPipe, windows.RunnerEndpoint.Transport);
        Assert.Equal("mypowertools.runner.hostcontrol", windows.RunnerEndpoint.Address);
        Assert.Equal(IpcTransport.NamedPipe, windows.CreateEndpoint("sample.grpc").Transport);
        Assert.Equal("mypowertools.sample.grpc", windows.CreateEndpoint("sample.grpc").Address);

        Assert.Equal(IpcTransport.UnixDomainSocket, linux.RunnerEndpoint.Transport);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "mpt-ipc-test"), linux.RunnerEndpoint.Address, StringComparison.Ordinal);
        Assert.Equal(IpcTransport.UnixDomainSocket, linux.CreateEndpoint("sample.grpc").Transport);
        Assert.EndsWith("mypowertools.sample.grpc.sock", linux.CreateEndpoint("sample.grpc").Address, StringComparison.Ordinal);
        Assert.Equal(IpcTransport.UnixDomainSocket, mac.RunnerEndpoint.Transport);
    }

    [Fact]
    public async Task Mac_and_linux_platform_packs_expose_truthful_degraded_services()
    {
        var mac = new MacPlatformPack();
        var linux = new LinuxPlatformPack();

        Assert.False(mac.Capabilities.Resolve("network.portForwarding").Supported);
        Assert.True(mac.Capabilities.Resolve("ipc.local").Supported);
        Assert.True(linux.Capabilities.Resolve("process.inspect").Supported);
        Assert.Equal(IpcTransport.UnixDomainSocket, mac.LocalIpc.RunnerEndpoint.Transport);
        Assert.Equal(IpcTransport.UnixDomainSocket, linux.LocalIpc.RunnerEndpoint.Transport);

        var macService = await mac.Services.GetStatusAsync("sample", CancellationToken.None);
        var linuxAutostart = await linux.Autostart.GetAsync("sample", CancellationToken.None);
        var linuxNetwork = await linux.Network.ApplyPortProxyRuleAsync(
            new PortProxyRule("127.0.0.1", 12345, "127.0.0.1", 12346),
            CancellationToken.None);
        var processes = await linux.Processes.ListAsync(CancellationToken.None);

        Assert.Equal("unsupported", macService.State);
        Assert.Contains("launchd", macService.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unsupported", linuxAutostart.State);
        Assert.False(linuxNetwork.Success);
        Assert.Equal("unsupported", linuxNetwork.State);
        Assert.NotEmpty(processes);
    }

    [Fact]
    public async Task Platform_packs_expose_hotkey_and_privilege_surfaces()
    {
        var mac = new MacPlatformPack();
        var linux = new LinuxPlatformPack();

        var registration = new HotkeyRegistration("command-palette", "Ctrl+Alt+Space", "runner", "Open the command palette.");
        var request = new PrivilegeRequest(
            "network.portproxy.apply",
            "elevated",
            "Apply a Windows portproxy rule through the broker.",
            "adb-forwarder",
            "127.0.0.1:45678",
            "Add a v4tov4 portproxy rule.",
            "Remove the v4tov4 portproxy rule.");

        var macHotkey = await mac.Hotkeys.RegisterAsync(registration, CancellationToken.None);
        var macPrivilege = await mac.Privileges.EvaluateAsync(request, CancellationToken.None);
        var linuxHotkey = await linux.Hotkeys.RegisterAsync(registration, CancellationToken.None);
        var linuxPrivilege = await linux.Privileges.EvaluateAsync(request, CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            var windows = new WindowsPlatformPack();
            var windowsHotkey = await windows.Hotkeys.RegisterAsync(registration, CancellationToken.None);
            var windowsPrivilege = await windows.Privileges.EvaluateAsync(request, CancellationToken.None);

            Assert.False(windows.Capabilities.Resolve("hotkey.global").Supported);
            Assert.True(windows.Capabilities.Resolve("privilege.elevated").Supported);
            Assert.False(windowsHotkey.Success);
            Assert.Equal("unsupported", windowsHotkey.State);
            Assert.True(windowsPrivilege.RequiresBroker);
            Assert.Equal("permission-required", windowsPrivilege.State);
            Assert.Contains("Broker", windowsPrivilege.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(mac.Capabilities.Resolve("hotkey.global").Supported);
        Assert.False(mac.Capabilities.Resolve("privilege.elevated").Supported);
        Assert.Equal("unsupported", macHotkey.State);
        Assert.Equal("unsupported", macPrivilege.State);
        Assert.True(macPrivilege.RequiresBroker);

        Assert.False(linux.Capabilities.Resolve("hotkey.global").Supported);
        Assert.False(linux.Capabilities.Resolve("privilege.elevated").Supported);
        Assert.Equal("unsupported", linuxHotkey.State);
        Assert.Equal("unsupported", linuxPrivilege.State);
        Assert.True(linuxPrivilege.RequiresBroker);
    }

    [Fact]
    public async Task Privileged_broker_implements_platform_privilege_contract()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), "mpt-privilege-contract", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var broker = new PrivilegedBroker(new AuditLog(auditPath));

        var decision = await broker.EvaluateAsync(
            new PrivilegeRequest("service.restart", "serviceUser", "Restart a user service.", "smartbird-thermostat", "SmartBirdService"),
            CancellationToken.None);

        Assert.True(decision.RequiresBroker);
        Assert.Equal("permission-required", decision.State);
        Assert.Equal(MptErrorCodes.PermissionRequired, decision.ErrorCode);
        Assert.Contains(broker.Audit, entry =>
            entry.ModuleId == "smartbird-thermostat" &&
            entry.ActionId == "service.restart" &&
            entry.RequiresBroker);
    }

    [Fact]
    public async Task Unsupported_tray_service_reports_actionable_degraded_state()
    {
        await using var tray = new UnsupportedTrayService("test tray", "native provider pending");
        var invoked = false;

        var result = await tray.StartAsync(
            new TrayOptions("test", "Test Tray", null, [new TrayMenuItem("open", "Open", IsDefault: true)]),
            (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unsupported", result.State);
        Assert.Equal("unsupported", tray.State);
        Assert.Contains("native provider pending", result.Message);
        Assert.False(invoked);
    }

    [Fact]
    public void Log_router_redacts_sensitive_values()
    {
        var router = new LogRouter(Path.Combine(Path.GetTempPath(), "mpt-log-test", Guid.NewGuid().ToString("N")));
        router.Append("pkg", "module", "info", "password=hunter2 token=abc", "invocation");

        var record = router.Tail("module").Single();
        Assert.Contains("password=****", record.Message);
        Assert.Contains("token=****", record.Message);
    }

    [Fact]
    public void Log_router_allows_parallel_append_to_same_module_file()
    {
        var router = new LogRouter(Path.Combine(Path.GetTempPath(), "mpt-log-parallel-test", Guid.NewGuid().ToString("N")));

        Parallel.For(0, 40, index => router.Append("pkg", "runner", "info", $"message={index}", $"invocation-{index}"));

        var records = router.Tail("runner", 100);
        Assert.Equal(40, records.Count);
        Assert.Contains(records, record => record.InvocationId == "invocation-0");
        Assert.Contains(records, record => record.InvocationId == "invocation-39");
    }

    [Fact]
    public async Task Inproc_sample_module_executes_command()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        var package = new PackageReader().ReadPackageDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var module = package.Modules.Single();
        var host = new InProcDotNetModuleHost();

        var loaded = await host.LoadAsync(module, CancellationToken.None);
        var result = await loaded.ExecuteCommandAsync(new CommandRequest("test", "sample.dotnet.ping", new JsonObject()), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
    }

    [Fact]
    public async Task Inproc_disk_module_uses_collectible_load_context_and_unloads()
    {
        var weakReference = await LoadAndDisposeAdbForwarderAsync();

        for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        Assert.False(weakReference.IsAlive);
    }

    [Fact]
    public async Task Runtime_restarts_clean_inproc_module_by_unloading_context()
    {
        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-inproc-clean-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules", "adb-forwarder"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("inproc-dotnet", before.PoolKey, CancellationToken.None);
        var after = runtime.GetRuntimeDiagnostics();

        Assert.True(dynamicCount > 0);
        Assert.Equal("loaded", before.State);
        Assert.Equal(Environment.ProcessId, before.ProcessId);
        Assert.True(restart.Success, restart.Message);
        Assert.Equal("unloaded", restart.State);
        Assert.Contains("adb-forwarder", restart.ModuleIds);
        Assert.Empty(after.Processes);
    }

    [Fact]
    public async Task Runtime_marks_inproc_unload_failure_as_pending_runner_restart()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-leaky-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.leaky",
            "sample-dotnet-leaky",
            "Leaky .NET Module",
            typeof(LeakyDotNetModule).FullName!);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-inproc-leaky-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("inproc-dotnet", before.PoolKey, CancellationToken.None);
        var pending = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(dynamicCount > 0);
        Assert.Equal("loaded", before.State);
        Assert.False(restart.Success);
        Assert.Equal("pending-runner-restart", restart.State);
        Assert.Contains("collectible AssemblyLoadContext", restart.Message);
        Assert.Equal("pending-runner-restart", pending.State);
        Assert.Equal("manual-runner-restart", pending.RestartPolicy);
        Assert.Contains("collectible AssemblyLoadContext", pending.PolicyReason);
        Assert.Contains("sample.dotnet.leaky", pending.ModuleIds);

        var package = new PackageReader().ReadPackageDirectory(packageRoot);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.LoadAsync(package.Modules.Single(), CancellationToken.None));
        Assert.Contains("restart Runner", blocked.Message);
    }

    [Fact]
    public async Task Inproc_plugins_with_conflicting_dependency_versions_load_in_separate_contexts()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-inproc-conflict", Guid.NewGuid().ToString("N"));
        var packageOne = Path.Combine(root, "package-one");
        var packageTwo = Path.Combine(root, "package-two");
        Directory.CreateDirectory(packageOne);
        Directory.CreateDirectory(packageTwo);

        await BuildGeneratedInProcPluginPackageAsync(packageOne, "conflict.one", "conflict-one", "ConflictPluginOne", "dependency-v1", "1.0.0.0");
        await BuildGeneratedInProcPluginPackageAsync(packageTwo, "conflict.two", "conflict-two", "ConflictPluginTwo", "dependency-v2", "2.0.0.0");

        await using var host = new InProcDotNetModuleHost();
        var reader = new PackageReader();
        var moduleOne = reader.ReadPackageDirectory(packageOne).Modules.Single();
        var moduleTwo = reader.ReadPackageDirectory(packageTwo).Modules.Single();

        var loadedOne = await host.LoadAsync(moduleOne, CreateGeneratedPluginContext("conflict-one", "conflict.one"), CancellationToken.None);
        var loadedTwo = await host.LoadAsync(moduleTwo, CreateGeneratedPluginContext("conflict-two", "conflict.two"), CancellationToken.None);
        var resultOne = await loadedOne.ExecuteCommandAsync(new CommandRequest("conflict-one", "conflict.one.dependency", new JsonObject()), CancellationToken.None);
        var resultTwo = await loadedTwo.ExecuteCommandAsync(new CommandRequest("conflict-two", "conflict.two.dependency", new JsonObject()), CancellationToken.None);
        var contextOne = AssemblyLoadContext.GetLoadContext(loadedOne.GetType().Assembly);
        var contextTwo = AssemblyLoadContext.GetLoadContext(loadedTwo.GetType().Assembly);
        var dependencyOne = contextOne!.Assemblies.Single(assembly => assembly.GetName().Name == "PluginSharedDependency");
        var dependencyTwo = contextTwo!.Assemblies.Single(assembly => assembly.GetName().Name == "PluginSharedDependency");

        Assert.True(resultOne.Success, resultOne.Error?.Message);
        Assert.True(resultTwo.Success, resultTwo.Error?.Message);
        Assert.Contains("dependency-v1", resultOne.Output);
        Assert.Contains("dependency-v2", resultTwo.Output);
        Assert.NotSame(contextOne, contextTwo);
        Assert.Equal(new Version(1, 0, 0, 0), dependencyOne.GetName().Version);
        Assert.Equal(new Version(2, 0, 0, 0), dependencyTwo.GetName().Version);
    }

    [Fact]
    public async Task Inproc_module_update_uses_shadow_copy_instead_of_original_package_dll()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-inproc-shadow-update", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "package");
        var replacementRoot = Path.Combine(root, "replacement");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(replacementRoot);

        await BuildGeneratedInProcPluginPackageAsync(packageRoot, "shadow.update", "shadow-update", "ShadowUpdatePlugin", "dependency-v1", "1.0.0.0");
        await BuildGeneratedInProcPluginPackageAsync(replacementRoot, "shadow.update", "shadow-update", "ShadowUpdatePlugin", "dependency-v2", "2.0.0.0");

        var reader = new PackageReader();
        var module = reader.ReadPackageDirectory(packageRoot).Modules.Single();
        var context = CreateGeneratedPluginContext("shadow-update", "shadow.update");
        await using var firstHost = new InProcDotNetModuleHost();

        var (loadedAssemblyPath, before, stillLoaded) = await LoadAndReplaceShadowPluginAsync(firstHost, module, context, packageRoot, replacementRoot);
        var restart = await firstHost.RestartProcessAsync("module:shadow.update", CancellationToken.None);
        await using var secondHost = new InProcDotNetModuleHost();
        var reloaded = await secondHost.LoadAsync(reader.ReadPackageDirectory(packageRoot).Modules.Single(), context, CancellationToken.None);
        var after = await reloaded.ExecuteCommandAsync(new CommandRequest("shadow-after", "shadow.update.dependency", new JsonObject()), CancellationToken.None);

        Assert.True(before.Success, before.Error?.Message);
        Assert.True(stillLoaded.Success, stillLoaded.Error?.Message);
        Assert.True(after.Success, after.Error?.Message);
        Assert.True(restart.Success, restart.Message);
        Assert.Contains("inproc-shadow", loadedAssemblyPath);
        Assert.StartsWith(context.CacheDirectory, loadedAssemblyPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(loadedAssemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dependency-v1", before.Output);
        Assert.Contains("dependency-v1", stillLoaded.Output);
        Assert.Contains("dependency-v2", after.Output);
    }

    [Fact]
    public void Production_module_projects_reference_abstractions_not_runtime()
    {
        var projectFiles = new[]
        {
            "src/AdbForwarder.MyPowerTools/AdbForwarder.MyPowerTools.csproj",
            "src/AndroidTools.MyPowerTools/AndroidTools.MyPowerTools.csproj",
            "src/DoubaoAgent.MyPowerTools/DoubaoAgent.MyPowerTools.csproj",
            "src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj",
            "src/SmartBirdThermostat.MyPowerTools/SmartBirdThermostat.MyPowerTools.csproj"
        };

        foreach (var projectFile in projectFiles)
        {
            var content = File.ReadAllText(Path.Combine(Root, projectFile));
            Assert.Contains("MyPowerTools.Abstractions.csproj", content);
            Assert.DoesNotContain("MyPowerTools.Runtime.csproj", content);
        }
    }

    [Fact]
    public void Runtime_shell_and_host_do_not_reference_concrete_module_projects()
    {
        var hostProjectFiles = new[]
        {
            "src/MyPowerTools.Runtime/MyPowerTools.Runtime.csproj",
            "src/MyPowerTools.HostControl/MyPowerTools.HostControl.csproj",
            "src/MyPowerTools.Shell.Avalonia/MyPowerTools.Shell.Avalonia.csproj"
        };
        var concreteModuleTokens = new[]
        {
            "AdbForwarder.MyPowerTools",
            "AndroidTools.MyPowerTools",
            "DoubaoAgent.MyPowerTools",
            "ScreenEase.MyPowerTools",
            "SmartBirdThermostat.MyPowerTools"
        };

        foreach (var projectFile in hostProjectFiles)
        {
            var content = File.ReadAllText(Path.Combine(Root, projectFile));
            foreach (var token in concreteModuleTokens)
            {
                Assert.DoesNotContain(token, content);
            }
        }

        foreach (var sourceRoot in new[] { "src/MyPowerTools.Runtime", "src/MyPowerTools.HostControl", "src/MyPowerTools.Shell.Avalonia" })
        {
            foreach (var sourceFile in Directory.EnumerateFiles(Path.Combine(Root, sourceRoot), "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(sourceFile);
                foreach (var token in concreteModuleTokens)
                {
                    Assert.DoesNotContain(token, content);
                }
            }
        }
    }

    [Fact]
    public async Task Runtime_delegates_dynamic_inproc_commands_to_transport_host()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-dynamic", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-dynamic-inproc", "sample.dotnet.ping", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ping"), command => command.Id == "sample.dotnet.ping");
        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "sample.dotnet" && card.State == "running");
    }

    private static async Task<WeakReference> LoadAndDisposeAdbForwarderAsync()
    {
        await using var host = new InProcDotNetModuleHost();
        var package = new PackageReader().ReadPackageDirectory(Path.Combine(Root, "modules", "adb-forwarder"));
        var module = package.Modules.Single();
        var loaded = await host.LoadAsync(module, CancellationToken.None);
        var loadContext = AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly);

        Assert.NotNull(loadContext);
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
        Assert.True(loadContext!.IsCollectible);

        var weakReference = new WeakReference(loadContext, trackResurrection: false);
        await loaded.DisposeAsync(CancellationToken.None);
        loaded = null!;
        loadContext = null;
        await host.DisposeAsync();
        return weakReference;
    }

    private static void WriteInProcDotNetModulePackage(string packageRoot, string moduleId, string packageId, string displayName, string typeName)
    {
        var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = packageId,
            ["displayName"] = displayName,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = assemblyName,
                    ["type"] = typeName
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(string LoadedAssemblyPath, CommandExecutionResult Before, CommandExecutionResult StillLoaded)> LoadAndReplaceShadowPluginAsync(
        InProcDotNetModuleHost host,
        MptModuleDefinition module,
        ModuleContext context,
        string packageRoot,
        string replacementRoot)
    {
        var loaded = await host.LoadAsync(module, context, CancellationToken.None);
        var loadedAssemblyPath = loaded.GetType().Assembly.Location;
        var before = await loaded.ExecuteCommandAsync(new CommandRequest("shadow-before", "shadow.update.dependency", new JsonObject()), CancellationToken.None);

        File.Copy(Path.Combine(replacementRoot, "ShadowUpdatePlugin.dll"), Path.Combine(packageRoot, "ShadowUpdatePlugin.dll"), overwrite: true);
        File.Copy(Path.Combine(replacementRoot, "PluginSharedDependency.dll"), Path.Combine(packageRoot, "PluginSharedDependency.dll"), overwrite: true);
        File.SetLastWriteTimeUtc(Path.Combine(packageRoot, "ShadowUpdatePlugin.dll"), DateTime.UtcNow.AddMinutes(1));
        var stillLoaded = await loaded.ExecuteCommandAsync(new CommandRequest("shadow-still-loaded", "shadow.update.dependency", new JsonObject()), CancellationToken.None);
        loaded = null!;
        return (loadedAssemblyPath, before, stillLoaded);
    }

    private static async Task BuildGeneratedInProcPluginPackageAsync(
        string packageRoot,
        string moduleId,
        string packageId,
        string assemblyName,
        string dependencyValue,
        string dependencyVersion)
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "mpt-generated-plugin-src", Guid.NewGuid().ToString("N"));
        var dependencyRoot = Path.Combine(sourceRoot, "PluginSharedDependency");
        var pluginRoot = Path.Combine(sourceRoot, assemblyName);
        Directory.CreateDirectory(dependencyRoot);
        Directory.CreateDirectory(pluginRoot);

        await File.WriteAllTextAsync(Path.Combine(dependencyRoot, "PluginSharedDependency.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>PluginSharedDependency</AssemblyName>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
</Project>
""");
        await File.WriteAllTextAsync(Path.Combine(dependencyRoot, "SharedDependency.cs"), $$"""
using System.Reflection;

[assembly: AssemblyVersion("{{dependencyVersion}}")]

namespace PluginSharedDependency;

public static class SharedDependency
{
    public static string Report() => "{{dependencyValue}}";
}
""");

        var abstractionsProject = Path.Combine(Root, "src", "MyPowerTools.Abstractions", "MyPowerTools.Abstractions.csproj");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, $"{assemblyName}.csproj"), $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{{assemblyName}}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{{abstractionsProject}}" />
    <ProjectReference Include="{{Path.Combine(dependencyRoot, "PluginSharedDependency.csproj")}}" />
  </ItemGroup>
</Project>
""");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "GeneratedModule.cs"), $$""""
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Runtime;
using PluginSharedDependency;

namespace {{assemblyName}};

public sealed class GeneratedModule : IMptModule
{
    public string Id => "{{moduleId}}";
    public string PackageId => "{{packageId}}";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            SharedDependency.Report(),
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("dependency", "Dependency", true, SharedDependency.Report())],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("{{moduleId}}.dependency", Id, "Read dependency", "Generated InProc dependency probe", "action")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            SharedDependency.Report()));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, "{}"));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
"""");

        var publish = await RunDotnetAsync(
            "publish",
            Path.Combine(pluginRoot, $"{assemblyName}.csproj"),
            "--nologo",
            "-c",
            "Release",
            "-o",
            packageRoot);
        Assert.True(publish.ExitCode == 0, publish.Output);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = packageId,
            ["displayName"] = moduleId,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = $"{assemblyName}.dll",
                    ["type"] = $"{assemblyName}.GeneratedModule"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "settings")
        };

        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ModuleContext CreateGeneratedPluginContext(string packageId, string moduleId)
    {
        return CreateModuleContext(
            packageId,
            moduleId,
            $"generated-{moduleId}",
            ["status", "commands", "settings"]);
    }

    private static JsonObject CreateRuntimePolicyManifest(JsonObject runtimePolicy)
    {
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "runtime-policy-sample",
            ["packageId"] = "runtime-policy-sample",
            ["displayName"] = "Runtime Policy Sample",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 90,
                    ["command"] = "sample-sidecar",
                    ["args"] = new JsonArray("sample")
                }
            },
            ["runtimePolicy"] = runtimePolicy,
            ["capabilities"] = new JsonArray("status", "commands")
        };
    }

    [Fact]
    public async Task Runtime_executes_http_facade_module_against_local_service()
    {
        await using var server = TestHttpFacadeServer.Start();
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, server.BaseUrl);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-http-ping", "sample.http-runtime.ping", new JsonObject()),
            CancellationToken.None);
        var logs = runtime.TailLogs("sample.http-runtime");

        Assert.Contains(snapshot.Cards, card => card.ModuleId == "sample.http-runtime" && card.State == "running");
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("HTTP 200", result.Output);
        Assert.Contains("pong", result.Output);
        Assert.Contains("token=****", result.Output);
        Assert.DoesNotContain("abc123", result.Output);
        Assert.Contains(logs, record => record.InvocationId == "runtime-http-ping" && record.EventSeq > 0);
    }

    [Fact]
    public async Task Runtime_supervisor_reports_repeated_http_facade_failures()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-supervisor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, ReserveUnusedLoopbackUrl());
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http-supervisor", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var module = Assert.Single(diagnostics.Modules);
        var alert = Assert.Single(runtime.GetDashboardSnapshot().Alerts);

        Assert.Equal("degraded", module.State);
        Assert.Equal("intervention-needed", module.SupervisorState);
        Assert.True(module.ObservationCount >= 4);
        Assert.True(module.ConsecutiveFailureCount >= 3);
        Assert.Contains("HTTP facade", module.SupervisorAction);
        Assert.Equal("module-supervisor-sample.http-runtime", alert.Id);
        Assert.Contains("HTTP facade", alert.Body);
    }

    [Fact]
    public async Task Runtime_supervisor_resets_after_http_facade_recovery()
    {
        await using var server = TestHttpFacadeServer.Start();
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-supervisor-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, server.BaseUrl);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http-supervisor-recovery", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var module = Assert.Single(runtime.GetRuntimeDiagnostics().Modules);

        Assert.Equal("running", module.State);
        Assert.Equal("healthy", module.SupervisorState);
        Assert.Equal(0, module.ConsecutiveFailureCount);
        Assert.Equal("No action required.", module.SupervisorAction);
        Assert.Empty(runtime.GetDashboardSnapshot().Alerts);
    }

    [Fact]
    public async Task Runtime_runs_grpc_sidecar_module_over_native_ipc()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-sidecar", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("gRPC"), command => command.Id == "sample.grpc.ping");
        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
    }

    [Fact]
    public async Task Runtime_recovers_grpc_sidecar_after_process_crash()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-crash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-crash", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var firstPing = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-first-ping", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var crash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);

        await Task.Delay(750);

        var recoveredPing = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-recovered-ping", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var logs = runtime.TailLogs("sample.grpc");

        Assert.True(firstPing.Success);
        Assert.True(crash.Success);
        Assert.True(recoveredPing.Success);
        Assert.Contains("pong", recoveredPing.Output);
        Assert.NotEqual(ExtractPid(firstPing.Output), ExtractPid(recoveredPing.Output));
        Assert.Contains(logs, record => record.InvocationId == "runtime-grpc-crash" && record.EventSeq > 0);
        Assert.Contains(logs, record => record.InvocationId == "runtime-grpc-recovered-ping" && record.EventSeq > 0);
    }

    [Fact]
    public async Task Runtime_restarts_grpc_process_pool_on_request()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-restart-first", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("grpc-ipc", before.PoolKey, CancellationToken.None);
        var afterRestart = runtime.GetRuntimeDiagnostics();
        var second = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-restart-second", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var afterSecond = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(restart.Success, restart.Message);
        Assert.Equal("restarting", restart.State);
        Assert.Contains("sample.grpc", restart.ModuleIds);
        Assert.Empty(afterRestart.Processes);
        Assert.True(second.Success, second.Error?.Message);
        Assert.NotEqual(ExtractPid(first.Output), ExtractPid(second.Output));
        Assert.Equal(2, afterSecond.StartCount);
        Assert.Equal(before.PoolKey, afterSecond.PoolKey);
    }

    [Fact]
    public async Task Runtime_pauses_and_resumes_grpc_restart_policy()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-policy", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-first", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var process = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", process.PoolKey, paused: true, "maintenance", CancellationToken.None);
        var pausedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);
        var crash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);
        await Task.Delay(500);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-blocked", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var blockedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var resumed = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", process.PoolKey, paused: false, "", CancellationToken.None);
        var recovered = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-recovered", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var recoveredDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.Equal("paused", pausedDiagnostics.RestartPolicy);
        Assert.Contains("maintenance", pausedDiagnostics.PolicyReason);
        Assert.True(crash.Success, crash.Error?.Message);
        Assert.False(blocked.Success);
        Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.Error!.Code);
        Assert.Contains("restart policy is paused", blocked.Error.Message);
        Assert.Equal("paused", blockedDiagnostics.State);
        Assert.Equal(0, blockedDiagnostics.ProcessId);
        Assert.Equal("paused", blockedDiagnostics.RestartPolicy);
        Assert.Contains("sample.grpc", blockedDiagnostics.ModuleIds);
        Assert.True(resumed.Success, resumed.Message);
        Assert.Equal("automatic", resumed.RestartPolicy);
        Assert.True(recovered.Success, recovered.Error?.Message);
        Assert.NotEqual(ExtractPid(first.Output), ExtractPid(recovered.Output));
        Assert.Equal("automatic", recoveredDiagnostics.RestartPolicy);
    }

    [Fact]
    public async Task Runtime_persists_grpc_restart_policy_across_runtime_reload()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.persisted.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-persisted-policy", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-persisted-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using (var host = new GrpcIpcModuleRuntime())
        {
            var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot),
                [host]);

            runtime.Load(packageRoot);
            var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: true, "maintenance window", CancellationToken.None, "test");
            var diagnostics = runtime.GetRuntimeDiagnostics();

            Assert.True(dynamicCount > 0);
            Assert.True(paused.Success, paused.Message);
            var process = Assert.Single(diagnostics.Processes);
            Assert.Equal("paused", process.RestartPolicy);
            Assert.Contains("maintenance window", process.PolicyReason);
            Assert.Contains(diagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "paused" && entry.Source == "test");

            await runtime.DisposeAsync();
        }

        await using (var host = new GrpcIpcModuleRuntime())
        {
            var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot),
                [host]);

            runtime.Load(packageRoot);
            var restoredDiagnostics = runtime.GetRuntimeDiagnostics();
            var restoredProcess = Assert.Single(restoredDiagnostics.Processes);
            var blockedDynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var resumed = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: false, "maintenance complete", CancellationToken.None, "test");
            var recoveredDynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var recovered = await runtime.ExecuteCommandAsync(
                new CommandRequest("runtime-grpc-persisted-policy-recovered", "sample.grpc.ping", new JsonObject()),
                CancellationToken.None);
            var finalDiagnostics = runtime.GetRuntimeDiagnostics();

            Assert.Equal("paused", restoredProcess.State);
            Assert.Equal(0, restoredProcess.ProcessId);
            Assert.Equal("paused", restoredProcess.RestartPolicy);
            Assert.Contains("maintenance window", restoredProcess.PolicyReason);
            Assert.Contains("sample.grpc", restoredProcess.ModuleIds);
            Assert.Equal(0, blockedDynamicCount);
            Assert.True(resumed.Success, resumed.Message);
            Assert.Equal("automatic", resumed.RestartPolicy);
            Assert.True(recoveredDynamicCount > 0);
            Assert.True(recovered.Success, recovered.Error?.Message);
            Assert.Contains(finalDiagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "automatic" && entry.Source == "test");
            Assert.Contains(finalDiagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "paused" && entry.Source == "test");

            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_expires_grpc_restart_policy_and_recovers_pool()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.expiring.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-expiring-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-expiring-policy", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(300);
        var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: true, "short window", CancellationToken.None, "test", expiresAt);
        var pausedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);
        await Task.Delay(900);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var recovered = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-expiring-policy-recovered", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes);

        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.NotNull(paused.ExpiresAt);
        Assert.Equal("paused", pausedDiagnostics.RestartPolicy);
        Assert.NotNull(pausedDiagnostics.PolicyExpiresAt);
        Assert.True(dynamicCount > 0);
        Assert.True(recovered.Success, recovered.Error?.Message);
        Assert.Equal("automatic", process.RestartPolicy);
        Assert.Contains(diagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "automatic" && entry.Source == "runtime.expiry");
    }

    [Fact]
    public async Task Grpc_ipc_runtime_enforces_restart_limit_after_crash_loop()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.limit." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-crash-limit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-crash-limit", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            var crash = await runtime.ExecuteCommandAsync(
                new CommandRequest($"runtime-grpc-limit-crash-{i}", "sample.grpc.crash", new JsonObject()),
                CancellationToken.None);
            await Task.Delay(500);
            var recovered = await runtime.ExecuteCommandAsync(
                new CommandRequest($"runtime-grpc-limit-recover-{i}", "sample.grpc.ping", new JsonObject()),
                CancellationToken.None);

            Assert.True(crash.Success, crash.Error?.Message);
            Assert.True(recovered.Success, recovered.Error?.Message);
        }

        var finalCrash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-limit-final-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);
        await Task.Delay(500);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-limit-blocked", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(finalCrash.Success, finalCrash.Error?.Message);
        Assert.False(blocked.Success);
        Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.Error!.Code);
        Assert.Contains("restart limit reached", blocked.Error.Message);
    }

    [Fact]
    public async Task Runtime_pools_package_runtime_sidecar_across_modules()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.shared." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-shared", Guid.NewGuid().ToString("N"));
        WriteSharedGrpcRuntimePackage(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-shared", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-shared-one", "sample.shared.one.ping", new JsonObject()),
            CancellationToken.None);
        var second = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-shared-two", "sample.shared.two.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(dynamicCount >= 4);
        Assert.Contains(runtime.ListCommands("sample.shared.one"), command => command.Id == "sample.shared.one.ping");
        Assert.Contains(runtime.ListCommands("sample.shared.two"), command => command.Id == "sample.shared.two.ping");
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(ExtractPid(first.Output), ExtractPid(second.Output));

        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));
        Assert.Equal("package:sample-shared-runtime:runtime:shared-grpc", process.PoolKey);
        Assert.Equal("running", process.State);
        Assert.Equal(int.Parse(ExtractPid(first.Output)), process.ProcessId);
        Assert.Contains(pipeName, process.Endpoint);
        Assert.True(process.StartCount > 0);
        Assert.Equal(4, process.RestartLimit);
        Assert.NotNull(process.LastStartedAt);
        Assert.Contains("sample.shared.one", process.ModuleIds);
        Assert.Contains("sample.shared.two", process.ModuleIds);
    }

    [Fact]
    public async Task AdbForwarder_inproc_module_collects_diagnostics()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-adb-forwarder", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-diagnostics", "adb-forwarder.diagnostics.summary", new JsonObject()),
            CancellationToken.None);
        var devices = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-devices", "adb-forwarder.devices.scan", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ADB"), command => command.Id == "adb-forwarder.diagnostics.summary");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "adb-forwarder" && card.State != "unsupported");
        Assert.True(summary.Success);
        Assert.Contains("\"moduleId\":\"adb-forwarder\"", summary.Output);
        Assert.True(devices.Success);
        Assert.Contains("\"tool\":\"adb\"", devices.Output);
    }

    [Fact]
    public async Task AndroidTools_powertoold_imports_powertool_commands_and_executes_text_tool()
    {
        await using var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-android-tools", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var catalog = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-catalog", "android-tools.remote-commands.catalog.summary", new JsonObject()),
            CancellationToken.None);
        var transform = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-remove-comments", "android-tools.remote-commands.run.remove_comments", new JsonObject
            {
                ["input"] = "int value = 1; // remove me\nconst char* text = \"// keep me\";\n"
            }),
            CancellationToken.None);
        var output = JsonNode.Parse(transform.Output)!.AsObject()["output"]!.GetValue<string>();
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("Remove C++"), command => command.Id == "android-tools.remote-commands.run.remove_comments");
        Assert.True(catalog.Success);
        Assert.Contains("11 command(s)", catalog.Output);
        Assert.True(transform.Success);
        Assert.DoesNotContain("remove me", output);
        Assert.Contains("// keep me", output);
        Assert.Equal("package:android-tools-suite:runtime:powertoold", process.PoolKey);
        Assert.Contains("android-tools.notifications", process.ModuleIds);
        Assert.Contains("android-tools.process-monitor", process.ModuleIds);
        Assert.Contains("android-tools.remote-commands", process.ModuleIds);
    }

    [Fact]
    public async Task AndroidTools_powertoold_process_monitor_persists_shared_watch_list()
    {
        await using var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-android-process", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var save = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-process-save", "android-tools.process-monitor.watch.save", new JsonObject
            {
                ["processes"] = new JsonArray("dotnet", "pwsh")
            }),
            CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-process-summary", "android-tools.process-monitor.status.summary", new JsonObject()),
            CancellationToken.None);
        var payload = JsonNode.Parse(summary.Output)!.AsObject();
        var configured = payload["configured"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

        Assert.True(save.Success);
        Assert.True(summary.Success);
        Assert.Contains("dotnet", configured);
        Assert.Contains("pwsh", configured);
        Assert.Contains(runtime.GetRuntimeDiagnostics().Processes, process =>
            process.PoolKey == "package:android-tools-suite:runtime:powertoold" &&
            process.ModuleIds.Contains("android-tools.process-monitor"));
    }

    [Fact]
    public async Task AndroidTools_notifications_reports_actionable_degraded_state_when_endpoint_config_is_invalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-android-notifications-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "simple_http_notification_conf.py");
        await File.WriteAllTextAsync(configPath, """
cloud_server_ip: str = ""
cloud_server_port: int = 0
cloud_server_protocol: str = "https"
""");

        var previous = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_NOTIFICATION_CONF");
        Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_NOTIFICATION_CONF", configPath);
        try
        {
            var module = new AndroidToolsNotificationsModule();
            await module.InitializeAsync(
                CreateModuleContext("android-tools-suite", "android-tools.notifications", "android-notifications-invalid", ["notifications.remote"]),
                CancellationToken.None);

            var status = await module.GetStatusAsync(CancellationToken.None);
            var serverCheck = await module.ExecuteCommandAsync(
                new CommandRequest("android-notifications-server-check", "android-tools.notifications.server.check", new JsonObject()),
                CancellationToken.None);
            var inbox = await module.ExecuteCommandAsync(
                new CommandRequest("android-notifications-inbox", "android-tools.notifications.inbox.summary", new JsonObject()),
                CancellationToken.None);

            var serverPayload = JsonNode.Parse(serverCheck.Output)!.AsObject();
            var inboxPayload = JsonNode.Parse(inbox.Output)!.AsObject();
            var endpoint = inboxPayload["endpoint"]!.AsObject();

            Assert.Equal("degraded", status.State);
            Assert.Contains(status.Checks, check => check.Id == "notification.config" && !check.Ok && check.Message.Contains("host or port", StringComparison.OrdinalIgnoreCase));
            Assert.True(serverCheck.Success);
            Assert.False(serverPayload["found"]!.GetValue<bool>());
            Assert.Contains("host or port", serverPayload["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.True(inbox.Success);
            Assert.False(endpoint["found"]!.GetValue<bool>());
            Assert.Contains("legacyHistory", inboxPayload.Select(item => item.Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_NOTIFICATION_CONF", previous);
        }
    }

    [Fact]
    public async Task AndroidTools_process_monitor_reports_actionable_degraded_state_when_watch_list_is_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-android-process-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var processesPath = Path.Combine(root, "processes.json");
        await File.WriteAllTextAsync(processesPath, "[]");

        var previous = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES");
        Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES", processesPath);
        try
        {
            var module = new AndroidToolsProcessMonitorModule();
            await module.InitializeAsync(
                CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "android-process-empty", ["process.monitor"]),
                CancellationToken.None);

            var status = await module.GetStatusAsync(CancellationToken.None);
            var watchList = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-watch-list", "android-tools.process-monitor.watch.list", new JsonObject()),
                CancellationToken.None);
            var summary = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-summary", "android-tools.process-monitor.status.summary", new JsonObject()),
                CancellationToken.None);
            var invalidSave = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-empty-save", "android-tools.process-monitor.watch.save", new JsonObject { ["processes"] = new JsonArray() }),
                CancellationToken.None);

            var watchPayload = JsonNode.Parse(watchList.Output)!.AsObject();
            var summaryPayload = JsonNode.Parse(summary.Output)!.AsObject();

            Assert.Equal("degraded", status.State);
            Assert.Contains(status.Checks, check => check.Id == "process.config" && !check.Ok && check.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
            Assert.True(watchList.Success);
            Assert.Equal("env:MPT_ANDROIDTOOLS_PROCESSES", watchPayload["source"]!.GetValue<string>());
            Assert.Empty(watchPayload["processes"]!.AsArray());
            Assert.True(summary.Success);
            Assert.Empty(summaryPayload["configured"]!.AsArray());
            Assert.Empty(summaryPayload["states"]!.AsArray());
            Assert.False(invalidSave.Success);
            Assert.Equal(MptErrorCodes.ValidationFailed, invalidSave.Error!.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES", previous);
        }
    }

    [Fact]
    public async Task ScreenEase_inproc_module_manages_profiles_and_display_state()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-screenease", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var status = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-status", "screenease.status.summary", new JsonObject()),
            CancellationToken.None);
        var apply = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-apply", "screenease.profile.apply", new JsonObject { ["profileId"] = "night" }),
            CancellationToken.None);
        var list = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-list", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        var applied = JsonNode.Parse(apply.Output)!.AsObject();
        var profiles = JsonNode.Parse(list.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ScreenEase status"), command => command.Id == "screenease.status.summary");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "screenease" && card.State != "unsupported");
        Assert.True(status.Success);
        Assert.Contains("\"displayCount\"", status.Output);
        Assert.True(apply.Success);
        Assert.Equal("night", applied["activeProfileId"]!.GetValue<string>());
        Assert.Equal("native-host-required", applied["nativeHost"]!.AsObject()["state"]!.GetValue<string>());
        Assert.True(list.Success);
        Assert.Equal("night", profiles["activeProfileId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScreenEase_profile_apply_keeps_hardware_write_disabled_by_default()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-default-write"), CancellationToken.None);

        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-default", "screenease.profile.apply", new JsonObject { ["profileId"] = "night" }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var nativeHost = payload["nativeHost"]!.AsObject();

        Assert.True(result.Success);
        Assert.Empty(display.AppliedIntents);
        Assert.Equal("night", payload["activeProfileId"]!.GetValue<string>());
        Assert.Equal("native-host-required", nativeHost["state"]!.GetValue<string>());
        Assert.False(nativeHost["hardwareWriteRequested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScreenEase_profile_apply_calls_display_writer_when_requested()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-explicit-write"), CancellationToken.None);

        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-native", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "night",
                ["displayId"] = @"\\.\DISPLAY1",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var nativeHost = payload["nativeHost"]!.AsObject();
        var intent = Assert.Single(display.AppliedIntents);

        Assert.True(result.Success);
        Assert.Equal("night", intent.ProfileId);
        Assert.Equal(@"\\.\DISPLAY1", intent.DisplayId);
        Assert.Equal(45, intent.Brightness);
        Assert.Equal(4200, intent.ColorTemperature);
        Assert.Equal("success", nativeHost["state"]!.GetValue<string>());
        Assert.True(nativeHost["success"]!.GetValue<bool>());
        Assert.True(nativeHost["hardwareWriteRequested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScreenEase_native_writer_configure_enables_future_hardware_apply()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-configured-write"), CancellationToken.None);

        var configure = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-writer-configure", "screenease.native-writer.configure", new JsonObject { ["enabled"] = true }),
            CancellationToken.None);
        var apply = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-configured", "screenease.profile.apply", new JsonObject { ["profileId"] = "focus" }),
            CancellationToken.None);
        var configured = JsonNode.Parse(configure.Output)!.AsObject();
        var applied = JsonNode.Parse(apply.Output)!.AsObject();

        Assert.True(configure.Success);
        Assert.True(configured["enabled"]!.GetValue<bool>());
        Assert.True(apply.Success);
        Assert.Single(display.AppliedIntents);
        Assert.Equal("success", applied["nativeHost"]!.AsObject()["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task DoubaoAgent_inproc_module_reports_planner_tool_and_mcp_services()
    {
        await using var planner = TestHttpFacadeServer.Start();
        await using var tool = TestHttpFacadeServer.Start();
        await using var mcp = TestHttpFacadeServer.Start();
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-doubao-agent", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-status", "doubao-agent.status.summary", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var plannerHealth = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-planner", "doubao-agent.planner.health", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var selfTest = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-self-test", "doubao-agent.self-test", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var logs = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-logs", "doubao-agent.logs.summary", new JsonObject()),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(summary.Output)!.AsObject();
        var services = statusPayload["services"]!.AsArray().Select(item => item!.AsObject()["id"]!.GetValue<string>()).ToArray();
        var selfTestPayload = JsonNode.Parse(selfTest.Output)!.AsObject();
        var logsPayload = JsonNode.Parse(logs.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.planner.health");
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.tool.health");
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.mcp.health");
        Assert.True(summary.Success);
        Assert.Equal("running", statusPayload["state"]!.GetValue<string>());
        Assert.Equal(3, statusPayload["runningServices"]!.GetValue<int>());
        Assert.Contains("planner", services);
        Assert.Contains("tool", services);
        Assert.Contains("mcp", services);
        Assert.True(plannerHealth.Success);
        Assert.Contains("HTTP 200", plannerHealth.Output);
        Assert.True(selfTest.Success);
        Assert.DoesNotContain("abc123", selfTest.Output);
        Assert.DoesNotContain("hunter2", selfTest.Output);
        Assert.Contains("token=****", selfTestPayload["redaction"]!.GetValue<string>());
        Assert.True(logs.Success);
        Assert.True(logsPayload["fileCount"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task DoubaoAgent_inproc_module_reports_role_specific_degraded_services()
    {
        await using var planner = TestHttpFacadeServer.Start();
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-doubao-agent-partial", Guid.NewGuid().ToString("N"))),
            [host]);
        var unavailable = "http://127.0.0.1:1";

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var args = DoubaoArgs(planner.BaseUrl, unavailable, unavailable);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-partial-status", "doubao-agent.status.summary", args),
            CancellationToken.None);
        var toolHealth = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-tool-degraded", "doubao-agent.tool.health", args),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(summary.Output)!.AsObject();
        var services = statusPayload["services"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        var toolDetails = toolHealth.Error!.Details!;

        Assert.True(summary.Success);
        Assert.Equal("degraded", statusPayload["state"]!.GetValue<string>());
        Assert.Equal(1, statusPayload["runningServices"]!.GetValue<int>());
        Assert.Equal(3, statusPayload["totalServices"]!.GetValue<int>());
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "planner" && service["ok"]!.GetValue<bool>());
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "tool" && !service["ok"]!.GetValue<bool>());
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "mcp" && !service["ok"]!.GetValue<bool>());
        Assert.False(toolHealth.Success);
        Assert.Equal("failed", toolHealth.State);
        Assert.Equal(MptErrorCodes.RuntimeUnavailable, toolHealth.Error.Code);
        Assert.True(toolHealth.Error.Retryable);
        Assert.Equal("tool", toolDetails["id"]!.GetValue<string>());
        Assert.False(toolDetails["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(toolDetails["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SmartBird_inproc_module_reports_facade_config_and_hardware_degradation()
    {
        await using var server = TestHttpFacadeServer.Start();
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-smartbird", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var status = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-status", "smartbird-thermostat.status.summary", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var events = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-events", "smartbird-thermostat.events.list", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var configSave = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-config-save", "smartbird-thermostat.config.save", new JsonObject
            {
                ["targetTemperatureC"] = 52,
                ["pollIntervalSeconds"] = 45
            }),
            CancellationToken.None);
        var config = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-config-get", "smartbird-thermostat.config.get", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var diagnostics = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-hardware", "smartbird-thermostat.hardware.diagnostics", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var restart = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-restart", "smartbird-thermostat.service.restart", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var selfTest = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-self-test", "smartbird-thermostat.self-test", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);
        var logs = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-logs", "smartbird-thermostat.logs.summary", SmartBirdArgs(server.BaseUrl)),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(status.Output)!.AsObject();
        var statusChecks = statusPayload["checks"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        var eventPayload = JsonNode.Parse(events.Output)!.AsObject();
        var configPayload = JsonNode.Parse(config.Output)!.AsObject();
        var diagnosticsPayload = JsonNode.Parse(diagnostics.Output)!.AsObject();
        var restartDetails = restart.Error!.Details!;
        var logsPayload = JsonNode.Parse(logs.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("SmartBird"), command => command.Id == "smartbird-thermostat.events.list");
        Assert.Contains(runtime.ListCommands("SmartBird"), command => command.Id == "smartbird-thermostat.hardware.diagnostics");
        Assert.True(status.Success);
        Assert.Equal("degraded", statusPayload["state"]!.GetValue<string>());
        Assert.Contains(statusChecks, check => check["id"]!.GetValue<string>() == "smartbird.status" && check["ok"]!.GetValue<bool>());
        Assert.Contains(statusChecks, check => check["id"]!.GetValue<string>() == "energy-server.status" && check["ok"]!.GetValue<bool>());
        Assert.Contains(statusChecks, check => check["id"]!.GetValue<string>() == "fnb58.power-meter" && !check["ok"]!.GetValue<bool>());
        Assert.True(events.Success);
        Assert.Single(eventPayload["events"]!.AsArray());
        Assert.True(configSave.Success);
        Assert.True(config.Success);
        Assert.Equal(52, configPayload["localConfig"]!.AsObject()["targetTemperatureC"]!.GetValue<double>());
        Assert.True(diagnostics.Success);
        Assert.Equal("degraded", diagnosticsPayload["state"]!.GetValue<string>());
        Assert.False(restart.Success);
        Assert.Equal("permission-required", restart.State);
        Assert.Equal("ServiceBroker", restartDetails["broker"]!.GetValue<string>());
        Assert.True(selfTest.Success);
        Assert.DoesNotContain("abc123", selfTest.Output);
        Assert.Contains("token=****", selfTest.Output);
        Assert.True(logs.Success);
        Assert.True(logsPayload["fileCount"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void AdbForwarder_parses_netsh_portproxy_rules()
    {
        var rules = PortProxyParser.Parse(PortProxySample);

        var rule = Assert.Single(rules);
        Assert.Equal("0.0.0.0", rule.ListenAddress);
        Assert.Equal(5555, rule.ListenPort);
        Assert.Equal("127.0.0.1", rule.ConnectAddress);
        Assert.Equal(7555, rule.ConnectPort);
    }

    [Fact]
    public void AdbForwarder_plans_apply_with_rollback()
    {
        var current = PortProxyParser.Parse(PortProxySample);
        var (mappings, messages) = AdbPortProxyModel.ParseMappings(CreateAdbPortProxyArgs(PortProxySample));

        var plan = AdbPortProxyPlanner.CreateApplyPlan(mappings, current);

        Assert.Empty(messages);
        Assert.Single(plan.ToApply);
        Assert.Single(plan.ToRemove);
        var rollback = Assert.Single(plan.Rollback);
        Assert.Equal("apply", rollback.Operation);
        Assert.Equal(7555, rollback.Rule.ConnectPort);
    }

    [Fact]
    public async Task AdbForwarder_apply_request_returns_broker_plan()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-adb-forwarder-apply", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-apply-request", "adb-forwarder.portproxy.apply", CreateAdbPortProxyArgs(PortProxySample)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("permission-required", result.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", result.Error!.Code);
        Assert.Equal("network.portproxy.apply", result.Error.Details!["actionId"]!.GetValue<string>());
        Assert.Equal("NetworkBroker", result.Error.Details!["broker"]!.GetValue<string>());
        Assert.Single(result.Error.Details!["expectedChange"]!.AsObject()["apply"]!.AsArray());
        Assert.Single(result.Error.Details!["expectedChange"]!.AsObject()["remove"]!.AsArray());
        Assert.Single(result.Error.Details!["rollback"]!.AsArray());
    }

    [Fact]
    public async Task HostControl_execute_command_exposes_permission_details()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var request = new MyPowerTools.Protocol.HostControl.V1.ExecuteCommandRequest
        {
            InvocationId = "hostcontrol-permission",
            CommandId = "adb-forwarder.portproxy.apply",
            Args = JsonStructMapper.ToStruct(CreateAdbPortProxyArgs(PortProxySample))
        };

        var response = await service.ExecuteCommand(request, new TestServerCallContext());

        Assert.Equal("permission-required", response.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", response.ErrorCode);
        Assert.Equal("network.portproxy.apply", response.ErrorDetails.Fields["actionId"].StringValue);
        Assert.Single(response.ErrorDetails.Fields["rollback"].ListValue.Values);
    }

    [Fact]
    public async Task HostControl_module_detail_exposes_declared_permissions()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime);

        var modules = await service.ListModules(new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true }, new TestServerCallContext());
        var summary = modules.Modules.Single(module => module.ModuleId == "adb-forwarder");
        var detail = await service.GetModuleDetail(new MyPowerTools.Protocol.HostControl.V1.GetModuleDetailRequest { ModuleId = "adb-forwarder" }, new TestServerCallContext());

        Assert.Contains(summary.Permissions, permission => permission.Id == "apply-portproxy" && permission.Level == "broker");
        Assert.Contains(summary.Requirements, requirement => requirement.Capability == "network.portForwarding" && requirement.Required);
        Assert.Contains(detail.Permissions, permission => permission.Capability == "network.portForwarding" && permission.Reason.Contains("端口转发", StringComparison.Ordinal));
        Assert.Contains(detail.Requirements, requirement => requirement.Capability == "adb.devices" && requirement.Required);
    }

    [Fact]
    public async Task HostControl_lists_broker_audit_entries()
    {
        var audit = new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-audit", Guid.NewGuid().ToString("N"), "audit.jsonl"));
        audit.Append(new BrokerAuditEntry(
            "audit-1",
            DateTimeOffset.UtcNow,
            "adb-forwarder",
            "network.portproxy.apply",
            "elevated",
            "0.0.0.0:5555",
            "test audit",
            true,
            "evaluated",
            "remove 0.0.0.0:5555"));
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, audit);

        var response = await service.ListBrokerAudit(
            new MyPowerTools.Protocol.HostControl.V1.ListBrokerAuditRequest { ModuleId = "adb-forwarder", Limit = 10 },
            new TestServerCallContext());

        var entry = Assert.Single(response.Entries);
        Assert.Equal("audit-1", entry.AuditId);
        Assert.Equal("network.portproxy.apply", entry.ActionId);
        Assert.Equal("evaluated", entry.Result);
    }

    [Fact]
    public async Task HostControl_lists_notification_center_entries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var notification = runtime.ExecuteCommand(new CommandRequest("hostcontrol-notification", "doubao-agent.notification.test", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-notifications-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListNotifications(
            new MyPowerTools.Protocol.HostControl.V1.ListNotificationsRequest { Limit = 10 },
            new TestServerCallContext());

        Assert.True(notification.Success);
        var entry = Assert.Single(response.Notifications);
        Assert.Equal("doubao-agent", entry.ModuleId);
        Assert.Equal("Send Doubao test notification", entry.Title);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public async Task HostControl_lists_package_summaries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-packages-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal(5, response.Packages.Count);
        var androidTools = response.Packages.Single(package => package.PackageId == "android-tools-suite");
        Assert.Equal(3u, androidTools.ModuleCount);
        Assert.Contains("android-tools.remote-commands", androidTools.ModuleIds);
        Assert.Equal("shared/package.hashes.json", androidTools.Hashes);
        Assert.Equal("signature-hook", androidTools.TrustState);
        Assert.Equal("local", androidTools.TrustPolicy);
        Assert.Equal("shared/package.signature.json", androidTools.SignaturePath);
        Assert.Equal(0u, androidTools.TrustIssueCount);
    }

    [Fact]
    public async Task HostControl_get_settings_schema_exposes_runtime_schema()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            transportRuntimes: [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-settings-schema-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var schema = await service.GetSettingsSchema(
            new HostProto.GetSettingsSchemaRequest { ModuleId = "doubao-agent" },
            new TestServerCallContext());

        Assert.Equal("doubao-agent", schema.ModuleId);
        Assert.Contains("plannerBaseUrl", schema.SchemaJson, StringComparison.Ordinal);
        Assert.Contains("redactSensitiveOutput", schema.SchemaJson, StringComparison.Ordinal);
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

    [Fact]
    public async Task HostControl_package_operations_reload_runtime_store()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store-data", Guid.NewGuid().ToString("N"))));
        runtime.Load(storeRoot);
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")),
            packageStore: store);

        var install = await service.InstallPackage(
            new MyPowerTools.Protocol.HostControl.V1.InstallPackageRequest
            {
                SourceDirectory = Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet")
            },
            new TestServerCallContext());
        var repair = await service.RepairPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var uninstall = await service.UninstallPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var afterUninstall = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());
        var rollback = await service.RollbackPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var afterRollback = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.True(install.Success, install.Message);
        Assert.Equal("install", install.Operation);
        Assert.Equal("sample-dotnet", install.PackageId);
        Assert.Equal(1u, install.PackageCount);
        Assert.Equal(1u, install.ModuleCount);
        Assert.True(repair.Success, repair.Message);
        Assert.Equal("repair", repair.Operation);
        Assert.Empty(repair.Issues);
        Assert.True(uninstall.Success, uninstall.Message);
        Assert.Empty(afterUninstall.Packages);
        Assert.True(rollback.Success, rollback.Message);
        var package = Assert.Single(afterRollback.Packages);
        Assert.Equal("sample-dotnet", package.PackageId);
        Assert.Equal("signature-hook", package.TrustState);
    }

    [Fact]
    public async Task HostControl_set_module_enabled_updates_runtime_state()
    {
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state", Guid.NewGuid().ToString("N"))));
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var detail = await service.SetModuleEnabled(
            new MyPowerTools.Protocol.HostControl.V1.SetModuleEnabledRequest { ModuleId = "doubao-agent", Enabled = false },
            new TestServerCallContext());
        var visibleOnly = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest(),
            new TestServerCallContext());
        var includeDisabled = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal("disabled", detail.State);
        Assert.DoesNotContain(visibleOnly.Modules, module => module.ModuleId == "doubao-agent");
        var disabledModule = includeDisabled.Modules.Single(module => module.ModuleId == "doubao-agent");
        Assert.False(disabledModule.Enabled);
        Assert.Equal("disabled", disabledModule.State);
    }

    [Fact]
    public async Task HostControl_returns_runtime_diagnostics_snapshot()
    {
        await using var host = new InProcDotNetModuleHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics", Guid.NewGuid().ToString("N"));
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        runtime.ExecuteCommand(new CommandRequest("diagnostics-history", "doubao-agent.open", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        Assert.Equal("0.2.0", diagnostics.RunnerVersion);
        Assert.Equal("1.0", diagnostics.HostControlProtocolVersion);
        Assert.Equal(5u, diagnostics.Counts.PackageCount);
        Assert.Equal(7u, diagnostics.Counts.ModuleCount);
        Assert.Equal((uint)runtime.CurrentEventSeq, diagnostics.CurrentEventSeq);
        Assert.Equal(dataRoot, diagnostics.Paths.Root);
        Assert.Contains(diagnostics.Transports, transport => transport.Kind == "inproc-dotnet" && transport.RuntimeRegistered);
        Assert.Contains(diagnostics.Modules, module => module.ModuleId == "doubao-agent" && module.Enabled);
        Assert.Contains(diagnostics.Modules, module => module.ModuleId == "doubao-agent" && module.SupervisorState == "healthy" && module.ObservationCount > 0);
        Assert.Contains(diagnostics.RecentCommands, command => command.InvocationId == "diagnostics-history");
    }

    [Fact]
    public async Task HostControl_returns_grpc_process_diagnostics_snapshot()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));
        Assert.True(dynamicCount > 0);
        Assert.Equal("module:sample.grpc", process.PoolKey);
        Assert.Equal("running", process.State);
        Assert.True(process.ProcessId > 0);
        Assert.Contains(pipeName, process.Endpoint);
        Assert.Equal(4u, process.RestartLimit);
        Assert.True(process.StartCount > 0);
        Assert.NotNull(process.LastStartedAt);
        Assert.Contains("sample.grpc", process.ModuleIds);
    }

    [Fact]
    public async Task HostControl_restarts_grpc_process_pool()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var process = Assert.Single(diagnostics.Processes);

        var restart = await service.RestartRuntimeProcess(
            new MyPowerTools.Protocol.HostControl.V1.RestartRuntimeProcessRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey
            },
            new TestServerCallContext());
        var afterRestart = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        Assert.True(restart.Success, restart.Message);
        Assert.Equal("grpc-ipc", restart.TransportKind);
        Assert.Equal("module:sample.grpc", restart.PoolKey);
        Assert.Equal("restarting", restart.State);
        Assert.Contains("sample.grpc", restart.ModuleIds);
        Assert.Empty(afterRestart.Processes);
    }

    [Fact]
    public async Task HostControl_sets_grpc_process_restart_policy()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var process = Assert.Single(diagnostics.Processes);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var paused = await service.SetRuntimeProcessRestartPolicy(
            new MyPowerTools.Protocol.HostControl.V1.SetRuntimeProcessRestartPolicyRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey,
                Paused = true,
                Reason = "operator pause",
                Source = "hostcontrol-test",
                ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiresAt)
            },
            new TestServerCallContext());
        var pausedDiagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var resumed = await service.SetRuntimeProcessRestartPolicy(
            new MyPowerTools.Protocol.HostControl.V1.SetRuntimeProcessRestartPolicyRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey,
                Paused = false
            },
            new TestServerCallContext());

        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.NotNull(paused.ExpiresAt);
        var pausedProcess = Assert.Single(pausedDiagnostics.Processes);
        Assert.Equal("paused", pausedProcess.RestartPolicy);
        Assert.Contains("operator pause", pausedProcess.PolicyReason);
        Assert.NotNull(pausedProcess.PolicyExpiresAt);
        var history = Assert.Single(pausedDiagnostics.ProcessPolicyHistory);
        Assert.Equal("paused", history.RestartPolicy);
        Assert.Equal("hostcontrol-test", history.Source);
        Assert.Contains("operator pause", history.Reason);
        Assert.NotNull(history.ExpiresAt);
        Assert.True(resumed.Success, resumed.Message);
        Assert.Equal("automatic", resumed.RestartPolicy);
    }

    [Fact]
    public async Task Cli_restarts_runner_grpc_process_pool_over_hostcontrol()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.cli.runner.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart-data", Guid.NewGuid().ToString("N"));
        var runner = StartDotnetProcess(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Runner", "MyPowerTools.Runner.csproj"),
            "--",
            "--modules",
            packageRoot,
            "--data-root",
            dataRoot,
            "--no-tray");

        try
        {
            using var client = await WaitForDefaultHostControlAsync();
            var diagnostics = await client.GetRuntimeDiagnosticsAsync();
            var process = Assert.Single(diagnostics.Processes);

            var restart = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "restart",
                process.TransportKind,
                process.PoolKey);
            var afterRestart = await client.GetRuntimeDiagnosticsAsync();

            Assert.Equal(0, restart.ExitCode);
            Assert.Contains("grpc-ipc module:sample.grpc: restarting", restart.Output);
            Assert.Empty(afterRestart.Processes);

            var repopulated = await client.ExecuteCommandAsync("sample.grpc.ping");
            var repopulatedProcess = Assert.Single((await client.GetRuntimeDiagnosticsAsync()).Processes);
            var pause = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "pause",
                ".",
                "--reason",
                "cli maintenance",
                "--duration-minutes",
                "30");
            var policyDiagnostics = await client.GetRuntimeDiagnosticsAsync();
            var crash = await client.ExecuteCommandAsync("sample.grpc.crash");
            await Task.Delay(500);
            var blocked = await client.ExecuteCommandAsync("sample.grpc.ping");
            var pausedProcess = Assert.Single((await client.GetRuntimeDiagnosticsAsync()).Processes);
            var resume = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "resume",
                repopulatedProcess.TransportKind,
                repopulatedProcess.PoolKey);
            var recovered = await client.ExecuteCommandAsync("sample.grpc.ping");

            Assert.Equal("succeeded", repopulated.State);
            Assert.Equal(0, pause.ExitCode);
            Assert.Contains("paused", pause.Output);
            Assert.Contains("expires:", pause.Output);
            Assert.Contains(policyDiagnostics.ProcessPolicyHistory, entry => entry.Source == "cli" && entry.RestartPolicy == "paused");
            Assert.Equal("succeeded", crash.State);
            Assert.Equal("failed", blocked.State);
            Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.ErrorCode);
            Assert.Contains("restart policy is paused", blocked.ErrorMessage);
            Assert.Equal("paused", pausedProcess.State);
            Assert.Equal("paused", pausedProcess.RestartPolicy);
            Assert.Contains("sample.grpc", pausedProcess.ModuleIds);
            Assert.Equal(0, resume.ExitCode);
            Assert.Contains("Automatic restart is enabled", resume.Output);
            Assert.Equal("succeeded", recovered.State);

            await client.QuitRunnerAsync();
            Assert.True(runner.WaitForExit(30000), "Runner did not exit after HostControl QuitRunner.");
            Assert.Equal(0, runner.ExitCode);
        }
        finally
        {
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync();
            }

            runner.Dispose();
        }
    }

    [Fact]
    public async Task HostControl_quit_runner_requests_host_shutdown()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        var lifetime = new RecordingHostApplicationLifetime();
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-quit-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")),
            lifetime);

        await service.QuitRunner(
            new MyPowerTools.Protocol.HostControl.V1.QuitRunnerRequest(),
            new TestServerCallContext());

        Assert.True(lifetime.StopRequested);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
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

    private static JsonObject CreateAdbPortProxyArgs(string currentPortProxyText)
    {
        return new JsonObject
        {
            ["reason"] = "test broker apply",
            ["currentPortProxyText"] = currentPortProxyText,
            ["mappings"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "device-5555",
                    ["name"] = "Test device 5555",
                    ["enabled"] = true,
                    ["listenAddress"] = "0.0.0.0",
                    ["listenPort"] = 5555,
                    ["connectAddress"] = "127.0.0.1",
                    ["connectPort"] = 5555
                }
            }
        };
    }

    private static string FindSampleGrpcSidecarCommand()
    {
        var fileName = OperatingSystem.IsWindows()
            ? "MyPowerTools.SampleSidecar.Grpc.exe"
            : "MyPowerTools.SampleSidecar.Grpc";
        var command = Path.Combine(Root, "src", "MyPowerTools.SampleSidecar.Grpc", "bin", "Debug", "net10.0", fileName);
        Assert.True(File.Exists(command), $"Expected sample gRPC sidecar command at {command}");
        return command;
    }

    private static string ReserveUnusedLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static async Task<(int ExitCode, string Output)> RunPwshAsync(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start pwsh.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static Process StartDotnetProcess(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
    }

    private static async Task<HostControlClient> WaitForDefaultHostControlAsync()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            HostControlClient? client = null;
            try
            {
                client = HostControlClient.ForDefaultEndpoint();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.PingAsync(timeout.Token);
                return client;
            }
            catch (Exception ex)
            {
                lastError = ex;
                client?.Dispose();
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException($"Runner HostControl endpoint did not become available: {lastError?.Message}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void WriteGrpcSidecarModuleManifest(string packageRoot, string sidecarCommand, string pipeName)
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample.grpc",
            ["packageId"] = "sample-grpc-sidecar",
            ["displayName"] = "Sample gRPC Sidecar",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 90,
                    ["command"] = sidecarCommand,
                    ["args"] = new JsonArray(pipeName),
                    ["windows"] = new JsonObject
                    {
                        ["transport"] = "named-pipe",
                        ["name"] = pipeName
                    },
                    ["linux"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = socketPath
                    },
                    ["macos"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = socketPath
                    }
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "settings", "dashboardCard")
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteHttpFacadeModuleManifest(string packageRoot, string baseUrl)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample.http-runtime",
            ["packageId"] = "sample-http-runtime",
            ["displayName"] = "Sample HTTP Runtime",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "http",
                    ["priority"] = 80,
                    ["baseUrl"] = baseUrl,
                    ["health"] = new JsonObject
                    {
                        ["path"] = "/api/status"
                    }
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "dashboardCard"),
            ["staticIndexes"] = new JsonObject
            {
                ["commands"] = "commands.index.json"
            }
        };

        var commands = new JsonObject
        {
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "sample.http-runtime.ping",
                    ["title"] = "Ping HTTP Runtime",
                    ["subtitle"] = "Calls the local HTTP facade.",
                    ["kind"] = "action",
                    ["category"] = "Tests",
                    ["timeoutMs"] = 5000,
                    ["execution"] = new JsonObject
                    {
                        ["type"] = "http.request",
                        ["method"] = "POST",
                        ["path"] = "/api/ping",
                        ["body"] = new JsonObject
                        {
                            ["message"] = "hello"
                        }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(packageRoot, "commands.index.json"),
            commands.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSharedGrpcRuntimePackage(string packageRoot, string sidecarCommand, string pipeName)
    {
        var modulesRoot = Path.Combine(packageRoot, "modules");
        var oneRoot = Path.Combine(modulesRoot, "one");
        var twoRoot = Path.Combine(modulesRoot, "two");
        Directory.CreateDirectory(oneRoot);
        Directory.CreateDirectory(twoRoot);

        var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        var package = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample-shared-runtime",
            ["displayName"] = "Sample Shared Runtime",
            ["version"] = "0.2.0",
            ["modules"] = new JsonArray("modules/one/module.json", "modules/two/module.json"),
            ["shared"] = new JsonObject
            {
                ["runtimes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "shared-grpc",
                        ["entrypoints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["kind"] = "grpc-ipc",
                                ["priority"] = 90,
                                ["command"] = sidecarCommand,
                                ["args"] = new JsonArray(pipeName),
                                ["windows"] = new JsonObject
                                {
                                    ["transport"] = "named-pipe",
                                    ["name"] = pipeName
                                },
                                ["linux"] = new JsonObject
                                {
                                    ["transport"] = "unix-domain-socket",
                                    ["path"] = socketPath
                                },
                                ["macos"] = new JsonObject
                                {
                                    ["transport"] = "unix-domain-socket",
                                    ["path"] = socketPath
                                }
                            }
                        }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        WritePackageRuntimeModule(oneRoot, "sample.shared.one");
        WritePackageRuntimeModule(twoRoot, "sample.shared.two");
    }

    private static void WritePackageRuntimeModule(string moduleRoot, string moduleId)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = "sample-shared-runtime",
            ["displayName"] = moduleId,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "package-runtime",
                    ["priority"] = 90,
                    ["runtimeId"] = "shared-grpc"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };

        File.WriteAllText(
            Path.Combine(moduleRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ExtractPid(string output)
    {
        var marker = "pid=";
        var start = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"Expected pid marker in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        return end < 0 ? output[start..] : output[start..end];
    }

    private static JsonObject DoubaoArgs(string plannerBaseUrl, string toolBaseUrl, string mcpBaseUrl)
    {
        return new JsonObject
        {
            ["plannerBaseUrl"] = plannerBaseUrl,
            ["toolBaseUrl"] = toolBaseUrl,
            ["mcpBaseUrl"] = mcpBaseUrl,
            ["healthPath"] = "/health"
        };
    }

    private static JsonObject SmartBirdArgs(string baseUrl)
    {
        return new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["energyServerBaseUrl"] = baseUrl,
            ["adbPath"] = "adb-missing-for-smartbird-test",
            ["fnb58Port"] = ""
        };
    }

    private static ModuleContext CreateScreenEaseContext(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", name, Guid.NewGuid().ToString("N"));
        return new ModuleContext(
            "test-host",
            "1.0",
            "screenease",
            "screenease",
            Path.Combine(root, "data"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"),
            PlatformId.Current().Rid,
            ["display.profile"]);
    }

    private static ModuleContext CreateModuleContext(string packageId, string moduleId, string name, IReadOnlyList<string> grantedCapabilities)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", name, Guid.NewGuid().ToString("N"));
        return new ModuleContext(
            "test-host",
            "1.0",
            packageId,
            moduleId,
            Path.Combine(root, "state", "modules", moduleId, "data"),
            Path.Combine(root, "state", "modules", moduleId, "cache"),
            Path.Combine(root, "state", "modules", moduleId, "logs"),
            PlatformId.Current().Rid,
            grantedCapabilities);
    }

    private sealed class RecordingDisplayService : IDisplayService
    {
        public List<DisplayProfileIntent> AppliedIntents { get; } = [];

        public Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<DisplaySnapshot> displays =
            [
                new(@"\\.\DISPLAY1", "Test display", "connected", 1920, 1080, 60, "landscape", true, "test display")
            ];
            return Task.FromResult(displays);
        }

        public Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new DisplayWriterStatus(true, "ready", "test writer ready"));
        }

        public Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken)
        {
            AppliedIntents.Add(intent);
            return Task.FromResult(new BrokerOperationResult(true, "success", $"applied {intent.ProfileId}"));
        }
    }

    private sealed class TestHttpFacadeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private TestHttpFacadeServer(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _acceptLoop = AcceptLoopAsync();
        }

        public string BaseUrl { get; }

        public static TestHttpFacadeServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new TestHttpFacadeServer(listener);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token), _cts.Token);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var _ = client;
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var length = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            var request = Encoding.ASCII.GetString(buffer, 0, length);
            var firstLine = request.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
            var (status, body) = firstLine switch
            {
                var line when line.Contains(" /api/status ", StringComparison.Ordinal) => ("200 OK", "{\"state\":\"running\",\"service\":\"local-http-facade\"}"),
                var line when line.Contains(" /api/events ", StringComparison.Ordinal) => ("200 OK", "{\"events\":[{\"id\":\"evt-1\",\"level\":\"info\",\"message\":\"temperature stable\"}]}"),
                var line when line.Contains(" /api/logs ", StringComparison.Ordinal) => ("200 OK", "{\"records\":[{\"level\":\"info\",\"message\":\"smartbird log ready\"}]}"),
                var line when line.Contains(" /api/config ", StringComparison.Ordinal) => ("200 OK", "{\"targetTemperatureC\":45,\"policy\":\"balanced\"}"),
                var line when line.Contains(" /api/ping ", StringComparison.Ordinal) => ("200 OK", "pong token=abc123"),
                var line when line.Contains(" /health ", StringComparison.Ordinal) => ("200 OK", "{\"status\":\"ok\",\"service\":\"test-health\"}"),
                _ => ("404 Not Found", "missing")
            };
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headers.AsMemory(0, headers.Length), cancellationToken);
            await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), cancellationToken);
        }
    }

    private sealed class RecordingNetworkBroker : INetworkBroker
    {
        public List<string> Operations { get; } = [];
        public bool FailNextApply { get; init; }
        private bool _applyFailed;

        public Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PortProxyRule>>([]);
        }

        public Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            Operations.Add($"apply:{rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}");
            if (FailNextApply && !_applyFailed)
            {
                _applyFailed = true;
                return Task.FromResult(new BrokerOperationResult(false, "failed", "synthetic apply failure"));
            }

            return Task.FromResult(new BrokerOperationResult(true, "success", "applied"));
        }

        public Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            Operations.Add($"remove:{rule.ListenAddress}:{rule.ListenPort}");
            return Task.FromResult(new BrokerOperationResult(true, "success", "removed"));
        }
    }

    private sealed class RecordingAutostartService : IAutostartService
    {
        private readonly Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);

        public Task<ServiceStatus> GetAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(_commands.TryGetValue(id, out var command)
                ? new ServiceStatus(id, "enabled", command)
                : new ServiceStatus(id, "disabled", $"No autostart entry exists for '{id}'."));
        }

        public Task<BrokerOperationResult> EnableAsync(string id, string command, CancellationToken cancellationToken)
        {
            _commands[id] = command;
            return Task.FromResult(new BrokerOperationResult(true, "enabled", $"Enabled {id}."));
        }

        public Task<BrokerOperationResult> DisableAsync(string id, CancellationToken cancellationToken)
        {
            _commands.Remove(id);
            return Task.FromResult(new BrokerOperationResult(true, "disabled", $"Disabled {id}."));
        }
    }

    private sealed class RecordingServiceManager : IServiceManager
    {
        public List<string> Operations { get; } = [];

        public Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ServiceStatus(serviceName, "running", $"{serviceName} is running."));
        }

        public Task<BrokerOperationResult> StartAsync(string serviceName, CancellationToken cancellationToken)
        {
            Operations.Add($"start:{serviceName}");
            return Task.FromResult(new BrokerOperationResult(true, "started", $"Started {serviceName}."));
        }

        public Task<BrokerOperationResult> StopAsync(string serviceName, CancellationToken cancellationToken)
        {
            Operations.Add($"stop:{serviceName}");
            return Task.FromResult(new BrokerOperationResult(true, "stopped", $"Stopped {serviceName}."));
        }
    }

    private sealed class SequenceHostControlProbe : IHostControlConnectionProbe
    {
        private readonly Queue<object> _steps;

        public SequenceHostControlProbe(params object[] steps)
        {
            _steps = new Queue<object>(steps);
        }

        public Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
        {
            var step = _steps.Count == 0
                ? new HostControlConnectionProbeResult("0.2.0", "running")
                : _steps.Dequeue();

            if (step is Exception ex)
            {
                return Task.FromException<HostControlConnectionProbeResult>(ex);
            }

            return Task.FromResult((HostControlConnectionProbeResult)step);
        }
    }

    private sealed class SequenceHostEventSource : IHostControlEventSource
    {
        private readonly Queue<IReadOnlyList<object>> _subscriptions;

        public SequenceHostEventSource(params IReadOnlyList<object>[] subscriptions)
        {
            _subscriptions = new Queue<IReadOnlyList<object>>(subscriptions);
        }

        public List<ulong> RequestedSeqs { get; } = [];

        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
            ulong lastEventSeq,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedSeqs.Add(lastEventSeq);
            if (_subscriptions.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            foreach (var step in _subscriptions.Dequeue())
            {
                await Task.Yield();
                if (step is Exception ex)
                {
                    throw ex;
                }

                yield return (HostProto.HostEvent)step;
            }
        }
    }

    private static HostProto.HostEvent HostEvent(ulong seq, string sourceId, string type)
    {
        return new HostProto.HostEvent
        {
            Seq = seq,
            SourceId = sourceId,
            Type = type,
            Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];
        private Status _status = new(StatusCode.OK, "");
        private WriteOptions? _writeOptions;

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "local";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new("", []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopRequested = true;
            _stopping.Cancel();
        }
    }
}
