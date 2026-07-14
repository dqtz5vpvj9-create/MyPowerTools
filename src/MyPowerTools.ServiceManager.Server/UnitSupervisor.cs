using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

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
    private readonly ServiceUnitManifest _manifest;
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
    /// Launches the unit process if inactive. Idempotent: an already-active unit keeps its PID.
    /// </summary>
    public async Task<ServiceUnitSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
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

            // Critical: do NOT assign this process into a job object that kills children on close.
            // The unit must outlive the ServiceManager. We rely on explicit Stop only.
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

            try
            {
                // Launch the unit detached from this process's job object (CREATE_BREAKAWAY_FROM_JOB
                // on Windows) so that force-killing or restarting the ServiceManager never cascades
                // to the unit. This is the concrete realization of the rule that units outlive the
                // ServiceManager; combined with persisted PID + instance token, a new ServiceManager
                // re-adopts the still-running unit rather than restarting it.
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
            Transition(ServiceUnitState.Active, $"started pid {_process.Id}");
            return Snapshot();
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
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is null || _process.HasExited)
            {
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

            // Request graceful termination first.
            try
            {
                _process.CloseMainWindow();
                if (!_process.WaitForExit((int)_manifest.StopTimeout.TotalMilliseconds))
                {
                    _process.Kill(entireProcessTree: false);
                    _process.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }

            _exitCode = _process.HasExited ? _process.ExitCode : null;
            ClearPersistedState();
            _stopping = false;
            Transition(ServiceUnitState.Inactive, "stopped");
            return Snapshot();
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
    public bool TryReadopt()
    {
        var persisted = _stateStore.Load(UnitId);
        if (persisted is null || persisted.Pid <= 0)
        {
            return false;
        }

        Process? candidate = null;
        try
        {
            candidate = Process.GetProcessById(persisted.Pid);
        }
        catch (ArgumentException)
        {
            // Process no longer alive — fall through to normal autostart handling.
        }
        catch (Exception)
        {
            return false;
        }

        if (candidate is null || candidate.HasExited)
        {
            return false;
        }

        // Verify the instance token matches the one the process was launched with.
        if (!string.IsNullOrWhiteSpace(persisted.InstanceToken) &&
            !string.Equals(persisted.InstanceToken, _manifest.InstanceToken, StringComparison.Ordinal))
        {
            return false;
        }

        _process = candidate;
        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;
        _startedAt = persisted.StartedAt;
        _restartCount = persisted.RestartCount;
        CaptureExistingStreamIfPossible();
        Transition(ServiceUnitState.Active, $"re-adopted pid {candidate.Id}");
        return true;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var process = sender as Process ?? _process;
        if (process is null)
        {
            return;
        }

        _exitCode = process.HasExited ? process.ExitCode : null;

        if (_stopping || _disposed)
        {
            ClearPersistedState();
            return;
        }

        // Unexpected exit — apply restart policy.
        var policy = _manifest.EffectiveRestartPolicy;
        if (_restartCount >= policy.MaxRestarts)
        {
            _lastError = $"process exited (code {_exitCode}); restart limit reached ({policy.MaxRestarts})";
            ClearPersistedState();
            Transition(ServiceUnitState.Failed, _lastError);
            return;
        }

        _lastError = $"process exited (code {_exitCode}); scheduling restart {_restartCount + 1}/{policy.MaxRestarts}";
        Transition(ServiceUnitState.Degraded, _lastError);
        _ = ScheduleRestartAsync(policy.Backoff);
    }

    private async Task ScheduleRestartAsync(TimeSpan backoff)
    {
        try
        {
            if (backoff > TimeSpan.Zero)
            {
                await Task.Delay(backoff);
            }

            _restartCount++;
            await StartAsync();
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

    /// <summary>
    /// Releases the manager's hold on the supervised process WITHOUT stopping it.
    /// A ServiceManager shutdown (or restart) must never kill still-running units — that is the
    /// core independence guarantee. The process is left alive so the next ServiceManager instance
    /// can re-adopt it via <see cref="TryReadopt"/>. Only an explicit Stop/Disable/Upgrade/Uninstall
    /// ends the process; call <see cref="StopAsync"/> directly for that.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
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

        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
