using System.Diagnostics;
using System.Globalization;
using NssmManager.Contracts;
using NssmManager.Windows;

namespace NssmManager.Supervisor;

public sealed record NssmHookThreadData(string Name, Task<int> ThreadHandle);

public sealed class NssmHookThreads
{
    internal object DataGate { get; } = new();
    internal SemaphoreSlim ExecutionGate { get; } = new(1, 1);
    public List<NssmHookThreadData> Data { get; } = [];
    public int NumThreads { get { lock (DataGate) return Data.Count; } }
}

public sealed class NssmHookServiceContext
{
    public required NssmServiceConfiguration Service { get; init; }
    public Process? ApplicationProcess { get; init; }
    public string LastControl { get; init; } = string.Empty;
    public DateTimeOffset NssmCreationTime { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApplicationCreationTime { get; init; }
    public DateTimeOffset? ApplicationExitTime { get; init; }
    public uint ExitCode { get; init; }
    public uint StartRequestedCount { get; init; }
    public uint StartCount { get; init; }
    public uint ExitCount { get; init; }
    public uint ThrottleCount { get; init; }
    public Func<string, string, IReadOnlyDictionary<string, string?>, Process?>? StartHook { get; init; }
    public Action<ProcessStartInfo>? ConfigureOutput { get; init; }
    public Func<Process, CancellationToken, Task>? PumpOutput { get; init; }
}

public sealed class NssmHookInvocation
{
    public required string Name { get; init; }
    public required Process Process { get; init; }
    public required uint Deadline { get; init; }
    public Task OutputPumps { get; init; } = Task.CompletedTask;
}

/// <summary>Function-for-function managed translation of hook.cpp.</summary>
public static class NssmHook
{
    public const int HookStatusSuccess = 0;
    public const int HookStatusNotFound = 1;
    public const int HookStatusAbort = 99;
    public const int HookStatusError = 100;
    public const int HookStatusNotRun = 101;
    public const int HookStatusTimeout = 102;
    public const int HookStatusFailed = 111;
    public const uint HookDeadline = 60000;

    [NssmUpstreamFunction("src/hook.cpp", 15, "static unsigned long WINAPI await_hook(void *arg)", "NssmHookTests.await_hook_maps_timeout_abort_and_failure_statuses")]
    public static async Task<int> await_hook(NssmHookInvocation? hook, CancellationToken cancellationToken = default)
    {
        if (hook is null) return HookStatusError;
        using var process = hook.Process;
        var creationTime = DateTime.UtcNow.ToFileTimeUtc();
        if (NssmProcess.get_process_creation_time(process.Handle, out var nativeCreationTime) == 0) creationTime = nativeCreationTime;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(checked((int)hook.Deadline));
        var result = HookStatusSuccess;
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await hook.OutputPumps.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            result = HookStatusTimeout;
        }
        catch
        {
            result = HookStatusError;
        }

        var cleanup = new NssmKillContext
        {
            Name = hook.Name,
            ProcessHandle = process.Handle,
            ProcessId = unchecked((uint)process.Id),
            StopMethod = uint.MaxValue,
            KillConsoleDelay = 1500,
            KillWindowDelay = 1500,
            KillThreadsDelay = 1500,
            CreationTime = creationTime,
            ExitTime = DateTime.UtcNow.ToFileTimeUtc()
        };
        NssmProcess.kill_process_tree(cleanup, cleanup.ProcessId);
        if (result != HookStatusSuccess) return result;
        var exitCode = process.ExitCode;
        if (exitCode == HookStatusAbort) return HookStatusAbort;
        if (exitCode != 0) return HookStatusFailed;
        return HookStatusSuccess;
    }

    [NssmUpstreamFunction("src/hook.cpp", 55, "static void set_hook_runtime(TCHAR *v, FILETIME *start, FILETIME *now)", "NssmHookTests.set_hook_runtime_uses_nonnegative_milliseconds")]
    public static void set_hook_runtime(IDictionary<string, string?> environment, string variable, DateTimeOffset? start, DateTimeOffset? now)
    {
        environment[variable] = start.HasValue && now.HasValue && now.Value >= start.Value
            ? Math.Max(0, (long)(now.Value - start.Value).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    [NssmUpstreamFunction("src/hook.cpp", 77, "static void add_thread_handle(hook_thread_t *hook_threads, HANDLE thread_handle, TCHAR *name)", "NssmHookTests.thread_collection_retains_only_running_hooks")]
    public static void add_thread_handle(NssmHookThreads? hookThreads, Task<int> threadHandle, string name)
    {
        if (hookThreads is null) return;
        lock (hookThreads.DataGate) hookThreads.Data.Add(new NssmHookThreadData(name, threadHandle));
    }

    [NssmUpstreamFunction("src/hook.cpp", 97, "bool valid_hook_name(const TCHAR *hook_event, const TCHAR *hook_action, bool quiet)", "NssmHookTests.valid_hook_name_matches_event_action_matrix")]
    public static bool valid_hook_name(string hookEvent, string hookAction, bool quiet)
    {
        string[] validActions;
        if (Equivalent(hookEvent, "Exit")) validActions = ["Post"];
        else if (Equivalent(hookEvent, "Power")) validActions = ["Change", "Resume"];
        else if (Equivalent(hookEvent, "Rotate") || Equivalent(hookEvent, "Start")) validActions = ["Pre", "Post"];
        else if (Equivalent(hookEvent, "Stop")) validActions = ["Pre"];
        else
        {
            if (!quiet)
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_INVALID_HOOK_EVENT"));
                foreach (var item in new[] { "Exit", "Power", "Rotate", "Start", "Stop" }) Console.Error.WriteLine(item);
            }
            return false;
        }
        if (validActions.Any(item => Equivalent(item, hookAction))) return true;
        if (!quiet)
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_INVALID_HOOK_ACTION"), hookEvent);
            foreach (var item in validActions) Console.Error.WriteLine(item);
        }
        return false;
    }

    [NssmUpstreamFunction("src/hook.cpp", 162, "void await_hook_threads(hook_thread_t *hook_threads, SERVICE_STATUS_HANDLE status_handle, SERVICE_STATUS *status, unsigned long deadline)", "NssmHookTests.thread_collection_retains_only_running_hooks")]
    public static async Task await_hook_threads(NssmHookThreads? hookThreads, uint deadline, CancellationToken cancellationToken = default)
    {
        if (hookThreads is null || hookThreads.NumThreads == 0) return;
        NssmHookThreadData[] current;
        lock (hookThreads.DataGate) current = hookThreads.Data.ToArray();
        var retained = new List<NssmHookThreadData>();
        foreach (var data in current)
        {
            if (deadline != 0)
            {
                try { await data.ThreadHandle.WaitAsync(TimeSpan.FromMilliseconds(deadline), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { retained.Add(data); }
            }
            else if (!data.ThreadHandle.IsCompleted) retained.Add(data);
        }
        lock (hookThreads.DataGate)
        {
            var addedDuringWait = hookThreads.Data.Skip(current.Length).ToArray();
            hookThreads.Data.Clear();
            hookThreads.Data.AddRange(retained);
            hookThreads.Data.AddRange(addedDuringWait);
        }
    }

    [NssmUpstreamFunction("src/hook.cpp", 225, "int nssm_hook(hook_thread_t *hook_threads, nssm_service_t *service, TCHAR *hook_event, TCHAR *hook_action, unsigned long *hook_control, unsigned long deadline, bool async)", "NssmHookTests.nssm_hook_sets_version_one_environment_and_status")]
    public static async Task<int> nssm_hook(NssmHookThreads? hookThreads, NssmHookServiceContext? context, string hookEvent, string hookAction, string? hookControl, uint deadline, bool async, CancellationToken cancellationToken = default)
    {
        if (hookThreads is null) return await nssm_hook_core(null, context, hookEvent, hookAction, hookControl, deadline, async, cancellationToken).ConfigureAwait(false);
        await hookThreads.ExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await nssm_hook_core(hookThreads, context, hookEvent, hookAction, hookControl, deadline, async, cancellationToken).ConfigureAwait(false); }
        finally { hookThreads.ExecutionGate.Release(); }
    }

    private static async Task<int> nssm_hook_core(NssmHookThreads? hookThreads, NssmHookServiceContext? context, string hookEvent, string hookAction, string? hookControl, uint deadline, bool async, CancellationToken cancellationToken)
    {
        if (context is null) return HookStatusError;
        var hook = context.Service.Hooks.FirstOrDefault(item => Equivalent(item.Event, hookEvent) && Equivalent(item.Action, hookAction));
        if (hook is null || hook.Command.Length == 0) return HookStatusNotFound;

        var now = DateTimeOffset.UtcNow;
        var serviceEnvironment = NativeChildProcess.BuildEnvironmentDictionary(context.Service);
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = NativeChildProcess.ResolveWorkingDirectory(
                context.Service,
                NativeChildProcess.Expand(context.Service.Application, serviceEnvironment),
                serviceEnvironment)
        };
        info.ArgumentList.Add("/d");
        info.ArgumentList.Add("/s");
        info.ArgumentList.Add("/c");
        info.Environment.Clear();
        foreach (var pair in serviceEnvironment) info.Environment[pair.Key] = pair.Value;
        info.Environment["NSSM_HOOK_VERSION"] = "1";
        info.Environment["NSSM_EVENT"] = hookEvent;
        info.Environment["NSSM_ACTION"] = hookAction;
        info.Environment["NSSM_TRIGGER"] = hookControl ?? string.Empty;
        info.Environment["NSSM_LAST_CONTROL"] = context.LastControl;
        info.Environment["NSSM_EXE"] = NssmCore.nssm_unquoted_imagepath();
        info.Environment["NSSM_CONFIGURATION"] = "64-bit";
        info.Environment["NSSM_VERSION"] = "2.24-101-g897c7ad";
        info.Environment["NSSM_BUILD_DATE"] = "2017-04-26";
        info.Environment["NSSM_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        set_hook_runtime(info.Environment, "NSSM_RUNTIME", context.NssmCreationTime, now);
        var applicationRunning = context.ApplicationProcess is { HasExited: false };
        info.Environment["NSSM_APPLICATION_PID"] = applicationRunning ? context.ApplicationProcess!.Id.ToString(CultureInfo.InvariantCulture) : string.Empty;
        if (applicationRunning)
        {
            set_hook_runtime(info.Environment, "NSSM_APPLICATION_RUNTIME", context.ApplicationCreationTime, now);
            info.Environment["NSSM_EXITCODE"] = string.Empty;
        }
        else if (Equivalent(hookEvent, "Start") && Equivalent(hookAction, "Pre"))
        {
            info.Environment["NSSM_APPLICATION_RUNTIME"] = string.Empty;
            info.Environment["NSSM_EXITCODE"] = string.Empty;
        }
        else
        {
            set_hook_runtime(info.Environment, "NSSM_APPLICATION_RUNTIME", context.ApplicationCreationTime, context.ApplicationExitTime);
            info.Environment["NSSM_EXITCODE"] = context.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
        info.Environment["NSSM_DEADLINE"] = deadline.ToString(CultureInfo.InvariantCulture);
        info.Environment["NSSM_SERVICE_NAME"] = context.Service.Name;
        info.Environment["NSSM_SERVICE_DISPLAYNAME"] = context.Service.DisplayName;
        info.Environment["NSSM_START_REQUESTED_COUNT"] = context.StartRequestedCount.ToString(CultureInfo.InvariantCulture);
        info.Environment["NSSM_START_COUNT"] = context.StartCount.ToString(CultureInfo.InvariantCulture);
        info.Environment["NSSM_EXIT_COUNT"] = context.ExitCount.ToString(CultureInfo.InvariantCulture);
        info.Environment["NSSM_THROTTLE_COUNT"] = context.ThrottleCount.ToString(CultureInfo.InvariantCulture);
        info.Environment["NSSM_COMMAND_LINE"] = $"\"{NativeChildProcess.Expand(context.Service.Application, serviceEnvironment)}\" {NativeChildProcess.Expand(context.Service.AppParameters, serviceEnvironment)}";
        var expandedCommand = Expand(hook.Command, info.Environment);
        info.ArgumentList.Add(expandedCommand);
        context.ConfigureOutput?.Invoke(info);

        Process? process;
        try
        {
            process = context.StartHook is null
                ? NativeChildProcess.StartHook(expandedCommand, info.WorkingDirectory,
                    new Dictionary<string, string?>(info.Environment, StringComparer.OrdinalIgnoreCase), null, false)
                : context.StartHook(expandedCommand, info.WorkingDirectory,
                    new Dictionary<string, string?>(info.Environment, StringComparer.OrdinalIgnoreCase));
        }
        catch { return HookStatusNotRun; }
        if (process is null) return HookStatusNotRun;
        var pumps = context.PumpOutput?.Invoke(process, cancellationToken) ?? Task.CompletedTask;
        var invocation = new NssmHookInvocation
        {
            Name = $"{context.Service.Name} ({hookEvent}/{hookAction})",
            Process = process,
            Deadline = deadline,
            OutputPumps = pumps
        };
        var task = await_hook(invocation, CancellationToken.None);
        if (async)
        {
            await await_hook_threads(hookThreads, 0, cancellationToken).ConfigureAwait(false);
            add_thread_handle(hookThreads, task, invocation.Name);
            return HookStatusSuccess;
        }
        return await task.ConfigureAwait(false);
    }

    [NssmUpstreamFunction("src/hook.cpp", 404, "int nssm_hook(hook_thread_t *hook_threads, nssm_service_t *service, TCHAR *hook_event, TCHAR *hook_action, unsigned long *hook_control, unsigned long deadline)", "NssmHookTests.nssm_hook_sets_version_one_environment_and_status")]
    public static Task<int> nssm_hook(NssmHookThreads? hookThreads, NssmHookServiceContext? context, string hookEvent, string hookAction, string? hookControl, uint deadline, CancellationToken cancellationToken = default) =>
        nssm_hook(hookThreads, context, hookEvent, hookAction, hookControl, deadline, true, cancellationToken);

    [NssmUpstreamFunction("src/hook.cpp", 408, "int nssm_hook(hook_thread_t *hook_threads, nssm_service_t *service, TCHAR *hook_event, TCHAR *hook_action, unsigned long *hook_control)", "NssmHookTests.nssm_hook_sets_version_one_environment_and_status")]
    public static Task<int> nssm_hook(NssmHookThreads? hookThreads, NssmHookServiceContext? context, string hookEvent, string hookAction, string? hookControl, CancellationToken cancellationToken = default) =>
        nssm_hook(hookThreads, context, hookEvent, hookAction, hookControl, HookDeadline, cancellationToken);

    private static bool Equivalent(string left, string right) => NssmCore.str_equiv(left, right) != 0;

    private static void ApplyEnvironment(IDictionary<string, string?> environment, IEnumerable<string> values, bool clear)
    {
        if (clear) environment.Clear();
        foreach (var item in values)
        {
            var separator = item.Length > 0 && item[0] == '=' ? item.IndexOf('=', 1) : item.IndexOf('=');
            if (separator > 0) environment[item[..separator]] = Expand(item[(separator + 1)..], environment);
        }
    }

    private static string Expand(string value, IDictionary<string, string?> environment)
    {
        var output = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var opening = value.IndexOf('%', index);
            if (opening < 0) { output.Append(value, index, value.Length - index); break; }
            var closing = value.IndexOf('%', opening + 1);
            if (closing < 0) { output.Append(value, index, value.Length - index); break; }
            output.Append(value, index, opening - index);
            var name = value[(opening + 1)..closing];
            if (environment.TryGetValue(name, out var expanded)) output.Append(expanded);
            else output.Append(value, opening, closing - opening + 1);
            index = closing + 1;
        }
        return output.ToString();
    }
}
