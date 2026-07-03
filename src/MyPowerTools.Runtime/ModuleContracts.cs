using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

public interface IMptModule
{
    string Id { get; }
    string PackageId { get; }
    Version Version { get; }

    ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken);
    ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken);
    ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken);
    ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken);
    ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken);
    ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken);
    ValueTask DisposeAsync(CancellationToken cancellationToken);
}

public interface IModuleTransportRuntime
{
    string Kind { get; }

    ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken);
    ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken);
}

public interface IModuleTransportDiagnosticsProvider
{
    string GetProcessPoolKey(RuntimeModuleRecord module);
    void RegisterProcessPool(string poolKey, string moduleId);
    void ApplyRestartPolicy(string poolKey, string restartPolicy, string reason, DateTimeOffset updatedAt, DateTimeOffset? expiresAt);
    IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics();
    ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken);
    ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(string poolKey, bool paused, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken);
}

public sealed record ModuleContext(
    string HostVersion,
    string ProtocolVersion,
    string PackageId,
    string ModuleId,
    string DataDirectory,
    string CacheDirectory,
    string LogDirectory,
    string Platform,
    IReadOnlyList<string> GrantedCapabilities);

public sealed record InitializeResult(bool Ok, string ProtocolVersion, IReadOnlyList<string> Capabilities, MptRuntimeError? Error = null);

public sealed record ModuleStatusSnapshot(
    string ModuleId,
    string State,
    string Summary,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HealthCheckSnapshot> Checks,
    ulong EventSeq);

public sealed record HealthCheckSnapshot(string Id, string Label, bool Ok, string Message);

public sealed record MptCommandDescriptor(
    string Id,
    string ModuleId,
    string Title,
    string Subtitle,
    string Kind,
    bool RequiresElevation = false,
    string Icon = "",
    string DangerLevel = "",
    string Category = "",
    int TimeoutMs = 30000,
    JsonObject? Execution = null);

public sealed record CommandRequest(string InvocationId, string CommandId, JsonObject Args);

public sealed record CommandExecutionResult(
    string InvocationId,
    string CommandId,
    string State,
    bool Success,
    string Output,
    MptRuntimeError? Error = null);

public sealed record EventCursor(ulong LastEventSeq);

public sealed record MptModuleEvent(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, JsonObject Payload);

public sealed record SettingsSchemaDocument(string ModuleId, string SchemaJson);

public sealed record SettingsSnapshotDocument(string ModuleId, ulong Revision, JsonObject Values, DateTimeOffset UpdatedAt);

public sealed record SettingsPatch(string ModuleId, ulong ExpectedRevision, JsonObject Patch);

public sealed record SettingsValidationResult(bool Ok, IReadOnlyList<string> Messages, MptRuntimeError? Error = null);

public sealed record UiSurfaceDescriptor(string Id, string Kind, string Title, JsonObject Model);

public sealed record MptRuntimeError(string Code, string Message, bool Retryable = false, JsonObject? Details = null);

public static class EmptyAsyncEnumerable
{
    public static async IAsyncEnumerable<T> Of<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
