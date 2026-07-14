using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

/// <summary>
/// Lifecycle states for a Service Unit. Mirrors the ServiceManager wire enum.
/// </summary>
public enum ServiceUnitState
{
    Inactive = 0,
    Activating = 1,
    Active = 2,
    Degraded = 3,
    Failed = 4,
    Deactivating = 5
}

/// <summary>
/// Restart policy governing automatic recovery of a crashed unit process.
/// </summary>
public sealed record ServiceUnitRestartPolicy(int MaxRestarts, TimeSpan Backoff)
{
    public static ServiceUnitRestartPolicy Default { get; } = new(3, TimeSpan.FromSeconds(2));
}

/// <summary>
/// Readiness probe descriptor: how the ServiceManager decides a started unit is actually serving.
/// </summary>
public sealed record ServiceUnitReadiness(string Kind, string? Address, TimeSpan Timeout)
{
    public static ServiceUnitReadiness None { get; } = new("none", null, TimeSpan.Zero);
}

/// <summary>
/// Static definition of a Service Unit, as declared by a tool and enforced by the ServiceManager.
/// A unit is a long-running process whose life is independent of the Shell and Runner.
/// </summary>
public sealed record ServiceUnitManifest(
    string Id,
    string ToolId,
    string DisplayName,
    string Exec,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool Autostart = false,
    ServiceUnitRestartPolicy? RestartPolicy = null,
    ServiceUnitReadiness? Readiness = null,
    TimeSpan StopTimeout = default,
    IReadOnlyList<string>? DataRoots = null,
    IReadOnlyList<string>? DependsOn = null,
    string InstanceToken = "")
{
    public ServiceUnitRestartPolicy EffectiveRestartPolicy => RestartPolicy ?? ServiceUnitRestartPolicy.Default;
    public ServiceUnitReadiness EffectiveReadiness => Readiness ?? ServiceUnitReadiness.None;
}

/// <summary>
/// Point-in-time view of a unit's runtime state.
/// </summary>
public sealed record ServiceUnitSnapshot(
    string Id,
    string ToolId,
    string DisplayName,
    ServiceUnitState State,
    int? Pid,
    DateTimeOffset? StartedAt,
    TimeSpan? Uptime,
    string? Version,
    bool Autostart,
    ServiceUnitRestartPolicy RestartPolicy,
    int RestartCount,
    string? LastError,
    ServiceUnitReadiness? Readiness,
    int? ExitCode,
    ulong EventSeq);

/// <summary>
/// A unit state-change or lifecycle observation. <see cref="Seq"/> is monotonic.
/// </summary>
public sealed record ServiceUnitEvent(
    string UnitId,
    ulong Seq,
    string Type,
    DateTimeOffset Time,
    JsonObject Payload);

/// <summary>
/// Scoped client a tool Surface uses to observe and control only the units it declares.
/// The host (<see cref="IMptHostContext"/>) injects an instance bound to the current tool's id;
/// operations targeting units owned by other tools fail with a scope-denied error at the server.
/// This interface intentionally cannot reach the raw ServiceManager administration token.
/// </summary>
public interface IServiceUnitClient
{
    /// <summary>The tool id this client is scoped to.</summary>
    string ToolId { get; }

    /// <summary>Latest snapshot of every unit declared by the owning tool.</summary>
    ValueTask<IReadOnlyList<ServiceUnitSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<ServiceUnitSnapshot> GetSnapshotAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>Activates the unit. Idempotent: an active unit keeps its PID.</summary>
    ValueTask<ServiceUnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>Graceful-then-forceful stop within the unit's stop_timeout.</summary>
    ValueTask<ServiceUnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>Stop then start; yields a new PID.</summary>
    ValueTask<ServiceUnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>Reload manifests and reconcile state.</summary>
    ValueTask ReloadAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams state-change events for this tool's units from a cursor.</summary>
    IAsyncEnumerable<ServiceUnitEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken = default);

    /// <summary>Streams recent log lines for a unit.</summary>
    IAsyncEnumerable<MptToolLogEntry> TailLogsAsync(string unitId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Host injection point. Tool code resolves this from the host context and obtains a client
/// scoped to its own tool id. The unified Services page does NOT use this; it uses the
/// administration client directly.
/// </summary>
public interface IServiceUnitClientFactory
{
    /// <summary>Returns a scoped client bound to <paramref name="toolId"/>.</summary>
    IServiceUnitClient ForTool(string toolId);
}

/// <summary>
/// No-op default used when no ServiceManager is available (e.g. tool running outside the host).
/// All operations report the unit as inactive and yield no events.
/// </summary>
public sealed class NullServiceUnitClient : IServiceUnitClient
{
    public NullServiceUnitClient(string toolId)
    {
        ToolId = toolId;
    }

    public string ToolId { get; }

    public ValueTask<IReadOnlyList<ServiceUnitSnapshot>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ServiceUnitSnapshot>>(Array.Empty<ServiceUnitSnapshot>());

    public ValueTask<ServiceUnitSnapshot> GetSnapshotAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new KeyNotFoundException($"Service unit '{unitId}' is not available (no ServiceManager).");

    public ValueTask<ServiceUnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No ServiceManager is available to start units.");

    public ValueTask<ServiceUnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No ServiceManager is available to stop units.");

    public ValueTask<ServiceUnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("No ServiceManager is available to restart units.");

    public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ServiceUnitEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<MptToolLogEntry> TailLogsAsync(string unitId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
