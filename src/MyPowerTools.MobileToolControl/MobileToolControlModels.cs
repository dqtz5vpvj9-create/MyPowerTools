using System.Text.Json.Nodes;

namespace MyPowerTools.MobileToolControl;

/// <summary>One imported computer as the module reports it. No token ever appears here.</summary>
internal sealed record MobileToolDeviceItem(
    string DeviceId,
    string DeviceName,
    string Endpoint,
    string Platform,
    string LastState,
    string LastDetail,
    bool CredentialConfigured)
{
    /// <summary>
    /// Honest connection wording. "在线" is only ever shown after the computer itself answered a
    /// check; an imported record that was never checked stays "尚未检查" and a failed check stays
    /// "无法连接".
    /// </summary>
    public string StateLabel => LastState switch
    {
        "reachable" => "已连接",
        "unreachable" => "无法连接",
        "imported" => "尚未检查",
        _ => "尚未检查"
    };

    public bool IsReachable => string.Equals(LastState, "reachable", StringComparison.Ordinal);

    public string PlatformLabel => Platform switch
    {
        "windows" => "Windows",
        "macos" or "darwin" => "macOS",
        "linux" => "Linux",
        _ => ""
    };

    public string Subtitle
    {
        get
        {
            var platform = PlatformLabel.Length > 0 ? PlatformLabel : "电脑";
            return LastDetail.Length > 0 ? $"{platform} · {LastDetail}" : platform;
        }
    }
}

/// <summary>One parameter of a real command, exactly as the catalog declared it.</summary>
internal sealed record MobileToolParameterItem(string Id, string Label, string Type, bool Required, string DefaultValue)
{
    public string Placeholder => DefaultValue.Length > 0 ? $"默认 {DefaultValue}" : "";
}

/// <summary>One command from the computer's catalog. Authorization always comes from the wire.</summary>
internal sealed record MobileToolCommandItem(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation,
    bool SupportsProgress,
    bool SupportsCancellation,
    IReadOnlyList<MobileToolParameterItem> Parameters,
    bool Allowed,
    string NotAllowedReason)
{
    public string NotAllowedText => NotAllowedReason.Length > 0 ? NotAllowedReason : "这台电脑未授权这条命令。";

    public bool HasParameters => Parameters.Count > 0;

    /// <summary>"只读" is only claimed when the computer itself sent an explicit danger level.</summary>
    public string SafetyLabel => DangerLevel.ToLowerInvariant() switch
    {
        "readonly" or "read-only" or "safe" => "只读",
        "dangerous" or "high" => "敏感操作",
        "medium" => "会修改电脑状态",
        _ => RequiresElevation ? "需要管理员权限" : ""
    };
}

/// <summary>One tool the computer's catalog offers.</summary>
internal sealed record MobileToolToolItem(
    string ToolId,
    string ModuleId,
    string Title,
    string Description,
    string Category,
    string State,
    string Availability);

/// <summary>The computer's catalog as one immutable snapshot.</summary>
internal sealed record MobileToolCatalogSnapshot(
    string DeviceId,
    string DeviceName,
    string Platform,
    IReadOnlyList<MobileToolToolItem> Tools,
    IReadOnlyList<MobileToolCommandItem> Commands,
    DateTimeOffset FetchedAt,
    bool FromCache)
{
    public IReadOnlyList<MobileToolCommandItem> CommandsFor(string toolId) => Commands
        .Where(command => string.Equals(command.ModuleId, toolId, StringComparison.Ordinal) ||
                          command.CommandId.StartsWith(toolId + ".", StringComparison.Ordinal))
        .ToArray();

    public MobileToolCommandItem? FindCommand(string commandId) => Commands.FirstOrDefault(command =>
        string.Equals(command.CommandId, commandId, StringComparison.Ordinal));
}

/// <summary>One invocation exactly as the module last reported it from the computer.</summary>
internal sealed record MobileToolInvocationSnapshot(
    string DeviceId,
    string InvocationId,
    string CommandId,
    string State,
    string Message,
    bool Terminal,
    bool CancelAccepted,
    JsonObject Result)
{
    public string ResultSummary => Read("summary");

    public string ResultErrorCode => Read("errorCode");

    public string ResultErrorMessage => Read("errorMessage");

    public bool Retryable => ReadFlag("retryable");

    public string ResultState => Read("state");

    private string Read(string name)
    {
        if (!Result.TryGetPropertyValue(name, out var node) || node is null)
        {
            return "";
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : "";
    }

    private bool ReadFlag(string name) =>
        Result.TryGetPropertyValue(name, out var node) && node is JsonValue value &&
        value.TryGetValue<bool>(out var flag) && flag;
}

/// <summary>
/// JSON readers for the module payloads. Absent values stay empty; the page then shows "未提供"
/// rather than inventing a placeholder that reads like real computer data.
/// </summary>
internal static class MobileToolControlJson
{
    public static IReadOnlyList<MobileToolDeviceItem> Devices(JsonObject document)
    {
        var devices = new List<MobileToolDeviceItem>();
        if (document["devices"] is not JsonArray array)
        {
            return devices;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject device)
            {
                continue;
            }

            devices.Add(new MobileToolDeviceItem(
                Text(device, "deviceId"),
                Text(device, "deviceName"),
                Text(device, "endpoint"),
                Text(device, "platform"),
                Text(device, "lastState"),
                Text(device, "lastDetail"),
                Flag(device, "credentialConfigured")));
        }

        return devices;
    }

    public static MobileToolCatalogSnapshot Catalog(string deviceId, string fallbackName, JsonObject document)
    {
        var device = document["device"] as JsonObject ?? new JsonObject();
        var tools = new List<MobileToolToolItem>();
        if (document["tools"] is JsonArray toolArray)
        {
            foreach (var item in toolArray)
            {
                if (item is not JsonObject tool)
                {
                    continue;
                }

                tools.Add(new MobileToolToolItem(
                    Text(tool, "toolId"),
                    Text(tool, "moduleId"),
                    Text(tool, "title"),
                    Text(tool, "description"),
                    Text(tool, "category"),
                    Text(tool, "state"),
                    Text(tool, "availability")));
            }
        }

        var commands = new List<MobileToolCommandItem>();
        if (document["commands"] is JsonArray commandArray)
        {
            foreach (var item in commandArray)
            {
                if (item is not JsonObject command)
                {
                    continue;
                }

                var commandId = Text(command, "commandId");
                if (commandId.Length == 0)
                {
                    continue;
                }

                commands.Add(new MobileToolCommandItem(
                    commandId,
                    Text(command, "moduleId"),
                    Text(command, "title"),
                    Text(command, "subtitle"),
                    Text(command, "dangerLevel"),
                    Flag(command, "requiresElevation"),
                    Flag(command, "supportsProgress"),
                    Flag(command, "supportsCancellation"),
                    Parameters(command["parameters"]),
                    command.ContainsKey("allowed") && Flag(command, "allowed"),
                    Text(command, "notAllowedReason")));
            }
        }

        var name = Text(device, "name");
        return new MobileToolCatalogSnapshot(
            deviceId,
            name.Length > 0 ? name : fallbackName,
            Text(device, "platform"),
            tools,
            commands,
            ParseTime(Text(document, "fetchedAt")),
            Flag(document, "fromCache"));
    }

    public static MobileToolInvocationSnapshot Invocation(string deviceId, JsonObject document) => new(
        Text(document, "deviceId") is { Length: > 0 } reported ? reported : deviceId,
        Text(document, "invocationId"),
        Text(document, "commandId"),
        Text(document, "state"),
        Text(document, "message"),
        Flag(document, "terminal"),
        Flag(document, "cancelAccepted"),
        document["result"] as JsonObject ?? new JsonObject());

    public static bool Ok(JsonObject document) => Flag(document, "ok");

    private static IReadOnlyList<MobileToolParameterItem> Parameters(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var parameters = new List<MobileToolParameterItem>();
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

            var label = Text(parameter, "label");
            parameters.Add(new MobileToolParameterItem(
                id,
                label.Length > 0 ? label : id,
                Text(parameter, "type"),
                Flag(parameter, "required"),
                Text(parameter, "defaultValue")));
        }

        return parameters;
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

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
}
