using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using static FileTransfer.Surface.CloudAccountsSnapshot;

namespace FileTransfer.Surface;

internal sealed partial class CloudAccountsView
{
    private void RenderManage()
    {
        var account = CurrentAccount;
        if (account is null) { RenderHome(); return; }
        Page("manage", "管理" + _core.Snapshot.ProviderName(account.ProviderId));
        _body.Children.Add(MobileUi.CardTitle(account.DisplayName));
        _body.Children.Add(Copy(account.StatusLabel + " · 仅这台设备"));
        if (account.StatusMessage.Length > 0) _body.Children.Add(Copy(account.StatusMessage));
        _body.Children.Add(Copy(account.SpaceLabel, true));
        _body.Children.Add(ActionButton("选择存放位置", token => ReadFoldersAsync(null, token)));
        if (account.IsReady && _core.Snapshot.DefaultAccountId != account.Id)
            _body.Children.Add(ActionButton("设为默认中转盘", token => ChangeAsync("default", new() { ["accountId"] = account.Id }, token)));
        if (account.Status != "paused")
            _body.Children.Add(Navigation("重新登录", () => { _providerId = account.ProviderId; RenderAuthorizationIntro(); }));
        _body.Children.Add(Navigation("清理中转文件", RenderCleanup));
        _body.Children.Add(ActionButton(account.Status == "paused" ? "恢复使用" : "暂停使用", token =>
        {
            if (account.Status == "paused") return ChangeAsync("pause", new() { ["accountId"] = account.Id, ["paused"] = false }, token);
            RenderPause(); return Task.CompletedTask;
        }));
        _body.Children.Add(Navigation("断开连接", RenderDisconnect));
        _body.Children.Add(Copy("连接信息留在本机。配对设备不会因此获得你的账号登录权限。", true));
    }

    private void RenderPause()
    {
        Page("pause", "暂停这个网盘？");
        _body.Children.Add(Copy("不会退出账号，也不会删除已有文件。"));
        _body.Children.Add(Copy(_core.Snapshot.Mode == "cloudOnly"
            ? "仅经网盘已开启，待发文件会等你恢复后继续；文字消息照常发送。"
            : "自动模式下，新发送会使用其他可用方式。"));
        _body.Children.Add(Copy("尚未领取的文件如果依赖这台设备提供下载，需要恢复后才能继续领取。", true));
        _body.Children.Add(ActionButton("确认暂停", token => ChangeAsync("pause", new() { ["accountId"] = _accountId, ["paused"] = true }, token), true));
        _body.Children.Add(Navigation("继续使用", RenderManage));
    }

    private void RenderDisconnect()
    {
        Page("disconnect", "断开网盘连接？");
        _body.Children.Add(Copy("移除这台设备保存的登录信息。网盘里的文件会保留。"));
        _body.Children.Add(Copy(_core.Snapshot.Mode == "cloudOnly"
            ? "仅经网盘已开启，待发文件会保留到你重新连接；文字消息照常发送。"
            : "以后的发送会使用其他可用方式。"));
        _body.Children.Add(Copy("已发出但尚未领取的文件，可能需要这台设备重新连接网盘。", true));
        _body.Children.Add(ActionButton("确认断开，保留网盘文件", token => ChangeAsync("disconnect", new() { ["accountId"] = _accountId }, token), true));
        _body.Children.Add(Navigation("保持连接", RenderManage));
    }

    private void RenderPolicy()
    {
        Page("policy", "使用方式");
        var auto = Choice("自动", _core.Snapshot.TransferAvailable
            ? "优先直连；需要中转时用默认网盘，网盘不可用可使用应用中转。"
            : "使用现有直连和应用中转。当前版本尚未接入网盘发送。", _core.Snapshot.Mode != "cloudOnly", "cloud-mode");
        var cloud = Choice("仅经网盘", "文件只经默认网盘发送，不可用时保留待发，不改用直连或应用中转。文字消息照常发送。", _core.Snapshot.Mode == "cloudOnly", "cloud-mode");
        _body.Children.Add(auto); _body.Children.Add(cloud);
        if (!_core.Snapshot.TransferAvailable) _body.Children.Add(Copy(_core.Snapshot.TransferUnavailableReason, true));
        if (_core.Snapshot.Accounts.Count == 0) _body.Children.Add(Copy("还没有连接网盘。选择仅经网盘后，文件会先等待；文字消息照常发送。", true));
        _body.Children.Add(ActionButton("保存使用方式", token => ChangeAsync("preferences", new() { ["mode"] = cloud.IsChecked == true ? "cloudOnly" : "auto" }, token), true));
    }

    private static RadioButton Choice(string title, string description, bool selected, string group)
    {
        var choice = new RadioButton
        {
            Content = MobileUi.Stack(5, MobileUi.CardTitle(title), Copy(description, true)),
            GroupName = group, IsChecked = selected, MinHeight = 48, Padding = new Thickness(0, 8),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        Avalonia.Automation.AutomationProperties.SetName(choice, title);
        return choice;
    }

    private void RenderDefault()
    {
        Page("default", "默认中转盘");
        _body.Children.Add(Copy("需要网盘时，使用你选择的可用账号。"));
        var ready = _core.Snapshot.Accounts.Where(a => a.IsReady).ToArray();
        var choices = new List<(CloudAccount Account, RadioButton Button)>();
        foreach (var account in ready)
        {
            var choice = Choice(_core.Snapshot.ProviderName(account.ProviderId) + " · " + account.DisplayName,
                account.FolderName, account.Id == _core.Snapshot.DefaultAccountId, "default-cloud");
            choices.Add((account, choice)); _body.Children.Add(choice);
        }
        if (ready.Length == 0)
        {
            _body.Children.Add(Copy("目前没有可用的账号。请先登录或恢复网盘，再设为默认。"));
            _body.Children.Add(Navigation("查看我的网盘", RenderHome));
            return;
        }
        _body.Children.Add(ActionButton("保存默认中转盘", async token =>
        {
            var selected = choices.FirstOrDefault(c => c.Button.IsChecked == true).Account;
            if (selected is null) { ShowStatus("请选择一个可用账号。"); return; }
            await ChangeAsync("default", new() { ["accountId"] = selected.Id }, token);
        }, true));
    }

    private async Task ReadFoldersAsync(string? parentId, CancellationToken token)
    {
        var answer = await _core.CallAsync("folders", new() { ["accountId"] = _accountId, ["parentId"] = parentId }, token);
        Page("folders", "选择存放位置");
        _body.Children.Add(Copy("选择位置后，MPT 使用其中的专用文件夹。仅影响以后的发送，已有文件不移动。"));
        if (!Flag(answer, "available"))
        {
            _body.Children.Add(Copy(Text(answer, "unavailableReason", "暂时不能读取目录，请恢复连接后重试。")));
            _body.Children.Add(ActionButton("重试读取目录", next => ReadFoldersAsync(parentId, next)));
            return;
        }
        if (parentId is not null)
        {
            _body.Children.Add(ActionButton("使用此位置", next => ChangeAsync("directory", new() { ["accountId"] = _accountId, ["folderId"] = parentId }, next), true));
            _body.Children.Add(ActionButton("回到根目录", next => ReadFoldersAsync(null, next)));
        }
        foreach (var folder in Rows(answer["folders"]))
        {
            var id = Text(folder, "id");
            _body.Children.Add(ActionButton(Text(folder, "name"), next => ReadFoldersAsync(id, next)));
        }
        if (!Rows(answer["folders"]).Any()) _body.Children.Add(Copy("这里没有其他文件夹。", true));
        _body.Children.Add(Navigation("保持原来的位置", RenderManage));
    }

    private void RenderCleanup()
    {
        Page("cleanup", "清理中转文件");
        _body.Children.Add(Copy("只清理 MPT 创建的中转副本，不动你的其他文件。"));
        // No deletion command exists until the module tracks owned objects and all recipients.
        // Offering a convincing button here would conceal a missing data-safety prerequisite.
        _body.Children.Add(Copy(_core.Snapshot.CleanupUnavailableReason.Length > 0
            ? _core.Snapshot.CleanupUnavailableReason
            : "当前版本还不能安全识别可清理的中转副本，暂不提供自动清理。网盘文件会继续保留。"));
        _body.Children.Add(Copy("如果空间不足，可以在网盘应用中查看空间，或选择另一个默认中转盘。", true));
        _body.Children.Add(Navigation("选择其他默认中转盘", RenderDefault));
        _body.Children.Add(Navigation("返回我的网盘", RenderHome));
    }

    private void RenderPrivacy()
    {
        Page("privacy", "账号与设备授权");
        _body.Children.Add(Copy("账号登录信息只保存在这台设备，不随设备配对或共享会话连接码发送。"));
        _body.Children.Add(Copy("接收方只领取消息中的指定文件，不需要登录发送方的网盘。"));
        _body.Children.Add(Copy("另一台设备想用这个网盘发送，需要在那台设备单独登录。"));
        _body.Children.Add(Copy("部分网盘的领取需要发送设备在线更新链接；暂停或断开账号可能影响尚未领取的文件。", true));
    }

    private void RenderHelp()
    {
        Page("help", "帮助与高级设置");
        _body.Children.Add(Copy("网盘是可选的中转方式。不添加网盘，仍可以使用现有直连和应用中转。"));
        _body.Children.Add(Copy("网盘分担应用中转服务器的带宽，手机上传和下载仍会消耗网络流量。", true));
        _body.Children.Add(ActionButton("重新检查账号状态", async token => { await _core.RefreshAsync(token); RenderHome(); }));
        _body.Children.Add(Navigation("高级手动配置", () => AdvancedRequested?.Invoke()));
        _body.Children.Add(Copy("手动配置供已有 OpenList / WebDAV 的用户使用，普通登录流程不需要填写这些信息。", true));
    }
}
