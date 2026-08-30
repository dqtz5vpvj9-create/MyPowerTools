using System.Diagnostics;
using NssmManager.Contracts;
using NssmManager.Supervisor;
using NssmManager.Windows;

namespace NssmManager.Tests;

public sealed class NssmServiceRuntimeTranslationTests
{
    [Fact]
    public void replacement_environment_ignores_windows_drive_current_directory_entries_like_upstream()
    {
        var environment = NativeChildProcess.BuildEnvironmentDictionary(new NssmServiceConfiguration
        {
            Environment = ["=C:=C:\\Windows", "NSSM_VALUE=translated"]
        });

        Assert.False(environment.ContainsKey("=C:"));
        Assert.Equal("translated", environment["NSSM_VALUE"]);
    }

    [Fact]
    public void missing_working_directory_uses_the_upstream_application_then_windows_fallback()
    {
        var configuration = new NssmServiceConfiguration { Application = "command.exe" };
        var environment = NativeChildProcess.BuildEnvironmentDictionary(configuration);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            NativeChildProcess.ResolveWorkingDirectory(configuration, "command.exe", environment));
        Assert.Equal(@"C:\tools", NativeChildProcess.ResolveWorkingDirectory(configuration, @"C:\tools\command.exe", environment));
    }

    [Fact]
    public async Task native_createprocess_path_inherits_online_logging_pipe()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-native-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "stdout.log");
        try
        {
            var configuration = new NssmServiceConfiguration
            {
                Name = "native-process-test",
                Application = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                AppDirectory = directory,
                AppParameters = "/d /s /c \"echo native-createprocess\"",
                AppStdout = output,
                RotateFiles = true,
                RotateOnline = true,
                NoConsole = true
            };
            await using var io = NativeChildProcessIo.Create(configuration);
            using var process = NativeChildProcess.Start(configuration, configuration.Application, directory, io);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            await io.DisposeAsync();
            Assert.Contains("native-createprocess", await File.ReadAllTextAsync(output), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task native_hook_path_uses_raw_createprocess_command_line_and_environment()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-native-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "hook.txt");
        try
        {
            var command = $"\"{Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"}\" /d /s /c \"echo %NSSM_TEST_VALUE%>{output}\"";
            var environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(item => (string)item.Key, item => (string?)item.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
            environment["NSSM_TEST_VALUE"] = "raw-createprocess";
            using var process = NativeChildProcess.StartHook(command, directory, environment, null, false);
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("raw-createprocess", (await File.ReadAllTextAsync(output)).Trim());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task managed_runtime_shares_logging_pipe_with_prestart_hook_and_application()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "combined.log");
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        try
        {
            await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
            {
                Name = "runtime-pipe-test",
                Application = commandProcessor,
                AppDirectory = directory,
                AppParameters = "/d /s /c \"echo application-output\"",
                AppStdout = output,
                RotateFiles = true,
                RotateOnline = true,
                RedirectHookOutput = true,
                NoConsole = true,
                DefaultExitAction = NssmExitAction.Exit,
                Hooks = [new NssmManager.Contracts.NssmHook("Start", "Pre", $"\"{commandProcessor}\" /d /s /c \"echo prestart-hook\"")]
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await runtime.RunAsync(timeout.Token));
            var text = await File.ReadAllTextAsync(output, timeout.Token);
            Assert.Contains("prestart-hook", text, StringComparison.Ordinal);
            Assert.Contains("application-output", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task replacement_environment_expands_application_directory_parameters_and_io_paths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-runtime-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "expanded.log");
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        try
        {
            await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
            {
                Name = "runtime-environment-test",
                Application = "%NSSM_TEST_EXE%",
                AppDirectory = "%NSSM_TEST_DIRECTORY%",
                AppParameters = "/d /s /c \"echo %NSSM_TEST_VALUE%\"",
                AppStdout = "%NSSM_TEST_DIRECTORY%\\expanded.log",
                Environment =
                [
                    "NSSM_TEST_EXE=" + commandProcessor,
                    "NSSM_TEST_DIRECTORY=" + directory,
                    "NSSM_TEST_VALUE=service-environment",
                    "SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "WINDIR=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                ],
                NoConsole = true,
                DefaultExitAction = NssmExitAction.Exit
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await runtime.RunAsync(timeout.Token));
            Assert.Contains("service-environment", await File.ReadAllTextAsync(output, timeout.Token), StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task hook_command_expands_service_and_hook_environment_before_createprocess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nssm-hook-environment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "hook-environment.txt");
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        try
        {
            await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
            {
                Name = "hook-expansion-test",
                Application = commandProcessor,
                AppDirectory = directory,
                AppParameters = "/d /s /c \"exit 0\"",
                EnvironmentExtra = ["NSSM_TEST_EXE=" + commandProcessor, "NSSM_TEST_OUTPUT=" + output],
                NoConsole = true,
                DefaultExitAction = NssmExitAction.Exit,
                Hooks = [new NssmManager.Contracts.NssmHook("Start", "Pre", "%NSSM_TEST_EXE% /d /s /c \"echo %NSSM_SERVICE_NAME%>%NSSM_TEST_OUTPUT%\"")]
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await runtime.RunAsync(timeout.Token));
            Assert.Equal("hook-expansion-test", (await File.ReadAllTextAsync(output, timeout.Token)).Trim());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task exit_action_propagates_application_exit_code_to_scm_result()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "exit-code-test",
            Application = commandProcessor,
            AppDirectory = Environment.CurrentDirectory,
            AppParameters = "/d /s /c \"exit 37\"",
            NoConsole = true,
            DefaultExitAction = NssmExitAction.Exit
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(37, await runtime.RunAsync(timeout.Token));
    }

    [Fact]
    public async Task default_suicide_for_zero_exit_uses_upstream_graceful_suicide_rule()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "graceful-suicide-test",
            Application = commandProcessor,
            AppDirectory = Environment.CurrentDirectory,
            AppParameters = "/d /s /c \"exit 0\"",
            NoConsole = true,
            DefaultExitAction = NssmExitAction.Suicide
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await runtime.RunAsync(timeout.Token));
    }

    [Fact]
    public async Task explicit_zero_exit_suicide_keeps_upstream_unclean_exit_rule()
    {
        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "unclean-suicide-test",
            Application = commandProcessor,
            AppDirectory = Environment.CurrentDirectory,
            AppParameters = "/d /s /c \"exit 0\"",
            NoConsole = true,
            DefaultExitAction = NssmExitAction.Restart,
            ExitRules = [new NssmExitRule(0, NssmExitAction.Suicide)]
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var exception = await Assert.ThrowsAsync<NssmServiceSuicideException>(() => runtime.RunAsync(timeout.Token));
        Assert.Equal(0, exception.ExitCode);
    }

    [Fact]
    public async Task initial_createprocess_failure_returns_upstream_service_specific_code()
    {
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "missing-application-test",
            Application = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe")
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(3, await runtime.RunAsync(timeout.Token));
    }

    [Fact]
    public async Task runtime_function_translation_is_wired()
    {
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "translation-test",
            Application = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.exe"),
            DefaultExitAction = NssmExitAction.Ignore
        });

        Assert.Equal(1, await ManagedServiceRuntime.await_single_handle(Process.GetCurrentProcess(), 0));
        await runtime.throttle_restart(CancellationToken.None);
        Assert.Equal(NssmExitAction.Ignore, await runtime.end_service(7, controlled: false));
        await runtime.wait_for_hooks(false);
        var startError = await Assert.ThrowsAsync<NssmServiceStartException>(() => runtime.start_service(CancellationToken.None));
        Assert.Equal(3, startError.ExitCode);
    }

    [Fact]
    public async Task continue_control_interrupts_restart_throttle_and_reports_upstream_states()
    {
        await using var runtime = new ManagedServiceRuntime(new NssmServiceConfiguration
        {
            Name = "throttle-test",
            RestartDelayMilliseconds = 30000
        });
        var states = new List<NssmRuntimeState>();
        runtime.StatusChanged += status => states.Add(status.State);
        await runtime.throttle_restart(CancellationToken.None);
        var throttled = runtime.throttle_restart(CancellationToken.None);
        await Task.Delay(50);
        Assert.Contains(NssmRuntimeState.Paused, states);
        await runtime.ContinueAsync();
        await throttled.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(NssmRuntimeState.ContinuePending, states);
    }
}
