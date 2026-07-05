using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

[Obsolete("Use MyPowerTools.Abstractions.IMptModule.")]
public interface IMptModule : MyPowerTools.Abstractions.IMptModule
{
}

[Obsolete("Use MyPowerTools.Abstractions.ModuleContext.")]
public sealed record ModuleContext(
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
    : MyPowerTools.Abstractions.ModuleContext(
        HostVersion,
        ProtocolVersion,
        PackageId,
        ModuleId,
        DataDirectory,
        CacheDirectory,
        LogDirectory,
        Platform,
        GrantedCapabilities,
        CapabilityProviders);

[Obsolete("Use MyPowerTools.Abstractions.InitializeResult.")]
public sealed record InitializeResult(
    bool Ok,
    string ProtocolVersion,
    IReadOnlyList<string> Capabilities,
    MyPowerTools.Abstractions.MptRuntimeError? Error = null)
    : MyPowerTools.Abstractions.InitializeResult(Ok, ProtocolVersion, Capabilities, Error);

[Obsolete("Use MyPowerTools.Abstractions.ModuleStatusSnapshot.")]
public sealed record ModuleStatusSnapshot(
    string ModuleId,
    string State,
    string Summary,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MyPowerTools.Abstractions.HealthCheckSnapshot> Checks,
    ulong EventSeq)
    : MyPowerTools.Abstractions.ModuleStatusSnapshot(ModuleId, State, Summary, UpdatedAt, Checks, EventSeq);

[Obsolete("Use MyPowerTools.Abstractions.HealthCheckSnapshot.")]
public sealed record HealthCheckSnapshot(string Id, string Label, bool Ok, string Message)
    : MyPowerTools.Abstractions.HealthCheckSnapshot(Id, Label, Ok, Message);

[Obsolete("Use MyPowerTools.Abstractions.MptCommandDescriptor.")]
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
    JsonObject? Execution = null,
    IReadOnlyList<MyPowerTools.Abstractions.CommandParameterDescriptor>? Parameters = null,
    IReadOnlyList<string>? Constraints = null)
    : MyPowerTools.Abstractions.MptCommandDescriptor(Id, ModuleId, Title, Subtitle, Kind, RequiresElevation, Icon, DangerLevel, Category, TimeoutMs, Execution, Parameters, Constraints);

[Obsolete("Use MyPowerTools.Abstractions.CommandParameterDescriptor.")]
public sealed record CommandParameterDescriptor(
    string Id,
    string Label,
    string Type,
    bool Required = false,
    string DefaultValue = "")
    : MyPowerTools.Abstractions.CommandParameterDescriptor(Id, Label, Type, Required, DefaultValue);

[Obsolete("Use MyPowerTools.Abstractions.CommandRequest.")]
public sealed record CommandRequest(string InvocationId, string CommandId, JsonObject Args)
    : MyPowerTools.Abstractions.CommandRequest(InvocationId, CommandId, Args);

[Obsolete("Use MyPowerTools.Abstractions.CommandExecutionResult.")]
public sealed record CommandExecutionResult(
    string InvocationId,
    string CommandId,
    string State,
    bool Success,
    string Output,
    MyPowerTools.Abstractions.MptRuntimeError? Error = null)
    : MyPowerTools.Abstractions.CommandExecutionResult(InvocationId, CommandId, State, Success, Output, Error);

[Obsolete("Use MyPowerTools.Abstractions.CommandExecutionEvent.")]
public sealed record CommandExecutionEvent(
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    int Sequence,
    bool Terminal,
    MyPowerTools.Abstractions.CommandExecutionResult? FinalResult = null)
    : MyPowerTools.Abstractions.CommandExecutionEvent(InvocationId, CommandId, State, Message, Sequence, Terminal, FinalResult);

[Obsolete("Use MyPowerTools.Abstractions.EventCursor.")]
public sealed record EventCursor(ulong LastEventSeq)
    : MyPowerTools.Abstractions.EventCursor(LastEventSeq);

[Obsolete("Use MyPowerTools.Abstractions.MptModuleEvent.")]
public sealed record MptModuleEvent(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, JsonObject Payload)
    : MyPowerTools.Abstractions.MptModuleEvent(ModuleId, Seq, Type, Time, Payload);

[Obsolete("Use MyPowerTools.Abstractions.SettingsSchemaDocument.")]
public sealed record SettingsSchemaDocument(string ModuleId, string SchemaJson)
    : MyPowerTools.Abstractions.SettingsSchemaDocument(ModuleId, SchemaJson);

[Obsolete("Use MyPowerTools.Abstractions.SettingsSnapshotDocument.")]
public sealed record SettingsSnapshotDocument(string ModuleId, ulong Revision, JsonObject Values, DateTimeOffset UpdatedAt)
    : MyPowerTools.Abstractions.SettingsSnapshotDocument(ModuleId, Revision, Values, UpdatedAt);

[Obsolete("Use MyPowerTools.Abstractions.SettingsPatch.")]
public sealed record SettingsPatch(string ModuleId, ulong ExpectedRevision, JsonObject Patch)
    : MyPowerTools.Abstractions.SettingsPatch(ModuleId, ExpectedRevision, Patch);

[Obsolete("Use MyPowerTools.Abstractions.SettingsValidationResult.")]
public sealed record SettingsValidationResult(
    bool Ok,
    IReadOnlyList<string> Messages,
    MyPowerTools.Abstractions.MptRuntimeError? Error = null)
    : MyPowerTools.Abstractions.SettingsValidationResult(Ok, Messages, Error);

[Obsolete("Use MyPowerTools.Abstractions.UiSurfaceDescriptor.")]
public sealed record UiSurfaceDescriptor(string Id, string Kind, string Title, JsonObject Model)
    : MyPowerTools.Abstractions.UiSurfaceDescriptor(Id, Kind, Title, Model);

[Obsolete("Use MyPowerTools.Abstractions.MptRuntimeError.")]
public sealed record MptRuntimeError(string Code, string Message, bool Retryable = false, JsonObject? Details = null)
    : MyPowerTools.Abstractions.MptRuntimeError(Code, Message, Retryable, Details);

[Obsolete("Use MyPowerTools.Abstractions.EmptyAsyncEnumerable.")]
public static class EmptyAsyncEnumerable
{
    public static async IAsyncEnumerable<T> Of<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in MyPowerTools.Abstractions.EmptyAsyncEnumerable.Of<T>(cancellationToken))
        {
            yield return item;
        }
    }
}
