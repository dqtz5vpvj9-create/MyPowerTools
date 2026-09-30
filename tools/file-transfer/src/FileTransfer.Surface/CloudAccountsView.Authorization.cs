using System.Text.Json.Nodes;
using Avalonia.Controls;
using static FileTransfer.Surface.CloudAccountsSnapshot;

namespace FileTransfer.Surface;

internal sealed partial class CloudAccountsView
{
    private void RenderProviders()
    {
        Page("providers", "添加网盘");
        _body.Children.Add(Copy("选择常用的网盘，登录后由 MPT 准备专用文件夹。"));
        foreach (var provider in _core.Snapshot.Providers.OrderBy(p => p.Id == "quark" ? 0 : p.Id == "baidu" ? 1 : 2))
            _body.Children.Add(Row("MptMobileIconCloud", provider.Name,
                provider.AuthorizationAvailable ? "登录并确认授权" : "查看连接条件",
                () => { _providerId = provider.Id; _accountId = ""; RenderAuthorizationIntro(); }));
        if (_core.Snapshot.Providers.Count == 0)
        {
            _body.Children.Add(Copy("暂未获取到可连接的网盘，请重试。现有传输方式不受影响。"));
            _body.Children.Add(ActionButton("重试", async token => { await _core.RefreshAsync(token); RenderProviders(); }));
        }
    }

    private void RenderAuthorizationIntro()
    {
        var provider = _core.Snapshot.Providers.FirstOrDefault(p => p.Id == _providerId);
        Page("authorizationIntro", (_accountId.Length > 0 ? "重新登录" : "连接") + (provider?.Name ?? "网盘"));
        _body.Children.Add(MobileUi.CardTitle("在网盘页面确认登录"));
        _body.Children.Add(Copy(_accountId.Length > 0 ? "重新登录会保留原来的文件夹和使用偏好。" : "完成后自动准备 MPT 专用文件夹。"));
        _body.Children.Add(MobileUi.Card(MobileUi.Stack(10,
            Copy("用于这台设备发送文件"), Copy("账号登录信息不会发给其他设备"),
            Copy("接收方只领取消息中的文件，不需要登录你的网盘", true))));
        _body.Children.Add(Copy("请在网盘页面确认实际申请的权限。无需在 MPT 中填写密码或复制登录信息。", true));
        if (provider is { AuthorizationAvailable: false } && provider.UnavailableReason.Length > 0)
            _body.Children.Add(Copy(provider.UnavailableReason));
        _body.Children.Add(ActionButton(provider?.AuthorizationAvailable == true ? "登录并连接" : "检查连接条件", BeginAuthorizationAsync, true));
        _body.Children.Add(Navigation("返回网盘列表", RenderHome));
    }

    private async Task BeginAuthorizationAsync(CancellationToken token)
    {
        var args = new JsonObject
        {
            ["providerId"] = _providerId,
            ["nativeAuthorizationAvailable"] = _context.AuthorizeCloudAccountAsync is not null
        };
        if (_accountId.Length > 0) args["accountId"] = _accountId;
        _operation = await _core.CallAsync("authorize.begin", args, token);
        _operationId = Text(_operation, "operationId");
        RenderAuthorization();
        if (Text(_operation, "state") != "waiting") return;
        if (_context.AuthorizeCloudAccountAsync is { } authorize)
        {
            // This local value is forwarded once to the module. It is never attached to a control,
            // retained in view state, included in a diagnostic, or persisted by the Surface.
            var result = await authorize(_providerId, token);
            token.ThrowIfCancellationRequested();
            if (result is null)
            {
                await CancelAuthorizationAsync();
                RenderAuthorizationIntro();
                ShowStatus("已取消登录，原来的账号与传输设置会保留。");
                return;
            }
            if (!string.Equals(result.ProviderId, _providerId, StringComparison.Ordinal))
                throw new CloudAccountsException("cloud.authorization_mismatch");
            ShowStatus("登录已返回，正在确认账号与专用文件夹。首次准备可能需要一些时间。");
            _operation = await _core.CallAsync("authorize.complete", new()
            {
                ["operationId"] = _operationId,
                ["credential"] = result.Credential,
                ["credentialKind"] = result.CredentialKind
            }, token);
            await ApplyAuthorizationAsync(token);
            return;
        }
        // Older desktop hosts can use the provider's browser flow only when the module actually
        // supplies one. Missing platform support never becomes a simulated successful login.
        if (!Uri.TryCreate(Text(_operation, "authorizationUrl"), UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            ShowStatus("当前平台没有可用的登录入口。请更新 MPT，或继续使用现有传输方式。");
            return;
        }
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null || !await launcher.LaunchUriAsync(uri))
            ShowStatus("没有打开网盘登录页。请检查默认浏览器后重试。");
    }

    private void RenderAuthorization()
    {
        Page("authorization", "连接" + _core.Snapshot.ProviderName(_providerId));
        var state = Text(_operation, "state");
        _body.Children.Add(MobileUi.CardTitle(state switch
        {
            "waiting" => "等待你确认登录", "preparing" => "正在准备 MPT 文件夹",
            "ready" => "网盘已连接", "cancelled" => "已取消登录", "blocked" => "还不能连接这个网盘",
            _ => "这次没有完成连接"
        }));
        var recovery = Text(_operation, "recovery", Text(_operation, "error"));
        var error = Text(_operation, "error");
        if (error.Length > 0 && error != recovery) _body.Children.Add(Copy(error));
        _body.Children.Add(Copy(recovery.Length > 0 ? recovery : state switch
        {
            "waiting" => "在网盘页面完成登录后，回到这里检查结果。",
            "preparing" => "正在创建专用目录并确认读写能力。完成前不会把账号标为可用。",
            "ready" => _core.Snapshot.TransferAvailable ? "回到会话就可以继续发送。接收方不需要登录同一个网盘。" : _core.Snapshot.TransferUnavailableReason,
            _ => "原有账号、文件和传输设置会保留。"
        }));
        if (state is "waiting" or "preparing")
            _body.Children.Add(ActionButton("检查登录结果", ReadAuthorizationAsync, true));
        else if (state == "ready")
            _body.Children.Add(Navigation("回到会话", () => CloseRequested?.Invoke()));
        else
            _body.Children.Add(ActionButton("重新检查连接条件", BeginAuthorizationAsync, true));
        _body.Children.Add(Navigation("返回我的网盘", () => { _ = CancelAuthorizationAsync(); RenderHome(); }));
    }

    private async Task ReadAuthorizationAsync(CancellationToken token)
    {
        if (_operationId is null) return;
        _operation = await _core.CallAsync("authorize.status", new() { ["operationId"] = _operationId }, token);
        await ApplyAuthorizationAsync(token);
    }

    private async Task ApplyAuthorizationAsync(CancellationToken token)
    {
        await _core.RefreshAsync(token);
        RenderAuthorization();
        if (Text(_operation, "state") is "ready" or "cancelled" or "failed" or "blocked") _operationId = null;
    }
}
