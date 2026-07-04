namespace MyPowerTools.Runtime;

public sealed class CommandHistory
{
    private readonly List<CommandHistoryRecord> _records = [];
    private readonly object _gate = new();

    public CommandHistoryRecord Add(CommandRequest request, MptCommandDescriptor? descriptor, string state)
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
        }

        return record;
    }

    public void Complete(CommandExecutionResult result)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(record => record.InvocationId == result.InvocationId);
            if (index >= 0)
            {
                var summary = string.IsNullOrWhiteSpace(result.Output)
                    ? result.Error?.Message ?? ""
                    : result.Output;
                _records[index] = _records[index] with { State = result.State, Summary = summary };
            }
        }
    }

    public IReadOnlyList<CommandHistoryRecord> List(string? moduleId = null)
    {
        lock (_gate)
        {
            return _records
                .Where(record => string.IsNullOrWhiteSpace(moduleId) || record.ModuleId == moduleId)
                .OrderByDescending(record => record.StartedAt)
                .ToArray();
        }
    }
}

public sealed record CommandHistoryRecord(string InvocationId, string CommandId, string ModuleId, DateTimeOffset StartedAt, string State, string Summary);
