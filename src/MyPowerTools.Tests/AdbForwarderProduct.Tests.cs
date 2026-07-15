using AdbForwarder.Surface.Services;
using AdbForwarder.Surface.ViewModels;
using System.Text.Json.Nodes;
using System.Diagnostics;

namespace MyPowerTools.Tests;

public sealed class AdbForwarderProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Mapping_editor_validates_ports_and_builds_typed_mapping()
    {
        var editor = new AdbForwarderMappingEditorViewModel(
            new AdbForwarderMapping("one", "Pixel", true, "0.0.0.0", 0, "127.0.0.1", 0),
            _ => { });

        Assert.False(editor.TryBuild(out _));
        Assert.Contains("监听端口", editor.ValidationMessage);
        Assert.Contains("目标端口", editor.ValidationMessage);

        editor.ListenPort = "15555";
        editor.ConnectPort = "30555";

        Assert.True(editor.TryBuild(out var mapping));
        Assert.Equal(15555, mapping.ListenPort);
        Assert.Equal(30555, mapping.ConnectPort);
        Assert.False(editor.HasValidationError);
    }

    [Fact]
    public void View_model_exposes_devices_rules_and_broker_plan()
    {
        var rule = new AdbForwarderRule("0.0.0.0", 15555, "127.0.0.1", 30555);
        var snapshot = new AdbForwarderSnapshot(
            true,
            "Android Debug Bridge version 1.0.41",
            [new AdbForwarderDevice("<adb-device-1>", "device", "Pixel 8", "shiba", "2")],
            true,
            [rule],
            [new AdbForwarderMapping("one", "Pixel", true, "0.0.0.0", 15555, "127.0.0.1", 30555)],
            new AdbForwarderPlan([rule], [rule], [], ["Broker approval required."], true),
            [],
            4);

        var viewModel = new AdbForwarderViewModel(snapshot);

        Assert.True(viewModel.IsForward);
        Assert.Equal("1 台设备", viewModel.DeviceCountText);
        Assert.Equal("1 条生效规则", viewModel.RuleCountText);
        Assert.Equal("将新增 1 条 · 移除 0 条", viewModel.PlanSummary);
        Assert.True(viewModel.HasMappings);
        Assert.True(viewModel.HasWarnings);
        Assert.Equal("应用更改时需要管理员确认。", Assert.Single(viewModel.UserFacingWarnings));
        Assert.True(viewModel.ApplyCommand.CanExecute(null));

        viewModel.Mappings[0].ListenPort = "16666";

        Assert.False(viewModel.IsPreviewCurrent);
        Assert.Equal("配置已修改，请重新预览", viewModel.PlanSummary);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void Shell_wires_adb_forwarder_to_a_dedicated_product_view()
    {
        var controller = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.Tools.cs"));
        var workspaceController = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.cs"));
        var commandController = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.Commands.cs"));
        var view = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Views",
            "AdbForwarderView.axaml"));

        Assert.Contains("AdbForwarderToolId", controller);
        Assert.Contains("LoadAdbForwarderToolAsync", controller);
        Assert.Contains("new AdbForwarderView", controller);
        Assert.Contains("ShowToolPageAsync(AdbForwarderToolId, \"forward\")", workspaceController);
        Assert.DoesNotContain("ShowToolPageAsync(AdbForwarderToolId, \"rules\")", workspaceController);
        Assert.Contains("IsAdbPortProxyBrokerCommand(commandId)", commandController);
        Assert.Contains("OpenAdbPortProxyBrokerWorkspaceAsync", commandController);
        Assert.Contains("ShowToolPageAsync(AdbForwarderToolId, \"rules\")", commandController);
        Assert.Contains("已管理的映射", view);
        Assert.Contains("更改预览", view);
        Assert.Contains("一次性审批", view);
        Assert.Contains("Windows UAC", view);
        Assert.Contains("RetryForwardLabel", view);
        Assert.Contains("MaxWidth=\"1480\"", view);
        Assert.DoesNotContain("Content=\"All tools\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("NetworkBroker", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Interface in development", view, StringComparison.Ordinal);

        var screenshotWriter = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "Mpt.Cli.VisualTesting",
            "ShellRealScreenshotWriter.cs"));
        Assert.Contains("WriteAdbForwarderSnapshotFromRunnerAsync", screenshotWriter);
        Assert.Contains("adb-forwarder-live", screenshotWriter);
        Assert.Contains("adb-forwarder.forward", screenshotWriter);
        Assert.Contains("return Task.Run", screenshotWriter);
    }

    [Fact]
    public void Manifest_and_shell_route_describe_the_execution_path_that_is_shipped()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "modules", "adb-forwarder", "module.json")))!.AsObject();
        var tool = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "modules", "adb-forwarder", "ui", "tool.json")))!.AsObject();
        var policy = manifest["runtimePolicy"]!.AsObject();
        var operationRules = policy["operationRules"]!.AsObject();
        var entrypoints = manifest["entrypoints"]!.AsArray();
        var toolService = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "AdbForwarderToolService.cs"));
        var moduleSource = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "AdbForwarder.MyPowerTools",
            "AdbForwarderModule.cs"));
        var elevationSource = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "AdbForwarderElevationService.cs"));

        Assert.Equal("forward", tool["primaryRouteId"]!.GetValue<string>());
        Assert.Equal("inproc", policy["preferred"]!.GetValue<string>());
        Assert.Null(policy["sidecarRules"]);
        Assert.Equal("inproc-or-sidecar", operationRules["externalProcess"]!.GetValue<string>());
        Assert.Single(entrypoints);
        Assert.Equal("inproc-dotnet", entrypoints[0]!["kind"]!.GetValue<string>());
        Assert.Contains("GetSettingsAsync(ModuleId", toolService);
        Assert.Contains("new AdbForwardingWorkflowService(adbPath: _workflowAdbPath)", toolService);
        Assert.Contains("arguments.Contains(\"devices\"", moduleSource);
        Assert.DoesNotContain("string.Equals(fileName, \"adb\"", moduleSource);
        Assert.Contains("MyPowerTools.ElevatedBroker.exe", elevationSource);
        Assert.Contains("WindowsProtectedExecutable.IsTrusted", elevationSource);
        Assert.Contains("FileName = launch.ExecutablePath", elevationSource);
        Assert.Contains("Verb = \"runas\"", elevationSource);
        Assert.DoesNotContain("conhost.exe", elevationSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet.exe", elevationSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--broker-sha256", elevationSource);
    }

    [Fact]
    public void Mapping_arguments_preserve_typed_ports_and_addresses()
    {
        var args = AdbForwarderToolService.BuildMappingArgs(
            [new AdbForwarderMapping("one", "Pixel", true, "0.0.0.0", 15555, "127.0.0.1", 30555)]);
        var mapping = Assert.IsType<System.Text.Json.Nodes.JsonObject>(args["mappings"]![0]);

        Assert.Equal(15555, mapping["listenPort"]!.GetValue<int>());
        Assert.Equal("127.0.0.1", mapping["connectAddress"]!.GetValue<string>());
    }

    [Fact]
    public async Task Revert_uses_saved_mappings_and_portproxy_availability_gates_dangerous_commands()
    {
        var savedMapping = new AdbForwarderMapping("saved", "Saved", true, "0.0.0.0", 15555, "127.0.0.1", 30555);
        var rule = new AdbForwarderRule("0.0.0.0", 15555, "127.0.0.1", 30555);
        var executed = new TaskCompletionSource<IReadOnlyList<AdbForwarderMapping>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new AdbForwarderSnapshot(
            true,
            "adb",
            [],
            true,
            [rule],
            [savedMapping],
            new AdbForwarderPlan([rule], [], [], [], false),
            [],
            1);
        var viewModel = new AdbForwarderViewModel(
            snapshot,
            executeBrokered: (_, mappings) =>
            {
                executed.TrySetResult(mappings);
                return Task.CompletedTask;
            });
        viewModel.Mappings[0].ListenPort = "16666";

        Assert.True(viewModel.RevertCommand.CanExecute(null));
        viewModel.RevertCommand.Execute(null);
        var revertedMappings = await executed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(15555, Assert.Single(revertedMappings).ListenPort);

        var unavailable = new AdbForwarderViewModel(snapshot with { PortProxyAvailable = false });
        Assert.False(unavailable.PreviewChangesCommand.CanExecute(null));
        Assert.False(unavailable.ApplyCommand.CanExecute(null));
        Assert.False(unavailable.RevertCommand.CanExecute(null));
    }

    [Fact]
    public async Task Elevated_broker_uses_a_single_expiring_request_and_requires_an_explicit_second_click()
    {
        var requestDirectory = Path.Combine(Path.GetTempPath(), "mpt-adb-broker", Guid.NewGuid().ToString("N"));
        var launcher = new RecordingElevatedLauncher();
        var now = DateTimeOffset.Parse("2026-07-11T09:00:00Z");
        var brokerExecutable = Path.Combine(requestDirectory, "MyPowerTools.ElevatedBroker.exe");
        var broker = new AdbForwarderElevationService(
            launcher,
            requestDirectory,
            () => now,
            new FixedBrokerLaunchResolver(brokerExecutable),
            new FixedPortProxySnapshotProvider([]));
        var mapping = new AdbForwarderMapping("workflow", "Shared ADB", true, "0.0.0.0", 15557, "127.0.0.1", 15556);

        var staged = await broker.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, mapping, CancellationToken.None);

        Assert.Equal(AdbForwarderBrokerDisposition.ApprovalRequired, staged.Disposition);
        var requestPath = Assert.Single(Directory.GetFiles(requestDirectory, "*.json"));
        var token = Path.GetFileNameWithoutExtension(requestPath);
        Assert.Contains(token[..8], staged.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Calls);

        var applied = await broker.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, mapping, CancellationToken.None);

        Assert.Equal(AdbForwarderBrokerDisposition.Applied, applied.Disposition);
        var call = Assert.Single(launcher.Calls);
        Assert.Equal(brokerExecutable, call.Launch.ExecutablePath);
        Assert.Contains("execute-request", call.Arguments);
        Assert.Contains(requestPath, call.Arguments);
        Assert.Contains(token, call.Arguments);
        var digestIndex = call.Arguments.ToList().IndexOf("--digest");
        Assert.True(digestIndex >= 0);
        Assert.Equal(64, call.Arguments[digestIndex + 1].Length);
        var brokerHashIndex = call.Arguments.ToList().IndexOf("--broker-sha256");
        Assert.True(brokerHashIndex >= 0);
        Assert.Equal(call.Launch.Sha256, call.Arguments[brokerHashIndex + 1]);
    }

    [Fact]
    public void Device_display_identifier_never_exposes_a_usb_serial()
    {
        var device = new AdbForwarderDevice("USB-SECRET-SERIAL-123", "device", "Pixel", "husky", "1");

        Assert.DoesNotContain("USB-SECRET", device.DisplayId, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<adb-device-", device.DisplayId, StringComparison.Ordinal);

        var untrustedModuleAlias = device with { SafeId = "USB-SECRET-SERIAL-123" };
        Assert.DoesNotContain("USB-SECRET", untrustedModuleAlias.DisplayId, StringComparison.OrdinalIgnoreCase);

        var wireless = new AdbForwarderDevice(
            "10.33.2.156:5555",
            "device",
            "Pixel",
            "husky",
            "2",
            "<adb-device-redacted>");
        Assert.Equal("10.33.2.156:5555", wireless.DisplayId);
    }

    [Fact]
    public async Task Module_tool_timeout_kills_and_confirms_the_entire_process_tree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var pwshPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        Assert.True(File.Exists(pwshPath), $"PowerShell 7 executable missing: {pwshPath}");
        var lockPath = Path.Combine(Path.GetTempPath(), $"mpt-adb-module-timeout-{Guid.NewGuid():N}.lock");
        var escapedLockPath = lockPath.Replace("'", "''", StringComparison.Ordinal);
        var escapedPwshPath = pwshPath.Replace("'", "''", StringComparison.Ordinal);
        var childScript = $"$stream=[IO.File]::Open('{escapedLockPath}','OpenOrCreate','ReadWrite','None'); Start-Sleep -Seconds 30";
        var childEncoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(childScript));
        var parentScript = $$"""
            $psi = [Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = '{{escapedPwshPath}}'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $psi.ArgumentList.Add('-NoLogo')
            $psi.ArgumentList.Add('-NoProfile')
            $psi.ArgumentList.Add('-NonInteractive')
            $psi.ArgumentList.Add('-EncodedCommand')
            $psi.ArgumentList.Add('{{childEncoded}}')
            $child = [Diagnostics.Process]::Start($psi)
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            while (-not (Test-Path -LiteralPath '{{escapedLockPath}}')) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'child did not acquire lock' }
                Start-Sleep -Milliseconds 20
            }
            Start-Sleep -Seconds 30
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(parentScript));
        var method = typeof(AdbForwarder.MyPowerTools.AdbForwarderModule).GetMethod(
            "RunToolAsync",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(
            null,
            [
                pwshPath,
                new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded },
                TimeSpan.FromSeconds(2),
                CancellationToken.None
            ]));
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var stderr = Assert.IsType<string>(result.GetType().GetProperty("Stderr")!.GetValue(result));
        Assert.Contains("process tree exited", stderr, StringComparison.OrdinalIgnoreCase);

        FileStream? exclusive = null;
        for (var attempt = 0; attempt < 30 && exclusive is null; attempt++)
        {
            try
            {
                exclusive = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
        Assert.NotNull(exclusive);
        await exclusive.DisposeAsync();
    }

    [Fact]
    public async Task Cli_refuses_to_consume_elevated_approval_requests()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cli = Path.Combine(Root, "src", "MyPowerTools.Cli", "bin", "Release", "net10.0", "MyPowerTools.Cli.dll");
        Assert.True(File.Exists(cli), "Release CLI must be built before the broker integration test.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            cli,
            "broker",
            "portproxy",
            "execute-request"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var output = (await stdout) + (await stderr);

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("only through the installed MyPowerTools.ElevatedBroker.exe", output, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class RecordingElevatedLauncher : IAdbForwarderElevatedProcessLauncher
    {
        public List<(AdbForwarderBrokerLaunch Launch, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<int> RunAsync(
            AdbForwarderBrokerLaunch launch,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((launch, arguments.ToArray()));
            return Task.FromResult(0);
        }
    }

    private sealed class FixedBrokerLaunchResolver(string executablePath) : IAdbForwarderBrokerLaunchResolver
    {
        public AdbForwarderBrokerLaunch Resolve() => new(executablePath, new string('a', 64));
    }

    private sealed class FixedPortProxySnapshotProvider(IReadOnlyList<MyPowerTools.Platform.Abstractions.PortProxyRule> rules)
        : IAdbForwarderPortProxySnapshotProvider
    {
        public Task<IReadOnlyList<MyPowerTools.Platform.Abstractions.PortProxyRule>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(rules);
        }
    }
}
