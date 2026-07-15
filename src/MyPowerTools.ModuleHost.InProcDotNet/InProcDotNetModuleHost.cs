using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MyPowerTools.Packaging;
using MyPowerTools.Runtime;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using IMptModuleLifecycle = MyPowerTools.Abstractions.IMptModuleLifecycle;
using IMptModule = MyPowerTools.Abstractions.IMptModule;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;
using SettingsValidationResult = MyPowerTools.Abstractions.SettingsValidationResult;

namespace MyPowerTools.ModuleHost.InProcDotNet;

public sealed class InProcDotNetModuleHost : IModuleTransportRuntime, IModuleTransportDiagnosticsProvider, IAsyncDisposable
{
    private const int CircuitBreakerFaultThreshold = 3;

    private static readonly string[] SharedAssemblies =
    [
        "MyPowerTools.Abstractions",
        "MyPowerTools.Platform.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    ];

    private readonly SemaphoreSlim _moduleLock = new(1, 1);
    private readonly Dictionary<string, InProcModuleSession> _loadedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _knownPoolModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InProcUnloadRecord> _unloadRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InProcFaultState> _faultStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InProcModuleSession> _quarantinedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InProcInvocationTracker> _invocationTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, InProcExecutionBoundary> _moduleExecutionBoundaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposeState;
    private long _invocationSequence;

    public string Kind => "inproc-dotnet";

    public string GetProcessPoolKey(RuntimeModuleRecord module)
    {
        return PoolKeyForModule(module.Module.Manifest.Id);
    }

    public void RegisterProcessPool(string poolKey, string moduleId)
    {
        _moduleLock.Wait();
        try
        {
            MarkKnown(poolKey, moduleId);
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    public void ApplyRestartPolicy(string poolKey, string restartPolicy, string reason, DateTimeOffset updatedAt, DateTimeOffset? expiresAt)
    {
        // InProc modules share the Runner process. Their soft circuit breaker is
        // reset through RestartProcessAsync; sidecar process policies do not apply.
    }

    public async ValueTask EnableModuleAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        await ExecuteWithBudgetAsync(module, "lifecycle.enable", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            if (loaded is IMptModuleLifecycle lifecycle)
            {
                await lifecycle.EnableAsync(context, token);
                token.ThrowIfCancellationRequested();
                await lifecycle.StartAsync(context, token);
            }

            return true;
        });
    }

    public async ValueTask DisableModuleAsync(RuntimeModuleRecord module, ModuleContext context, IReadOnlySet<string> enabledModuleIds, CancellationToken cancellationToken)
    {
        InProcModuleSession? session = null;
        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            if (_loadedModules.Remove(module.Module.Manifest.Id, out var loaded))
            {
                session = loaded;
            }
            else if (_quarantinedModules.Remove(module.Module.Manifest.Id, out var quarantined))
            {
                session = quarantined;
            }

            RemoveFaultStatesForModule(module.Module.Manifest.Id);
        }
        finally
        {
            _moduleLock.Release();
        }

        if (session is null)
        {
            return;
        }

        CancelModuleCallbacks(session.ModuleId);
        var tracker = _invocationTrackers.GetOrAdd(session.ModuleId, static _ => new InProcInvocationTracker());
        if (!await tracker.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
        {
            var activeCount = tracker.ActiveCount;
            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                _quarantinedModules[session.ModuleId] = session;
                _unloadRecords[session.PoolKey] = new InProcUnloadRecord(
                    session.ModuleId,
                    session.PoolKey,
                    "pending-runner-restart",
                    "manual-runner-restart",
                    $"InProc module '{session.ModuleId}' was disabled with {activeCount} callback(s) still running. Runner restart is required before re-enabling it.",
                    session.LoadContextName,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                _moduleLock.Release();
            }

            throw new InvalidOperationException(
                $"InProc module '{session.ModuleId}' still has {activeCount} active callback(s); disposal was skipped and Runner restart is required.");
        }

        Exception? lifecycleFailure = null;
        try
        {
            if (session.TryGetModule(out var loaded) && loaded is IMptModuleLifecycle lifecycle)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var lifecycleCleanup = StartTrackedInvocationAsync(
                    session.ModuleId,
                    async () =>
                    {
                        await lifecycle.StopAsync(context, cleanupDeadline.Token).ConfigureAwait(false);
                        await lifecycle.DisableAsync(context, cleanupDeadline.Token).ConfigureAwait(false);
                        return true;
                    },
                    enforceBoundary: false);
                await lifecycleCleanup.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            }
        }
        catch (TimeoutException ex)
        {
            var activeCount = tracker.ActiveCount;
            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                _quarantinedModules[session.ModuleId] = session;
                _unloadRecords[session.PoolKey] = new InProcUnloadRecord(
                    session.ModuleId,
                    session.PoolKey,
                    "pending-runner-restart",
                    "manual-runner-restart",
                    $"InProc module '{session.ModuleId}' lifecycle cleanup exceeded two seconds with {activeCount} callback(s) still active. Runner restart is required.",
                    session.LoadContextName,
                    DateTimeOffset.UtcNow);
            }
            finally
            {
                _moduleLock.Release();
            }

            throw new TimeoutException(
                $"InProc module '{session.ModuleId}' lifecycle cleanup exceeded two seconds; disposal was skipped to protect running code.",
                ex);
        }
        catch (Exception ex)
        {
            lifecycleFailure = ex;
        }

        var result = await session.DisposeAndUnloadAsync(CancellationToken.None);
        ResetModuleCancellation(session.ModuleId);
        await _moduleLock.WaitAsync(CancellationToken.None);
        try
        {
            RecordUnloadResult(result);
        }
        finally
        {
            _moduleLock.Release();
        }

        if (lifecycleFailure is not null)
        {
            if (lifecycleFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw lifecycleFailure;
            }

            throw new InvalidOperationException(
                $"InProc module '{module.Module.Manifest.Id}' failed while stopping; its soft-isolation container was unloaded. {LogRouter.Redact(lifecycleFailure.Message)}",
                lifecycleFailure);
        }
    }

    public async ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "status.get", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.GetStatusAsync(token);
        });
    }

    public async ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "settings.schema", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.GetSettingsSchemaAsync(token);
        });
    }

    public async ValueTask<SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsPatch patch, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "settings.validate", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.ValidateSettingsAsync(patch, token);
        });
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "settings.apply", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.ApplySettingsAsync(snapshot, token);
        });
    }

    public async ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "commands.list", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.ListCommandsAsync(token);
        });
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, "command.execute", cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            token.ThrowIfCancellationRequested();
            return await loaded.ExecuteCommandAsync(request, token);
        });
    }

    public async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamAsync(
        RuntimeModuleRecord module,
        ModuleContext context,
        CommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var moduleId = module.Module.Manifest.Id;
        var streamSequence = Interlocked.Increment(ref _invocationSequence);
        await EnsureModuleCallAllowedAsync(moduleId, "command.stream", cancellationToken);
        var streamTracker = _invocationTrackers.GetOrAdd(moduleId, static _ => new InProcInvocationTracker());
        using var streamLifetime = streamTracker.EnterLifetime();
        var maxCallMs = module.Entrypoint?.InProcMaxCallMs;
        using var setupTimeout = new CancellationTokenSource();
        if (maxCallMs is > 0)
        {
            setupTimeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
        }

        var moduleCancellationToken = GetModuleCancellationToken(moduleId);
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            moduleCancellationToken);
        var streamToken = streamCancellation.Token;
        using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            streamToken,
            setupTimeout.Token);
        var setupToken = setupCancellation.Token;
        var loaded = await LoadForStreamAsync(
            module,
            context,
            "command.stream",
            cancellationToken,
            setupToken,
            moduleCancellationToken,
            setupTimeout.Token,
            maxCallMs,
            Interlocked.Increment(ref _invocationSequence));
        var enumerator = await CreateStreamEnumeratorGuardedAsync(
            moduleId,
            "command.stream.open",
            () => loaded.ExecuteCommandStreamAsync(request, streamToken).GetAsyncEnumerator(streamToken),
            cancellationToken,
            setupToken,
            moduleCancellationToken,
            setupTimeout.Token,
            maxCallMs,
            Interlocked.Increment(ref _invocationSequence));
        var streamInvocationState = new InProcStreamInvocationState();
        var streamCompletedNormally = false;
        try
        {
            while (true)
            {
                using var moveTimeout = new CancellationTokenSource();
                if (maxCallMs is > 0)
                {
                    moveTimeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
                }

                using var moveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    streamToken,
                    moveTimeout.Token);
                if (!await MoveNextGuardedAsync(
                        module,
                        "command.stream",
                        enumerator,
                        recordSuccess: false,
                        callerCancellationToken: cancellationToken,
                        effectiveCancellationToken: moveCancellation.Token,
                        moduleCancellationToken: moduleCancellationToken,
                        timeoutCancellationToken: moveTimeout.Token,
                        streamInvocationState,
                        timeoutMs: maxCallMs,
                        invocationSequence: Interlocked.Increment(ref _invocationSequence)))
                {
                    break;
                }

                using var currentTimeout = new CancellationTokenSource();
                if (maxCallMs is > 0)
                {
                    currentTimeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
                }

                using var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    streamToken,
                    currentTimeout.Token);
                var evt = await ReadStreamCurrentGuardedAsync(
                    module,
                    "command.stream.current",
                    enumerator,
                    cancellationToken,
                    currentCancellation.Token,
                    moduleCancellationToken,
                    currentTimeout.Token,
                    streamInvocationState,
                    maxCallMs,
                    Interlocked.Increment(ref _invocationSequence));
                yield return new CommandProgressEvent(
                    evt.InvocationId,
                    evt.CommandId,
                    evt.State,
                    evt.Message,
                    evt.Sequence,
                    evt.Terminal,
                    evt.FinalResult);
            }

            await RecordModuleSuccessAsync(module.Module.Manifest.Id, "command.stream", streamSequence);
            streamCompletedNormally = true;
        }
        finally
        {
            if (!streamInvocationState.HasOutstandingCallback)
            {
                try
                {
                    await DisposeStreamGuardedAsync(
                        module.Module.Manifest.Id,
                        "command.stream.dispose",
                        enumerator,
                        cancellationToken,
                        moduleCancellationToken,
                        Interlocked.Increment(ref _invocationSequence));
                }
                catch when (!streamCompletedNormally)
                {
                    // Preserve the original stream failure after recording cleanup.
                }
            }
        }
    }

    public async IAsyncEnumerable<MyPowerTools.Abstractions.MptModuleEvent> SubscribeEventsAsync(
        RuntimeModuleRecord module,
        ModuleContext context,
        MyPowerTools.Abstractions.EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var moduleId = module.Module.Manifest.Id;
        var streamSequence = Interlocked.Increment(ref _invocationSequence);
        await EnsureModuleCallAllowedAsync(moduleId, "events.subscribe", cancellationToken);
        var streamTracker = _invocationTrackers.GetOrAdd(moduleId, static _ => new InProcInvocationTracker());
        using var streamLifetime = streamTracker.EnterLifetime();
        var maxCallMs = module.Entrypoint?.InProcMaxCallMs;
        using var setupTimeout = new CancellationTokenSource();
        if (maxCallMs is > 0)
        {
            setupTimeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
        }

        var moduleCancellationToken = GetModuleCancellationToken(moduleId);
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            moduleCancellationToken);
        var streamToken = streamCancellation.Token;
        using var setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            streamToken,
            setupTimeout.Token);
        var setupToken = setupCancellation.Token;
        var loaded = await LoadForStreamAsync(
            module,
            context,
            "events.subscribe",
            cancellationToken,
            setupToken,
            moduleCancellationToken,
            setupTimeout.Token,
            timeoutMs: maxCallMs,
            invocationSequence: Interlocked.Increment(ref _invocationSequence));
        var enumerator = await CreateStreamEnumeratorGuardedAsync(
            moduleId,
            "events.subscribe.open",
            () => loaded.SubscribeEventsAsync(cursor, streamToken).GetAsyncEnumerator(streamToken),
            cancellationToken,
            setupToken,
            moduleCancellationToken,
            setupTimeout.Token,
            timeoutMs: maxCallMs,
            invocationSequence: Interlocked.Increment(ref _invocationSequence));
        var streamInvocationState = new InProcStreamInvocationState();
        var streamStartedAt = DateTimeOffset.UtcNow;
        var stabilityRecorded = false;
        var streamCompletedNormally = false;
        try
        {
            while (await MoveNextGuardedAsync(
                       module,
                       "events.subscribe",
                       enumerator,
                       recordSuccess: false,
                       callerCancellationToken: cancellationToken,
                       effectiveCancellationToken: streamToken,
                       moduleCancellationToken: moduleCancellationToken,
                       timeoutCancellationToken: CancellationToken.None,
                       streamInvocationState,
                       timeoutMs: null,
                       invocationSequence: Interlocked.Increment(ref _invocationSequence)))
            {
                if (!stabilityRecorded && DateTimeOffset.UtcNow - streamStartedAt >= TimeSpan.FromSeconds(30))
                {
                    await RecordModuleSuccessAsync(module.Module.Manifest.Id, "events.subscribe", streamSequence);
                    stabilityRecorded = true;
                }

                using var currentTimeout = new CancellationTokenSource();
                if (maxCallMs is > 0)
                {
                    currentTimeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
                }

                using var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    streamToken,
                    currentTimeout.Token);
                yield return await ReadStreamCurrentGuardedAsync(
                    module,
                    "events.subscribe.current",
                    enumerator,
                    cancellationToken,
                    currentCancellation.Token,
                    moduleCancellationToken,
                    currentTimeout.Token,
                    streamInvocationState,
                    timeoutMs: maxCallMs,
                    invocationSequence: Interlocked.Increment(ref _invocationSequence));
            }

            await RecordModuleSuccessAsync(module.Module.Manifest.Id, "events.subscribe", streamSequence);
            streamCompletedNormally = true;
        }
        finally
        {
            if (!streamInvocationState.HasOutstandingCallback)
            {
                try
                {
                    await DisposeStreamGuardedAsync(
                        module.Module.Manifest.Id,
                        "events.subscribe.dispose",
                        enumerator,
                        cancellationToken,
                        moduleCancellationToken,
                        Interlocked.Increment(ref _invocationSequence));
                }
                catch when (!streamCompletedNormally)
                {
                    // Preserve the original stream failure after recording cleanup.
                }
            }
        }
    }

    public ValueTask<IMptModule> LoadAsync(MptModuleDefinition module, CancellationToken cancellationToken)
    {
        var context = new ModuleContext(
            "0.2.0",
            "1.0",
            module.Manifest.PackageId,
            module.Manifest.Id,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "data", module.Manifest.Id),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "cache", module.Manifest.Id),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", module.Manifest.Id),
            Environment.OSVersion.Platform.ToString(),
            module.Manifest.Capabilities);

        return LoadAsync(module, context, cancellationToken);
    }

    public async ValueTask<IMptModule> LoadAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            throw new ObjectDisposedException(nameof(InProcDotNetModuleHost));
        }

        var moduleId = module.Manifest.Id;
        var loadGate = _loadGates.GetOrAdd(moduleId, static _ => new SemaphoreSlim(1, 1));
        await loadGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                throw new ObjectDisposedException(nameof(InProcDotNetModuleHost));
            }

            await ThrowIfCircuitOpenAsync(moduleId, cancellationToken);
            await ThrowIfPendingRunnerRestartAsync(moduleId, cancellationToken);
            await _moduleLock.WaitAsync(cancellationToken);
            try
            {
                if (_loadedModules.TryGetValue(moduleId, out var loaded))
                {
                    return loaded.Module;
                }
            }
            finally
            {
                _moduleLock.Release();
            }

            var entrypoint = module.Manifest.Entrypoints
                .Where(entry => entry.Kind == "inproc-dotnet")
                .OrderByDescending(entry => entry.Priority)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"Module '{moduleId}' has no inproc-dotnet entrypoint.");

            if (string.IsNullOrWhiteSpace(entrypoint.Assembly) || string.IsNullOrWhiteSpace(entrypoint.Type))
            {
                throw new InvalidOperationException($"Module '{moduleId}' inproc entrypoint requires assembly and type.");
            }

            var attempt = await CreateLoadAttemptAsync(module, context, entrypoint, cancellationToken);
            if (attempt.Failure is not null)
            {
                var cleanup = await attempt.CleanupAsync(CancellationToken.None);
                await _moduleLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (!cleanup.Unloaded)
                    {
                        RecordUnloadResult(cleanup);
                    }
                }
                finally
                {
                    _moduleLock.Release();
                }

                throw attempt.Failure;
            }

            var candidate = attempt.Session!;
            Exception? publicationFailure = null;
            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                if (Volatile.Read(ref _disposeState) != 0)
                {
                    publicationFailure = new ObjectDisposedException(nameof(InProcDotNetModuleHost));
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    publicationFailure = new OperationCanceledException(cancellationToken);
                }
                else if (_unloadRecords.TryGetValue(candidate.PoolKey, out var pending) &&
                    string.Equals(pending.State, "pending-runner-restart", StringComparison.OrdinalIgnoreCase))
                {
                    publicationFailure = new InvalidOperationException(pending.Message);
                }
                else if (FaultForModule(moduleId, circuitOnly: true) is { } circuit)
                {
                    publicationFailure = new InProcModuleCircuitOpenException(
                        moduleId,
                        "load",
                        circuit.ConsecutiveFaults,
                        $"InProc module '{moduleId}' became quarantined while a new instance was initializing. Last error: {circuit.LastError}");
                }
                else
                {
                    MarkKnown(candidate.PoolKey, moduleId);
                    _loadedModules.Add(moduleId, candidate);
                    _unloadRecords.Remove(candidate.PoolKey);
                }
            }
            finally
            {
                _moduleLock.Release();
            }

            if (publicationFailure is not null)
            {
                var cleanup = await candidate.DisposeAndUnloadAsync(CancellationToken.None);
                await _moduleLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (!cleanup.Unloaded)
                    {
                        RecordUnloadResult(cleanup);
                    }
                }
                finally
                {
                    _moduleLock.Release();
                }

                throw publicationFailure;
            }

            return candidate.Module;
        }
        finally
        {
            loadGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeState, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        InProcModuleSession[] sessions;
        try
        {
            await _moduleLock.WaitAsync();
            try
            {
                sessions = _loadedModules.Values
                    .Concat(_quarantinedModules.Values)
                    .Distinct()
                    .ToArray();
                _loadedModules.Clear();
                _quarantinedModules.Clear();
                _faultStates.Clear();
            }
            finally
            {
                _moduleLock.Release();
            }

            foreach (var session in sessions)
            {
                CancelModuleCallbacks(session.ModuleId);
            }

            foreach (var boundary in _moduleExecutionBoundaries.Values)
            {
                boundary.Cancel();
            }

            foreach (var session in sessions)
            {
                var tracker = _invocationTrackers.GetOrAdd(session.ModuleId, static _ => new InProcInvocationTracker());
                InProcUnloadResult result;
                if (await tracker.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None))
                {
                    result = await session.DisposeAndUnloadAsync(CancellationToken.None);
                }
                else
                {
                    result = new InProcUnloadResult(
                        session.ModuleId,
                        session.PoolKey,
                        false,
                        "pending-runner-restart",
                        $"InProc module '{session.ModuleId}' still had {tracker.ActiveCount} active callback(s) during host shutdown; in-process disposal was skipped.",
                        session.LoadContextName);
                }

                await _moduleLock.WaitAsync();
                try
                {
                    RecordUnloadResult(result);
                }
                finally
                {
                    _moduleLock.Release();
                }
            }

            // Late completions can still execute bookkeeping after a timed-out
            // callback. Keep synchronization primitives alive until the host is
            // collected; only detach registries from new work here.
            _loadGates.Clear();
            _invocationTrackers.Clear();
            _moduleExecutionBoundaries.Clear();
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
        }
    }

    public IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics()
    {
        _moduleLock.Wait();
        try
        {
            var active = _loadedModules.Values
                .Select(session =>
                {
                    var fault = FaultForModule(session.ModuleId);
                    var state = session.IsUnisolatedDevelopment
                        ? "unisolated-development"
                        : fault is not null
                            ? "degraded"
                            : "loaded";
                    var message = session.IsUnisolatedDevelopment
                        ? "Development-only instance loaded from the default AppDomain. Collectible AssemblyLoadContext isolation is unavailable for this module."
                        : fault is not null
                        ? $"Soft/in-process isolation: {fault.ConsecutiveFaults}/{CircuitBreakerFaultThreshold} consecutive {fault.OperationFamily} fault(s). Last error: {fault.LastError}"
                        : "Soft/in-process isolation in a collectible AssemblyLoadContext; this module still shares the Runner process fault domain.";
                    return new RuntimeProcessDiagnostics(
                        Kind,
                        session.PoolKey,
                        state,
                        Environment.ProcessId,
                        session.LoadContextName,
                        1,
                        0,
                        session.IsUnisolatedDevelopment ? "development-unisolated" : "soft-in-process",
                        message,
                        session.LoadedAt,
                        [session.ModuleId]);
                })
                .ToArray();
            var circuitOpen = _faultStates.Values
                .Where(fault => fault.CircuitOpen)
                .Where(fault => !_loadedModules.ContainsKey(fault.ModuleId))
                .GroupBy(fault => fault.ModuleId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(fault => fault.RequiresRunnerRestart)
                    .ThenByDescending(fault => fault.UpdatedAt)
                    .First())
                .Select(fault =>
                {
                    _unloadRecords.TryGetValue(PoolKeyForModule(fault.ModuleId), out var unload);
                    var hasPendingUnload = unload is not null &&
                                           string.Equals(unload.State, "pending-runner-restart", StringComparison.OrdinalIgnoreCase);
                    var state = fault.RequiresRunnerRestart
                        ? "runner-restart-required"
                        : hasPendingUnload
                            ? unload!.State
                            : "circuit-open";
                    var restartPolicy = fault.RequiresRunnerRestart || hasPendingUnload
                        ? "manual-runner-restart"
                        : "manual-module-restart";
                    return new RuntimeProcessDiagnostics(
                        Kind,
                        PoolKeyForModule(fault.ModuleId),
                        state,
                        Environment.ProcessId,
                        _quarantinedModules.TryGetValue(fault.ModuleId, out var quarantined)
                            ? quarantined.LoadContextName
                            : fault.ModuleId,
                        0,
                        fault.ConsecutiveFaults,
                        restartPolicy,
                        fault.RequiresRunnerRestart
                            ? $"Soft isolation cannot safely reclaim this module in-process. Restart Runner before loading it again. {fault.CleanupMessage} Last error: {fault.LastError}"
                            : hasPendingUnload
                                ? unload!.Message
                            : $"Soft isolation quarantined this module after {fault.ConsecutiveFaults} consecutive {fault.OperationFamily} faults. Restart this module to retry. {fault.CleanupMessage} Last error: {fault.LastError}",
                        fault.UpdatedAt,
                        [fault.ModuleId]);
                })
                .ToArray();
            var pending = _unloadRecords.Values
                .Where(record => !_loadedModules.Values.Any(session => string.Equals(session.PoolKey, record.PoolKey, StringComparison.OrdinalIgnoreCase)))
                .Where(record => !circuitOpen.Any(process => string.Equals(process.PoolKey, record.PoolKey, StringComparison.OrdinalIgnoreCase)))
                .Select(record => new RuntimeProcessDiagnostics(
                    Kind,
                    record.PoolKey,
                    record.State,
                    Environment.ProcessId,
                    record.LoadContextName,
                    1,
                    0,
                    record.RestartPolicy,
                    record.Message,
                    record.UpdatedAt,
                    [record.ModuleId]))
                .ToArray();

            return active.Concat(circuitOpen).Concat(pending)
                .OrderBy(process => process.PoolKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    public async ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken)
    {
        InProcModuleSession? session = null;
        InProcFaultState? circuit = null;
        InProcUnloadRecord? pending = null;
        string? moduleId = null;

        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            session = _loadedModules.Values.FirstOrDefault(value => string.Equals(value.PoolKey, poolKey, StringComparison.OrdinalIgnoreCase));
            if (session is not null)
            {
                moduleId = session.ModuleId;
                _loadedModules.Remove(moduleId);
            }
            else
            {
                circuit = _faultStates.Values.FirstOrDefault(value =>
                    value.CircuitOpen &&
                    string.Equals(PoolKeyForModule(value.ModuleId), poolKey, StringComparison.OrdinalIgnoreCase));
                if (circuit is not null)
                {
                    moduleId = circuit.ModuleId;
                    _quarantinedModules.Remove(moduleId, out session);
                    _unloadRecords.TryGetValue(poolKey, out pending);
                }
                else
                {
                    _unloadRecords.TryGetValue(poolKey, out pending);
                }
            }
        }
        finally
        {
            _moduleLock.Release();
        }

        if (session is not null)
        {
            CancelModuleCallbacks(session.ModuleId);
            var tracker = _invocationTrackers.GetOrAdd(session.ModuleId, static _ => new InProcInvocationTracker());
            var drained = await tracker.WaitForDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            if (!drained)
            {
                await _moduleLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (circuit is null)
                    {
                        _loadedModules[session.ModuleId] = session;
                    }
                    else
                    {
                        _quarantinedModules[session.ModuleId] = session;
                    }
                }
                finally
                {
                    _moduleLock.Release();
                }

                var activeCount = tracker.ActiveCount;
                return new RuntimeProcessRestartResult(
                    false,
                    Kind,
                    poolKey,
                    circuit is null ? "busy" : "runner-restart-required",
                    $"InProc module '{session.ModuleId}' still has {activeCount} active callback(s). In-process disposal was skipped to avoid releasing resources underneath running code.",
                    [session.ModuleId]);
            }

            if (circuit is not null && !session.QuarantineStarted)
            {
                await session.BeginQuarantineAsync(CancellationToken.None);
            }

            var result = circuit is null
                ? await session.DisposeAndUnloadAsync(CancellationToken.None)
                : await session.CompleteUnloadAsync(CancellationToken.None);
            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                RecordUnloadResult(result);

                if (result.Unloaded)
                {
                    RemoveFaultStatesForModule(session.ModuleId);
                    _unloadRecords.Remove(poolKey);
                }
                else if (circuit is not null)
                {
                    _quarantinedModules[session.ModuleId] = session;
                }
            }
            finally
            {
                _moduleLock.Release();
            }

            if (result.Unloaded)
            {
                ResetModuleCancellation(session.ModuleId);
            }

            return new RuntimeProcessRestartResult(
                result.Unloaded,
                Kind,
                poolKey,
                result.Unloaded && circuit is not null ? "circuit-reset" : result.State,
                result.Unloaded && circuit is not null
                    ? $"Soft-isolation circuit reset for '{session.ModuleId}'; the next call will load a fresh module instance."
                    : result.Message,
                [session.ModuleId]);
        }

        if (circuit is not null && moduleId is not null)
        {
            if (pending is not null &&
                string.Equals(pending.State, "pending-runner-restart", StringComparison.OrdinalIgnoreCase))
            {
                return new RuntimeProcessRestartResult(
                    false,
                    Kind,
                    poolKey,
                    pending.State,
                    pending.Message,
                    [moduleId]);
            }

            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                RemoveFaultStatesForModule(moduleId);
                _unloadRecords.Remove(poolKey);
            }
            finally
            {
                _moduleLock.Release();
            }

            ResetModuleCancellation(moduleId);

            return new RuntimeProcessRestartResult(
                true,
                Kind,
                poolKey,
                "circuit-reset",
                $"Soft-isolation circuit reset for '{moduleId}'; the next call will load a fresh module instance.",
                [moduleId]);
        }

        if (pending is not null)
        {
            return new RuntimeProcessRestartResult(
                false,
                Kind,
                poolKey,
                pending.State,
                pending.Message,
                [pending.ModuleId]);
        }

        return new RuntimeProcessRestartResult(false, Kind, poolKey, "missing", $"InProc module pool '{poolKey}' is not loaded.", ModuleIdsForPool(poolKey));
    }

    public ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(string poolKey, bool paused, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var modules = ModuleIdsForPool(poolKey);
        return ValueTask.FromResult(new RuntimeProcessPolicyResult(
            false,
            Kind,
            poolKey,
            "unsupported",
            "soft-in-process",
            "InProc modules share the Runner process. Faulted modules use a per-module soft circuit breaker and explicit module restart.",
            modules,
            null));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async ValueTask<InProcLoadAttempt> CreateLoadAttemptAsync(
        MptModuleDefinition module,
        ModuleContext context,
        MptEntrypointManifest entrypoint,
        CancellationToken cancellationToken)
    {
        var moduleId = module.Manifest.Id;
        var poolKey = PoolKeyForModule(moduleId);
        MptPluginLoadContext? loadContext = null;
        InProcModuleSession? session = null;
        try
        {
            var assemblyPath = Path.GetFullPath(Path.Combine(module.Directory, entrypoint.Assembly!));
            var assembly = File.Exists(assemblyPath)
                ? LoadIsolatedAssembly(module, context, entrypoint, assemblyPath, out loadContext)
                : ResolveAlreadyLoadedIfAllowed(module, entrypoint.Assembly!);
            var type = assembly.GetType(entrypoint.Type!, throwOnError: true)!;
            if (Activator.CreateInstance(type) is not IMptModule instance)
            {
                throw new InvalidCastException($"{entrypoint.Type} does not implement {nameof(IMptModule)}.");
            }

            session = new InProcModuleSession(
                instance,
                loadContext,
                moduleId,
                poolKey,
                DateTimeOffset.UtcNow,
                isUnisolatedDevelopment: loadContext is null);
            loadContext = null;
            var initialized = await instance.InitializeAsync(context, cancellationToken);
            if (!initialized.Ok)
            {
                throw new InvalidOperationException(initialized.Error?.Message ?? $"Module '{moduleId}' rejected initialization.");
            }

            return InProcLoadAttempt.Succeeded(session);
        }
        catch (Exception ex)
        {
            var orphanedContext = loadContext is null ? null : new InProcLoadContextLease(loadContext, moduleId, poolKey);
            return InProcLoadAttempt.Failed(
                moduleId,
                poolKey,
                SanitizeLoadFailure(ex, cancellationToken),
                session,
                orphanedContext);
        }
    }

    private static Exception SanitizeLoadFailure(Exception failure, CancellationToken cancellationToken)
    {
        var message = LogRouter.Redact(failure.Message);
        return failure switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested =>
                new OperationCanceledException(message, cancellationToken),
            FileNotFoundException fileNotFound => new FileNotFoundException(message, fileNotFound.FileName),
            BadImageFormatException badImage => new BadImageFormatException(message, badImage.FileName),
            TypeLoadException => new TypeLoadException(message),
            InvalidCastException => new InvalidCastException(message),
            InvalidOperationException => new InvalidOperationException(message),
            _ => new InvalidOperationException($"InProc module load failed: {message}")
        };
    }

    private async ValueTask<IMptModule> LoadCachedAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await LoadAsync(module, context, cancellationToken);
    }

    private async ValueTask<T> ExecuteWithBudgetAsync<T>(
        RuntimeModuleRecord module,
        string operation,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<T>> action)
    {
        var moduleId = module.Module.Manifest.Id;
        var invocationSequence = Interlocked.Increment(ref _invocationSequence);
        await EnsureModuleCallAllowedAsync(moduleId, operation, cancellationToken);
        var maxCallMs = module.Entrypoint?.InProcMaxCallMs;
        using var timeout = new CancellationTokenSource();
        if (maxCallMs is > 0)
        {
            timeout.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
        }

        var moduleCancellationToken = GetModuleCancellationToken(moduleId);
        using var effectiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            moduleCancellationToken,
            timeout.Token);
        var effectiveToken = effectiveCancellation.Token;
        var execution = StartTrackedInvocationAsync(
            moduleId,
            async () => await action(effectiveToken).ConfigureAwait(false));
        try
        {
            var result = await execution.WaitAsync(effectiveToken);
            await RecordModuleSuccessAsync(moduleId, operation, invocationSequence);
            return result;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            if (await IsInvocationStillRunningAsync(execution))
            {
                await RecordModuleFaultAsync(
                    moduleId,
                    operation,
                    new InProcInvocationTimeoutException(
                        moduleId,
                        operation,
                        true,
                        $"InProc module '{moduleId}' did not stop {operation} after caller cancellation.",
                        ex),
                    invocationSequence);
            }

            throw;
        }
        catch (OperationCanceledException ex) when (moduleCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"InProc module '{moduleId}' callback was cancelled by its soft-isolation boundary during {operation}.",
                ex,
                moduleCancellationToken);
        }
        catch (OperationCanceledException ex) when (maxCallMs is > 0 && timeout.IsCancellationRequested)
        {
            var callbackStillRunning = await IsInvocationStillRunningAsync(execution);
            var timeoutFailure = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' exceeded runtimePolicy.inProcRules.maxCallMs={maxCallMs.Value} during {operation}.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeoutFailure, invocationSequence);
            throw timeoutFailure;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private Task<T> StartTrackedInvocationAsync<T>(string moduleId, Func<Task<T>> action, bool enforceBoundary = true)
    {
        var tracker = _invocationTrackers.GetOrAdd(moduleId, static _ => new InProcInvocationTracker());
        var lease = tracker.Enter();
        var boundary = GetModuleExecutionBoundary(moduleId);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<T> pluginTask;
        try
        {
            pluginTask = Task.Run(
                async () =>
                {
                    if (enforceBoundary)
                    {
                        boundary.Token.ThrowIfCancellationRequested();
                        if (!_moduleExecutionBoundaries.TryGetValue(moduleId, out var current) ||
                            !ReferenceEquals(current, boundary))
                        {
                            throw new OperationCanceledException(
                                $"InProc module '{moduleId}' invocation belongs to an expired module generation.",
                                boundary.Token);
                        }
                    }

                    return await action().ConfigureAwait(false);
                },
                CancellationToken.None);
        }
        catch (Exception failure)
        {
            lease.Dispose();
            completion.TrySetException(SanitizeCallbackFailure(failure));
            return completion.Task;
        }

        var bridge = new InProcInvocationBridge<T>(completion, lease);
        var detached = pluginTask.ContinueWith(
            static (task, state) =>
            {
                return ((InProcInvocationBridge<T>)state!).Detach(task);
            },
            bridge,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = detached.ContinueWith(
            static task => task.GetAwaiter().GetResult().Publish(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    private sealed class InProcInvocationBridge<TValue>
    {
        private readonly TaskCompletionSource<TValue> _completion;
        private readonly IDisposable _lease;

        public InProcInvocationBridge(TaskCompletionSource<TValue> completion, IDisposable lease)
        {
            _completion = completion;
            _lease = lease;
        }

        public InProcInvocationOutcome<TValue> Detach(Task<TValue> pluginTask)
        {
            TValue? result = default;
            Exception? failure = null;
            if (pluginTask.IsCompletedSuccessfully)
            {
                result = pluginTask.Result;
            }
            else
            {
                try
                {
                    result = pluginTask.GetAwaiter().GetResult();
                }
                catch (Exception pluginFailure)
                {
                    failure = SanitizeCallbackFailure(pluginFailure);
                }
            }

            _ = pluginTask.Exception;
            return new InProcInvocationOutcome<TValue>(_completion, _lease, result, failure);
        }
    }

    private sealed class InProcInvocationOutcome<TValue>
    {
        private readonly TaskCompletionSource<TValue> _completion;
        private readonly IDisposable _lease;
        private readonly TValue? _result;
        private readonly Exception? _failure;

        public InProcInvocationOutcome(
            TaskCompletionSource<TValue> completion,
            IDisposable lease,
            TValue? result,
            Exception? failure)
        {
            _completion = completion;
            _lease = lease;
            _result = result;
            _failure = failure;
        }

        public void Publish()
        {
            _lease.Dispose();
            if (_failure is OperationCanceledException cancelled)
            {
                _completion.TrySetCanceled(cancelled.CancellationToken);
            }
            else if (_failure is not null)
            {
                _completion.TrySetException(_failure);
            }
            else
            {
                _completion.TrySetResult(_result!);
            }
        }
    }

    private static Exception SanitizeCallbackFailure(Exception failure)
    {
        var message = LogRouter.Redact(failure.Message);
        return failure switch
        {
            InProcModuleCircuitOpenException circuit => new InProcModuleCircuitOpenException(
                circuit.ModuleId,
                circuit.Operation,
                circuit.ConsecutiveFaults,
                message),
            InProcInvocationTimeoutException timeout => new InProcInvocationTimeoutException(
                timeout.ModuleId,
                timeout.Operation,
                timeout.CallbackStillRunning,
                message,
                new TimeoutException(message)),
            OperationCanceledException cancelled => new OperationCanceledException(message, cancelled.CancellationToken),
            FileNotFoundException fileNotFound => new FileNotFoundException(message, fileNotFound.FileName),
            BadImageFormatException badImage => new BadImageFormatException(message, badImage.FileName),
            TypeLoadException => new TypeLoadException(message),
            InvalidCastException => new InvalidCastException(message),
            InvalidOperationException => new InvalidOperationException(message),
            TimeoutException => new TimeoutException(message),
            ArgumentException argument => new ArgumentException(message, argument.ParamName),
            NotSupportedException => new NotSupportedException(message),
            _ => new InvalidOperationException($"InProc module callback failed: {message}")
        };
    }

    private static async ValueTask<bool> IsInvocationStillRunningAsync(Task invocation)
    {
        if (invocation.IsCompleted)
        {
            return false;
        }

        await Task.WhenAny(invocation, Task.Delay(250));
        return !invocation.IsCompleted;
    }

    private async ValueTask<bool> MoveNextGuardedAsync<T>(
        RuntimeModuleRecord module,
        string operation,
        IAsyncEnumerator<T> enumerator,
        bool recordSuccess,
        CancellationToken callerCancellationToken,
        CancellationToken effectiveCancellationToken,
        CancellationToken moduleCancellationToken,
        CancellationToken timeoutCancellationToken,
        InProcStreamInvocationState streamInvocationState,
        int? timeoutMs,
        long invocationSequence)
    {
        var moduleId = module.Module.Manifest.Id;
        await EnsureModuleCallAllowedAsync(moduleId, operation, callerCancellationToken);
        Task<bool>? moveNext = null;
        try
        {
            moveNext = StartTrackedInvocationAsync(
                moduleId,
                async () =>
                {
                    effectiveCancellationToken.ThrowIfCancellationRequested();
                    return await enumerator.MoveNextAsync().AsTask().ConfigureAwait(false);
                });
            var result = await moveNext.WaitAsync(effectiveCancellationToken);
            if (recordSuccess && result)
            {
                await RecordModuleSuccessAsync(moduleId, operation, invocationSequence);
            }

            return result;
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            if (moveNext is not null && await IsInvocationStillRunningAsync(moveNext))
            {
                streamInvocationState.MarkOutstanding();
                await RecordModuleFaultAsync(
                    moduleId,
                    operation,
                    new InProcInvocationTimeoutException(
                        moduleId,
                        operation,
                        true,
                        $"InProc module '{moduleId}' did not stop {operation} after caller cancellation.",
                        ex),
                    invocationSequence);
            }

            throw;
        }
        catch (OperationCanceledException ex) when (moduleCancellationToken.IsCancellationRequested)
        {
            if (moveNext is not null && await IsInvocationStillRunningAsync(moveNext))
            {
                streamInvocationState.MarkOutstanding();
            }

            throw new OperationCanceledException(
                $"InProc module '{moduleId}' stream was cancelled by its soft-isolation boundary during {operation}.",
                ex,
                moduleCancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutMs is > 0 && timeoutCancellationToken.IsCancellationRequested)
        {
            var callbackStillRunning = moveNext is not null && await IsInvocationStillRunningAsync(moveNext);
            if (callbackStillRunning)
            {
                streamInvocationState.MarkOutstanding();
            }

            var timeout = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' exceeded runtimePolicy.inProcRules.maxCallMs={timeoutMs.Value} during {operation}.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeout, invocationSequence);
            throw timeout;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private async ValueTask<T> ReadStreamCurrentGuardedAsync<T>(
        RuntimeModuleRecord module,
        string operation,
        IAsyncEnumerator<T> enumerator,
        CancellationToken callerCancellationToken,
        CancellationToken effectiveCancellationToken,
        CancellationToken moduleCancellationToken,
        CancellationToken timeoutCancellationToken,
        InProcStreamInvocationState streamInvocationState,
        int? timeoutMs,
        long invocationSequence)
    {
        var moduleId = module.Module.Manifest.Id;
        await EnsureModuleCallAllowedAsync(moduleId, operation, callerCancellationToken);
        Task<T>? read = null;
        try
        {
            read = StartTrackedInvocationAsync(
                moduleId,
                () =>
                {
                    effectiveCancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(enumerator.Current);
                });
            return await read.WaitAsync(effectiveCancellationToken);
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            if (read is not null && await IsInvocationStillRunningAsync(read))
            {
                streamInvocationState.MarkOutstanding();
                await RecordModuleFaultAsync(
                    moduleId,
                    operation,
                    new InProcInvocationTimeoutException(
                        moduleId,
                        operation,
                        true,
                        $"InProc module '{moduleId}' did not stop {operation} after caller cancellation.",
                        ex),
                    invocationSequence);
            }

            throw;
        }
        catch (OperationCanceledException ex) when (moduleCancellationToken.IsCancellationRequested)
        {
            if (read is not null && await IsInvocationStillRunningAsync(read))
            {
                streamInvocationState.MarkOutstanding();
            }

            throw new OperationCanceledException(
                $"InProc module '{moduleId}' stream was cancelled by its soft-isolation boundary during {operation}.",
                ex,
                moduleCancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutMs is > 0 && timeoutCancellationToken.IsCancellationRequested)
        {
            var callbackStillRunning = read is not null && await IsInvocationStillRunningAsync(read);
            if (callbackStillRunning)
            {
                streamInvocationState.MarkOutstanding();
            }

            var timeout = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' exceeded runtimePolicy.inProcRules.maxCallMs={timeoutMs.Value} during {operation}.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeout, invocationSequence);
            throw timeout;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private async ValueTask<IMptModule> LoadForStreamAsync(
        RuntimeModuleRecord module,
        ModuleContext context,
        string operation,
        CancellationToken callerCancellationToken,
        CancellationToken effectiveCancellationToken,
        CancellationToken moduleCancellationToken,
        CancellationToken timeoutCancellationToken,
        int? timeoutMs,
        long invocationSequence)
    {
        var moduleId = module.Module.Manifest.Id;
        Task<IMptModule>? load = null;
        try
        {
            load = StartTrackedInvocationAsync(
                moduleId,
                async () =>
                {
                    effectiveCancellationToken.ThrowIfCancellationRequested();
                    var loaded = await LoadCachedAsync(module.Module, context, effectiveCancellationToken).ConfigureAwait(false);
                    effectiveCancellationToken.ThrowIfCancellationRequested();
                    return loaded;
                });
            return await load.WaitAsync(effectiveCancellationToken);
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            if (load is not null && await IsInvocationStillRunningAsync(load))
            {
                await RecordModuleFaultAsync(
                    moduleId,
                    operation,
                    new InProcInvocationTimeoutException(
                        moduleId,
                        operation,
                        true,
                        $"InProc module '{moduleId}' did not stop {operation} after caller cancellation.",
                        ex),
                    invocationSequence);
            }

            throw;
        }
        catch (OperationCanceledException ex) when (moduleCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"InProc module '{moduleId}' stream setup was cancelled by its soft-isolation boundary during {operation}.",
                ex,
                moduleCancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutMs is > 0 && timeoutCancellationToken.IsCancellationRequested)
        {
            var callbackStillRunning = load is not null && await IsInvocationStillRunningAsync(load);
            var timeout = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' exceeded runtimePolicy.inProcRules.maxCallMs={timeoutMs.Value} during {operation}.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeout, invocationSequence);
            throw timeout;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private async ValueTask<IAsyncEnumerator<T>> CreateStreamEnumeratorGuardedAsync<T>(
        string moduleId,
        string operation,
        Func<IAsyncEnumerator<T>> factory,
        CancellationToken callerCancellationToken,
        CancellationToken effectiveCancellationToken,
        CancellationToken moduleCancellationToken,
        CancellationToken timeoutCancellationToken,
        int? timeoutMs,
        long invocationSequence)
    {
        Task<IAsyncEnumerator<T>>? create = null;
        try
        {
            create = StartTrackedInvocationAsync(
                moduleId,
                () =>
                {
                    effectiveCancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(factory());
                });
            return await create.WaitAsync(effectiveCancellationToken);
        }
        catch (OperationCanceledException ex) when (callerCancellationToken.IsCancellationRequested)
        {
            if (create is not null && await IsInvocationStillRunningAsync(create))
            {
                await RecordModuleFaultAsync(
                    moduleId,
                    operation,
                    new InProcInvocationTimeoutException(
                        moduleId,
                        operation,
                        true,
                        $"InProc module '{moduleId}' did not stop {operation} after caller cancellation.",
                        ex),
                    invocationSequence);
            }

            throw;
        }
        catch (OperationCanceledException ex) when (moduleCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                $"InProc module '{moduleId}' stream setup was cancelled by its soft-isolation boundary during {operation}.",
                ex,
                moduleCancellationToken);
        }
        catch (OperationCanceledException ex) when (timeoutMs is > 0 && timeoutCancellationToken.IsCancellationRequested)
        {
            var callbackStillRunning = create is not null && await IsInvocationStillRunningAsync(create);
            var timeout = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' exceeded runtimePolicy.inProcRules.maxCallMs={timeoutMs.Value} during {operation}.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeout, invocationSequence);
            throw timeout;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private async ValueTask DisposeStreamGuardedAsync<T>(
        string moduleId,
        string operation,
        IAsyncEnumerator<T> enumerator,
        CancellationToken callerCancellationToken,
        CancellationToken moduleCancellationToken,
        long invocationSequence)
    {
        Task<bool>? dispose = null;
        try
        {
            dispose = StartTrackedInvocationAsync(
                moduleId,
                async () =>
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                    return true;
                });
            await dispose.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            // The stream is already ending due to its caller's cancellation.
        }
        catch (OperationCanceledException) when (moduleCancellationToken.IsCancellationRequested)
        {
            // Quarantine already owns this stream's terminal state.
        }
        catch (TimeoutException ex)
        {
            var callbackStillRunning = dispose is not null && await IsInvocationStillRunningAsync(dispose);
            var timeout = new InProcInvocationTimeoutException(
                moduleId,
                operation,
                callbackStillRunning,
                $"InProc module '{moduleId}' did not dispose {operation} within two seconds.",
                ex);
            await RecordModuleFaultAsync(moduleId, operation, timeout, invocationSequence);
            throw timeout;
        }
        catch (Exception ex)
        {
            await RecordModuleFaultAsync(moduleId, operation, ex, invocationSequence);
            throw;
        }
    }

    private ValueTask EnsureModuleCallAllowedAsync(string moduleId, string operation, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return ValueTask.FromException(new ObjectDisposedException(nameof(InProcDotNetModuleHost)));
        }

        return ThrowIfCircuitOpenAsync(moduleId, cancellationToken, operation);
    }

    private InProcExecutionBoundary GetModuleExecutionBoundary(string moduleId)
    {
        var boundary = _moduleExecutionBoundaries.GetOrAdd(moduleId, static _ => new InProcExecutionBoundary());
        if (Volatile.Read(ref _disposeState) != 0)
        {
            boundary.Cancel();
            _moduleExecutionBoundaries.TryRemove(new KeyValuePair<string, InProcExecutionBoundary>(moduleId, boundary));
        }

        return boundary;
    }

    private CancellationToken GetModuleCancellationToken(string moduleId)
    {
        return GetModuleExecutionBoundary(moduleId).Token;
    }

    private void CancelModuleCallbacks(string moduleId)
    {
        if (_moduleExecutionBoundaries.TryGetValue(moduleId, out var boundary))
        {
            boundary.Cancel();
        }
    }

    private void ResetModuleCancellation(string moduleId)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        var replacement = new InProcExecutionBoundary();
        while (true)
        {
            if (!_moduleExecutionBoundaries.TryGetValue(moduleId, out var previous))
            {
                if (_moduleExecutionBoundaries.TryAdd(moduleId, replacement))
                {
                    return;
                }

                continue;
            }

            if (_moduleExecutionBoundaries.TryUpdate(moduleId, replacement, previous))
            {
                previous.Cancel();
                return;
            }
        }
    }

    private async ValueTask RecordModuleSuccessAsync(string moduleId, string operation, long invocationSequence)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        await _moduleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            var faultKey = FaultKey(moduleId, OperationFamily(operation));
            if (_faultStates.TryGetValue(faultKey, out var state) &&
                !state.CircuitOpen &&
                invocationSequence > state.LastFaultSequence &&
                string.Equals(state.OperationFamily, OperationFamily(operation), StringComparison.OrdinalIgnoreCase))
            {
                _faultStates.Remove(faultKey);
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private async ValueTask RecordModuleFaultAsync(string moduleId, string operation, Exception exception, long invocationSequence)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        InProcModuleSession? quarantine = null;
        var circuitOpened = false;
        var family = OperationFamily(operation);
        var faultKey = FaultKey(moduleId, family);
        var tracker = _invocationTrackers.GetOrAdd(moduleId, static _ => new InProcInvocationTracker());
        var forceCircuitOpen = exception is InProcInvocationTimeoutException { CallbackStillRunning: true };
        var error = LogRouter.Redact(exception.Message);
        if (string.IsNullOrWhiteSpace(error))
        {
            error = exception.GetType().Name;
        }

        if (error.Length > 512)
        {
            error = error[..512];
        }

        await _moduleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            var previous = _faultStates.GetValueOrDefault(faultKey);
            if (previous is not null && invocationSequence < previous.LastFaultSequence)
            {
                return;
            }

            var sameFamily = previous is not null &&
                             string.Equals(previous.OperationFamily, family, StringComparison.OrdinalIgnoreCase);
            var consecutive = forceCircuitOpen
                ? CircuitBreakerFaultThreshold
                : sameFamily
                    ? previous!.ConsecutiveFaults + 1
                    : 1;
            var circuitOpen = previous?.CircuitOpen == true || consecutive >= CircuitBreakerFaultThreshold;
            circuitOpened = circuitOpen;
            _faultStates[faultKey] = new InProcFaultState(
                moduleId,
                family,
                operation,
                consecutive,
                circuitOpen,
                error,
                DateTimeOffset.UtcNow,
                forceCircuitOpen
                    ? "A timed-out callback remained active after cancellation; new calls are blocked immediately."
                    : circuitOpen
                    ? "Module callbacks are blocked until an explicit module restart."
                    : $"{CircuitBreakerFaultThreshold - consecutive} consecutive fault(s) remain before quarantine.",
                Math.Max(invocationSequence, previous?.LastFaultSequence ?? 0),
                forceCircuitOpen || previous?.RequiresRunnerRestart == true);

            if (circuitOpen && _loadedModules.Remove(moduleId, out var loaded))
            {
                quarantine = loaded;
                _quarantinedModules[moduleId] = loaded;
            }
        }
        finally
        {
            _moduleLock.Release();
        }

        if (circuitOpened)
        {
            CancelModuleCallbacks(moduleId);
        }

        if (quarantine is null)
        {
            return;
        }

        var drained = await tracker.WaitForCallbackDrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        if (!drained)
        {
            var activeCount = tracker.ActiveCount;
            await _moduleLock.WaitAsync(CancellationToken.None);
            try
            {
                if (_faultStates.TryGetValue(faultKey, out var state) && state.CircuitOpen)
                {
                    _faultStates[faultKey] = state with
                    {
                        CleanupMessage = $"{activeCount} callback(s) ignored cancellation or are still running. Module disposal was skipped; restart Runner to terminate this fault domain safely.",
                        RequiresRunnerRestart = true,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
            }
            finally
            {
                _moduleLock.Release();
            }

            return;
        }

        string cleanupMessage;
        try
        {
            cleanupMessage = await quarantine.BeginQuarantineAsync(CancellationToken.None);
        }
        catch (Exception cleanupFailure)
        {
            cleanupMessage = $"Quarantine cleanup failed: {LogRouter.Redact(cleanupFailure.Message)}";
        }

        await _moduleLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_faultStates.TryGetValue(faultKey, out var state) && state.CircuitOpen)
            {
                _faultStates[faultKey] = state with
                {
                    CleanupMessage = cleanupMessage,
                    RequiresRunnerRestart = state.RequiresRunnerRestart || !quarantine.CleanupCompleted,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private async ValueTask ThrowIfCircuitOpenAsync(
        string moduleId,
        CancellationToken cancellationToken,
        string operation = "load")
    {
        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            if (FaultForModule(moduleId, circuitOnly: true) is { } state)
            {
                throw new InProcModuleCircuitOpenException(
                    moduleId,
                    operation,
                    state.ConsecutiveFaults,
                    state.RequiresRunnerRestart
                        ? $"InProc module '{moduleId}' is quarantined after a callback escaped its cancellation boundary. Restart Runner before retrying. Last error: {state.LastError}"
                        : $"InProc module '{moduleId}' is quarantined by the soft-isolation circuit breaker after {state.ConsecutiveFaults} consecutive {state.OperationFamily} faults. Restart this module before retrying. Last error: {state.LastError}");
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private static string OperationFamily(string operation)
    {
        if (operation.StartsWith("command", StringComparison.OrdinalIgnoreCase) ||
            operation.StartsWith("commands", StringComparison.OrdinalIgnoreCase))
        {
            return "command";
        }

        var separator = operation.IndexOf('.');
        return separator > 0 ? operation[..separator] : operation;
    }

    private static string FaultKey(string moduleId, string operationFamily)
    {
        return $"{moduleId}\u001f{operationFamily}";
    }

    private InProcFaultState? FaultForModule(string moduleId, bool circuitOnly = false)
    {
        return _faultStates.Values
            .Where(state => string.Equals(state.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
            .Where(state => !circuitOnly || state.CircuitOpen)
            .OrderByDescending(state => state.CircuitOpen)
            .ThenByDescending(state => state.UpdatedAt)
            .FirstOrDefault();
    }

    private void RemoveFaultStatesForModule(string moduleId)
    {
        foreach (var key in _faultStates
                     .Where(pair => string.Equals(pair.Value.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _faultStates.Remove(key);
        }
    }

    private async ValueTask ThrowIfPendingRunnerRestartAsync(string moduleId, CancellationToken cancellationToken)
    {
        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            if (_unloadRecords.TryGetValue(PoolKeyForModule(moduleId), out var pending) &&
                string.Equals(pending.State, "pending-runner-restart", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(pending.Message);
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private void MarkKnown(string poolKey, string moduleId)
    {
        if (!_knownPoolModules.TryGetValue(poolKey, out var modules))
        {
            modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _knownPoolModules[poolKey] = modules;
        }

        modules.Add(moduleId);
    }

    private IReadOnlyList<string> ModuleIdsForPool(string poolKey)
    {
        _moduleLock.Wait();
        try
        {
            return _knownPoolModules.TryGetValue(poolKey, out var modules)
                ? modules.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private void RecordUnloadResult(InProcUnloadResult result)
    {
        if (result.Unloaded)
        {
            _unloadRecords.Remove(result.PoolKey);
            return;
        }

        _unloadRecords[result.PoolKey] = new InProcUnloadRecord(
            result.ModuleId,
            result.PoolKey,
            result.State,
            "manual-runner-restart",
            result.Message,
            result.LoadContextName,
            DateTimeOffset.UtcNow);
    }

    private static string PoolKeyForModule(string moduleId) => $"module:{moduleId}";

    private static Assembly ResolveAlreadyLoadedIfAllowed(MptModuleDefinition module, string assemblyNameOrPath)
    {
        if (module.Manifest.Development?.AllowAlreadyLoadedFallback != true)
        {
            throw new FileNotFoundException($"Assembly '{assemblyNameOrPath}' was not found on disk, and development.allowAlreadyLoadedFallback is not enabled.");
        }

        return ResolveAlreadyLoaded(assemblyNameOrPath);
    }

    private static Assembly ResolveAlreadyLoaded(string assemblyNameOrPath)
    {
        var simpleName = Path.GetFileNameWithoutExtension(assemblyNameOrPath);
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Assembly '{assemblyNameOrPath}' was not found on disk or in the current load context.");
    }

    private static Assembly LoadIsolatedAssembly(
        MptModuleDefinition module,
        ModuleContext context,
        MptEntrypointManifest entrypoint,
        string assemblyPath,
        out MptPluginLoadContext loadContext)
    {
        var sourceRoot = Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException($"Module '{module.Manifest.Id}' assembly path has no directory.");
        var shadowRoot = ShadowCopyModule(module, context, entrypoint, assemblyPath, sourceRoot);
        var shadowAssemblyPath = Path.Combine(shadowRoot, Path.GetFileName(assemblyPath));
        loadContext = new MptPluginLoadContext(shadowAssemblyPath, SharedAssemblies);
        return loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
    }

    private static string ShadowCopyModule(
        MptModuleDefinition module,
        ModuleContext context,
        MptEntrypointManifest entrypoint,
        string assemblyPath,
        string sourceRoot)
    {
        var fingerprint = HashForPath(module, entrypoint, assemblyPath);
        var safePackage = SanitizePathSegment(module.Manifest.PackageId);
        var safeModule = SanitizePathSegment(module.Manifest.Id);
        var shadowRoot = Path.Combine(context.CacheDirectory, "inproc-shadow", safePackage, safeModule, fingerprint);
        var marker = Path.Combine(shadowRoot, ".complete");
        if (File.Exists(marker))
        {
            return shadowRoot;
        }

        Directory.CreateDirectory(shadowRoot);
        CopyDirectory(sourceRoot, shadowRoot);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        return shadowRoot;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (IsIgnoredSegment(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (IsIgnoredSegment(relative))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsIgnoredSegment(string relativePath)
    {
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string HashForPath(MptModuleDefinition module, MptEntrypointManifest entrypoint, string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        var input = string.Join(
            "|",
            module.Manifest.PackageId,
            module.Manifest.Id,
            entrypoint.Assembly,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

internal sealed class MptPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblies;

    public MptPluginLoadContext(string mainAssemblyPath, IEnumerable<string> sharedAssemblies)
        : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sharedAssemblies = sharedAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && _sharedAssemblies.Contains(assemblyName.Name))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

internal sealed class InProcLoadAttempt
{
    private InProcLoadAttempt(
        string moduleId,
        string poolKey,
        InProcModuleSession? session,
        InProcLoadContextLease? orphanedContext,
        Exception? failure)
    {
        ModuleId = moduleId;
        PoolKey = poolKey;
        Session = session;
        OrphanedContext = orphanedContext;
        Failure = failure;
    }

    public string ModuleId { get; }
    public string PoolKey { get; }
    public InProcModuleSession? Session { get; }
    public InProcLoadContextLease? OrphanedContext { get; }
    public Exception? Failure { get; }

    public static InProcLoadAttempt Succeeded(InProcModuleSession session)
    {
        return new InProcLoadAttempt(session.ModuleId, session.PoolKey, session, null, null);
    }

    public static InProcLoadAttempt Failed(
        string moduleId,
        string poolKey,
        Exception failure,
        InProcModuleSession? session,
        InProcLoadContextLease? orphanedContext)
    {
        return new InProcLoadAttempt(moduleId, poolKey, session, orphanedContext, failure);
    }

    public async ValueTask<InProcUnloadResult> CleanupAsync(CancellationToken cancellationToken)
    {
        if (Session is not null)
        {
            return await Session.DisposeAndUnloadAsync(cancellationToken);
        }

        if (OrphanedContext is not null)
        {
            return await OrphanedContext.DisposeAndUnloadAsync(cancellationToken);
        }

        return new InProcUnloadResult(
            ModuleId,
            PoolKey,
            true,
            "load-failed-clean",
            $"InProc module '{ModuleId}' failed before creating a module instance; no isolated context remained.",
            ModuleId);
    }
}

internal sealed class InProcLoadContextLease
{
    private MptPluginLoadContext? _loadContext;
    private readonly WeakReference _reference;

    public InProcLoadContextLease(MptPluginLoadContext loadContext, string moduleId, string poolKey)
    {
        _loadContext = loadContext;
        _reference = new WeakReference(loadContext, trackResurrection: false);
        ModuleId = moduleId;
        PoolKey = poolKey;
        LoadContextName = loadContext.Name ?? moduleId;
    }

    public string ModuleId { get; }
    public string PoolKey { get; }
    public string LoadContextName { get; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async ValueTask<InProcUnloadResult> DisposeAndUnloadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _loadContext, null)?.Unload();
        var probe = Task.Run(
            () =>
            {
                for (var attempt = 0; attempt < 10 && _reference.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(100);
                }

                return !_reference.IsAlive;
            },
            CancellationToken.None);

        bool unloaded;
        try
        {
            unloaded = await probe.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            unloaded = false;
        }

        return unloaded
            ? new InProcUnloadResult(ModuleId, PoolKey, true, "unloaded", $"Failed load context for '{ModuleId}' unloaded cleanly.", LoadContextName)
            : new InProcUnloadResult(ModuleId, PoolKey, false, "pending-runner-restart", $"Failed load context for '{ModuleId}' remained alive; restart Runner before retrying.", LoadContextName);
    }
}

internal sealed class InProcModuleSession
{
    private readonly object _quarantineGate = new();
    private MptPluginLoadContext? _loadContext;
    private readonly WeakReference? _loadContextReference;
    private IMptModule? _module;
    private string _quarantineCleanupMessage = "";
    private Task<string>? _quarantineTask;
    private bool _quarantineStarted;
    private bool _cleanupCompleted;

    public InProcModuleSession(
        IMptModule module,
        MptPluginLoadContext? loadContext,
        string moduleId,
        string poolKey,
        DateTimeOffset loadedAt,
        bool isUnisolatedDevelopment = false)
    {
        _module = module;
        _loadContext = loadContext;
        _loadContextReference = loadContext is null ? null : new WeakReference(loadContext, trackResurrection: false);
        ModuleId = moduleId;
        PoolKey = poolKey;
        LoadedAt = loadedAt;
        LoadContextName = loadContext?.Name ?? "default-appdomain";
        IsUnisolatedDevelopment = isUnisolatedDevelopment;
    }

    public string ModuleId { get; }
    public string PoolKey { get; }
    public DateTimeOffset LoadedAt { get; }
    public string LoadContextName { get; }
    public bool IsUnisolatedDevelopment { get; }
    public bool CleanupCompleted
    {
        get
        {
            lock (_quarantineGate)
            {
                return _cleanupCompleted;
            }
        }
    }

    public bool QuarantineStarted
    {
        get
        {
            lock (_quarantineGate)
            {
                return _quarantineStarted;
            }
        }
    }
    public IMptModule Module => _module ?? throw new ObjectDisposedException(ModuleId);

    public bool TryGetModule(out IMptModule? module)
    {
        module = _module;
        return module is not null;
    }

    public async ValueTask<InProcUnloadResult> DisposeAndUnloadAsync(CancellationToken cancellationToken)
    {
        var cleanupMessage = await BeginQuarantineAsync(cancellationToken);
        return await CompleteUnloadAsync(cancellationToken, cleanupMessage);
    }

    public async ValueTask<string> BeginQuarantineAsync(CancellationToken cancellationToken)
    {
        Task<string> quarantineTask;
        lock (_quarantineGate)
        {
            if (!_quarantineStarted)
            {
                _quarantineStarted = true;
                _quarantineTask = Task.Run(BeginQuarantineCoreAsync, CancellationToken.None);
            }
            else if (_quarantineTask is null)
            {
                return _quarantineCleanupMessage;
            }

            quarantineTask = _quarantineTask;
        }

        try
        {
            return await quarantineTask.WaitAsync(cancellationToken);
        }
        finally
        {
            if (quarantineTask.IsCompleted)
            {
                lock (_quarantineGate)
                {
                    if (ReferenceEquals(_quarantineTask, quarantineTask))
                    {
                        _quarantineTask = null;
                    }
                }
            }
        }
    }

    private async Task<string> BeginQuarantineCoreAsync()
    {
        var module = Interlocked.Exchange(ref _module, null);
        var cleanupMessage = $"InProc module '{ModuleId}' cleanup completed.";
        var cleanupCompleted = module is null;
        if (module is not null)
        {
            Task disposeTask;
            try
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                disposeTask = module.DisposeAsync(cleanupDeadline.Token).AsTask();
                _ = disposeTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                await disposeTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                cleanupCompleted = true;
            }
            catch (TimeoutException)
            {
                cleanupMessage = $"InProc module '{ModuleId}' ignored its two-second cleanup deadline; Runner restart may be required.";
            }
            catch (OperationCanceledException)
            {
                cleanupMessage = $"InProc module '{ModuleId}' exceeded its two-second cleanup deadline; Runner restart may be required.";
            }
            catch (Exception ex)
            {
                cleanupMessage = $"InProc module '{ModuleId}' cleanup failed: {LogRouter.Redact(ex.Message)}";
            }
        }

        RequestUnloadAndClear();
        lock (_quarantineGate)
        {
            _cleanupCompleted = cleanupCompleted;
            _quarantineCleanupMessage = cleanupMessage;
        }

        return cleanupMessage;
    }

    public async ValueTask<InProcUnloadResult> CompleteUnloadAsync(CancellationToken cancellationToken)
    {
        var cleanupMessage = await BeginQuarantineAsync(cancellationToken);
        return await CompleteUnloadAsync(cancellationToken, cleanupMessage);
    }

    private async ValueTask<InProcUnloadResult> CompleteUnloadAsync(CancellationToken cancellationToken, string cleanupMessage)
    {
        var unloaded = await WaitForUnloadAsync(cancellationToken);
        if (!CleanupCompleted)
        {
            return new InProcUnloadResult(
                ModuleId,
                PoolKey,
                false,
                "pending-runner-restart",
                $"InProc module '{ModuleId}' did not complete cleanup; restart Runner before loading it again. {cleanupMessage}",
                LoadContextName);
        }

        if (IsUnisolatedDevelopment)
        {
            return new InProcUnloadResult(
                ModuleId,
                PoolKey,
                true,
                "unisolated-development-stopped",
                $"Development-only default-AppDomain instance '{ModuleId}' stopped cleanly. {cleanupMessage}",
                LoadContextName);
        }

        return unloaded
            ? new InProcUnloadResult(ModuleId, PoolKey, true, "unloaded", $"InProc module '{ModuleId}' unloaded cleanly. {cleanupMessage}", LoadContextName)
            : new InProcUnloadResult(ModuleId, PoolKey, false, "pending-runner-restart", $"InProc module '{ModuleId}' did not release its collectible AssemblyLoadContext; restart Runner before loading it again. {cleanupMessage}", LoadContextName);
    }

    private static void RequestUnload(MptPluginLoadContext loadContext)
    {
        loadContext.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RequestUnloadAndClear()
    {
        var loadContext = Interlocked.Exchange(ref _loadContext, null);
        if (loadContext is not null)
        {
            RequestUnload(loadContext);
        }
    }

    private async ValueTask<bool> WaitForUnloadAsync(CancellationToken cancellationToken)
    {
        if (_loadContextReference is null)
        {
            return true;
        }

        var reference = _loadContextReference;
        var probe = Task.Run(
            () =>
            {
                for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(100);
                }

                return !reference.IsAlive;
            },
            CancellationToken.None);

        try
        {
            return await probe.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

internal sealed record InProcUnloadResult(
    string ModuleId,
    string PoolKey,
    bool Unloaded,
    string State,
    string Message,
    string LoadContextName);

internal sealed record InProcUnloadRecord(
    string ModuleId,
    string PoolKey,
    string State,
    string RestartPolicy,
    string Message,
    string LoadContextName,
    DateTimeOffset UpdatedAt);

internal sealed record InProcFaultState(
    string ModuleId,
    string OperationFamily,
    string LastOperation,
    int ConsecutiveFaults,
    bool CircuitOpen,
    string LastError,
    DateTimeOffset UpdatedAt,
    string CleanupMessage,
    long LastFaultSequence,
    bool RequiresRunnerRestart);

public sealed class InProcModuleCircuitOpenException : InvalidOperationException
{
    public InProcModuleCircuitOpenException(
        string moduleId,
        string operation,
        int consecutiveFaults,
        string message)
        : base(message)
    {
        ModuleId = moduleId;
        Operation = operation;
        ConsecutiveFaults = consecutiveFaults;
    }

    public string ModuleId { get; }
    public string Operation { get; }
    public int ConsecutiveFaults { get; }
}

internal sealed class InProcInvocationTimeoutException : TimeoutException
{
    public InProcInvocationTimeoutException(
        string moduleId,
        string operation,
        bool callbackStillRunning,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        ModuleId = moduleId;
        Operation = operation;
        CallbackStillRunning = callbackStillRunning;
    }

    public string ModuleId { get; }
    public string Operation { get; }
    public bool CallbackStillRunning { get; }
}

internal sealed class InProcStreamInvocationState
{
    private int _hasOutstandingCallback;

    public bool HasOutstandingCallback => Volatile.Read(ref _hasOutstandingCallback) != 0;

    public void MarkOutstanding()
    {
        Interlocked.Exchange(ref _hasOutstandingCallback, 1);
    }
}

internal sealed class InProcExecutionBoundary
{
    private readonly CancellationTokenSource _cancellation = new();

    public CancellationToken Token => _cancellation.Token;

    public void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed class InProcInvocationTracker
{
    private readonly object _gate = new();
    private int _activeCallbacks;
    private int _activeLifetimes;
    private TaskCompletionSource<bool> _drained = CompletedSignal();
    private TaskCompletionSource<bool> _callbacksDrained = CompletedSignal();

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _activeCallbacks + _activeLifetimes;
            }
        }
    }

    public IDisposable Enter()
    {
        return EnterCore(isCallback: true);
    }

    public IDisposable EnterLifetime()
    {
        return EnterCore(isCallback: false);
    }

    private IDisposable EnterCore(bool isCallback)
    {
        lock (_gate)
        {
            if (_activeCallbacks + _activeLifetimes == 0)
            {
                _drained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (isCallback)
            {
                if (_activeCallbacks == 0)
                {
                    _callbacksDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _activeCallbacks++;
            }
            else
            {
                _activeLifetimes++;
            }
        }

        return new InvocationLease(this, isCallback);
    }

    public async ValueTask<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task drainTask;
        lock (_gate)
        {
            if (_activeCallbacks + _activeLifetimes == 0)
            {
                return true;
            }

            drainTask = _drained.Task;
        }

        try
        {
            await drainTask.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public async ValueTask<bool> WaitForCallbackDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task drainTask;
        lock (_gate)
        {
            if (_activeCallbacks == 0)
            {
                return true;
            }

            drainTask = _callbacksDrained.Task;
        }

        try
        {
            await drainTask.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Exit(bool isCallback)
    {
        TaskCompletionSource<bool>? drained = null;
        TaskCompletionSource<bool>? callbacksDrained = null;
        lock (_gate)
        {
            if (isCallback)
            {
                if (_activeCallbacks <= 0)
                {
                    return;
                }

                _activeCallbacks--;
                if (_activeCallbacks == 0)
                {
                    callbacksDrained = _callbacksDrained;
                }
            }
            else
            {
                if (_activeLifetimes <= 0)
                {
                    return;
                }

                _activeLifetimes--;
            }

            if (_activeCallbacks + _activeLifetimes == 0)
            {
                drained = _drained;
            }
        }

        callbacksDrained?.TrySetResult(true);
        drained?.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }

    private sealed class InvocationLease : IDisposable
    {
        private InProcInvocationTracker? _owner;
        private readonly bool _isCallback;

        public InvocationLease(InProcInvocationTracker owner, bool isCallback)
        {
            _owner = owner;
            _isCallback = isCallback;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit(_isCallback);
        }
    }
}
