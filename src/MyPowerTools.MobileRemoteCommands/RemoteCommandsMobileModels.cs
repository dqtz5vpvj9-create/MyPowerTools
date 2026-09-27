using System.Text.Json.Nodes;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>One entry of the shared <c>commands.yaml</c> catalog, as the module reports it.</summary>
internal sealed record MobileCommandDefinition(
    string Id,
    string Label,
    string Command,
    string Description,
    string Type,
    string Host,
    string Input1Label,
    string Input1Placeholder,
    string Input2Label,
    string Input2Placeholder,
    bool ShowSecondInput,
    bool UsesRemoteHost)
{
    /// <summary>Local <c>py</c> transforms never open SSH; the module runs them in-process.</summary>
    public bool IsLocalTransform => !UsesRemoteHost;

    public string TypeBadge => IsLocalTransform ? "本地转换" : "SSH";

    public string HostText => string.IsNullOrWhiteSpace(Host) ? "跟随当前主机" : $"固定 {Host}";

    public string Input1LabelText => string.IsNullOrWhiteSpace(Input1Label) ? "输入 1" : Input1Label;

    public string Input2LabelText => string.IsNullOrWhiteSpace(Input2Label) ? "输入 2" : Input2Label;

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? Id : Label;
}

/// <summary>One alias → real address mapping from the Android host catalog.</summary>
internal sealed record MobileHostEntry(
    string Alias,
    string Host,
    int Port,
    string Username,
    string Auth,
    bool CredentialConfigured)
{
    public string AuthText => string.Equals(Auth, RemoteCommandsMobileContract.AuthPassword, StringComparison.OrdinalIgnoreCase)
        ? "密码"
        : "私钥";

    public string EndpointText => $"{Username}@{Host}:{Port}";

    /// <summary>Only the presence of a credential is ever shown; its value stays in the secret store.</summary>
    public string CredentialText => CredentialConfigured ? "凭据已保存" : "缺少凭据";

    public override string ToString() => $"{Alias} → {EndpointText}";
}

/// <summary>One confirmed host key. Trust is per host:port + exact SHA256 fingerprint.</summary>
internal sealed record MobileHostKeyEntry(
    string Host,
    int Port,
    string HostKeyName,
    string Fingerprint,
    string AddedAt)
{
    public string EndpointText => $"{Host}:{Port}";

    public string FingerprintText => string.IsNullOrWhiteSpace(Fingerprint) ? "（未记录指纹）" : Fingerprint;

    public string AddedAtText => string.IsNullOrWhiteSpace(AddedAt) ? "" : $"确认于 {AddedAt}";
}

internal sealed record MobileHistorySummary(
    int Count,
    string LatestTimestamp,
    string LatestLabel,
    string LatestHost)
{
    public string CountText => Count == 0 ? "暂无执行历史" : $"已保存 {Count} 条执行历史";

    public string LatestText => Count == 0 || string.IsNullOrWhiteSpace(LatestLabel)
        ? "还没有执行过远程命令"
        : $"最近：{LatestTimestamp} {LatestLabel}" + (string.IsNullOrWhiteSpace(LatestHost) ? "" : $" @ {LatestHost}");
}

/// <summary>
/// A host key the user has not confirmed yet. It is surfaced with the exact fingerprint so the
/// confirmation is a decision about one key, never a blanket "trust everything" switch.
/// </summary>
internal sealed record MobilePendingHostKey(
    string Host,
    int? Port,
    string HostKeyName,
    string Fingerprint,
    bool FirstUse,
    string Message)
{
    public string EndpointText => Port is { } port and > 0 ? $"{Host}:{port}" : Host;

    public string ReasonText => FirstUse
        ? "这是第一次连接该主机，MPT 不会自动信任。"
        : "该主机返回的密钥与已确认的指纹不同（可能是换机或中间人）。";

    public string DetailText => string.IsNullOrWhiteSpace(HostKeyName)
        ? Fingerprint
        : $"{HostKeyName} · {Fingerprint}";
}

internal sealed record MobileHostsSnapshot(
    IReadOnlyList<MobileHostEntry> Hosts,
    IReadOnlyList<string> MissingAliases,
    string CatalogPath,
    bool LoadFailed);

/// <summary>Result of the module <c>catalog</c> command, including the YAML the editor starts from.</summary>
internal sealed record MobileCatalogSnapshot(
    IReadOnlyList<MobileCommandDefinition> Commands,
    string Error,
    string CommandsPath,
    string YamlText)
{
    public static MobileCatalogSnapshot Empty { get; } = new([], "", "", "");

    /// <summary>True when the module handed back the catalog text, so the editor needs no file read.</summary>
    public bool HasModuleYaml => YamlText.Length > 0;
}

/// <summary>Result of the module <c>status</c> command: the single source of phone-side state.</summary>
internal sealed record MobileModuleState(
    string CommandsPath,
    int CommandCount,
    string CommandsError,
    string DefaultHost,
    string KnownHosts,
    int HistoryRetention,
    string CondaExecutable,
    int CommandTimeoutMinutes,
    IReadOnlyList<MobileHostEntry> Hosts,
    IReadOnlyList<MobileHostKeyEntry> TrustedHostKeys,
    string ActiveInvocationId,
    string ActiveStage,
    string LastRunState,
    string LastRunSummary,
    bool BackgroundAvailable,
    bool TransportAvailable,
    string DataDirectory)
{
    public static MobileModuleState Empty { get; } = new(
        "", 0, "", "", "", 0, "", 0, [], [], "", "", "idle", "尚未执行过远程命令", false, false, "");

    /// <summary>Aliases the shared settings.json knows about, one per line.</summary>
    public IReadOnlyList<string> KnownHostAliases => KnownHosts
        .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

/// <summary>Terminal outcome of one <c>run</c> invocation.</summary>
internal sealed record MobileRunOutcome(
    string State,
    string Message,
    int? ExitCode,
    string Host,
    string Alias,
    string ResolvedHost,
    string Output,
    MobilePendingHostKey? PendingHostKey,
    string Transport)
{
    public bool Succeeded => string.Equals(State, RemoteCommandsMobileContract.StateSucceeded, StringComparison.Ordinal);

    public bool NeedsHostKeyTrust =>
        string.Equals(State, RemoteCommandsMobileContract.StateHostKeyRequired, StringComparison.Ordinal);

    public bool Cancelled => string.Equals(State, RemoteCommandsMobileContract.StateCancelled, StringComparison.Ordinal);

    public string EndpointText => string.IsNullOrWhiteSpace(Alias)
        ? ResolvedHost
        : $"{Alias} → {ResolvedHost}";

    public string ResultText => State switch
    {
        RemoteCommandsMobileContract.StateSucceeded => $"执行成功（退出码 {ExitCode?.ToString() ?? "0"}）",
        RemoteCommandsMobileContract.StateCancelled => "已取消",
        RemoteCommandsMobileContract.StateHostKeyRequired => "等待确认主机密钥",
        _ => string.IsNullOrWhiteSpace(Message) ? "执行失败" : $"执行失败：{Message}"
    };
}

/// <summary>Small readers for the module's JSON payloads; every payload is written by the module.</summary>
internal static class RemoteCommandsMobileJson
{
    public static string Text(JsonObject? json, string key)
    {
        if (json is null || !json.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return "";
        }

        return value.TryGetValue<string>(out var text) ? text : "";
    }

    public static int Int(JsonObject? json, string key, int fallback = 0)
    {
        if (json is null || !json.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return fallback;
        }

        return value.TryGetValue<int>(out var number) ? number : fallback;
    }

    public static int? NullableInt(JsonObject? json, string key)
    {
        if (json is null || !json.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        return value.TryGetValue<int>(out var number) ? number : null;
    }

    public static bool Flag(JsonObject? json, string key)
    {
        if (json is null || !json.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return false;
        }

        return value.TryGetValue<bool>(out var flag) && flag;
    }

    /// <summary>Reads a flag whose contract default is <see langword="true"/> (for example <c>usesRemoteHost</c>).</summary>
    public static bool FlagDefaultTrue(JsonObject? json, string key)
    {
        if (json is null || !json.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return true;
        }

        return !value.TryGetValue<bool>(out var flag) || flag;
    }

    public static JsonArray Array(JsonObject? json, string key) =>
        json is not null && json.TryGetPropertyValue(key, out var node) && node is JsonArray array ? array : [];

    public static IEnumerable<JsonObject> Objects(JsonObject? json, string key) =>
        Array(json, key).OfType<JsonObject>();

    public static IReadOnlyList<MobileHostEntry> Hosts(JsonObject? json, string key = "hosts") =>
        Objects(json, key).Select(Host).ToArray();

    public static MobileHostEntry Host(JsonObject json) => new(
        Text(json, "alias"),
        Text(json, "host"),
        Int(json, "port", RemoteCommandsMobileContract.DefaultPort),
        Text(json, "username"),
        Text(json, "auth"),
        Flag(json, "credentialConfigured"));

    public static IReadOnlyList<MobileHostKeyEntry> HostKeys(JsonObject? json, string key = "keys") =>
        Objects(json, key).Select(HostKey).ToArray();

    public static MobileHostKeyEntry HostKey(JsonObject json) => new(
        Text(json, "host"),
        Int(json, "port", RemoteCommandsMobileContract.DefaultPort),
        Text(json, "hostKeyName"),
        Text(json, "fingerprint"),
        Text(json, "addedAt"));

    public static IReadOnlyList<MobileCommandDefinition> Commands(JsonObject? json) =>
        Objects(json, "commands").Select(Command).ToArray();

    /// <summary>
    /// Catalog text for the editor. The module owns the file: when it returns the text the page uses it
    /// verbatim, otherwise the page falls back to reading the canonical path the module reported.
    /// </summary>
    public static string CatalogYaml(JsonObject? json)
    {
        foreach (var key in new[] { "yaml", "content", "commandsYaml", "catalogYaml" })
        {
            if (Text(json, key) is { Length: > 0 } text)
            {
                return text;
            }
        }

        return "";
    }

    public static MobileCatalogSnapshot Catalog(JsonObject? json) => new(
        Commands(json),
        Text(json, "error"),
        Text(json, "commandsPath"),
        CatalogYaml(json));

    public static MobileCommandDefinition Command(JsonObject json) => new(
        Text(json, "id"),
        Text(json, "label"),
        Text(json, "command"),
        Text(json, "description"),
        Text(json, "type"),
        Text(json, "host"),
        Text(json, "input1Label"),
        Text(json, "input1Placeholder"),
        Text(json, "input2Label"),
        Text(json, "input2Placeholder"),
        Flag(json, "showSecondInput"),
        FlagDefaultTrue(json, "usesRemoteHost"));

    public static MobileModuleState State(JsonObject? json) => new(
        Text(json, "commandsPath"),
        Int(json, "commandCount"),
        Text(json, "commandsError"),
        Text(json, "defaultHost"),
        Text(json, "knownHosts"),
        Int(json, "historyRetention", 500),
        Text(json, "condaExecutable"),
        Int(json, "commandTimeoutMinutes", 30),
        Hosts(json),
        HostKeys(json, "trustedHostKeys"),
        Text(json, "activeInvocationId"),
        Text(json, "activeStage"),
        Text(json, "lastRunState"),
        Text(json, "lastRunSummary"),
        Flag(json, "backgroundAvailable"),
        // A module that does not report the transport flag must not silently disable the page: only an
        // explicit false means "no managed SSH on this device".
        FlagDefaultTrue(json, "transportAvailable"),
        Text(json, "dataDirectory"));

    public static MobileHostsSnapshot HostsSnapshot(JsonObject? json) => new(
        Hosts(json),
        Array(json, "missingAliases").Select(node => node?.GetValue<string>() ?? "")
            .Where(alias => alias.Length > 0)
            .ToArray(),
        Text(json, "catalogPath"),
        Flag(json, "loadFailed"));

    public static MobileHistorySummary History(JsonObject? json) => new(
        Int(json, "count"),
        Text(json, "latestTimestamp"),
        Text(json, "latestLabel"),
        Text(json, "latestHost"));

    public static MobilePendingHostKey? PendingHostKey(JsonObject? json)
    {
        if (json is null ||
            !json.TryGetPropertyValue("pendingHostKey", out var node) ||
            node is not JsonObject pending)
        {
            return null;
        }

        return Pending(pending, json);
    }

    public static MobilePendingHostKey Pending(JsonObject pending, JsonObject? parent = null) => new(
        Text(pending, "host") is { Length: > 0 } host ? host : Text(parent, "resolvedHost"),
        NullableInt(pending, "port"),
        Text(pending, "hostKeyName"),
        Text(pending, "fingerprint"),
        FlagDefaultTrue(pending, "firstUse"),
        Text(pending, "message"));

    public static MobileRunOutcome Run(JsonObject? json) => new(
        Text(json, "state"),
        Text(json, "message"),
        NullableInt(json, "exitCode"),
        Text(json, "host"),
        Text(json, "alias"),
        Text(json, "resolvedHost"),
        Text(json, "output"),
        PendingHostKey(json),
        Text(json, "transport"));
}
