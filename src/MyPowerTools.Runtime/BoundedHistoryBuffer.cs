using System.Globalization;

namespace MyPowerTools.Runtime;

/// <summary>Per-process retention limits. Bytes charge UTF-16 text plus a fixed record allowance.</summary>
public sealed record HistoryBufferOptions(int MaxEntries, long MaxBytes, int MaxRecordBytes)
{
    public static HistoryBufferOptions CommandDefaults { get; } = new(4096, 4 * 1024 * 1024, 16 * 1024);
    public static HistoryBufferOptions NotificationDefaults { get; } = new(2048, 2 * 1024 * 1024, 16 * 1024);

    internal void Validate()
    {
        if (MaxEntries <= 0 || MaxBytes < HistoryRecordText.RecordOverheadBytes
            || MaxRecordBytes < HistoryRecordText.RecordOverheadBytes || MaxRecordBytes > MaxBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(HistoryBufferOptions),
                "History requires positive entries and 512 <= record bytes <= buffer bytes.");
        }
    }
}

/// <summary>Unfiltered, lifetime counters; timestamps refer to the last removed record.</summary>
public sealed record HistoryBufferStats(
    int Count, long RetainedBytes, int MaxEntries, long MaxBytes, int MaxRecordBytes,
    long AddedCount, long EvictedCount, long TruncatedCount, long TruncatedBytes,
    long RejectedCount, int UnreadCount, long EvictedUnreadCount,
    DateTimeOffset? LastEvictedAt, DateTimeOffset? LastEvictedUnreadAt,
    long UnretainedCompletionCount);

// Owners hold their gate for every access. Retention never owns command execution,
// cancellation sources, notification actions, or a persistent archive.
internal sealed class BoundedHistoryBuffer<T>(HistoryBufferOptions options, bool preferReadEviction = false)
{
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byId = new(StringComparer.Ordinal);
    private long _bytes, _added, _evicted, _truncated, _truncatedBytes, _rejected, _evictedUnread, _unretainedCompletions;
    private int _unread;
    private DateTimeOffset? _lastEvicted, _lastEvictedUnread;

    public int Count => _entries.Count;
    public IEnumerable<T> Values => _entries.Select(entry => entry.Value);

    public bool TryGet(string id, out T value)
    {
        if (_byId.TryGetValue(id, out var node))
        {
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public bool TryFind(Func<T, bool> predicate, out string id, out T value)
    {
        foreach (var entry in _entries)
        {
            if (!predicate(entry.Value)) continue;
            id = entry.Id;
            value = entry.Value;
            return true;
        }
        id = "";
        value = default!;
        return false;
    }

    public void Reject() => _rejected++;
    public void RecordUnretainedCompletion() => _unretainedCompletions++;

    public void Add(string id, T value, long bytes, long originalBytes, DateTimeOffset time, bool unread = false)
    {
        if (_byId.TryGetValue(id, out var previous)) Remove(previous, eviction: false);
        var node = _entries.AddLast(new Entry(id, value, bytes, time, unread));
        _byId.Add(id, node);
        _bytes += bytes;
        if (unread) _unread++;
        _added++;
        RecordTruncation(bytes, originalBytes);
        Prune();
    }

    public void Update(string id, T value, long bytes, long originalBytes, bool unread = false, bool countTruncation = true)
    {
        var node = _byId[id];
        var previous = node.Value;
        _bytes += bytes - previous.Bytes;
        _unread += (unread ? 1 : 0) - (previous.Unread ? 1 : 0);
        node.Value = previous with { Value = value, Bytes = bytes, Unread = unread };
        if (countTruncation) RecordTruncation(bytes, originalBytes);
        Prune();
    }

    public HistoryBufferStats GetStats() => new(Count, _bytes, options.MaxEntries, options.MaxBytes,
        options.MaxRecordBytes, _added, _evicted, _truncated, _truncatedBytes, _rejected,
        _unread, _evictedUnread, _lastEvicted, _lastEvictedUnread, _unretainedCompletions);

    private void RecordTruncation(long bytes, long originalBytes)
    {
        if (originalBytes <= bytes) return;
        _truncated++;
        _truncatedBytes += originalBytes - bytes;
    }

    private void Prune()
    {
        while (Count > options.MaxEntries || _bytes > options.MaxBytes)
        {
            var oldest = _entries.First!;
            if (preferReadEviction && _unread != Count)
            {
                while (oldest.Value.Unread) oldest = oldest.Next!;
            }
            Remove(oldest, eviction: true);
        }
    }

    private void Remove(LinkedListNode<Entry> node, bool eviction)
    {
        var entry = node.Value;
        _entries.Remove(node);
        _byId.Remove(entry.Id);
        _bytes -= entry.Bytes;
        if (entry.Unread) _unread--;
        if (!eviction) return;
        _evicted++;
        _lastEvicted = entry.Time;
        if (entry.Unread)
        {
            _evictedUnread++;
            _lastEvictedUnread = entry.Time;
        }
    }

    private sealed record Entry(string Id, T Value, long Bytes, DateTimeOffset Time, bool Unread);
}

internal static class HistoryRecordText
{
    // Covers record/entry/list/dictionary bookkeeping and string headers, not exact
    // CLR/RSS measurement. All variable-sized retained text is charged separately.
    public const int RecordOverheadBytes = 512;
    private const string TruncationMarker = "… [truncated]";

    public static long Bytes(string value) => (long)value.Length * sizeof(char);

    // Keep complete Unicode text elements, including surrogate pairs, combining
    // characters and emoji sequences. Redaction must happen BEFORE this operation.
    public static string Fit(string value, long maxBytes)
    {
        if (Bytes(value) <= maxBytes) return value;
        var maxChars = (int)Math.Max(0, maxBytes / sizeof(char));
        var marker = maxChars >= TruncationMarker.Length ? TruncationMarker : "";
        var contentChars = maxChars - marker.Length;
        var length = 0;
        while (length < value.Length && length < contentChars)
        {
            var next = StringInfo.GetNextTextElementLength(value.AsSpan(length));
            if (length + next > contentChars) break;
            length += next;
        }
        return string.Concat(value.AsSpan(0, length), marker);
    }
}
