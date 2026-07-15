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
            },
            ["operationRules"] = new JsonObject
            {
                ["status"] = "inproc-or-sidecar",
                ["settings"] = "inproc-or-sidecar",
                ["commandProvider"] = "inproc-or-sidecar",
                ["longRunningCommand"] = "sidecar-required",
                ["systemMutation"] = "broker-required",
                ["nativeHardware"] = "sidecar-required",
                ["elevatedWrite"] = "broker-required",
                ["externalProcess"] = "sidecar-required"
            }
        });
        manifest["development"] = new JsonObject
        {
            ["allowAlreadyLoadedFallback"] = true
        };
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
        Assert.NotNull(module.RuntimePolicy.OperationRules);
        Assert.Equal("inproc-or-sidecar", module.RuntimePolicy.OperationRules!.Status);
        Assert.Equal("broker-required", module.RuntimePolicy.OperationRules.SystemMutation);
        Assert.Equal("sidecar-required", module.RuntimePolicy.OperationRules.ExternalProcess);
        Assert.NotNull(module.Development);
        Assert.True(module.Development!.AllowAlreadyLoadedFallback);
    }

    [Fact]
    public void Module_schema_accepts_hotkeys_and_runtime_lists_bindings()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hotkey-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "module-hotkey-sample",
            ["packageId"] = "module-hotkey-sample",
            ["displayName"] = "Module Hotkey Sample",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = assemblyName,
                    ["type"] = "MyPowerTools.SampleModules.DotNet.SampleDotNetModule"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands"),
            ["hotkeys"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "quick.toggle",
                    ["default"] = "Ctrl+Alt+Space",
                    ["commandId"] = "module-hotkey-sample.toggle",
                    ["scope"] = "module",
                    ["reason"] = "Toggle the sample module.",
                    ["enabledByDefault"] = true
                }
            }
        };
        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var report = new SchemaPackageValidator(Path.Combine(Root, "schemas")).ValidatePackageDirectory(packageRoot);
        var module = new PackageReader().ReadPackageDirectory(packageRoot).Modules.Single().Manifest;
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(packageRoot);
        var binding = Assert.Single(runtime.ListHotkeyBindings());

        Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
        Assert.Equal("quick.toggle", module.Hotkeys.Single().Id);
        Assert.True(module.Hotkeys.Single().EnabledByDefault);
        Assert.Equal("module-hotkey-sample.quick.toggle", binding.Id);
        Assert.Equal("module-hotkey-sample", binding.ModuleId);
        Assert.Equal("module-hotkey-sample.toggle", binding.CommandId);
        Assert.Equal("Ctrl+Alt+Space", binding.Gesture);

        var hotkeys = runtime.GetRuntimeDiagnostics().Hotkeys;
        Assert.Contains(hotkeys, hotkey =>
            hotkey.Id == "command-palette" &&
            hotkey.Gesture == "Ctrl+Alt+Space" &&
            hotkey.State == "conflict");
        Assert.Contains(hotkeys, hotkey =>
            hotkey.Id == "module-hotkey-sample.quick.toggle" &&
            hotkey.State == "conflict" &&
            hotkey.Message.Contains("command-palette", StringComparison.OrdinalIgnoreCase));
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
    public void Runtime_policy_prefers_sidecar_over_higher_priority_inproc()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-sidecar", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "tools"));
        File.WriteAllText(Path.Combine(packageRoot, "tools", "sidecar.exe"), "");
        WriteRuntimePolicySelectionModule(
            packageRoot,
            new JsonObject
            {
                ["preferred"] = "sidecar",
                ["allowInProc"] = true,
                ["inProcRules"] = RuntimePolicyInProcRules(3000),
                ["sidecarRules"] = RuntimePolicySidecarRules(9000, 2, 12)
            },
            includeSidecar: true,
            includeInProc: true);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-sidecar-state", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var diagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Modules);

        Assert.Equal("grpc-ipc", module.Entrypoint!.Kind);
        Assert.Equal(2, module.Entrypoint.SidecarRestartLimit);
        Assert.Equal(12, module.Entrypoint.SidecarRestartWindowSeconds);
        Assert.Contains("runtimePolicy.preferred='sidecar' matched", module.Entrypoint.SelectionReason);
        Assert.Contains(module.TransportDiagnostics, diagnostic => diagnostic.State == "selected" && diagnostic.TransportKind == "grpc-ipc");
        Assert.Contains("runtimePolicy.preferred='sidecar' matched", diagnostics.TransportSelectionReason);
        Assert.Contains(diagnostics.TransportSelectionDiagnostics, item => item.Contains("selected:grpc-ipc", StringComparison.Ordinal));
    }

    [Fact]
    public void Runtime_policy_blocks_inproc_when_allow_inproc_is_false()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-no-inproc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteRuntimePolicySelectionModule(
            packageRoot,
            new JsonObject
            {
                ["preferred"] = "inproc",
                ["allowInProc"] = false,
                ["inProcRules"] = RuntimePolicyInProcRules(3000)
            },
            includeSidecar: false,
            includeInProc: true);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-no-inproc-state", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);

        Assert.Null(module.Entrypoint);
        Assert.Equal("unsupported", module.Status.State);
        Assert.Contains(module.TransportDiagnostics, diagnostic =>
            diagnostic.State == "skipped" &&
            diagnostic.TransportKind == "inproc-dotnet" &&
            diagnostic.Reason.Contains("allowInProc=false", StringComparison.Ordinal));
    }

    [Fact]
    public void Platform_path_service_expands_runtime_path_tokens()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-platform-paths", Guid.NewGuid().ToString("N"));
        var localAppData = Path.Combine(root, "local-appdata");
        var appData = Path.Combine(root, "appdata");
        var userProfile = Path.Combine(root, "user-profile");
        var xdgRuntime = Path.Combine(root, "xdg-runtime");
        var tmpDir = Path.Combine(root, "tmpdir");
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(userProfile);
        Directory.CreateDirectory(xdgRuntime);
        Directory.CreateDirectory(tmpDir);
        var previous = CaptureEnvironment(["LOCALAPPDATA", "APPDATA", "USERPROFILE", "XDG_RUNTIME_DIR", "TMPDIR"]);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
            Environment.SetEnvironmentVariable("APPDATA", appData);
            Environment.SetEnvironmentVariable("USERPROFILE", userProfile);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdgRuntime);
            Environment.SetEnvironmentVariable("TMPDIR", tmpDir);

            var service = new PlatformPathService();

            Assert.Equal(CanonicalPathText(Path.Combine(localAppData, "MyPowerTools", "runner.pipe")), CanonicalPathText(service.ExpandRuntimePath("%LOCALAPPDATA%\\MyPowerTools\\runner.pipe")));
            Assert.Equal(CanonicalPathText(Path.Combine(appData, "MyPowerTools", "settings.json")), CanonicalPathText(service.ExpandRuntimePath("%APPDATA%\\MyPowerTools\\settings.json")));
            Assert.Equal(CanonicalPathText(Path.Combine(userProfile, ".mypowertools", "state.json")), CanonicalPathText(service.ExpandRuntimePath("%USERPROFILE%\\.mypowertools\\state.json")));
            Assert.Equal(CanonicalPathText(Path.Combine(xdgRuntime, "mypowertools", "runner.sock")), CanonicalPathText(service.ExpandRuntimePath("$XDG_RUNTIME_DIR/mypowertools/runner.sock")));
            Assert.Equal(CanonicalPathText(Path.Combine(tmpDir, "mypowertools", "runner.sock")), CanonicalPathText(service.ExpandRuntimePath("${TMPDIR}/mypowertools/runner.sock")));
            Assert.False(service.ExpandRuntimePath("~/mypowertools/runner.sock").StartsWith("~", StringComparison.Ordinal));
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    [Fact]
    public void Transport_selector_expands_runtime_endpoint_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-selector-paths", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "package");
        var localAppData = Path.Combine(root, "local-appdata");
        var xdgRuntime = Path.Combine(root, "xdg-runtime");
        var tmpDir = Path.Combine(root, "tmpdir");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(xdgRuntime);
        Directory.CreateDirectory(tmpDir);
        File.WriteAllText(Path.Combine(packageRoot, "module.json"), """
        {
          "schemaVersion": "1.0",
          "id": "selector.path-expansion",
          "packageId": "selector-path-expansion",
          "displayName": "Selector Path Expansion",
          "version": "0.1.0",
          "moduleSdk": "1.0",
          "entrypoints": [
            {
              "kind": "grpc-ipc",
              "priority": 90,
              "windows": { "transport": "named-pipe", "name": "%LOCALAPPDATA%\\MyPowerTools\\runner.pipe" },
              "linux": { "transport": "unix-domain-socket", "path": "$XDG_RUNTIME_DIR/mypowertools/runner.sock" },
              "macos": { "transport": "unix-domain-socket", "path": "${TMPDIR}/mypowertools/runner.sock" }
            }
          ],
          "capabilities": ["status", "commands"]
        }
        """);
        var previous = CaptureEnvironment(["LOCALAPPDATA", "XDG_RUNTIME_DIR", "TMPDIR"]);
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", xdgRuntime);
            Environment.SetEnvironmentVariable("TMPDIR", tmpDir);

            var package = new PackageReader().ReadPackageDirectory(packageRoot);
            var module = Assert.Single(package.Modules);

            AssertSelectedEndpoint("windows", Path.Combine(localAppData, "MyPowerTools", "runner.pipe"));
            AssertSelectedEndpoint("linux", Path.Combine(xdgRuntime, "mypowertools", "runner.sock"));
            AssertSelectedEndpoint("macos", Path.Combine(tmpDir, "mypowertools", "runner.sock"));

            void AssertSelectedEndpoint(string operatingSystem, string expectedAddress)
            {
                var selector = new TransportSelector(new PlatformId(operatingSystem, "x64"));
                var selected = selector.Select(package, module).Entrypoint;

                Assert.NotNull(selected);
                Assert.Equal("grpc-ipc", selected!.Kind);
                Assert.Equal(CanonicalPathText(expectedAddress), CanonicalPathText(selected.EndpointAddress!));
            }
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    [Fact]
    public async Task Runtime_policy_blocks_disallowed_inproc_operation_command()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-operation-block", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteRuntimePolicyOperationModule(packageRoot, "runtime-policy-selection.external", new JsonArray(MptOperationConstraints.RunsExternalProcesses));
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet");
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-operation-block-state", Guid.NewGuid().ToString("N"))),
            [transport]);

        runtime.Load(packageRoot);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("policy-block", "runtime-policy-selection.external", new JsonObject()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("failed", result.State);
        Assert.Equal(MptErrorCodes.RuntimePolicyBlocked, result.Error!.Code);
        Assert.Equal("inproc-dotnet", result.Error.Details!["selectedTransport"]!.GetValue<string>());
        Assert.Contains("runsExternalProcesses", result.Error.Details!["constraints"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Equal("sidecar-or-service", result.Error.Details!["alternateRequiredRoute"]!.GetValue<string>());
        Assert.Contains("transport 'inproc-dotnet' is unavailable", result.Error.Details!["unavailableReason"]!.GetValue<string>());
        Assert.Contains(result.Error.Details!["routeDiagnostics"]!.AsArray(), item => item!.GetValue<string>().Contains("runtime-policy-selection.external", StringComparison.Ordinal));
        Assert.Equal(0, transport.ExecuteCount);
    }

    [Fact]
    public async Task Runtime_policy_routes_constrained_command_to_sidecar_while_status_stays_inproc()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-command-route", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteRuntimePolicyMixedRouteModule(packageRoot);
        var inproc = new RecordingSettingsTransportRuntime("inproc-dotnet");
        var sidecar = new RecordingSettingsTransportRuntime("grpc-ipc");
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-command-route-state", Guid.NewGuid().ToString("N"))),
            [inproc, sidecar]);

        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var status = await runtime.ExecuteCommandAsync(
            new CommandRequest("policy-route-status", "runtime-policy-selection.status", new JsonObject()),
            CancellationToken.None);
        var external = await runtime.ExecuteCommandAsync(
            new CommandRequest("policy-route-external", "runtime-policy-selection.external", new JsonObject()),
            CancellationToken.None);

        Assert.Equal("inproc-dotnet", module.Entrypoint!.Kind);
        Assert.True(status.Success, status.Error?.Message);
        Assert.True(external.Success, external.Error?.Message);
        Assert.Equal(1, inproc.ExecuteCount);
        Assert.Equal(1, sidecar.ExecuteCount);
    }

    [Fact]
    public async Task Runtime_policy_allows_inproc_broker_approval_command()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-broker-approval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteRuntimePolicyOperationModule(
            packageRoot,
            "runtime-policy-selection.broker-plan",
            new JsonArray(MptOperationConstraints.MutatesSystemState, MptOperationConstraints.RequiresElevatedWrites),
            brokerApprovalOnly: true);
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet");
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-policy-broker-approval-state", Guid.NewGuid().ToString("N"))),
            [transport]);

        runtime.Load(packageRoot);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("policy-broker", "runtime-policy-selection.broker-plan", new JsonObject()),
            CancellationToken.None);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(1, transport.ExecuteCount);
    }

    [Fact]
    public void Production_modules_declare_runtime_policy_without_development_fallback()
    {
        var packages = new PackageReader().DiscoverPackages(Path.Combine(Root, "modules"));

        foreach (var module in packages.SelectMany(package => package.Modules))
        {
            Assert.NotNull(module.Manifest.RuntimePolicy);
            Assert.Null(module.Manifest.Development);
            if (module.Manifest.PackageId == "android-tools-suite")
            {
                Assert.False(module.Manifest.RuntimePolicy!.AllowInProc);
                Assert.Equal("sidecar", module.Manifest.RuntimePolicy.Preferred);
            }
        }
    }

    [Fact]
    public void Production_screenease_hotkeys_match_source_defaults_and_remain_disabled()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var diagnostics = runtime.ListHotkeyDiagnostics()
            .Where(binding => binding.ModuleId == "screenease")
            .ToArray();

        Assert.Empty(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease"));
        Assert.Equal(8, diagnostics.Length);
        Assert.All(diagnostics, hotkey =>
        {
            Assert.Equal("disabled", hotkey.State);
            Assert.True(hotkey.IsDefault);
            Assert.True(WindowsHotkeyGesture.TryParse(hotkey.Gesture, out _, out var error), error);
        });
        var toggle = Assert.Single(diagnostics.Where(hotkey => hotkey.Id == "screenease.toggle-enabled"));
        Assert.Equal("screenease.effect.toggle", toggle.CommandId);
        Assert.Equal("Ctrl+Alt+F9", toggle.Gesture);
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
}
