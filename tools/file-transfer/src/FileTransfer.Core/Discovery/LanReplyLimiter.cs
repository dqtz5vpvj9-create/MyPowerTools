using System.Net;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Bounds how often the advertiser answers one source address, so an MPT install on a LAN cannot be
/// used to amplify a spoofed who-is flood. The state is one timestamp per source, capped in size and
/// swept lazily, so the advertised service stays event driven and does not grow without bound.
/// </summary>
public sealed class LanReplyLimiter
{
    private readonly TimeSpan _interval;
    private readonly int _capacity;
    private readonly Dictionary<string, long> _lastReply = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="interval">Minimum time between two answers to the same source. Default one second.</param>
    /// <param name="capacity">Maximum tracked sources; the oldest entries are dropped first.</param>
    public LanReplyLimiter(TimeSpan? interval = null, int capacity = 64)
    {
        _interval = interval ?? TimeSpan.FromSeconds(1);
        if (_interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_gate) return _lastReply.Count; }
    }

    /// <summary>Uses the current monotonic tick as the timestamp.</summary>
    public bool TryAccept(IPAddress source) => TryAccept(source, Environment.TickCount64);

    /// <summary>Deterministic overload; <paramref name="timestampMs"/> is an <see cref="Environment.TickCount64"/> style value.</summary>
    public bool TryAccept(IPAddress source, long timestampMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        var key = source.ToString();
        lock (_gate)
        {
            if (_lastReply.TryGetValue(key, out var last) && timestampMs - last < _interval.TotalMilliseconds) return false;
            if (_lastReply.Count >= _capacity) Sweep(timestampMs);
            _lastReply[key] = timestampMs;
            return true;
        }
    }

    private void Sweep(long timestampMs)
    {
        var cutoff = timestampMs - (long)_interval.TotalMilliseconds;
        foreach (var entry in _lastReply.Where(entry => entry.Value <= cutoff).Select(entry => entry.Key).ToArray())
            _lastReply.Remove(entry);
        if (_lastReply.Count < _capacity) return;
        // Still full of recent senders: drop the oldest half so the table cannot grow past the cap.
        foreach (var entry in _lastReply.OrderBy(entry => entry.Value).Take(_capacity / 2 + 1).Select(entry => entry.Key).ToArray())
            _lastReply.Remove(entry);
    }
}
