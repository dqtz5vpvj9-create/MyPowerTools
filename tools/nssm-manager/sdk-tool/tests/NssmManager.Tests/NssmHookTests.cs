using System.Diagnostics;
using NssmManager.Contracts;
using NssmManager.Supervisor;
using HookRuntime = NssmManager.Supervisor.NssmHook;

namespace NssmManager.Tests;

public sealed class NssmHookTests
{
    [Fact]
    public async Task await_hook_maps_timeout_abort_and_failure_statuses()
    {
        Assert.Equal(HookRuntime.HookStatusAbort, await AwaitExit("exit /b 99", 5000));
        Assert.Equal(HookRuntime.HookStatusFailed, await AwaitExit("exit /b 7", 5000));
        Assert.Equal(HookRuntime.HookStatusTimeout, await AwaitExit("ping 127.0.0.1 -n 6 >nul", 20));
    }

    [Fact]
    public void set_hook_runtime_uses_nonnegative_milliseconds()
    {
        var environment = new Dictionary<string, string?>();
        var start = DateTimeOffset.UnixEpoch;
        HookRuntime.set_hook_runtime(environment, "RUNTIME", start, start.AddMilliseconds(1234));
        Assert.Equal("1234", environment["RUNTIME"]);
        HookRuntime.set_hook_runtime(environment, "RUNTIME", start.AddSeconds(1), start);
        Assert.Equal(string.Empty, environment["RUNTIME"]);
        HookRuntime.set_hook_runtime(environment, "RUNTIME", null, start);
        Assert.Equal(string.Empty, environment["RUNTIME"]);
    }

    [Fact]
    public async Task thread_collection_retains_only_running_hooks()
    {
        var collection = new NssmHookThreads();
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        HookRuntime.add_thread_handle(collection, Task.FromResult(0), "done");
        HookRuntime.add_thread_handle(collection, pending.Task, "pending");
        await HookRuntime.await_hook_threads(collection, 0);
        Assert.Single(collection.Data);
        Assert.Equal("pending", collection.Data[0].Name);
        pending.SetResult(0);
        await HookRuntime.await_hook_threads(collection, 1000);
        Assert.Empty(collection.Data);
    }

    [Theory]
    [InlineData("Exit", "Post", true)]
    [InlineData("Power", "Change", true)]
    [InlineData("Power", "Resume", true)]
    [InlineData("Rotate", "Pre", true)]
    [InlineData("Rotate", "Post", true)]
    [InlineData("Start", "Pre", true)]
    [InlineData("Start", "Post", true)]
    [InlineData("Stop", "Pre", true)]
    [InlineData("Exit", "Pre", false)]
    [InlineData("Stop", "Post", false)]
    [InlineData("Bogus", "Pre", false)]
    public void valid_hook_name_matches_event_action_matrix(string hookEvent, string hookAction, bool expected) =>
        Assert.Equal(expected, HookRuntime.valid_hook_name(hookEvent, hookAction, quiet: true));

    [Fact]
    public async Task nssm_hook_sets_version_one_environment_and_status()
    {
        var path = Path.Combine(Path.GetTempPath(), "nssm-hook-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            var configuration = new NssmServiceConfiguration
            {
                Name = "hook-test",
                DisplayName = "Hook Test",
                Application = commandProcessor,
                AppDirectory = Path.GetTempPath(),
                Hooks = [new NssmManager.Contracts.NssmHook("Start", "Pre", $"\"{commandProcessor}\" /d /s /c \"echo %NSSM_HOOK_VERSION%:%NSSM_EVENT%:%NSSM_ACTION%:%NSSM_SERVICE_NAME% > \"\"{path}\"\"\"")]
            };
            var context = new NssmHookServiceContext { Service = configuration, LastControl = "START" };
            var status = await HookRuntime.nssm_hook(new NssmHookThreads(), context, "Start", "Pre", "START", 5000, async: false);
            Assert.Equal(HookRuntime.HookStatusSuccess, status);
            Assert.Equal("1:Start:Pre:hook-test", (await File.ReadAllTextAsync(path)).Trim());
        }
        finally { File.Delete(path); }
    }

    private static async Task<int> AwaitExit(string command, uint deadline)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/s");
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add(command);
        var process = Process.Start(info)!;
        return await HookRuntime.await_hook(new NssmHookInvocation { Name = "test", Process = process, Deadline = deadline });
    }
}
