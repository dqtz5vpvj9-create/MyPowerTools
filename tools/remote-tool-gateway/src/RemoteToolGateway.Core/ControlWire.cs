using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MyPowerTools.Abstractions;

namespace RemoteToolGateway.Core;

/// <summary>
/// Wire v1 shapes for <c>/mpt-control/v1</c>. Field names and bounds are frozen by
/// docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md; the JSON property names are camelCase and every
/// payload that leaves the gateway is bounded so a phone can never make the desktop allocate an
/// unbounded buffer or hand back an unbounded result.
/// </summary>
public static class ControlWire
{
    public const string Prefix = "/mpt-control/v1";
    public const string CatalogPath = Prefix + "/catalog";
    public const string InvocationsPath = Prefix + "/invocations";

    /// <summary>Maximum request body accepted on any endpoint (args JSON included).</summary>
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxHeaderCount = 64;
    public const int MaxPathLength = 512;
    public const int MaxInvocationIdLength = 64;
    public const int MaxCommandIdLength = 200;
    public const int MaxSummaryLength = 4000;
    public const int MaxMessageLength = 2000;
    public const int MaxErrorDetailsChars = 8000;
    public const int MaxTextLength = 400;
    public const int MaxCatalogTools = 500;
    public const int MaxCatalogCommands = 2000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>The frozen error envelope; a failure is never wrapped into a 2xx success body.</summary>
    public static string ErrorBody(string code, string message) =>
        Serialize(new ControlErrorEnvelope(new ControlError(code, ControlText.Bound(message, MaxMessageLength))));
}

public sealed record ControlError(string Code, string Message);

public sealed record ControlErrorEnvelope(ControlError Error);

public static class ControlErrorCodes
{
    public const string BadRequest = "bad-request";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string ForbiddenPeer = "forbidden-peer";
    public const string NotFound = "not-found";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string CommandNotAuthorized = "command-not-authorized";
    public const string CommandNotFound = "command-not-found";
    public const string ElevationNotAllowed = "elevation-not-allowed";
    public const string UnsupportedConfirmation = "unsupported-confirmation";
    public const string InvocationExists = "invocation-exists";
    public const string ConfirmationNotClaimed = "confirmation-not-claimed";
    public const string ConfirmationAlreadyClaimed = "confirmation-already-claimed";
    public const string CancelUnconfirmed = "cancel-unconfirmed";
    public const string HostStreamEnded = "host-stream-ended";
    public const string UnsupportedInvocationScope = "unsupported-invocation-scoped-execution";
    public const string InvocationNotCancellable = "invocation-not-cancellable";
    public const string PayloadTooLarge = "payload-too-large";
    public const string Busy = "busy";
    public const string HostUnavailable = "host-unavailable";
    public const string ListenerUnavailable = "listener-unavailable";
    public const string InternalError = "internal-error";
}

public static class ControlStates
{
    public const string Accepted = "accepted";
    public const string AwaitingConfirmation = "awaiting-confirmation";
    /// <summary>The desktop page has taken this pending request; exactly one surface can hold it.</summary>
    public const string Claimed = "claimed";
    public const string Cancelling = "cancelling";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string PermissionRequired = "permission-required";
}

public sealed record ControlCommandParameter(string Id, string Label, string Type, bool Required, string DefaultValue);

public sealed record ControlToolInfo(
    string ToolId,
    string ModuleId,
    string Title,
    string Description,
    string Category,
    string State,
    string Availability);

public sealed record ControlCommandInfo(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation,
    bool SupportsProgress,
    bool SupportsCancellation,
    IReadOnlyList<ControlCommandParameter> Parameters,
    bool Allowed,
    // Additive, optional field: the phone may show why a command is not allowed. The contract's
    // frozen fields are unchanged; clients that do not know it ignore it.
    string NotAllowedReason = "");

public sealed record ControlDeviceInfo(string Name, string Platform);

public sealed record ControlCatalog(ControlDeviceInfo Device, IReadOnlyList<ControlToolInfo> Tools, IReadOnlyList<ControlCommandInfo> Commands);

/// <summary>
/// The bounded, camelCase projection of the existing HostControl <c>CommandExecutionResponse</c>.
/// </summary>
public sealed record ControlInvocationResult(
    string InvocationId,
    string State,
    string Summary,
    string LogCursor,
    string ErrorCode,
    string ErrorMessage,
    bool Retryable,
    JsonNode? ErrorDetails);

public sealed record ControlInvocation(
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    bool Terminal,
    ControlInvocationResult Result);

/// <summary>One parsed HTTP request handed to the gateway service.</summary>
public sealed record ControlRequest(string Method, string Path, string Authorization, string Body, IPAddress RemoteAddress);

/// <summary>One bounded HTTP response written back to the phone.</summary>
public sealed record ControlResponse(int StatusCode, string Body)
{
    public static ControlResponse Json(int statusCode, string body) => new(statusCode, body);
    public static ControlResponse Error(int statusCode, string code, string message) =>
        new(statusCode, ControlWire.ErrorBody(code, message));
}

public static class ControlText
{
    public static string Bound(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>Bounded error details: an oversized Struct is replaced by a truncated preview.</summary>
    public static JsonNode? BoundDetails(JsonNode? details)
    {
        if (details is null) return null;
        string text;
        try { text = details.ToJsonString(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return null; }
        if (text.Length <= ControlWire.MaxErrorDetailsChars) return details;
        return new JsonObject
        {
            ["truncated"] = true,
            ["preview"] = text[..ControlWire.MaxErrorDetailsChars]
        };
    }
}

public static class ControlRedaction
{
    private static readonly string[] SensitiveWords =
        ["password", "passwd", "token", "secret", "credential", "cookie", "authorization", "apikey", "api_key", "access_key", "private_key"];

    /// <summary>A parameter name that must never appear with its value in a summary or an audit entry.</summary>
    public static bool IsSensitiveKey(string key)
    {
        var normalized = key.Replace("-", "").Replace("_", "").ToLowerInvariant();
        return SensitiveWords.Any(word => normalized.Contains(word.Replace("_", ""), StringComparison.Ordinal));
    }

    /// <summary>
    /// A short, redacted argument summary for the desktop confirmation list and the audit trail.
    /// Values are previews only, credential-shaped keys lose their value entirely, and the whole
    /// summary is bounded before it can reach a log, a preference file or the phone.
    /// </summary>
    public static string SummarizeArgs(JsonObject? args, int maxLength = 400)
    {
        if (args is null || args.Count == 0) return "";
        var parts = new List<string>();
        foreach (var pair in args)
        {
            var name = ControlText.Bound(pair.Key, 60);
            if (IsSensitiveKey(pair.Key)) { parts.Add(name + "=****"); continue; }
            var preview = pair.Value switch
            {
                null => "null",
                JsonValue value => Preview(value),
                JsonArray array => $"[{array.Count} 项]",
                JsonObject obj => $"{{{obj.Count} 项}}",
                _ => "?"
            };
            parts.Add(name + "=" + ControlText.Bound(preview, 80));
        }

        var summary = MptLogRedactor.Redact(string.Join(", ", parts));
        return ControlText.Bound(summary, maxLength);
    }

    private static string Preview(JsonValue value)
    {
        if (value.TryGetValue<string>(out var text)) return text;
        if (value.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
        try { return value.ToJsonString(); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException) { return "?"; }
    }
}
