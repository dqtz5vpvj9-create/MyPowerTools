using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class CommandHistory : IDisposable
{
    public const int DefaultMaxCount = 500;
    public const int DefaultSummaryLength = 1024;
    private readonly List<CommandHistoryRecord> _records = [];
    private readonly Dictionary<string, string> _outputs = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _outputDirectory;
    private readonly int _maxCount;
    private readonly int _summaryLength;

    public CommandHistory(string? outputDirectory = null, int maxCount = DefaultMaxCount, int summaryLength = DefaultSummaryLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(summaryLength);
        _maxCount = maxCount;
        _summaryLength = summaryLength;
        _outputDirectory = CommandOutputFile.SessionDirectory(outputDirectory ?? Path.Combine(Path.GetTempPath(), "MyPowerTools", "command-history"));
    }

    public int Count { get { lock (_gate) return _records.Count; } }

    public CommandHistoryRecord Add(Sdk.CommandRequest request, Sdk.MptCommandDescriptor? descriptor, string state)
    {
        var record = new CommandHistoryRecord(
            request.InvocationId,
            request.CommandId,
            descriptor?.ModuleId ?? "",
            DateTimeOffset.UtcNow,
            state,
            "");

        lock (_gate)
        {
            _records.Add(record);
            while (_records.Count > _maxCount)
            {
                var removed = _records[0];
                _records.RemoveAt(0);
                if (_outputs.Remove(removed.InvocationId, out var path)) CommandOutputFile.Delete(path);
            }
        }

        return record;
    }

    public void Complete(Sdk.CommandExecutionResult result)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(record => record.InvocationId == result.InvocationId);
            if (index >= 0)
            {
                var rawSummary = string.IsNullOrWhiteSpace(result.Output)
                    ? result.Error?.Message ?? ""
                    : result.Output;
                var summary = LogRouter.Redact(rawSummary);
                if (_outputs.Remove(result.InvocationId, out var previous)) CommandOutputFile.Delete(previous);
                if (summary.Length > _summaryLength)
                {
                    try
                    {
                        _outputs[result.InvocationId] = CommandOutputFile.Write(_outputDirectory, summary);
                        var length = _summaryLength;
                        if (char.IsHighSurrogate(summary[length - 1])) length--;
                        summary = summary[..length] + "…";
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // Preserve the complete history if the output volume is unavailable.
                    }
                }
                _records[index] = _records[index] with { State = result.State, Summary = summary };
            }
        }
    }

    public IReadOnlyList<CommandHistoryRecord> List(string? moduleId = null, bool includeFullOutput = true, int limit = int.MaxValue)
    {
        lock (_gate)
        {
            return _records
                .Where(record => string.IsNullOrWhiteSpace(moduleId) || record.ModuleId == moduleId)
                .OrderByDescending(record => record.StartedAt)
                .Take(limit)
                .Select(record => includeFullOutput && _outputs.TryGetValue(record.InvocationId, out var path)
                    ? record with { Summary = CommandOutputFile.Read(path) }
                    : record)
                .ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _outputs.Clear();
            _records.Clear();
            CommandOutputFile.DeleteDirectory(_outputDirectory);
        }
    }
}

public sealed record CommandHistoryRecord(string InvocationId, string CommandId, string ModuleId, DateTimeOffset StartedAt, string State, string Summary);
