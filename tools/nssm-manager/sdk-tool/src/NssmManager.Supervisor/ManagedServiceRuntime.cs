using System.Diagnostics;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Windows;

namespace NssmManager.Supervisor;

public sealed class ManagedServiceRuntime : INssmServiceRuntime
{
    private const uint SkipConsole = 1;
    private const uint SkipWindow = 2;
    private const uint SkipThreads = 4;
    private const uint SkipTerminate = 8;
    private readonly NssmServiceConfiguration _configuration;
    private readonly CancellationTokenSource _stopping = new();
    private readonly NssmHookThreads _hookThreads = new();
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly object _throttleGate = new();
    private readonly TaskCompletionSource _stopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _runtimeStarted = DateTimeOffset.UtcNow;
    private TaskCompletionSource? _throttleWake;
    private Process? _process;
    private NativeChildProcessIo? _processIo;
    private uint _lastExitCode;
    private uint _startRequestedCount;
    private uint _startCount;
    private uint _exitCount;
    private uint _throttleCount;
    private DateTimeOffset? _applicationStarted;
    private DateTimeOffset? _applicationExited;
    private string _lastControl = "START";
    private int _stopStarted;
    private int _preStopStarted;
    private int _endStarted;
    private bool _lastExitActionWasDefault;
    private bool _disposed;

    public event Action<NssmRuntimeStatus>? StatusChanged;

    public ManagedServiceRuntime(string serviceName) : this(new NssmRegistryStore().Read(serviceName)) { }
    public ManagedServiceRuntime(NssmServiceConfiguration configuration) => _configuration = configuration;
    public Task Started => _started.Task;

    public Task<int> RunAsync(CancellationToken cancellationToken) => launch_service(cancellationToken);

    [NssmUpstreamFunction("src/service.cpp", 319, "static unsigned long WINAPI launch_service(void *arg)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal Task<int> launch_service(CancellationToken cancellationToken) => monitor_service(cancellationToken);

    [NssmUpstreamFunction("src/service.cpp", 1655, "int monitor_service(nssm_service_t *service)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task<int> monitor_service(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        while (!linked.IsCancellationRequested)
        {
            await throttle_restart(linked.Token).ConfigureAwait(false);
            try
            {
                await _processGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (linked.IsCancellationRequested) break;
                    await start_service(linked.Token).ConfigureAwait(false);
                }
                finally { _processGate.Release(); }
                await _process!.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
            catch (NssmServiceStartException exception)
            {
                if (_startCount == 0) return exception.ExitCode;
                await Task.Delay(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false);
                continue;
            }
            catch (FileNotFoundException)
            {
                if (_startCount == 0) return 2;
                await Task.Delay(TimeSpan.FromSeconds(30), linked.Token).ConfigureAwait(false);
                continue;
            }
            var exitCode = _process?.HasExited == true ? unchecked((uint)_process.ExitCode) : 0u;
            var action = await end_service(exitCode, controlled: false).ConfigureAwait(false);
            if (linked.IsCancellationRequested) { if (_stopping.IsCancellationRequested) await _stopCompleted.Task.ConfigureAwait(false); return 0; }
            switch (action)
            {
                case NssmExitAction.Restart:
                    continue;
                case NssmExitAction.Ignore:
                    await wait_for_hooks(false).ConfigureAwait(false);
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { return 0; }
                    break;
                case NssmExitAction.Suicide:
                    await wait_for_hooks(false).ConfigureAwait(false);
                    if (_lastExitActionWasDefault && exitCode == 0) return 0;
                    throw new NssmServiceSuicideException(unchecked((int)exitCode));
                case NssmExitAction.Exit:
                    await wait_for_hooks(true).ConfigureAwait(false);
                    return unchecked((int)exitCode);
            }
        }
        if (_stopping.IsCancellationRequested) await _stopCompleted.Task.ConfigureAwait(false);
        return 0;
    }

    public Task StopAsync() => shutdown_service();

    public async Task PreStopAsync()
    {
        if (Interlocked.Exchange(ref _preStopStarted, 1) != 0) return;
        _lastControl = "STOP";
        await RunHookAsync("Stop", "Pre", "STOP", CancellationToken.None, 20000, async: false).ConfigureAwait(false);
    }

    [NssmUpstreamFunction("src/service.cpp", 311, "static unsigned long WINAPI shutdown_service(void *arg)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task shutdown_service() => await stop_service(0, true, true).ConfigureAwait(false);

    [NssmUpstreamFunction("src/service.cpp", 1979, "int stop_service(nssm_service_t *service, unsigned long exitcode, bool graceful, bool default_action)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task<int> stop_service(uint exitCode, bool graceful, bool defaultAction)
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0) { await _stopCompleted.Task.ConfigureAwait(false); return unchecked((int)exitCode); }
        var gateHeld = false;
        using var statusPulse = new CancellationTokenSource();
        var pulse = PulseStopStatusAsync(statusPulse.Token);
        try
        {
            await PreStopAsync().ConfigureAwait(false);
            _stopping.Cancel();
            await _processGate.WaitAsync().ConfigureAwait(false);
            gateHeld = true;
            var process = _process;
            if (process is null)
            {
                await wait_for_hooks(true).ConfigureAwait(false);
                return unchecked((int)exitCode);
            }
            if (process.HasExited)
            {
                await end_service(unchecked((uint)process.ExitCode), controlled: true).ConfigureAwait(false);
                return unchecked((int)exitCode);
            }
            var context = CreateKillContext(process, 0);
            _ = NssmProcess.kill_process(context);
            var applicationExitCode = process.HasExited ? unchecked((uint)process.ExitCode) : 259u;
            await end_service(applicationExitCode, controlled: true).ConfigureAwait(false);
            await wait_for_hooks(true).ConfigureAwait(false);
        }
        finally
        {
            statusPulse.Cancel();
            try { await pulse.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _stopping.Cancel();
            if (gateHeld) _processGate.Release();
            _stopCompleted.TrySetResult();
        }
        return unchecked((int)exitCode);
    }

    public Task PauseAsync()
    {
        return Task.CompletedTask;
    }

    public Task ContinueAsync()
    {
        TaskCompletionSource? wake;
        lock (_throttleGate)
        {
            _throttleCount = 0;
            wake = _throttleWake;
        }
        if (wake is not null) StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.ContinuePending, 3000));
        wake?.TrySetResult();
        return Task.CompletedTask;
    }
    public async Task RotateAsync() { _lastControl = "ROTATE"; await RunHookAsync("Rotate", "Pre", "ROTATE", CancellationToken.None, 60000, async: false).ConfigureAwait(false); _processIo?.RequestRotation(); await RunHookAsync("Rotate", "Post", "ROTATE", CancellationToken.None).ConfigureAwait(false); }
    public async Task PowerAsync(bool resume) { _lastControl = "POWEREVENT"; await RunHookAsync("Power", resume ? "Resume" : "Change", "POWEREVENT", CancellationToken.None).ConfigureAwait(false); }

    [NssmUpstreamFunction("src/service.cpp", 1834, "int start_service(nssm_service_t *service)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task start_service(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _endStarted, 0);
        _startRequestedCount++;
        var environment = NativeChildProcess.BuildEnvironmentDictionary(_configuration);
        var application = NativeChildProcess.Expand(_configuration.Application, environment);
        NssmConsole.alloc_console(_configuration);
        try
        {
            _processIo = NativeChildProcessIo.Create(_configuration, environment);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new NssmServiceStartException(4, exception);
        }
        finally { NssmConsole.free_console(); }
        StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.StartPending, _configuration.ThrottleDelayMilliseconds + 2000));
        if (await RunHookAsync("Start", "Pre", "START", CancellationToken.None, 20000, async: false).ConfigureAwait(false) == NssmHook.HookStatusAbort)
        {
            await _processIo.DisposeAsync().ConfigureAwait(false);
            _processIo = null;
            throw new NssmServiceStartException(5, new InvalidOperationException("Start/Pre hook requested service start abort (99)."));
        }
        try
        {
            var workingDirectory = NativeChildProcess.ResolveWorkingDirectory(_configuration, application, environment);
            _process = NativeChildProcess.Start(_configuration, application, workingDirectory, _processIo, environment);
        }
        catch (Exception exception)
        {
            await _processIo.DisposeAsync().ConfigureAwait(false);
            _processIo = null;
            throw new NssmServiceStartException(3, exception);
        }
        _applicationStarted = DateTimeOffset.UtcNow;
        _startCount++;
        _started.TrySetResult();
        if (await await_single_handle(_process, _configuration.ThrottleDelayMilliseconds).ConfigureAwait(false) == 1) _throttleCount = 0;
        StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.Running, 0));
        if (_throttleCount == 0) await RunHookAsync("Start", "Post", "START", CancellationToken.None).ConfigureAwait(false);
        if (_configuration.RestartDelayMilliseconds > 0 && _throttleCount == 0) _throttleCount++;
    }

    public static ulong ParseAffinity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("All", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Default", StringComparison.OrdinalIgnoreCase) || value == "*") return 0;
        ulong mask = 0;
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = item.Split('-', 2);
            if (!int.TryParse(bounds[0], out var first) || first is < 0 or > 63) throw new ArgumentException($"Invalid affinity '{value}'.");
            var last = bounds.Length == 1 ? first : int.TryParse(bounds[1], out var parsed) ? parsed : -1;
            if (last < first || last > 63) throw new ArgumentException($"Invalid affinity '{value}'.");
            for (var cpu = first; cpu <= last; cpu++) mask |= 1UL << cpu;
        }
        return mask;
    }

    private Task<int> RunHookAsync(string eventName, string actionName, string trigger, CancellationToken cancellationToken, int deadlineMilliseconds = 60000, bool async = true)
    {
        var context = new NssmHookServiceContext
        {
            Service = _configuration,
            ApplicationProcess = _process,
            LastControl = _lastControl,
            NssmCreationTime = _runtimeStarted,
            ApplicationCreationTime = _applicationStarted,
            ApplicationExitTime = _applicationExited,
            ExitCode = _lastExitCode,
            StartRequestedCount = _startRequestedCount,
            StartCount = _startCount,
            ExitCount = _exitCount,
            ThrottleCount = _throttleCount,
            StartHook = (command, directory, environment) => NativeChildProcess.StartHook(command, directory, environment, _processIo, _configuration.RedirectHookOutput)
        };
        return NssmHook.nssm_hook(_hookThreads, context, eventName, actionName, trigger, checked((uint)deadlineMilliseconds), async, cancellationToken);
    }

    [NssmUpstreamFunction("src/service.cpp", 2033, "void CALLBACK end_service(void *arg, unsigned char why)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task<NssmExitAction> end_service(uint exitCode, bool controlled)
    {
        var actionExitCode = exitCode == 259 ? 0u : exitCode;
        var rule = _configuration.ExitRules.FirstOrDefault(candidate => candidate.ExitCode == actionExitCode);
        _lastExitActionWasDefault = rule is null;
        var action = rule?.Action ?? _configuration.DefaultExitAction;
        if (Interlocked.Exchange(ref _endStarted, 1) != 0) return action;
        _lastExitCode = exitCode;
        var process = _process;
        _applicationExited = process is { HasExited: true } ? process.ExitTime.ToUniversalTime() : DateTimeOffset.UtcNow;
        _process = null;
        if (process is not null)
        {
            if (_configuration.KillProcessTree)
            {
                var context = CreateKillContext(process, actionExitCode);
                NssmProcess.kill_process_tree(context, unchecked((uint)process.Id));
            }
            process.Dispose();
        }
        _exitCount++;
        await RunHookAsync("Exit", "Post", string.Empty, CancellationToken.None, 60000, async: true).ConfigureAwait(false);
        if (_processIo is not null)
        {
            await _processIo.DisposeAsync().ConfigureAwait(false);
            _processIo = null;
        }
        return controlled ? NssmExitAction.Exit : action;
    }

    private NssmKillContext CreateKillContext(Process process, uint exitCode)
    {
        var context = new NssmKillContext
        {
            Name = _configuration.Name,
            ProcessHandle = process.Handle,
            ProcessId = unchecked((uint)process.Id),
            ExitCode = exitCode,
            StopMethod = (SkipConsole | SkipWindow | SkipThreads | SkipTerminate) & ~_configuration.StopMethodSkip,
            KillConsoleDelay = _configuration.StopMethodConsoleMilliseconds,
            KillWindowDelay = _configuration.StopMethodWindowMilliseconds,
            KillThreadsDelay = _configuration.StopMethodThreadsMilliseconds,
            ExitTime = DateTime.UtcNow.ToFileTimeUtc()
        };
        if (NssmProcess.get_process_creation_time(process.Handle, out var creationTime) == 0) context.CreationTime = creationTime;
        if (process.HasExited && NssmProcess.get_process_exit_time(process.Handle, out var processExitTime) == 0) context.ExitTime = processExitTime;
        return context;
    }

    [NssmUpstreamFunction("src/service.cpp", 2137, "void throttle_restart(nssm_service_t *service)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal async Task throttle_restart(CancellationToken cancellationToken)
    {
        var previous = _throttleCount++;
        if (previous == 0) return;
        var throttle = NssmServiceTranslation.throttle_milliseconds(_throttleCount);
        var milliseconds = Math.Max(_configuration.RestartDelayMilliseconds, throttle);
        var wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_throttleGate) _throttleWake = wake;
        StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.Paused, _configuration.ThrottleDelayMilliseconds + 2000));
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromMilliseconds(milliseconds), cancellationToken), wake.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_throttleGate) if (ReferenceEquals(_throttleWake, wake)) _throttleWake = null;
        }
    }

    [NssmUpstreamFunction("src/service.cpp", 2209, "int await_single_handle(SERVICE_STATUS_HANDLE status_handle, SERVICE_STATUS *status, HANDLE handle, TCHAR *name, TCHAR *function_name, unsigned long timeout)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal static async Task<int> await_single_handle(Process process, uint milliseconds)
    {
        try
        {
            if (process.HasExited) return 0;
            if (milliseconds == 0) return 1;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(milliseconds));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { return process.HasExited ? 0 : 1; }
        catch { return -1; }
    }

    [NssmUpstreamFunction("src/service.cpp", 116, "static inline void wait_for_hooks(nssm_service_t *service, bool notify)", "NssmServiceRuntimeTranslationTests.runtime_function_translation_is_wired")]
    internal Task wait_for_hooks(bool notify) => NssmHook.await_hook_threads(_hookThreads, 60000);

    private async Task PulseStopStatusAsync(CancellationToken cancellationToken)
    {
        uint waitHint = NssmServiceTranslation.WaitHintMargin;
        StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.StopPending, waitHint));
        while (true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(NssmServiceTranslation.ServiceStatusDeadline), cancellationToken).ConfigureAwait(false);
            waitHint = unchecked(waitHint + NssmServiceTranslation.ServiceStatusDeadline);
            StatusChanged?.Invoke(new NssmRuntimeStatus(NssmRuntimeState.StopPending, waitHint));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process is not null) await StopAsync().ConfigureAwait(false);
        else
        {
            _stopping.Cancel();
            _stopCompleted.TrySetResult();
        }
        await wait_for_hooks(true).ConfigureAwait(false);
        if (_processIo is not null) await _processIo.DisposeAsync().ConfigureAwait(false);
        _process?.Dispose();
        _processGate.Dispose();
        _stopping.Dispose();
    }
}

internal sealed class NssmServiceStartException(int exitCode, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public int ExitCode { get; } = exitCode;
}
