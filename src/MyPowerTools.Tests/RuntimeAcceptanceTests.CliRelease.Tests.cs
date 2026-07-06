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
    public async Task Cli_restarts_runner_grpc_process_pool_over_hostcontrol()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.cli.runner.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart-data", Guid.NewGuid().ToString("N"));
        var previousDataRoot = Environment.GetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, dataRoot);
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
            if (afterRestart.Processes.Count > 0)
            {
                var restartedProcess = Assert.Single(afterRestart.Processes);
                Assert.Equal(process.TransportKind, restartedProcess.TransportKind);
                Assert.Equal(process.PoolKey, restartedProcess.PoolKey);
                Assert.NotEqual(process.ProcessId, restartedProcess.ProcessId);
                Assert.True(restartedProcess.StartCount > process.StartCount);
            }

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
            Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, previousDataRoot);
        }
    }
}
