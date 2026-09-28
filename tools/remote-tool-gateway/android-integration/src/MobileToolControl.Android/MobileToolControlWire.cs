using System.Text.Json.Nodes;

namespace MobileToolControl.Android;

internal sealed record MobileToolCatalogDevice(string Name, string Platform);

internal sealed record MobileToolParameter(
    string Id,
    string Label,
    string Type,
    bool Required,
    string DefaultValue);

internal sealed record MobileToolCommand(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation,
    bool SupportsProgress,
    bool SupportsCancellation,
    IReadOnlyList<MobileToolParameter> Parameters,
    bool Allowed,
    string NotAllowedReason);

internal sealed record MobileToolTool(
    string ToolId,
    string ModuleId,
    string Title,
    string Description,
    string Category,
    string State,
    string Availability);

internal sealed record MobileToolCatalog(
    MobileToolCatalogDevice Device,
    IReadOnlyList<MobileToolTool> Tools,
    IReadOnlyList<MobileToolCommand> Commands,
    DateTimeOffset FetchedAt,
    bool FromCache)
{
    public MobileToolCommand? Find(string commandId) => Commands.FirstOrDefault(command =>
        string.Equals(command.CommandId, commandId, StringComparison.Ordinal));

    public MobileToolTool? FindTool(string toolId) => Tools.FirstOrDefault(tool =>
        string.Equals(tool.ToolId, toolId, StringComparison.Ordinal));
}

/// <summary>
/// One invocation exactly as the wire reported it. Nothing here is derived: <see cref="State"/>,
/// <see cref="Message"/>, <see cref="Terminal"/> and <see cref="Result"/> are the gateway's values,
/// and an unknown state stays unknown so the page can show it verbatim instead of inventing one.
/// </summary>
internal sealed record MobileToolInvocation(
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    bool Terminal,
    JsonObject? Result,
    // Tri-state on purpose: the cancel endpoint reports accepted/cancelAccepted, a status read does
    // not. null means "this document did not report it", so a later poll can never turn an accepted
    // cancel into a refusal (the module keeps the last reported value).
    bool? CancelAccepted = null)
{
    /// <summary>Copy of the wire <c>result</c> block, or an empty object when the call has none.</summary>
    public JsonObject ResultOrEmpty => Result ?? new JsonObject();
}

/// <summary>
/// Parsing for the <c>/mpt-control/v1</c> JSON documents. Every reader is tolerant about missing
/// optional properties but never invents data: an absent value stays empty and the surface then shows
/// "未提供" instead of a fabricated default.
/// </summary>
internal static class MobileToolControlWire
{
    public static MobileToolCatalog ParseCatalog(JsonObject document)
    {
        var deviceNode = document["device"] as JsonObject ?? new JsonObject();
        var device = new MobileToolCatalogDevice(
            Text(deviceNode, "name"),
            Text(deviceNode, "platform"));

        var tools = new List<MobileToolTool>();
        if (document["tools"] is JsonArray toolArray)
        {
            foreach (var item in toolArray)
            {
                if (item is not JsonObject tool)
                {
                    continue;
                }

                tools.Add(new MobileToolTool(
                    Text(tool, "toolId"),
                    Text(tool, "moduleId"),
                    Text(tool, "title"),
                    Text(tool, "description"),
                    Text(tool, "category"),
                    Text(tool, "state"),
                    Text(tool, "availability")));
            }
        }

        var commands = new List<MobileToolCommand>();
        if (document["commands"] is JsonArray commandArray)
        {
            foreach (var item in commandArray)
            {
                if (item is not JsonObject command)
                {
                    continue;
                }

                commands.Add(new MobileToolCommand(
                    Text(command, "commandId"),
                    Text(command, "moduleId"),
                    Text(command, "title"),
                    Text(command, "subtitle"),
                    Text(command, "dangerLevel"),
                    Flag(command, "requiresElevation"),
                    Flag(command, "supportsProgress"),
                    Flag(command, "supportsCancellation"),
                    ParseParameters(command["parameters"]),
                    // Older gateways may omit `allowed`; treat a missing flag as not allowed so the
                    // phone never submits a command the computer did not explicitly authorize.
                    command.ContainsKey("allowed") && Flag(command, "allowed"),
                    FirstText(command, "notAllowedReason", "reason", "allowedReason")));
            }
        }

        return new MobileToolCatalog(device, tools, commands, DateTimeOffset.UtcNow, false);
    }

    private static IReadOnlyList<MobileToolParameter> ParseParameters(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var parameters = new List<MobileToolParameter>();
        foreach (var item in array)
        {
            if (item is not JsonObject parameter)
            {
                continue;
            }

            var id = Text(parameter, "id");
            if (id.Length == 0)
            {
                continue;
            }

            parameters.Add(new MobileToolParameter(
                id,
                Text(parameter, "label") is { Length: > 0 } label ? label : id,
                Text(parameter, "type"),
                Flag(parameter, "required"),
                Text(parameter, "defaultValue")));
        }

        return parameters;
    }

    public static MobileToolInvocation ParseInvocation(JsonObject document)
    {
        var state = Text(document, "state");
        var explicitTerminal = document.ContainsKey("terminal") && Flag(document, "terminal");
        return new MobileToolInvocation(
            Text(document, "invocationId"),
            Text(document, "commandId"),
            state,
            Text(document, "message"),
            explicitTerminal || IsTerminalState(state),
            document["result"] as JsonObject,
            // The cancel answer carries both spellings; a status document carries neither.
            NullableFlag(document, "cancelAccepted") ?? NullableFlag(document, "accepted"));
    }

    /// <summary>
    /// Wire states the contract names. The list is used as a fallback for a gateway that omits
    /// <c>terminal</c>; it never turns an unknown state into a known one. <c>awaiting-confirmation</c>,
    /// <c>claimed</c> and <c>cancelling</c> are deliberately absent: they are all non-terminal.
    /// </summary>
    private static bool IsTerminalState(string state) => state switch
    {
        "succeeded" or "success" or "completed" or "failed" or "cancelled" or "canceled" or "rejected" => true,
        _ => false
    };

    /// <summary>The <c>{"error":{"code":...,"message":...}}</c> envelope.</summary>
    public static (string Code, string Message) ParseError(JsonObject? document)
    {
        if (document?["error"] is JsonObject error)
        {
            return (Text(error, "code"), Text(error, "message"));
        }

        return ("", "");
    }

    public static string Text(JsonObject document, string name)
    {
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
        {
            return "";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            return value.ToJsonString().Trim('"');
        }

        return "";
    }

    public static bool Flag(JsonObject document, string name)
    {
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return bool.TryParse(text, out var parsed) && parsed;
            }
        }

        return false;
    }

    /// <summary>Boolean value, or <see langword="null"/> when the document did not report the field.</summary>
    public static bool? NullableFlag(JsonObject document, string name)
    {
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return bool.TryParse(text, out var parsed) ? parsed : null;
            }
        }

        return null;
    }

    private static string FirstText(JsonObject document, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Text(document, name);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }
}
