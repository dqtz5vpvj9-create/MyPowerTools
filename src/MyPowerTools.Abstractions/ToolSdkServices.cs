using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

public interface IMptToolRuntime : IMptModule, IMptModuleLifecycle
{
    IAsyncEnumerable<MptToolLogEntry> ReadLogsAsync(
        DateTimeOffset? after,
        string? level,
        CancellationToken cancellationToken = default);
}

public interface IMptToolLogger
{
    void Write(string level, string message, JsonObject? properties = null);
}

public interface IMptSecretStore
{
    ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);
}

public interface IMptPermissionContext
{
    bool IsGranted(string permissionId);
    IReadOnlySet<string> GrantedPermissions { get; }
}

public interface IMptHostContext
{
    string HostVersion { get; }
    string ProtocolVersion { get; }
    string ToolId { get; }
    string ModuleId { get; }
    string DataDirectory { get; }
    string CacheDirectory { get; }
    IMptToolLogger Logger { get; }
    IMptSecretStore Secrets { get; }
    IMptPermissionContext Permissions { get; }
    ValueTask PublishEventAsync(string type, JsonObject payload, CancellationToken cancellationToken = default);
}

public sealed record MptToolHealth(
    string State,
    string Summary,
    IReadOnlyList<HealthCheckSnapshot> Checks,
    DateTimeOffset UpdatedAt);

public sealed record MptToolPermission(
    string Id,
    string Level,
    string Reason,
    string Capability = "");

public sealed record MptToolLogEntry(
    DateTimeOffset Time,
    string Level,
    string Category,
    string Message,
    JsonObject? Properties = null);

public static class MptToolStates
{
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string Unavailable = "unavailable";
    public const string Disabled = "disabled";
}
