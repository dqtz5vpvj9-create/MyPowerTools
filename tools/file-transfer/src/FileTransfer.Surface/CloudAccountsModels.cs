using System.Text.Json.Nodes;

namespace FileTransfer.Surface;

// These are presentation snapshots, deliberately containing no provider credentials.
internal sealed record CloudProvider(string Id, string Name, bool AuthorizationAvailable, string UnavailableReason);
internal sealed record CloudAccount(string Id, string ProviderId, string DisplayName, string Status,
    string FolderId, string FolderName, long? UsedBytes, long? TotalBytes, string StatusMessage)
{
    public bool IsReady => Status == "ready";
    public string StatusLabel => Status switch
    {
        "ready" => "已连接", "paused" => "已暂停", "authorizing" => "等待授权",
        "preparing" => "正在准备文件夹", "authExpired" => "需要重新登录", "noSpace" => "空间不足",
        "blocked" => "暂时无法连接", "failed" => "连接需要处理", _ => "正在确认状态"
    };
    public string SpaceLabel => UsedBytes is { } used && TotalBytes is > 0
        ? $"已用 {Bytes(used)} / {Bytes(TotalBytes.Value)}" : "空间信息暂不可用";
    public static string Bytes(long bytes) => bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.#} TB"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}

internal sealed record CloudAccountsSnapshot(IReadOnlyList<CloudProvider> Providers, IReadOnlyList<CloudAccount> Accounts,
    string? DefaultAccountId, string Mode, string CleanupPolicy, bool CanCleanup, string CleanupUnavailableReason,
    bool TransferAvailable, string TransferUnavailableReason)
{
    public static CloudAccountsSnapshot Empty { get; } = new([], [], null, "auto", "manual", false, "", false, "");
    public CloudAccount? DefaultAccount => Accounts.FirstOrDefault(a => a.Id == DefaultAccountId);
    public string ProviderName(string id) => Providers.FirstOrDefault(p => p.Id == id)?.Name ?? "网盘";

    public static CloudAccountsSnapshot Parse(JsonNode node)
    {
        var preferences = node["preferences"];
        return new(
            Rows(node["providers"]).Select(p => new CloudProvider(Text(p, "id"), Text(p, "name"),
                Flag(p, "authorizationAvailable"), Text(p, "unavailableReason"))).ToArray(),
            Rows(node["accounts"]).Select(a => new CloudAccount(Text(a, "id"), Text(a, "providerId"),
                Text(a, "displayName"), Text(a, "status"), Text(a, "folderId"), Text(a, "folderName"),
                Number(a["capacity"], "usedBytes"), Number(a["capacity"], "totalBytes"), Text(a, "recovery", Text(a, "error")))).ToArray(),
            Text(preferences, "defaultAccountId") is { Length: > 0 } id ? id : null,
            Text(preferences, "mode", "auto"), Text(preferences, "cleanupPolicy", "manual"),
            Flag(preferences, "cleanupAvailable"), Text(preferences, "cleanupUnavailableReason"),
            Flag(preferences, "transferAvailable"), Text(preferences, "transferUnavailableReason", "当前版本尚未接入网盘发送。自动方式仍可传输；仅经网盘会保留待发内容。"));
    }

    internal static IEnumerable<JsonNode> Rows(JsonNode? node) => node is JsonArray rows ? rows.OfType<JsonNode>() : [];
    internal static string Text(JsonNode? node, string key, string fallback = "") =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    internal static bool Flag(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<bool>(out var value) && value;
    private static long? Number(JsonNode? node, string key) => node is JsonObject row && row[key] is JsonValue v && v.TryGetValue<long>(out var n) && n >= 0 ? n : null;
}
