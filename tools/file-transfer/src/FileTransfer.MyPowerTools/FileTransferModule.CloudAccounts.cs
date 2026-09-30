using System.Text.Json.Nodes;
using FileTransfer.Core.Cloud;
using MyPowerTools.Platform.Abstractions;
using FileTransfer.Core;

namespace FileTransfer.MyPowerTools;

public sealed partial class FileTransferModule
{
    private readonly SemaphoreSlim _cloudAccountGate = new(1, 1);
    private CloudAccountStore? _cloudAccountStore;
    private volatile CloudAccountState _cloudAccounts = new([], new());
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CloudAuthorization> _cloudAuthorizations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _cloudPreparation = new();
    private bool CloudOnly => _cloudAccounts.Preferences.Mode == "cloudOnly";
    private static readonly CloudProvider[] CloudProviders = [
        new("quark", "夸克网盘", false, "尚未接入可用于文件上传的同机授权。Cookie 驱动不能直接读取其他浏览器的登录；TV 扫码驱动不支持上传。"),
        new("baidu", "百度网盘", false, "正在接入百度授权回调；当前版本没有可完成并验证的同机授权入口。")
    ];
    private void EmitCloudAccountsChanged(string reason) => _events.Writer.TryWrite(new(Id,
        (ulong)Interlocked.Increment(ref _seq), "file-transfer.cloud.accounts.changed", DateTimeOffset.UtcNow,
        new JsonObject { ["reason"] = reason }));
    private object CloudAccountInspection(bool native = false) => new { providers = CloudProviders.Select(p => native ? p with { AuthorizationAvailable = true, UnavailableReason = null } : p).ToArray(), accounts = _cloudAccounts.Accounts,
        preferences = _cloudAccounts.Preferences with { TransferAvailable = _cloudTransportAvailable, TransferUnavailableReason = _cloudTransportAvailable ? "共享附件可通过网盘领取；领取时发送设备需在线。私聊附件尚未接入此通道。" : "网盘附件领取服务尚未确认可用；仅经网盘的附件会保留待发，文字仍正常发送。" }, operations = _cloudAuthorizations.Values.ToArray() };
    private async Task<object> CloudAccountCommandAsync(string command, JsonObject args, CancellationToken token)
    {
        if (command == "authorize.status" && args["operationId"]?.GetValue<string>() is { } lookup)
            return _cloudAuthorizations.TryGetValue(lookup, out var current) ? current : throw new ArgumentException("授权操作已结束，请重新添加网盘。");
        if (command == "authorize.cancel" && args["operationId"]?.GetValue<string>() is { } cancelling
            && _cloudPreparation.TryGetValue(cancelling, out var active))
        {
            try { active.Cancel(); } catch (ObjectDisposedException) { /* preparation has just completed */ }
            return _cloudAuthorizations[cancelling] with { State = "cancelled", AuthorizationUrl = null };
        }
        await _cloudAccountGate.WaitAsync(token);
        try
        {
            _cloudAccountStore ??= new CloudAccountStore(Path.Combine(_data, "cloud-accounts.json"));
            string Required(string name) => args[name]?.GetValue<string>() is { Length: > 0 } value ? value : throw new ArgumentException($"缺少 {name}。");
            CloudAuthorization Operation() => _cloudAuthorizations.TryGetValue(Required("operationId"), out var op) ? op : throw new ArgumentException("授权操作已结束，请重新添加网盘。");
            switch (command)
            {
                case "inspect": return CloudAccountInspection(args["nativeAuthorizationAvailable"]?.GetValue<bool>() == true);
                case "authorize.begin":
                    var provider = CloudProviders.SingleOrDefault(p => p.Id == Required("providerId")) ?? throw new ArgumentException("不支持该网盘。");
                    var accountId = args["accountId"]?.GetValue<string>();
                    if (accountId is not null && !_cloudAccounts.Accounts.Any(a => a.Id == accountId && a.ProviderId == provider.Id)) throw new ArgumentException("重新登录的账号不存在或品牌不符。");
                    var available = args["nativeAuthorizationAvailable"]?.GetValue<bool>() == true;
                    var operation = new CloudAuthorization(Guid.NewGuid().ToString("N"), provider.Id, accountId, available ? "waiting" : "blocked", available ? (provider.Id == "quark" ? "https://pan.quark.cn/" : "https://api.oplist.org/") : null, available ? null : provider.UnavailableReason,
                        "现有文件互传仍可使用自动方式；无需提供 Cookie、开发者令牌或管理员密码。");
                    _cloudAuthorizations[operation.OperationId] = operation;
                    EmitCloudAccountsChanged("authorization");
                    return operation;
                case "authorize.complete": return await CompleteCloudAuthorizationAsync(Operation(), Required("credential"), Required("credentialKind"), token);
                case "authorize.status": return Operation();
                case "authorize.cancel":
                    var pending = Operation();
                    if (pending.State == "ready") throw new InvalidOperationException("账号已连接；如需移除，请使用断开账号。");
                    var cancelled = pending with { State = "cancelled", AuthorizationUrl = null };
                    _cloudAuthorizations[cancelled.OperationId] = cancelled;
                    EmitCloudAccountsChanged("cancelled");
                    return cancelled;
                case "default": _cloudAccounts = CloudAccountRules.SetDefault(_cloudAccounts, Required("accountId")); break;
                case "pause":
                    var pauseId = Required("accountId");
                    var pauseAccount = _cloudAccounts.Accounts.SingleOrDefault(a => a.Id == pauseId) ?? throw new ArgumentException("网盘账号不存在。");
                    var paused = args["paused"]?.GetValue<bool>() ?? true;
                    using (var api = await CloudAdminAsync(token))
                    {
                        await api.SetPausedAsync(pauseAccount.StorageId, paused, token);
                        if (!paused) await api.PrepareAsync(pauseAccount.MountPath, pauseAccount.FolderId, token);
                    }
                    _cloudAccounts = _cloudAccounts with { Accounts = _cloudAccounts.Accounts.Select(a => a.Id == pauseId ? a with { Status = paused ? "paused" : "ready", Error = null, Recovery = null } : a).ToArray() };
                    break;
                case "disconnect":
                    var id = Required("accountId");
                    if (!_cloudAccounts.Accounts.Any(a => a.Id == id)) throw new ArgumentException("网盘账号不存在。");
                    using (var api = await CloudAdminAsync(token)) await api.UnmountAsync(_cloudAccounts.Accounts.Single(a => a.Id == id).StorageId, token);
                    await _secrets.DeleteAsync(SecretReference.Create(Id, "cloud-account-" + id), token);
                    _cloudAccounts = _cloudAccounts with { Accounts = _cloudAccounts.Accounts.Where(a => a.Id != id).ToArray(),
                        Preferences = _cloudAccounts.Preferences.DefaultAccountId == id ? _cloudAccounts.Preferences with { DefaultAccountId = null } : _cloudAccounts.Preferences };
                    break;
                case "folders":
                    var folderAccount = _cloudAccounts.Accounts.SingleOrDefault(a => a.Id == Required("accountId")) ?? throw new ArgumentException("网盘账号不存在。");
                    using (var api = await CloudAdminAsync(token))
                        return new { folders = await api.FoldersAsync(folderAccount.MountPath, args["parentId"]?.GetValue<string>() ?? folderAccount.MountPath, token), available = true, unavailableReason = (string?)null };
                case "directory":
                    var selected = _cloudAccounts.Accounts.SingleOrDefault(a => a.Id == Required("accountId")) ?? throw new ArgumentException("网盘账号不存在。");
                    var folder = Required("folderId").TrimEnd('/') + "/MPT-" + selected.Id;
                    using (var api = await CloudAdminAsync(token)) await api.PrepareAsync(selected.MountPath, folder, token);
                    _cloudAccounts = _cloudAccounts with { Accounts = _cloudAccounts.Accounts.Select(a => a.Id == selected.Id ? a with { FolderId = folder, FolderName = folder[(selected.MountPath.Length + 1)..] } : a).ToArray() };
                    break;
                case "preferences":
                    _cloudAccounts = _cloudAccounts with { Preferences = CloudAccountRules.SetMode(_cloudAccounts.Preferences, Required("mode")) };
                    if (CloudOnly) await StopNonCloudAttachmentsAsync(token);
                    break;
                default: throw new ArgumentException("未知网盘账号操作。");
            }
            await _cloudAccountStore.SaveAsync(_cloudAccounts, token);
            SignalAssistant();
            WakeCloudPayloads();
            EmitCloudAccountsChanged("accounts");
            return CloudAccountInspection();
        }
        finally { _cloudAccountGate.Release(); }
    }
    private async Task<OpenListCloudAccountClient> CloudAdminAsync(CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            var initial = await _openList.InitializeAdminAsync(token);
            if (initial is not null) await _secrets.SaveAsync(Id, "openlist-admin", initial, token);
            if (!_openList.Running) await _openList.StartAsync("127.0.0.1", token);
            var api = new OpenListCloudAccountClient(new Uri(_openList.AdminUrl), await SecretAsync("openlist-admin", token) ?? throw new InvalidOperationException("本机网盘服务凭据不可用。"));
            try { await api.LoginAsync(token); return api; }
            catch { api.Dispose(); throw; }
        }
        finally { _operations.Release(); }
    }
    private async Task<object> CompleteCloudAuthorizationAsync(CloudAuthorization operation, string credential, string kind, CancellationToken token)
    {
        if (operation.State != "waiting") throw new InvalidOperationException("授权操作已取消或结束，请重新登录。");
        if (kind != (operation.ProviderId == "quark" ? "cookie" : "refreshToken") || credential.Length > 32768 || credential.Any(char.IsControl))
            throw new ArgumentException("授权返回内容无效，请重新登录。");
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = preparation.Token;
        _cloudPreparation[operation.OperationId] = preparation;
        _cloudAuthorizations[operation.OperationId] = operation with { State = "preparing", AuthorizationUrl = null };
        EmitCloudAccountsChanged("preparing");
        var old = _cloudAccounts.Accounts.SingleOrDefault(a => a.Id == operation.AccountId);
        var id = old?.Id ?? Guid.NewGuid().ToString("N");
        var mount = "/mpt-account-" + Guid.NewGuid().ToString("N");
        int storageId = 0;
        string? previousSecret = null;
        var credentialSaved = false;
        try
        {
            previousSecret = await SecretAsync("cloud-account-" + id, token);
            using var api = await CloudAdminAsync(token);
            storageId = await api.MountAsync(operation.ProviderId, credential, mount, token);
            var relative = old is null ? "MPT-" + id : old.FolderId[(old.MountPath.Length + 1)..];
            var folder = mount + "/" + relative;
            await api.PrepareAsync(mount, folder, token);
            await _secrets.SaveAsync(Id, "cloud-account-" + id, credential, token);
            credentialSaved = true;
            var account = new CloudAccount(id, operation.ProviderId, old?.DisplayName ?? (operation.ProviderId == "quark" ? "夸克网盘" : "百度网盘"), "ready", folder, relative, storageId, mount);
            var next = _cloudAccounts with { Accounts = _cloudAccounts.Accounts.Where(a => a.Id != id).Append(account).ToArray(),
                Preferences = _cloudAccounts.Preferences with { DefaultAccountId = _cloudAccounts.Preferences.DefaultAccountId ?? id } };
            token.ThrowIfCancellationRequested();
            await _cloudAccountStore!.SaveAsync(next, token);
            _cloudAccounts = next;
            if (old is not null)
            {
                if (_cloudPayloads is not null)
                    foreach (var mapping in (await _cloudPayloads.ReadAsync(token)).Where(m => m.AccountId == id && m.MountPath == old.MountPath))
                        await _cloudPayloads.SaveAsync(mapping with { MountPath = mount, ObjectPath = mount + mapping.ObjectPath[old.MountPath.Length..] }, token);
                try { await api.UnmountAsync(old.StorageId, token); }
                catch { /* New verified account is committed. Keep old mount until explicit maintenance; never remove user files. */ }
            }
            return _cloudAuthorizations[operation.OperationId] = operation with { AccountId = id, State = "ready", AuthorizationUrl = null, Error = null, Recovery = null };
        }
        catch (Exception)
        {
            if (credentialSaved && !_cloudAccounts.Accounts.Any(a => a.StorageId == storageId))
            {
                if (previousSecret is not null) await _secrets.SaveAsync(Id, "cloud-account-" + id, previousSecret, CancellationToken.None);
                else await _secrets.DeleteAsync(SecretReference.Create(Id, "cloud-account-" + id), CancellationToken.None);
            }
            if (storageId > 0 && !_cloudAccounts.Accounts.Any(a => a.StorageId == storageId))
            {
                try { using var cleanup = await CloudAdminAsync(CancellationToken.None); await cleanup.UnmountAsync(storageId, CancellationToken.None); } catch { /* keep original failure; no user files are deleted */ }
            }
            return _cloudAuthorizations[operation.OperationId] = operation with { State = token.IsCancellationRequested ? "cancelled" : "failed", AuthorizationUrl = null,
                Error = "网盘授权或目录读写确认未完成，已有账号未被替换。", Recovery = "请重新登录，并检查网盘空间与网络后重试。" };
        }
        finally { _cloudPreparation.TryRemove(operation.OperationId, out _); EmitCloudAccountsChanged("authorization"); WakeCloudPayloads(); }
    }

}
