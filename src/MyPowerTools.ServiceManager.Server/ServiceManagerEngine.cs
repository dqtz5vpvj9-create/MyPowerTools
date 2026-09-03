using System.Collections.Concurrent;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Coordinator over the catalog, supervisors, event bus and state store.
/// Enforces scope: a caller identified by <paramref name="callerToolId"/> may only
/// observe/operate units whose <see cref="ServiceUnitManifest.ToolId"/> matches.
/// Administration callers pass a null/empty tool id to bypass scope (cross-tool visibility).
/// </summary>
public sealed class ServiceManagerEngine : IAsyncDisposable
{
    private readonly ServiceUnitCatalog _catalog;
    private readonly UnitEventBus _events;
    private readonly UnitStateStore _stateStore;
    private readonly ConcurrentDictionary<string, UnitSupervisor> _supervisors = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadGate = new(1, 1);
    private int _disposed;

    public ServiceManagerEngine(ServiceUnitCatalog catalog, UnitEventBus events, UnitStateStore stateStore)
    {
        _catalog = catalog;
        _events = events;
        _stateStore = stateStore;
    }

    public UnitEventBus Events => _events;

    public async Task<int> ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            return await ReloadCoreAsync(activateExisting: false, cancellationToken);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>
    /// On ServiceManager startup, attempt to re-adopt any still-running unit for which we have
    /// persisted state, then autostart the rest. Re-adoption never restarts a live process.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            await ReloadCoreAsync(activateExisting: true, cancellationToken);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private async Task<int> ReloadCoreAsync(bool activateExisting, CancellationToken cancellationToken)
    {
        var count = _catalog.Reload();
        var manifests = _catalog.Manifests.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var unitId in _supervisors.Keys.Where(unitId => !manifests.ContainsKey(unitId)).ToArray())
        {
            if (_supervisors.TryRemove(unitId, out var removed))
            {
                await removed.StopAsync(cancellationToken);
                await removed.DisposeAsync();
            }
        }

        var activation = new List<UnitSupervisor>();
        foreach (var manifest in OrderByDependencies(manifests.Values))
        {
            if (!_supervisors.TryGetValue(manifest.Id, out var supervisor))
            {
                supervisor = new UnitSupervisor(manifest, _events, _stateStore);
                _supervisors[manifest.Id] = supervisor;
                activation.Add(supervisor);
                continue;
            }

            if (!ManifestEquals(supervisor.Manifest, manifest))
            {
                await supervisor.ApplyManifestAsync(manifest, cancellationToken);
            }

            if (activateExisting)
            {
                activation.Add(supervisor);
            }
        }

        // A supervisor created here has no process yet; the reload that introduced it is the only
        // place that can adopt or autostart it, and it runs after every dependency of the unit.
        foreach (var supervisor in activation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await supervisor.TryReadoptAsync(cancellationToken))
            {
                continue;
            }

            if (supervisor.Manifest.Autostart)
            {
                await supervisor.StartAsync(cancellationToken);
            }
        }

        return count;
    }

    /// <summary>
    /// Orders units so that every unit follows the units it declares in <c>dependsOn</c>.
    /// Units with no relationship keep a stable order by id. A dependency that is not installed is
    /// simply skipped, and a cycle is reported and then broken at the unit that closes it — an
    /// unstartable catalog is a worse outcome than an imperfect order.
    /// </summary>
    internal static IReadOnlyList<ServiceUnitManifest> OrderByDependencies(
        IReadOnlyCollection<ServiceUnitManifest> manifests)
    {
        var byId = new Dictionary<string, ServiceUnitManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            byId[manifest.Id] = manifest;
        }

        var ordered = new List<ServiceUnitManifest>(manifests.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ServiceUnitManifest manifest)
        {
            if (placed.Contains(manifest.Id))
            {
                return;
            }

            if (!visiting.Add(manifest.Id))
            {
                Console.Error.WriteLine(
                    $"[ServiceManager] unit '{manifest.Id}' is part of a dependsOn cycle; starting it without waiting for that dependency.");
                return;
            }

            foreach (var dependency in manifest.DependsOn ?? Array.Empty<string>())
            {
                if (byId.TryGetValue(dependency, out var required))
                {
                    Visit(required);
                }
            }

            visiting.Remove(manifest.Id);
            placed.Add(manifest.Id);
            ordered.Add(manifest);
        }

        foreach (var manifest in manifests.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            Visit(manifest);
        }

        return ordered;
    }

    private UnitSupervisor RequireSupervisor(string unitId)
    {
        if (_supervisors.TryGetValue(unitId, out var supervisor))
        {
            return supervisor;
        }

        throw new KeyNotFoundException($"Service unit '{unitId}' is not registered.");
    }

    private void EnsureScope(UnitSupervisor supervisor, string? callerToolId)
    {
        if (string.IsNullOrEmpty(callerToolId))
        {
            return; // administration caller — cross-tool access allowed
        }

        if (!string.Equals(supervisor.ToolId, callerToolId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ServiceUnitScopeDeniedException(supervisor.UnitId, supervisor.ToolId, callerToolId);
        }
    }

    public IReadOnlyList<ServiceUnitSnapshot> List(string? callerToolId = null, ServiceUnitState? stateFilter = null)
    {
        var query = _supervisors.Values.AsEnumerable();
        if (!string.IsNullOrEmpty(callerToolId))
        {
            query = query.Where(s => string.Equals(s.ToolId, callerToolId, StringComparison.OrdinalIgnoreCase));
        }

        var snapshots = query.Select(s => s.Snapshot());
        if (stateFilter is not null)
        {
            snapshots = snapshots.Where(s => s.State == stateFilter.Value);
        }

        return snapshots.ToArray();
    }

    public ServiceUnitSnapshot GetSnapshot(string unitId, string? callerToolId = null)
    {
        var supervisor = RequireSupervisor(unitId);
        EnsureScope(supervisor, callerToolId);
        return supervisor.Snapshot();
    }

    public async Task<ServiceUnitSnapshot> StartAsync(string unitId, string? callerToolId = null, CancellationToken cancellationToken = default)
    {
        var supervisor = RequireSupervisor(unitId);
        EnsureScope(supervisor, callerToolId);
        return await supervisor.StartAsync(cancellationToken);
    }

    public async Task<ServiceUnitSnapshot> StopAsync(string unitId, string? callerToolId = null, CancellationToken cancellationToken = default)
    {
        var supervisor = RequireSupervisor(unitId);
        EnsureScope(supervisor, callerToolId);
        return await supervisor.StopAsync(cancellationToken);
    }

    public async Task<ServiceUnitSnapshot> RestartAsync(string unitId, string? callerToolId = null, CancellationToken cancellationToken = default)
    {
        var supervisor = RequireSupervisor(unitId);
        EnsureScope(supervisor, callerToolId);
        return await supervisor.RestartAsync(cancellationToken);
    }

    public IReadOnlyList<MptToolLogEntry> TailLogs(string unitId, int tailLines, string? callerToolId = null)
    {
        var supervisor = RequireSupervisor(unitId);
        EnsureScope(supervisor, callerToolId);
        return supervisor.TailLogs(tailLines);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var supervisor in _supervisors.Values)
        {
            await supervisor.DisposeAsync();
        }

        _supervisors.Clear();
        _reloadGate.Dispose();
    }

    private static bool ManifestEquals(ServiceUnitManifest left, ServiceUnitManifest right)
    {
        return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ToolId, right.ToolId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
               PathEquals(left.Exec, right.Exec) &&
               left.Arguments.SequenceEqual(right.Arguments, StringComparer.Ordinal) &&
               PathEquals(left.WorkingDirectory, right.WorkingDirectory) &&
               DictionaryEquals(left.Environment, right.Environment) &&
               left.Autostart == right.Autostart &&
               left.EffectiveRestartPolicy == right.EffectiveRestartPolicy &&
               left.EffectiveReadiness == right.EffectiveReadiness &&
               left.StopTimeout == right.StopTimeout &&
               SequenceEquals(left.DataRoots, right.DataRoots, PathComparer) &&
               SequenceEquals(left.DependsOn, right.DependsOn, StringComparer.OrdinalIgnoreCase) &&
               string.Equals(left.InstanceToken, right.InstanceToken, StringComparison.Ordinal);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool PathEquals(string? left, string? right)
        => PathComparer.Equals(left ?? string.Empty, right ?? string.Empty);

    private static bool SequenceEquals(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right,
        StringComparer comparer)
        => (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), comparer);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        left ??= new Dictionary<string, string>();
        right ??= new Dictionary<string, string>();
        return left.Count == right.Count &&
               left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }
}

/// <summary>
/// Thrown when a scoped caller attempts to operate a unit owned by a different tool.
/// Maps to <c>MPT_SCOPE_DENIED</c> on the wire.
/// </summary>
public sealed class ServiceUnitScopeDeniedException : Exception
{
    public ServiceUnitScopeDeniedException(string unitId, string ownerToolId, string callerToolId)
        : base($"Scope denied: unit '{unitId}' belongs to tool '{ownerToolId}', caller is '{callerToolId}'.")
    {
        UnitId = unitId;
        OwnerToolId = ownerToolId;
        CallerToolId = callerToolId;
    }

    public string UnitId { get; }
    public string OwnerToolId { get; }
    public string CallerToolId { get; }
}
