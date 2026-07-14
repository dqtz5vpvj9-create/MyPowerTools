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
    private int _disposed;

    public ServiceManagerEngine(ServiceUnitCatalog catalog, UnitEventBus events, UnitStateStore stateStore)
    {
        _catalog = catalog;
        _events = events;
        _stateStore = stateStore;
    }

    public UnitEventBus Events => _events;

    public int Reload()
    {
        var count = _catalog.Reload();
        // Ensure a supervisor exists for every known manifest.
        foreach (var manifest in _catalog.Manifests)
        {
            _supervisors.GetOrAdd(manifest.Id, id => new UnitSupervisor(manifest, _events, _stateStore));
        }

        return count;
    }

    /// <summary>
    /// On ServiceManager startup, attempt to re-adopt any still-running unit for which we have
    /// persisted state, then autostart the rest. Re-adoption never restarts a live process.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        Reload();
        foreach (var (unitId, supervisor) in _supervisors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (supervisor.TryReadopt())
            {
                continue;
            }

            if (supervisor.Manifest.Autostart)
            {
                await supervisor.StartAsync(cancellationToken);
            }
        }
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
