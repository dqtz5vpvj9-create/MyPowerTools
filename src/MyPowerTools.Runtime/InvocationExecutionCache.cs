using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class InvocationExecutionCache
{
    public const int DefaultMaxCount = 1000;
    public static readonly TimeSpan DefaultCompletedTtl = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, InvocationExecutionEntry> _entries;
    private readonly object _gate = new();
    private readonly int _maxCount;
    private readonly TimeSpan _completedTtl;
    private readonly Func<DateTimeOffset> _utcNow;

    public InvocationExecutionCache(int maxCount = DefaultMaxCount, TimeSpan? completedTtl = null, Func<DateTimeOffset>? utcNow = null)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), "maxCount must be positive.");
        }

        _maxCount = maxCount;
        _completedTtl = completedTtl ?? DefaultCompletedTtl;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _entries = new Dictionary<string, InvocationExecutionEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public Task<Sdk.CommandExecutionResult> GetOrAdd(string invocationId, Func<Task<Sdk.CommandExecutionResult>> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(factory);

        Cleanup();

        InvocationExecutionEntry entry;
        lock (_gate)
        {
            if (_entries.TryGetValue(invocationId, out entry!))
            {
                return entry.Task;
            }

            entry = new InvocationExecutionEntry(_utcNow());
            _entries[invocationId] = entry;
            entry.Task = RunAndCompleteAsync(invocationId, entry, factory);
        }

        return entry.Task;
    }

    public bool IsCompleted(string invocationId)
    {
        if (string.IsNullOrWhiteSpace(invocationId))
        {
            return false;
        }

        Cleanup();
        lock (_gate)
        {
            return _entries.TryGetValue(invocationId, out var entry) && entry.IsCompleted;
        }
    }

    public void Cleanup()
    {
        var now = _utcNow();
        lock (_gate)
        {
            CleanupCore(now);
        }
    }

    private async Task<Sdk.CommandExecutionResult> RunAndCompleteAsync(
        string invocationId,
        InvocationExecutionEntry entry,
        Func<Task<Sdk.CommandExecutionResult>> factory)
    {
        try
        {
            var result = await factory();
            lock (_gate)
            {
                entry.MarkCompleted(_utcNow());
                CleanupCore(_utcNow());
            }

            return result;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_entries.GetValueOrDefault(invocationId), entry))
                {
                    _entries.Remove(invocationId);
                }
            }

            throw;
        }
    }

    private void CleanupCore(DateTimeOffset now)
    {
        foreach (var stale in _entries
                     .Where(pair => pair.Value.IsCompleted && pair.Value.CompletedAt + _completedTtl <= now)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(stale);
        }

        if (_entries.Count <= _maxCount)
        {
            return;
        }

        foreach (var overflow in _entries
                     .Where(pair => pair.Value.IsCompleted)
                     .OrderBy(pair => pair.Value.CompletedAt)
                     .ThenBy(pair => pair.Value.CreatedAt)
                     .Take(Math.Max(0, _entries.Count - _maxCount))
                     .Select(pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(overflow);
        }
    }

    private sealed class InvocationExecutionEntry
    {
        public InvocationExecutionEntry(DateTimeOffset createdAt)
        {
            CreatedAt = createdAt;
            CompletedAt = DateTimeOffset.MaxValue;
        }

        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset CompletedAt { get; private set; }
        public bool IsCompleted { get; private set; }
        public Task<Sdk.CommandExecutionResult> Task { get; set; } = System.Threading.Tasks.Task.FromException<Sdk.CommandExecutionResult>(
            new InvalidOperationException("Invocation execution was not initialized."));

        public void MarkCompleted(DateTimeOffset completedAt)
        {
            CompletedAt = completedAt;
            IsCompleted = true;
        }
    }
}
