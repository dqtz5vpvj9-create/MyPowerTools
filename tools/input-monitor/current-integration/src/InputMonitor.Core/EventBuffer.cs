namespace InputMonitor.Core;

public sealed class EventBuffer : IDisposable
{
    private readonly int _capacity;
    private readonly TimeSpan _flushInterval;
    private readonly object _gate = new();
    private readonly List<InputEventRecord> _buffer = [];
    private readonly Timer _timer;
    private bool _disposed;

    public Action<IReadOnlyList<InputEventRecord>>? OnFlush { get; set; }

    public EventBuffer(int capacity = 500, TimeSpan? flushInterval = null)
    {
        _capacity = capacity;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        _timer = new Timer(_ => Flush(), null, _flushInterval, _flushInterval);
    }

    public void Append(InputEventRecord record)
    {
        IReadOnlyList<InputEventRecord>? batch = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Add(record);
            if (_buffer.Count >= _capacity)
            {
                batch = DrainLocked();
            }
        }

        if (batch is not null)
        {
            OnFlush?.Invoke(batch);
        }
    }

    public void Flush()
    {
        IReadOnlyList<InputEventRecord> batch;
        lock (_gate)
        {
            batch = DrainLocked();
        }

        if (batch.Count > 0)
        {
            OnFlush?.Invoke(batch);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _timer.Dispose();
        Flush();
    }

    private IReadOnlyList<InputEventRecord> DrainLocked()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        var batch = _buffer.ToArray();
        _buffer.Clear();
        return batch;
    }
}
