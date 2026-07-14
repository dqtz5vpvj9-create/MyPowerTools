using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.ServiceManager.Client;

/// <summary>
/// Scoped <see cref="IServiceUnitClient"/> bound to a single tool id. It delegates to a
/// <see cref="ServiceManagerAdminClient"/> but injects the <c>x-mpt-caller-tool</c> header on every
/// call, so the ServiceManager server enforces that only the owning tool's units are visible/controllable.
/// The scoped client never holds the raw admin token; the header carries only the tool identity.
/// </summary>
public sealed class ScopedServiceUnitClient : IServiceUnitClient
{
    private readonly ServiceManagerAdminClient _scoped;
    private readonly string _toolId;

    public ScopedServiceUnitClient(ServiceManagerAdminClient admin, string toolId)
    {
        _scoped = admin.WithScope(toolId);
        _toolId = toolId;
    }

    public string ToolId => _toolId;

    public async ValueTask<IReadOnlyList<ServiceUnitSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        var response = await _scoped.ListUnitsAsync(_toolId, SM.UnitState.Unspecified, cancellationToken);
        return response.Units.Select(FromProto).ToList();
    }

    public async ValueTask<ServiceUnitSnapshot> GetSnapshotAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _scoped.GetUnitAsync(unitId, cancellationToken);
        return FromProto(snapshot);
    }

    public async ValueTask<ServiceUnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _scoped.StartAsync(unitId, cancellationToken);
        return FromProto(snapshot);
    }

    public async ValueTask<ServiceUnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _scoped.StopAsync(unitId, cancellationToken);
        return FromProto(snapshot);
    }

    public async ValueTask<ServiceUnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _scoped.RestartAsync(unitId, cancellationToken);
        return FromProto(snapshot);
    }

    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default)
    {
        // Reload is an administration operation; scoped clients observe the result via events.
        await _scoped.ReloadAsync(cancellationToken);
    }

    public async IAsyncEnumerable<ServiceUnitEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _scoped.SubscribeUnitEventsAsync(cursor.LastEventSeq, unitId: null, cancellationToken))
        {
            yield return FromProto(evt);
        }
    }

    public async IAsyncEnumerable<MptToolLogEntry> TailLogsAsync(string unitId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await _scoped.TailLogsAsync(unitId, 200, cancellationToken);
        foreach (var entry in entries)
        {
            yield return FromProto(entry);
        }
    }

    private static ServiceUnitSnapshot FromProto(SM.UnitSnapshot s)
    {
        var restartParts = (s.RestartPolicy ?? "").Split('/');
        var maxRestarts = restartParts.Length > 0 && int.TryParse(restartParts[0].TrimEnd('m', 's'), out var mr) ? mr : 3;
        var backoffMs = restartParts.Length > 1 && int.TryParse(restartParts[1].TrimEnd('m', 's'), out var bf) ? bf : 2000;
        return new ServiceUnitSnapshot(
            Id: s.UnitId,
            ToolId: s.ToolId,
            DisplayName: s.DisplayName,
            State: MapToEngine(s.State),
            Pid: s.Pid > 0 ? s.Pid : null,
            StartedAt: ToDateTimeOffset(s.StartedAt),
            Uptime: s.Uptime is null ? null : s.Uptime.ToTimeSpan(),
            Version: s.Version,
            Autostart: s.Autostart,
            RestartPolicy: new ServiceUnitRestartPolicy(maxRestarts, TimeSpan.FromMilliseconds(backoffMs)),
            RestartCount: s.RestartCount,
            LastError: string.IsNullOrEmpty(s.LastError) ? null : s.LastError,
            Readiness: null,
            ExitCode: s.ExitCode != 0 ? s.ExitCode : null,
            EventSeq: s.EventSeq);
    }

    private static DateTimeOffset ToDateTimeOffset(Google.Protobuf.WellKnownTypes.Timestamp? ts)
        => ts is null || ts.Seconds == 0 && ts.Nanos == 0 ? DateTimeOffset.UtcNow : ts.ToDateTimeOffset();

    private static ServiceUnitEvent FromProto(SM.UnitEvent e)
        => new(e.UnitId, e.Seq, e.Type, ToDateTimeOffset(e.Time), new JsonObject());

    private static MptToolLogEntry FromProto(SM.LogEntry e)
        => new(ToDateTimeOffset(e.Time), e.Level, e.Category, e.Message, null);

    private static ServiceUnitState MapToEngine(SM.UnitState state) => state switch
    {
        SM.UnitState.Inactive => ServiceUnitState.Inactive,
        SM.UnitState.Activating => ServiceUnitState.Activating,
        SM.UnitState.Active => ServiceUnitState.Active,
        SM.UnitState.Degraded => ServiceUnitState.Degraded,
        SM.UnitState.Failed => ServiceUnitState.Failed,
        SM.UnitState.Deactivating => ServiceUnitState.Deactivating,
        _ => ServiceUnitState.Inactive
    };
}

/// <summary>
/// Default <see cref="IServiceUnitClientFactory"/>: returns scoped clients bound to a shared
/// admin client, or a <see cref="NullServiceUnitClient"/> when no ServiceManager is reachable.
/// </summary>
public sealed class ServiceUnitClientFactory : IServiceUnitClientFactory
{
    private readonly ServiceManagerAdminClient _admin;

    public ServiceUnitClientFactory(ServiceManagerAdminClient admin)
    {
        _admin = admin;
    }

    public IServiceUnitClient ForTool(string toolId) => new ScopedServiceUnitClient(_admin, toolId);
}
