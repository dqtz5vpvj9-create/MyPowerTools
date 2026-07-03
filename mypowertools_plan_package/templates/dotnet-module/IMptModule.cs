namespace MyPowerTools.Sdk;

public interface IMptModule
{
    string Id { get; }
    string PackageId { get; }
    Version Version { get; }

    ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken ct);
    ValueTask<ModuleStatus> GetStatusAsync(CancellationToken ct);
    ValueTask<IReadOnlyList<ToolCommand>> ListCommandsAsync(CancellationToken ct);
    ValueTask<CommandExecution> ExecuteCommandAsync(CommandRequest request, CancellationToken ct);
    ValueTask<CommandCancelResult> CancelCommandAsync(string invocationId, CancellationToken ct);
    ValueTask<SettingsSchema> GetSettingsSchemaAsync(CancellationToken ct);
    ValueTask<SettingsSnapshot> GetSettingsAsync(CancellationToken ct);
    ValueTask<ValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken ct);
    ValueTask<IReadOnlyList<UiSurface>> ListSurfacesAsync(CancellationToken ct);
    IAsyncEnumerable<ModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken ct);
    IAsyncEnumerable<LogEntry> TailLogsAsync(LogCursor cursor, CancellationToken ct);
    ValueTask DisposeAsync(CancellationToken ct);
}

public sealed record ModuleContext(
    string HostVersion,
    string ProtocolVersion,
    string PackageId,
    string ModuleId,
    string DataDir,
    string CacheDir,
    string LogDir,
    string Platform,
    IReadOnlyList<string> GrantedCapabilities);

public sealed record InitializeResult(bool Ok, string ProtocolVersion, IReadOnlyList<string> Capabilities);
public sealed record ModuleStatus(string ModuleId, string State, string Summary, DateTimeOffset UpdatedAt, ulong EventSeq);
public sealed record ToolCommand(string Id, string Title, string Kind, bool RequiresElevation = false);
public sealed record CommandRequest(string ModuleId, string CommandId, string InvocationId, IReadOnlyDictionary<string, string> Args);
public sealed record CommandExecution(string InvocationId, string CommandId, string State, bool Success, string Output);
public sealed record CommandCancelResult(bool Accepted);
public sealed record SettingsSchema(string ModuleId, string SchemaJson);
public sealed record SettingsSnapshot(string ModuleId, ulong Revision, string ValuesJson, DateTimeOffset UpdatedAt);
public sealed record SettingsPatch(string ModuleId, ulong ExpectedRevision, string PatchJson);
public sealed record ValidationResult(bool Ok, IReadOnlyList<string> Messages);
public sealed record UiSurface(string Id, string Kind, string Title, string ModelJson);
public sealed record EventCursor(ulong LastEventSeq);
public sealed record ModuleEvent(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, string PayloadJson);
public sealed record LogCursor(string Cursor);
public sealed record LogEntry(string ModuleId, string Cursor, DateTimeOffset Time, string Level, string Message);
