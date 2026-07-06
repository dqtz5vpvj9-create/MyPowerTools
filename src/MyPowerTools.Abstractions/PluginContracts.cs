using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

public interface IMptModule
{
    string Id { get; }
    string PackageId { get; }
    Version Version { get; }

    ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken);
    ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken);
    ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken);
    async IAsyncEnumerable<CommandExecutionEvent> ExecuteCommandStreamAsync(CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(request, cancellationToken);
        yield return new CommandExecutionEvent(
            result.InvocationId,
            result.CommandId,
            result.State,
            result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            1,
            true,
            result);
    }

    IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken);
    ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken);
    ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken);
    ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken);
    ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(snapshot);
    }

    ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken);
    ValueTask DisposeAsync(CancellationToken cancellationToken);
}

public interface IMptModuleLifecycle
{
    ValueTask EnableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask DisableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    async ValueTask RestartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        await StopAsync(context, cancellationToken);
        await StartAsync(context, cancellationToken);
    }
}

public record ModuleContext(
    string HostVersion,
    string ProtocolVersion,
    string PackageId,
    string ModuleId,
    string DataDirectory,
    string CacheDirectory,
    string LogDirectory,
    string Platform,
    IReadOnlyList<string> GrantedCapabilities,
    IReadOnlyDictionary<string, object>? CapabilityProviders = null)
{
    public bool TryGetCapability<T>(string capabilityId, out T capability)
        where T : class
    {
        capability = null!;
        if (CapabilityProviders is null ||
            !CapabilityProviders.TryGetValue(capabilityId, out var provider) ||
            provider is not T typed)
        {
            return false;
        }

        capability = typed;
        return true;
    }

    public T GetCapability<T>(string capabilityId)
        where T : class
    {
        return TryGetCapability<T>(capabilityId, out var capability)
            ? capability
            : throw new InvalidOperationException($"Capability provider '{capabilityId}' is not available for module '{ModuleId}'.");
    }
}

public record InitializeResult(bool Ok, string ProtocolVersion, IReadOnlyList<string> Capabilities, MptRuntimeError? Error = null);

public record ModuleStatusSnapshot(
    string ModuleId,
    string State,
    string Summary,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HealthCheckSnapshot> Checks,
    ulong EventSeq);

public record HealthCheckSnapshot(string Id, string Label, bool Ok, string Message);

public record MptCommandDescriptor(
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
    JsonObject? Execution = null,
    IReadOnlyList<CommandParameterDescriptor>? Parameters = null,
    IReadOnlyList<string>? Constraints = null,
    bool SupportsProgress = false,
    bool SupportsCancellation = false);

public static class MptOperationConstraints
{
    public const string MutatesSystemState = "mutatesSystemState";
    public const string RequiresElevatedWrites = "requiresElevatedWrites";
    public const string UsesNativeHardware = "usesNativeHardware";
    public const string RunsExternalProcesses = "runsExternalProcesses";
    public const string RequiresLongRunningLoop = "requiresLongRunningLoop";
}

public record CommandParameterDescriptor(
    string Id,
    string Label,
    string Type,
    bool Required = false,
    string DefaultValue = "");

public record CommandRequest(string InvocationId, string CommandId, JsonObject Args);

public record CommandExecutionResult(
    string InvocationId,
    string CommandId,
    string State,
    bool Success,
    string Output,
    MptRuntimeError? Error = null);

public record CommandExecutionEvent(
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    int Sequence,
    bool Terminal,
    CommandExecutionResult? FinalResult = null);

public record EventCursor(ulong LastEventSeq);

public record MptModuleEvent(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, JsonObject Payload);

public record SettingsSchemaDocument(string ModuleId, string SchemaJson);

public record SettingsSnapshotDocument(string ModuleId, ulong Revision, JsonObject Values, DateTimeOffset UpdatedAt);

public record SettingsPatch(string ModuleId, ulong ExpectedRevision, JsonObject Patch);

public record SettingsValidationResult(bool Ok, IReadOnlyList<string> Messages, MptRuntimeError? Error = null);

public record UiSurfaceDescriptor(string Id, string Kind, string Title, JsonObject Model);

public record MptRuntimeError(string Code, string Message, bool Retryable = false, JsonObject? Details = null);

public static class EmptyAsyncEnumerable
{
    public static async IAsyncEnumerable<T> Of<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
