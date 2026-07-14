using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Bounded, monotonic-seq event bus for Service Unit lifecycle observations.
/// Mirrors the Runtime <c>EventBus</c> shape so subscribers reconcile via a cursor.
/// </summary>
public sealed class UnitEventBus
{
    private readonly ConcurrentQueue<ServiceUnitEvent> _events = new();
    private long _seq;
    private readonly int _capacity;

    public UnitEventBus(int capacity = 1000)
    {
        _capacity = capacity;
    }

    public ulong CurrentSeq => (ulong)Volatile.Read(ref _seq);

    public ServiceUnitEvent Publish(string unitId, string type, JsonObject payload)
    {
        var seq = (ulong)Interlocked.Increment(ref _seq);
        var evt = new ServiceUnitEvent(unitId, seq, type, DateTimeOffset.UtcNow, payload);
        _events.Enqueue(evt);

        while (_events.Count > _capacity && _events.TryDequeue(out _))
        {
        }

        return evt;
    }

    public IReadOnlyList<ServiceUnitEvent> Since(ulong lastEventSeq, string? unitId = null)
    {
        IEnumerable<ServiceUnitEvent> query = _events;
        if (!string.IsNullOrEmpty(unitId))
        {
            query = query.Where(evt => string.Equals(evt.UnitId, unitId, StringComparison.OrdinalIgnoreCase));
        }

        return query.Where(evt => evt.Seq > lastEventSeq).OrderBy(evt => evt.Seq).ToArray();
    }
}
