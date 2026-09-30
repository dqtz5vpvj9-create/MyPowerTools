using System.Text.Json;

namespace FileTransfer.Core.Cloud;

public sealed record CloudProvider(string Id, string Name, bool AuthorizationAvailable, string? UnavailableReason);
public sealed record CloudAccount(string Id, string ProviderId, string DisplayName, string Status,
    string FolderId, string FolderName, int StorageId = 0, string MountPath = "", long? Capacity = null, string? Error = null, string? Recovery = null);
public sealed record CloudAuthorization(string OperationId, string ProviderId, string? AccountId, string State,
    string? AuthorizationUrl, string? Error, string? Recovery);
public sealed record CloudPreferences(string? DefaultAccountId = null, string Mode = "auto",
    bool TransferAvailable = false, string TransferUnavailableReason = "账号目录已接入后，还需完成指定文件领取通道；当前自动方式继续使用现有传输，仅经网盘会保留待发内容。",
    bool CleanupAvailable = false, string CleanupUnavailableReason = "尚未建立专用副本的归属记录，暂不提供自动清理。");
public sealed record CloudAccountState(CloudAccount[] Accounts, CloudPreferences Preferences);

/// <summary>Contains public metadata only. Provider credentials belong in the platform SecretStore.</summary>
public sealed class CloudAccountStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<CloudAccountState> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(path)) return new([], new());
        return JsonSerializer.Deserialize<CloudAccountState>(await File.ReadAllTextAsync(path, token), Json)
            ?? throw new InvalidDataException("网盘账号记录无法读取，原记录未修改。");
    }
    public async Task SaveAsync(CloudAccountState state, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".new";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, Json), token);
        File.Move(temporary, path, true);
    }
}

public static class CloudAccountRules
{
    public const string PendingReason = "已保留待发内容。仅经网盘需要完成网盘授权和指定文件领取能力；可恢复网盘，或在我的网盘中改为自动。";
    public static CloudPreferences SetMode(CloudPreferences prior, string mode) =>
        mode is "auto" or "cloudOnly" ? prior with { Mode = mode } : throw new ArgumentException("传输方式必须是 auto 或 cloudOnly。");
    public static CloudAccountState SetDefault(CloudAccountState state, string id)
    {
        if (!state.Accounts.Any(a => a.Id == id && a.Status == "ready")) throw new ArgumentException("请选择当前可用且未暂停的网盘。");
        return state with { Preferences = state.Preferences with { DefaultAccountId = id } };
    }
    public static CloudAccountState Pause(CloudAccountState state, string id, bool paused)
    {
        var account = state.Accounts.SingleOrDefault(a => a.Id == id) ?? throw new ArgumentException("网盘账号不存在。");
        // Resume requires a fresh provider check; never turn an expired credential into ready.
        var updated = paused ? account with { Status = "paused" } : account with { Status = "blocked", Error = "需要重新验证账号。", Recovery = "请重新登录后检查目录读写权限。" };
        return state with { Accounts = state.Accounts.Select(a => a.Id == id ? updated : a).ToArray() };
    }
}
