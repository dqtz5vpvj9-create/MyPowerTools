using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class CommandHistory
{
    private readonly HistoryBufferOptions _options;
    private readonly BoundedHistoryBuffer<CommandHistoryRecord> _records;
    private readonly object _gate = new();
    private ulong _nextRecordId;

    public CommandHistory() : this(null) { }

    public CommandHistory(HistoryBufferOptions? options)
    {
        _options = options ?? HistoryBufferOptions.CommandDefaults;
        _options.Validate();
        _records = new(_options);
    }

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    public HistoryBufferStats GetStats()
    {
        lock (_gate) return _records.GetStats();
    }

    public CommandHistoryRecord Add(Sdk.CommandRequest request, Sdk.MptCommandDescriptor? descriptor, string state)
    {
        var record = Fit(new CommandHistoryRecord(request.InvocationId, request.CommandId,
            descriptor?.ModuleId ?? "", DateTimeOffset.UtcNow, LogRouter.Redact(state), ""));
        lock (_gate)
        {
            if (!record.IsRetained) _records.Reject();
            else _records.Add((++_nextRecordId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                record, record.RetainedBytes, record.OriginalBytes, record.StartedAt);
        }
        return record;
    }

    public void Complete(Sdk.CommandExecutionResult result)
    {
        lock (_gate)
        {
            // Duplicate invocation IDs have always been separate history records;
            // complete the first retained match, just as the original List did.
            if (!_records.TryFind(item => item.InvocationId == result.InvocationId, out var recordId, out var current))
            {
                // Execution/cancellation belongs to MptHostRuntime. Do not resurrect
                // evicted history or keep a second unbounded active-history map.
                _records.RecordUnretainedCompletion();
                return;
            }
            var rawSummary = string.IsNullOrWhiteSpace(result.Output) ? result.Error?.Message ?? "" : result.Output;
            var record = Fit(current with { State = LogRouter.Redact(result.State), Summary = LogRouter.Redact(rawSummary) });
            _records.Update(recordId, record, record.RetainedBytes, record.OriginalBytes);
        }
    }

    public IReadOnlyList<CommandHistoryRecord> List(string? moduleId = null) => Query(moduleId);

    public IReadOnlyList<CommandHistoryRecord> Query(string? moduleId = null, DateTimeOffset? since = null,
        DateTimeOffset? until = null, string? state = null, int? limit = null)
    {
        lock (_gate)
        {
            return _records.Values
                .Where(record => (string.IsNullOrWhiteSpace(moduleId) || record.ModuleId == moduleId)
                    && (!since.HasValue || record.StartedAt >= since.Value)
                    && (!until.HasValue || record.StartedAt < until.Value)
                    && (string.IsNullOrWhiteSpace(state) || string.Equals(record.State, state, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(record => record.StartedAt)
                .Take(limit.HasValue ? Math.Max(0, limit.Value) : int.MaxValue)
                .ToArray();
        }
    }

    private CommandHistoryRecord Fit(CommandHistoryRecord record)
    {
        var identityBytes = HistoryRecordText.RecordOverheadBytes + HistoryRecordText.Bytes(record.InvocationId)
            + HistoryRecordText.Bytes(record.CommandId) + HistoryRecordText.Bytes(record.ModuleId);
        var originalBytes = identityBytes + HistoryRecordText.Bytes(record.State) + HistoryRecordText.Bytes(record.Summary);
        if (identityBytes > _options.MaxRecordBytes)
        {
            // Identity is never truncated: callers still use it for execution and
            // read/cancel targeting. An impossible-to-fit identity is not retained.
            return record with { State = "", Summary = "", IsRetained = false,
                IsTruncated = originalBytes != identityBytes, OriginalBytes = originalBytes, RetainedBytes = 0 };
        }
        var state = HistoryRecordText.Fit(record.State, Math.Min(128, _options.MaxRecordBytes - identityBytes));
        var summary = HistoryRecordText.Fit(record.Summary, _options.MaxRecordBytes - identityBytes - HistoryRecordText.Bytes(state));
        var retainedBytes = identityBytes + HistoryRecordText.Bytes(state) + HistoryRecordText.Bytes(summary);
        return record with { State = state, Summary = summary, IsRetained = true,
            IsTruncated = retainedBytes < originalBytes, OriginalBytes = originalBytes, RetainedBytes = retainedBytes };
    }
}

public sealed record CommandHistoryRecord(string InvocationId, string CommandId, string ModuleId, DateTimeOffset StartedAt, string State, string Summary)
{
    public bool IsTruncated { get; init; }
    public bool IsRetained { get; init; } = true;
    public long OriginalBytes { get; init; }
    public long RetainedBytes { get; init; }
}
