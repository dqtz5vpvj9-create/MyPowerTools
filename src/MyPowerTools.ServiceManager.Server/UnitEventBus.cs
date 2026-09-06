using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Bounded, monotonic-seq event bus for Service Unit lifecycle observations.
/// Mirrors the Runtime <c>EventBus</c> shape so subscribers reconcile via a cursor.
/// </summary>
public sealed class UnitEventBus
{
    private readonly object _gate = new();
    private readonly Queue<ServiceUnitEvent> _events = new();
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _seq;
    private readonly int _capacity;

    public UnitEventBus(int capacity = 1000)
    {
        _capacity = Math.Max(0, capacity);
    }

    public ulong CurrentSeq => (ulong)Volatile.Read(ref _seq);

    public ServiceUnitEvent Publish(string unitId, string type, JsonObject payload)
    {
        TaskCompletionSource changed;
        ServiceUnitEvent evt;
        lock (_gate)
        {
            evt = new ServiceUnitEvent(unitId, (ulong)++_seq, type, DateTimeOffset.UtcNow, payload);
            _events.Enqueue(evt);
            while (_events.Count > _capacity) _events.Dequeue();
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();

        return evt;
    }

    public IReadOnlyList<ServiceUnitEvent> Since(ulong lastEventSeq, string? unitId = null)
    {
        lock (_gate)
        {
            return _events.Where(evt => evt.Seq > lastEventSeq &&
                (string.IsNullOrEmpty(unitId) || string.Equals(evt.UnitId, unitId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    public Task WaitForEventsAsync(ulong lastEventSeq, CancellationToken cancellationToken)
    {
        lock (_gate)
            return (ulong)_seq > lastEventSeq
                ? Task.CompletedTask
                : _changed.Task.WaitAsync(cancellationToken);
    }
}
