using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

public sealed class EventBus
{
    private readonly ConcurrentQueue<MyPowerTools.Abstractions.MptModuleEvent> _events = [];
    private long _seq;

    public ulong CurrentSeq => (ulong)Volatile.Read(ref _seq);

    public MyPowerTools.Abstractions.MptModuleEvent Publish(string sourceId, string type, JsonObject payload)
    {
        var seq = (ulong)Interlocked.Increment(ref _seq);
        var evt = new MyPowerTools.Abstractions.MptModuleEvent(sourceId, seq, type, DateTimeOffset.UtcNow, payload);
        _events.Enqueue(evt);

        while (_events.Count > 500 && _events.TryDequeue(out _))
        {
        }

        return evt;
    }

    public IReadOnlyList<MyPowerTools.Abstractions.MptModuleEvent> Since(ulong lastEventSeq)
    {
        return _events.Where(evt => evt.Seq > lastEventSeq).OrderBy(evt => evt.Seq).ToArray();
    }
}
