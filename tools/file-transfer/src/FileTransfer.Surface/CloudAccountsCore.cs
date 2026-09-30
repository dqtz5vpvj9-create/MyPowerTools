using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>Command boundary for the account page. All account state remains module-owned.</summary>
internal sealed class CloudAccountsCore(MptAvaloniaSurfaceContext context)
{
    public CloudAccountsSnapshot Snapshot { get; private set; } = CloudAccountsSnapshot.Empty;

    public async Task RefreshAsync(CancellationToken token)
    {
        var node = await CallAsync("inspect", new() { ["nativeAuthorizationAvailable"] = context.AuthorizeCloudAccountAsync is not null }, token);
        Snapshot = CloudAccountsSnapshot.Parse(node);
    }

    public async Task<JsonNode> CallAsync(string operation, JsonObject? args, CancellationToken token)
    {
        var result = await context.ExecuteCommandAsync("file-transfer.cloud.accounts." + operation, args, token);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new CloudAccountsException(result.Error?.Code ?? "cloud.command_failed");
        if (string.IsNullOrWhiteSpace(result.Output)) return new JsonObject();
        try { return JsonNode.Parse(result.Output) ?? throw new JsonException(); }
        catch (JsonException) { throw new CloudAccountsException("cloud.invalid_response"); }
    }

    public IDisposable? Subscribe(Action changed) => context.SubscribeEvents?.Invoke(message =>
    {
        if (message.Type is "cloud.accounts.changed" or "file-transfer.cloud.accounts.changed") changed();
    });
}

// Neither exception messages nor command output are copied into the UI: an upstream error may
// contain a request URL or credentials. The module's typed code selects the recovery text.
internal sealed class CloudAccountsException(string code) : Exception("网盘操作未完成")
{
    public string Code { get; } = code;
    public string UserMessage => Code switch
    {
        "cloud.authorization_unavailable" or "cloud.application_not_configured" => "这个网盘的应用授权尚未准备好。你可以稍后重试，或继续使用现有传输方式。",
        "cloud.auth_expired" => "登录已过期，请重新登录这个网盘。",
        "cloud.no_space" => "网盘空间不足。释放空间或选择另一个默认中转盘后重试。",
        "cloud.account_not_ready" => "这个账号还不可用，请先完成登录或恢复连接。",
        "cloud.cleanup_unavailable" => "暂时不能安全清理中转副本。网盘里的文件已保留。",
        "cloud.invalid_response" => "网盘状态没有完整返回，请重试。",
        "command_not_found" or "runtime.command_not_found" => "当前文件互传模块尚未提供网盘账号管理，请更新模块后再试。",
        _ => "操作暂未完成。请检查连接后重试；原有文件和传输设置会保留。"
    };
}
