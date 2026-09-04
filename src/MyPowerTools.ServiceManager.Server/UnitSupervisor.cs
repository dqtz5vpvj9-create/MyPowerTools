using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Manages the lifecycle of a single Service Unit process.
///
/// Independence guarantees:
/// - The supervised process is started WITHOUT a job object, so it survives the
///   ServiceManager process exiting. The manager re-adopts it on next start via
///   <see cref="TryReadopt"/>.
/// - Only an explicit Stop/Disable/Upgrade/Uninstall ends the process; a crash
///   triggers restart-policy-driven recovery instead.
/// </summary>
public sealed class UnitSupervisor : IAsyncDisposable
{
    private ServiceUnitManifest _manifest;
    private readonly UnitEventBus _events;
    private readonly UnitLogStore _logs;
    private readonly UnitStateStore _stateStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ServiceUnitState _state = ServiceUnitState.Inactive;
    private Process? _process;
    private DateTimeOffset? _startedAt;
    private int _restartCount;
    private string? _lastError;
    private int? _exitCode;
    private bool _stopping; // explicit stop in progress; suppress auto-restart
    private bool _disposed;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _restartGeneration;
    private Task _scheduledRestart = Task.CompletedTask;

    public UnitSupervisor(ServiceUnitManifest manifest, UnitEventBus events, UnitStateStore stateStore)
    {
        _manifest = manifest;
        _events = events;
        _stateStore = stateStore;
        _logs = new UnitLogStore();
    }

    public string UnitId => _manifest.Id;
    public string ToolId => _manifest.ToolId;
    public ServiceUnitManifest Manifest => _manifest;

    public ServiceUnitSnapshot Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        TimeSpan? uptime = _startedAt is null ? null : now - _startedAt.Value;
        var readiness = ComputeReadiness();
        return new ServiceUnitSnapshot(
            Id: _manifest.Id,
            ToolId: _manifest.ToolId,
            DisplayName: _manifest.DisplayName,
            State: _state,
            Pid: _process is { HasExited: false } p ? p.Id : null,
            StartedAt: _startedAt,
            Uptime: uptime,
            Version: "",
            Autostart: _manifest.Autostart,
            RestartPolicy: _manifest.EffectiveRestartPolicy,
            RestartCount: _restartCount,
            LastError: _lastError,
            Readiness: readiness,
            ExitCode: _exitCode,
            EventSeq: _events.CurrentSeq);
    }

    public IReadOnlyList<MptToolLogEntry> TailLogs(int count) => _logs.Tail(count);

    /// <summary>
    /// Replaces the unit definition while holding the same lifecycle gate used by Start/Stop.
    /// A live unit is stopped with its old definition and then relaunched with the new one, so
    /// an installer reload cannot leave an old executable or readiness endpoint active.
    /// </summary>
    public async Task<ServiceUnitSnapshot> ApplyManifestAsync(
        ServiceUnitManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(manifest.Id, UnitId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Manifest '{manifest.Id}' cannot replace supervisor '{UnitId}'.",
                nameof(manifest));
        }

        CancelScheduledRestart();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var wasRunning = _process is { HasExited: false } ||
                             _state is ServiceUnitState.Active or ServiceUnitState.Degraded or ServiceUnitState.Activating;
            if (wasRunning)
            {
                await StopCoreAsync();
            }

            _manifest = manifest;
            _restartCount = 0;
            _lastError = null;
            _exitCode = null;
            Transition(ServiceUnitState.Inactive, "manifest updated");

            return wasRunning
                ? await StartCoreAsync(cancellationToken)
                : Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Launches the unit process if inactive. Idempotent: an already-active unit keeps its PID.
    /// </summary>
    public async Task<ServiceUnitSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await StartCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Graceful stop (CTRL-C/break wait) then forceful kill within stop_timeout.
    /// Sets the stopping flag so the Exited handler does not auto-restart.
    /// </summary>
    public async Task<ServiceUnitSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        CancelScheduledRestart();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceUnitSnapshot> RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        _restartCount++;
        return await StartAsync(cancellationToken);
    }

    /// <summary>
    /// Attempts to re-adopt a previously-running process using persisted PID + instance token.
    /// Returns true if a live process was found and adopted without restarting it.
    /// </summary>
    public async Task<bool> TryReadoptAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_process is { HasExited: false })
            {
                return true;
            }

            return await TryReadoptCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryReadoptCoreAsync(CancellationToken cancellationToken)
    {
        var persisted = _stateStore.Load(UnitId);
        var matchingProcesses = UnitProcessDiscovery.FindMatching(_manifest);

        if (persisted is not null &&
            !string.IsNullOrWhiteSpace(persisted.InstanceToken) &&
            !string.Equals(persisted.InstanceToken, _manifest.InstanceToken, StringComparison.Ordinal))
        {
            await TerminateDuplicatesAsync(matchingProcesses, keeper: null);
            ClearPersistedState();
            return false;
        }

        Process? candidate = null;
        if (persisted is { Pid: > 0 })
        {
            try
            {
                var persistedCandidate = Process.GetProcessById(persisted.Pid);
                if (!persistedCandidate.HasExited && ProcessIdentityMatches(persistedCandidate, persisted))
                {
                    candidate = matchingProcesses.FirstOrDefault(process => process.Id == persistedCandidate.Id);
                    if (candidate is not null)
                    {
                        persistedCandidate.Dispose();
                    }
                    else
                    {
                        candidate = persistedCandidate;
                    }
                }
                else
                {
                    persistedCandidate.Dispose();
                }
            }
            catch (ArgumentException)
            {
                // The recorded PID has exited. Command discovery below can still recover a unit
                // whose state write was lost or replaced during an older manager restart.
            }
            catch (Exception)
            {
                // Treat inaccessible or stale state as a discovery miss.
            }
        }

        candidate ??= matchingProcesses
            .Where(IsAlive)
            .OrderBy(TryGetStartTime)
            .FirstOrDefault();

        if (candidate is null)
        {
            DisposeProcesses(matchingProcesses, keeper: null);
            return false;
        }

        await TerminateDuplicatesAsync(matchingProcesses, candidate);

        _process = candidate;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _startedAt = persisted?.Pid == candidate.Id ? persisted.StartedAt : TryGetStartTime(candidate);
        _restartCount = persisted?.Pid == candidate.Id ? persisted.RestartCount : 0;
        PersistState();
        CaptureExistingStreamIfPossible();
        var readiness = await WaitForReadinessAsync(cancellationToken);
        if (candidate.HasExited)
        {
            return true;
        }

        if (readiness.Ready)
        {
            _lastError = null;
            Transition(ServiceUnitState.Active, $"re-adopted pid {candidate.Id}; {readiness.Message}");
        }
        else
        {
            _lastError = readiness.Message;
            Transition(ServiceUnitState.Degraded, $"re-adopted pid {candidate.Id}; {readiness.Message}");
        }
        return true;
    }

    private async Task<ServiceUnitSnapshot> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
        {
            return Snapshot();
        }

        _stopping = false;
        Transition(ServiceUnitState.Activating, "start requested");

        var psi = new ProcessStartInfo
        {
            FileName = _manifest.Exec,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(_manifest.WorkingDirectory)
                ? Environment.CurrentDirectory
                : _manifest.WorkingDirectory
        };

        foreach (var arg in _manifest.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (_manifest.Environment is not null)
        {
            foreach (var (key, value) in _manifest.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        if (_manifest.Environment is not null &&
            _manifest.Environment.TryGetValue("MPT_INSTALL_ROOT", out var installRoot) &&
            !string.IsNullOrWhiteSpace(installRoot))
        {
            DotNetRuntimeEnvironment.ConfigureChildProcess(psi, installRoot);
        }
        else
        {
            // A Service Unit must never inherit an account-scoped runtime override.
            psi.Environment.Remove(DotNetRuntimeEnvironment.VariableName);
        }

        try
        {
            var launched = BreakawayProcessStarter.Start(psi);
            _process = launched.Process;
            if (launched.StandardOutput is not null)
            {
                CaptureStream(launched.StandardOutput, "stdout");
            }

            if (launched.StandardError is not null)
            {
                CaptureStream(launched.StandardError, "stderr");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ServiceManager] unit '{UnitId}' failed to start: {ex}");
            _lastError = ex.Message;
            Transition(ServiceUnitState.Failed, $"failed to start: {ex.Message}");
            return Snapshot();
        }

        _startedAt = DateTimeOffset.UtcNow;
        _exitCode = null;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        PersistState();
        var readiness = await WaitForReadinessAsync(cancellationToken);
        if (_process.HasExited)
        {
            return Snapshot();
        }

        if (readiness.Ready)
        {
            _lastError = null;
            Transition(ServiceUnitState.Active, $"started pid {_process.Id}; {readiness.Message}");
        }
        else
        {
            _lastError = readiness.Message;
            Transition(ServiceUnitState.Degraded, $"started pid {_process.Id}; {readiness.Message}");
        }
        return Snapshot();
    }

    private async Task<ServiceUnitSnapshot> StopCoreAsync()
    {
        if (_process is null || _process.HasExited)
        {
            CancelScheduledRestart();
            _stopping = false;
            ClearPersistedState();
            Transition(ServiceUnitState.Inactive, "already stopped");
            return Snapshot();
        }

        _stopping = true;
        Transition(ServiceUnitState.Deactivating, "stop requested");

        try
        {
            _process.Exited -= OnProcessExited;
        }
        catch
        {
            // Event unsubscribe is best-effort.
        }

        try
        {
            // Units are launched with CREATE_NO_WINDOW, so they are not attached to this process's
            // console and GenerateConsoleCtrlEvent cannot reach them — the console-signal attempt
            // that used to run here always returned FALSE. Delivering CTRL_BREAK for real would
            // mean AttachConsole/SetConsoleCtrlHandler/FreeConsole around every stop, tearing the
            // manager's own console state down and back up; CloseMainWindow plus the manifest's
            // stop timeout is the simpler correct behaviour. The grace period is always
            // StopTimeout: a unit that declares 30 s must get 30 s before it is killed.
            _process.CloseMainWindow();

            if (!await WaitForExitAsync(_process, _manifest.StopTimeout))
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }

        _exitCode = TryGetExitCode(_process);
        ClearPersistedState();
        CancelScheduledRestart();
        _stopping = false;
        Transition(ServiceUnitState.Inactive, "stopped");
        return Snapshot();
    }

    private async Task<(bool Ready, string Message)> WaitForReadinessAsync(CancellationToken cancellationToken)
    {
        var readiness = _manifest.EffectiveReadiness;
        if (string.Equals(readiness.Kind, "none", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(readiness.Kind))
        {
            return (true, "process readiness");
        }

        var isPipe = string.Equals(readiness.Kind, "pipe", StringComparison.OrdinalIgnoreCase);
        var isUnixSocket = string.Equals(readiness.Kind, "unix-socket", StringComparison.OrdinalIgnoreCase);
        if (!isPipe && !isUnixSocket)
        {
            return (false, $"unsupported readiness kind '{readiness.Kind}'");
        }

        if (string.IsNullOrWhiteSpace(readiness.Address))
        {
            return (false, "pipe readiness address is empty");
        }

        var timeout = readiness.Timeout > TimeSpan.Zero ? readiness.Timeout : TimeSpan.FromSeconds(5);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Exception? lastError = null;

        while (!deadline.IsCancellationRequested)
        {
            try
            {
                await using var stream = await ConnectReadinessStreamAsync(
                    readiness.Kind,
                    readiness.Address,
                    deadline.Token);

                var payload = JsonSerializer.SerializeToUtf8Bytes(new { command = "ping" });
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await stream.WriteAsync(header, deadline.Token);
                await stream.WriteAsync(payload, deadline.Token);
                await stream.FlushAsync(deadline.Token);

                await ReadExactlyAsync(stream, header, deadline.Token);
                var responseLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (responseLength <= 0 || responseLength > 1024 * 1024)
                {
                    throw new InvalidDataException($"readiness response length {responseLength} is invalid");
                }

                var responsePayload = new byte[responseLength];
                await ReadExactlyAsync(stream, responsePayload, deadline.Token);
                using var response = JsonDocument.Parse(responsePayload);
                if (response.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                {
                    return (true, $"{readiness.Kind} '{readiness.Address}' ready");
                }

                throw new InvalidDataException("readiness response did not report ok=true");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (false, $"{readiness.Kind} '{readiness.Address}' readiness timed out after {timeout.TotalMilliseconds:0} ms: {lastError?.Message ?? "no response"}");
    }

    private static async Task<Stream> ConnectReadinessStreamAsync(
        string kind,
        string address,
        CancellationToken cancellationToken)
    {
        if (string.Equals(kind, "unix-socket", StringComparison.OrdinalIgnoreCase))
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(address), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        var pipe = new NamedPipeClientStream(
            ".",
            address,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("readiness pipe closed before the complete response arrived");
            }

            offset += read;
        }
    }

    private bool ProcessIdentityMatches(Process candidate, UnitRuntimeState persisted)
    {
        if (DateTimeOffset.TryParse(
                persisted.ProcessStartTimeIso,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expectedStart))
        {
            try
            {
                var actualStart = candidate.StartTime.ToUniversalTime();
                if (Math.Abs((actualStart - expectedStart.UtcDateTime).TotalSeconds) > 1)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        // A manifest that names its executable without a directory cannot be verified against a
        // running image — UnitProcessDiscovery declines those outright — but the persisted pid, its
        // start time and the instance token already identify the process on this path.
        return !Path.IsPathRooted(_manifest.Exec) || UnitProcessDiscovery.Matches(candidate, _manifest);
    }

    private async Task TerminateDuplicatesAsync(IReadOnlyList<Process> processes, Process? keeper)
    {
        foreach (var duplicate in processes.Where(process => process.Id != keeper?.Id))
        {
            try
            {
                if (!duplicate.HasExited)
                {
                    Console.WriteLine($"[ServiceManager] unit '{UnitId}' retiring duplicate pid {duplicate.Id}.");
                    duplicate.Kill(entireProcessTree: true);
                    await WaitForExitAsync(duplicate, _manifest.StopTimeout);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[ServiceManager] unit '{UnitId}' could not retire duplicate pid {duplicate.Id}: {ex.Message}");
            }
        }

        DisposeProcesses(processes, keeper);
    }

    private static void DisposeProcesses(IReadOnlyList<Process> processes, Process? keeper)
    {
        foreach (var process in processes)
        {
            if (!ReferenceEquals(process, keeper))
            {
                process.Dispose();
            }
        }
    }

    private static DateTimeOffset TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        if (!process.HasExited)
        {
            return null;
        }

        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // Processes re-adopted after a manager restart were not started by this
            // Process instance, so the runtime refuses to surface their exit code.
            return null;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = sender as Process ?? _process;
        if (process is null)
        {
            return;
        }

        _exitCode = TryGetExitCode(process);

        if (_stopping || _disposed)
        {
            ClearPersistedState();
            return;
        }

        // Unexpected exit — apply restart policy.
        var policy = _manifest.EffectiveRestartPolicy;
        if (_restartCount > 0 &&
            _startedAt is { } startedAt &&
            DateTimeOffset.UtcNow - startedAt >= HealthyWindowFor(policy))
        {
            // The unit held up for a full start-limit window, so this exit opens a new failure
            // burst instead of spending a budget that an unrelated crash consumed hours ago.
            _restartCount = 0;
        }

        if (_restartCount >= policy.MaxRestarts)
        {
            _lastError = $"process exited (code {_exitCode}); restart limit reached ({policy.MaxRestarts})";
            ClearPersistedState();
            Transition(ServiceUnitState.Failed, _lastError);
            return;
        }

        _lastError = $"process exited (code {_exitCode}); scheduling restart {_restartCount + 1}/{policy.MaxRestarts}";
        Transition(ServiceUnitState.Degraded, _lastError);
        var restartGeneration = Volatile.Read(ref _restartGeneration);
        _scheduledRestart = ScheduleRestartAsync(
            BackoffFor(policy, _restartCount),
            restartGeneration,
            _lifetimeCancellation.Token);
    }

    /// <summary>Lower bound for the start-limit window, used when the policy backoff is short.</summary>
    private static readonly TimeSpan MinimumHealthyWindow = TimeSpan.FromSeconds(60);

    /// <summary>Ceiling for the exponentially growing restart backoff.</summary>
    private static readonly TimeSpan MaximumRestartBackoff = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a unit must stay up before its accumulated restart count is forgiven. Without this
    /// the count only ever grows, so a unit that crashed <c>maxRestarts</c> times over its whole
    /// lifetime is never restarted again.
    /// </summary>
    internal static TimeSpan HealthyWindowFor(ServiceUnitRestartPolicy policy)
    {
        var scaled = policy.Backoff > TimeSpan.Zero ? policy.Backoff * 10 : TimeSpan.Zero;
        return scaled > MinimumHealthyWindow ? scaled : MinimumHealthyWindow;
    }

    /// <summary>
    /// Delay before restart attempt number <paramref name="restartCount"/>, doubling per attempt
    /// from the policy backoff so a unit that fails on every launch stops hammering the machine.
    /// </summary>
    internal static TimeSpan BackoffFor(ServiceUnitRestartPolicy policy, int restartCount)
    {
        if (policy.Backoff <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var backoff = policy.Backoff * (1 << Math.Clamp(restartCount, 0, 8));
        return backoff > MaximumRestartBackoff ? MaximumRestartBackoff : backoff;
    }

    private async Task ScheduleRestartAsync(
        TimeSpan backoff,
        long restartGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (backoff > TimeSpan.Zero)
            {
                await Task.Delay(backoff, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed ||
                _stopping ||
                restartGeneration != Volatile.Read(ref _restartGeneration))
            {
                return;
            }

            _restartCount++;
            await StartAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Explicit stop, manifest replacement, removal, or manager disposal cancels stale restarts.
        }
        catch (Exception ex)
        {
            _lastError = $"restart failed: {ex.Message}";
            Transition(ServiceUnitState.Failed, _lastError);
        }
    }

    private void Transition(ServiceUnitState next, string reason)
    {
        _state = next;
        var payload = new JsonObject
        {
            ["state"] = next.ToString().ToLowerInvariant(),
            ["reason"] = reason,
            ["pid"] = _process is { HasExited: false } p ? p.Id : 0
        };
        _events.Publish(UnitId, "state-change", payload);
    }

    private ServiceUnitReadiness ComputeReadiness()
    {
        if (_state != ServiceUnitState.Active && _state != ServiceUnitState.Degraded)
        {
            return new ServiceUnitReadiness("none", null, TimeSpan.Zero);
        }

        var probe = _manifest.EffectiveReadiness;
        if (string.Equals(probe.Kind, "none", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(probe.Kind))
        {
            // No probe configured: a live process is considered ready.
            return new ServiceUnitReadiness("none", null, TimeSpan.Zero) with { };
        }

        return probe;
    }

    private void CaptureStream(StreamReader reader, string stream)
    {
        _ = Task.Run(() =>
        {
            try
            {
                while (!_disposed)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        break;
                    }

                    var level = string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase) ? "warn" : "info";
                    _logs.Append(level, UnitId, line, stream);
                }
            }
            catch
            {
                // Stream capture must never crash the supervisor.
            }
        });
    }

    private void CaptureExistingStreamIfPossible()
    {
        // For re-adopted processes we cannot redirect already-started stdout/stderr;
        // log capture resumes only for freshly started processes.
    }

    private void PersistState()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        var state = new UnitRuntimeState(
            UnitId,
            _process.Id,
            _manifest.InstanceToken,
            _startedAt ?? DateTimeOffset.UtcNow,
            _restartCount,
            _process.StartTime.ToUniversalTime().ToString("O"));
        _stateStore.Save(state);
    }

    private void ClearPersistedState() => _stateStore.Delete(UnitId);

    private void CancelScheduledRestart()
    {
        Interlocked.Increment(ref _restartGeneration);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>
    /// Releases the manager's hold on the supervised process WITHOUT stopping it.
    /// A ServiceManager shutdown (or restart) must never kill still-running units — that is the
    /// core independence guarantee. The process is left alive so the next ServiceManager instance
    /// can re-adopt it via <see cref="TryReadoptAsync"/>. Only an explicit Stop/Disable/Upgrade/Uninstall
    /// ends the process; call <see cref="StopAsync"/> directly for that.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        CancelScheduledRestart();
        _lifetimeCancellation.Cancel();
        try
        {
            if (_process is not null)
            {
                _process.Exited -= OnProcessExited;
            }
        }
        catch
        {
            // event detach is best-effort
        }

        try
        {
            await _scheduledRestart;
        }
        catch (OperationCanceledException)
        {
        }

        _lifetimeCancellation.Dispose();
        _gate.Dispose();
    }
}
