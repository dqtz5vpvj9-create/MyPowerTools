using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly Queue<MyPowerTools.Abstractions.MptModuleEvent> _events = new();
    private TaskCompletionSource _changed = NewSignal();
    private long _seq;

    public ulong CurrentSeq => (ulong)Volatile.Read(ref _seq);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MyPowerTools.Abstractions.MptModuleEvent Publish(string sourceId, string type, JsonObject payload)
    {
        TaskCompletionSource changed;
        MyPowerTools.Abstractions.MptModuleEvent evt;
        lock (_gate)
        {
            evt = new(sourceId, (ulong)++_seq, type, DateTimeOffset.UtcNow, payload);
            _events.Enqueue(evt);
            while (_events.Count > 500) _events.Dequeue();
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
        return evt;
    }

    public IReadOnlyList<MyPowerTools.Abstractions.MptModuleEvent> Since(ulong lastEventSeq)
    {
        lock (_gate) return _events.Where(evt => evt.Seq > lastEventSeq).ToArray();
    }

    // Check the cursor and capture the broadcast signal under the same lock as
    // Publish, so an event between reading history and waiting is never missed.
    public Task WaitForEventsAsync(ulong lastEventSeq, CancellationToken cancellationToken)
    {
        lock (_gate)
            return (ulong)_seq > lastEventSeq
                ? Task.CompletedTask
                : _changed.Task.WaitAsync(cancellationToken);
    }
}
