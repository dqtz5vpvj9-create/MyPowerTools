using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

public interface IMptModuleFactory
{
    ValueTask<IMptModule> CreateAsync(
        ModuleContext context,
        CancellationToken cancellationToken);
}

public interface IModuleContext
{
    string HostVersion { get; }
    string ProtocolVersion { get; }
    string PackageId { get; }
    string ModuleId { get; }
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string LogDirectory { get; }
    string Platform { get; }
    IReadOnlyList<string> GrantedCapabilities { get; }
}

public interface ICommandContext
{
    string InvocationId { get; }
    string CommandId { get; }
    JsonObject Args { get; }
}

public sealed record ModuleStatus(
    string ModuleId,
    string State,
    string Summary,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<HealthCheck> Checks,
    ulong EventSeq);

public sealed record HealthCheck(string Id, string Label, bool Ok, string Message);

public sealed record ModuleCommand(
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
    IReadOnlyList<CommandParameter>? Parameters = null);

public sealed record CommandParameter(
    string Id,
    string Label,
    string Type,
    bool Required = false,
    string DefaultValue = "");

public sealed record CommandResult(
    string InvocationId,
    string CommandId,
    string State,
    bool Success,
    string Output,
    RuntimeError? Error = null);

public sealed record SettingsSchema(string ModuleId, string SchemaJson);

public sealed record ModuleEvent(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, JsonObject Payload);

public sealed record RuntimeError(string Code, string Message, bool Retryable = false, JsonObject? Details = null);
