using System.Collections.Concurrent;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Per-unit bounded ring of captured stdout/stderr lines plus a recent-error summary.
/// Lines are captured from the supervised process's redirected streams.
/// </summary>
public sealed class UnitLogStore
{
    private readonly ConcurrentQueue<MptToolLogEntry> _entries = new();
    private readonly int _capacity;
    private MptToolLogEntry? _lastError;

    public UnitLogStore(int capacity = 500)
    {
        _capacity = capacity;
    }

    public void Append(string level, string category, string message, string stream)
    {
        var entry = new MptToolLogEntry(DateTimeOffset.UtcNow, level, category, message, null);
        _entries.Enqueue(entry);

        while (_entries.Count > _capacity && _entries.TryDequeue(out _))
        {
        }

        if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stream, "stderr", StringComparison.OrdinalIgnoreCase))
        {
            _lastError = entry;
        }
    }

    public IReadOnlyList<MptToolLogEntry> Tail(int count)
    {
        var snapshot = _entries.ToArray();
        if (snapshot.Length <= count)
        {
            return snapshot;
        }

        return snapshot.Skip(snapshot.Length - count).ToArray();
    }

    public MptToolLogEntry? LastError => _lastError;
}
