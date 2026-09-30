using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using static FileTransfer.Surface.CloudAccountsSnapshot;

namespace FileTransfer.Surface;

/// <summary>
/// Account management stays separate from the chat. Opening this lazy page does not start an
/// OpenList process or a provider login; only an explicit account command can do that.
/// </summary>
internal sealed partial class CloudAccountsView : UserControl
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CloudAccountsCore _core;
    private readonly StackPanel _body = new() { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _status = MobileUi.Note("");
    private readonly Button _back = MobileUi.BackButton("返回我的网盘");
    private CancellationTokenSource _lifetime = new();
    private IDisposable? _events;
    private bool _active, _busy, _refreshQueued, _refreshPending, _authorizationRefreshQueued;
    private string _page = "home", _accountId = "", _providerId = "";
    private string? _operationId;
    private JsonNode? _operation;

    public event Action<string>? TitleChanged;
    public event Action? CloseRequested;
    public event Action? AdvancedRequested;
    internal CloudAccountsCore Core => _core;
    internal string CurrentPage => _page;

    public CloudAccountsView(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        _core = new CloudAccountsCore(context);
        Classes.Add(MobileUi.Classes.Root);
        MobileUi.EnsureMobileTheme(this);
        _back.IsVisible = false;
        _back.Click += (_, _) => TryHandleBack();
        _status.IsVisible = false;
        Content = MobileUi.Stack(12, _back, _status, _body);
        AttachedToVisualTree += (_, _) => Activate();
        DetachedFromVisualTree += (_, _) => Deactivate();
        KeyDown += (_, e) => { if (e.Key == Key.Escape && TryHandleBack()) e.Handled = true; };
        RenderHome();
    }

    internal void Activate()
    {
        if (_active) return;
        if (_lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        _active = true;
        _events = _core.Subscribe(QueueRefresh);
        _ = RunAsync(async token => { await _core.RefreshAsync(token); RenderHome(); });
    }

    internal void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _events?.Dispose(); _events = null;
        _lifetime.Cancel();
        _ = CancelAuthorizationAsync();
    }

    private void QueueRefresh()
    {
        // The shared module event stream drives this page. No polling timer runs while it is idle.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_active || _refreshQueued) return;
            if (_busy)
            {
                _refreshPending = true;
                // Preparing the provider may hold the account writer for a while. Status has its
                // own read path; inspect would wait for the writer and hide the preparing state.
                if (_page == "authorization" && _operationId is not null && !_authorizationRefreshQueued)
                    _ = RefreshAuthorizationWhileBusyAsync(_lifetime.Token);
                return;
            }
            _refreshQueued = true;
            _ = RunAsync(async token =>
            {
                try
                {
                    await _core.RefreshAsync(token);
                    if (_page == "home") RenderHome();
                    else if (_page == "manage") RenderManage();
                    else if (_page == "authorization" && _operationId is not null) await ReadAuthorizationAsync(token);
                }
                finally { _refreshQueued = false; }
            });
        });
    }

    private async Task RefreshAuthorizationWhileBusyAsync(CancellationToken token)
    {
        _authorizationRefreshQueued = true;
        var id = _operationId;
        try
        {
            var operation = await _core.CallAsync("authorize.status", new() { ["operationId"] = id }, token);
            if (_active && _page == "authorization" && _operationId == id && !token.IsCancellationRequested)
            { _operation = operation; RenderAuthorization(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* The active command still owns its final answer and recovery text. */ }
        finally { _authorizationRefreshQueued = false; }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || !_active) return;
        _busy = true;
        _body.IsEnabled = false;
        _status.IsVisible = false;
        var token = _lifetime.Token;
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (CloudAccountsException ex) { if (!token.IsCancellationRequested) ShowStatus(ex.UserMessage); }
        catch (Exception) { if (!token.IsCancellationRequested) ShowStatus("操作暂未完成，请重试。已有账号、文件和会话会保留。"); }
        finally
        {
            _busy = false;
            _body.IsEnabled = true;
            if (_refreshPending && _active) { _refreshPending = false; QueueRefresh(); }
        }
    }

    private void ShowStatus(string text) { _status.Text = text; _status.IsVisible = text.Length > 0; }
    private void Page(string page, string title)
    {
        _page = page;
        _back.IsVisible = page != "home";
        _status.IsVisible = false;
        _body.Children.Clear();
        TitleChanged?.Invoke(title);
    }

    public bool TryHandleBack()
    {
        if (_page == "home") return false;
        // Cancelling navigation also cancels a pending native login; a late result cannot create an
        // account after its page was dismissed. It never cancels an already accepted file transfer.
        _lifetime.Cancel();
        _lifetime = new CancellationTokenSource();
        _ = CancelAuthorizationAsync();
        RenderHome();
        return true;
    }

    private async Task CancelAuthorizationAsync()
    {
        var id = _operationId;
        _operationId = null; _operation = null;
        if (id is null) return;
        try { await _core.CallAsync("authorize.cancel", new() { ["operationId"] = id }, CancellationToken.None); }
        catch (Exception) { /* No credential or remote error body is logged during best-effort cancellation. */ }
    }

    private Button ActionButton(string label, Func<CancellationToken, Task> action, bool primary = false)
    {
        var button = primary ? MobileUi.PrimaryButton(label) : MobileUi.SecondaryButton(label);
        button.MinHeight = 44;
        button.Command = new MptMobileAsyncCommand(() => RunAsync(action));
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Button Navigation(string label, Action action)
    {
        var button = MobileUi.TextButton(label);
        button.MinHeight = 44;
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, label);
        return button;
    }

    private Button Row(string icon, string title, string detail, Action action)
    {
        var row = MobileUi.ListRow(icon, title, detail, () => { action(); return Task.CompletedTask; });
        row.MinHeight = 56;
        AutomationProperties.SetName(row, title);
        return row;
    }

    private static TextBlock Copy(string text, bool caption = false)
    {
        var block = caption ? MobileUi.Caption(text) : MobileUi.Body(text);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private void RenderHome()
    {
        Page("home", "我的网盘");
        var state = _core.Snapshot;
        _body.Children.Add(Copy("从这台设备发送时，按需用网盘中转。", true));
        if (!state.TransferAvailable && (state.Accounts.Count > 0 || state.Mode == "cloudOnly"))
            _body.Children.Add(Copy(state.TransferUnavailableReason));
        if (state.Mode == "cloudOnly")
            _body.Children.Add(MobileUi.Card(MobileUi.Stack(7, MobileUi.CardTitle("仅经网盘已开启"),
                Copy("网盘不可用时，内容会保留在待发队列，不会改用其他方式。", true),
                Navigation("更改使用方式", RenderPolicy))));
        if (state.Accounts.Count == 0)
            _body.Children.Add(MobileUi.Card(MobileUi.Stack(10, MobileUi.EmptyTitle("添加你的网盘"),
                Copy("登录后准备 MPT 专用文件夹。以后从会话里照常发送。", true))));
        else
            foreach (var account in state.Accounts) _body.Children.Add(AccountCard(account));
        _body.Children.Add(ActionButton(state.Accounts.Count == 0 ? "添加网盘" : "添加另一个网盘", _ => { RenderProviders(); return Task.CompletedTask; }, true));
        _body.Children.Add(Copy("不添加网盘，也可以直接传文件。", true));
        _body.Children.Add(MobileUi.SectionTitle("使用偏好 · 仅在这台设备生效"));
        _body.Children.Add(MobileUi.ListCard(MobileUi.Stack(2,
            Row("MptMobileIconSend", "使用方式", state.Mode == "cloudOnly" ? "仅经网盘" : "自动", RenderPolicy),
            Row("MptMobileIconCloud", "默认中转盘", state.DefaultAccount is { } current ? state.ProviderName(current.ProviderId) + " · " + current.DisplayName : "尚未选择", RenderDefault),
            Row("MptMobileIconFile", "清理中转文件", "只处理 MPT 创建的文件", RenderCleanup))));
        _body.Children.Add(Navigation("账号与设备授权", RenderPrivacy));
        _body.Children.Add(Navigation("帮助与高级设置", RenderHelp));
        _body.Children.Add(Navigation("回到会话", () => CloseRequested?.Invoke()));
    }

    private Control AccountCard(CloudAccount account)
    {
        var state = _core.Snapshot;
        var body = MobileUi.Stack(8, MobileUi.CardTitle(state.ProviderName(account.ProviderId)),
            Copy(account.DisplayName + (state.DefaultAccountId == account.Id ? " · 默认中转盘" : "") + " · 仅这台设备", true),
            Copy(account.StatusLabel), Copy(account.SpaceLabel, true));
        if (!account.IsReady && account.StatusMessage.Length > 0) body.Children.Add(Copy(account.StatusMessage, true));
        if (account.FolderName.Length > 0) body.Children.Add(Copy("存放位置 · " + account.FolderName, true));
        body.Children.Add(Navigation("管理" + state.ProviderName(account.ProviderId) + " · " + account.DisplayName,
            () => { _accountId = account.Id; RenderManage(); }));
        return MobileUi.Card(body);
    }

    private CloudAccount? CurrentAccount => _core.Snapshot.Accounts.FirstOrDefault(a => a.Id == _accountId);
    private async Task ChangeAsync(string operation, JsonObject args, CancellationToken token)
    {
        await _core.CallAsync(operation, args, token);
        await _core.RefreshAsync(token);
        RenderHome();
    }
}
