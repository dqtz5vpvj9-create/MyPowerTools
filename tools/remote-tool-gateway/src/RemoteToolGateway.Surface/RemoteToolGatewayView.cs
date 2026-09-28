using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using MyPowerTools.AvaloniaSdk.Controls;
using MyPowerTools.AvaloniaSdk;

namespace RemoteToolGateway.Surface;

/// <summary>
/// Desktop page for remote tool access. It shows the listener switch, one card per authorized
/// phone and the pending-confirmation list. A remote request only ever runs after the computer's
/// user clicks that request's own confirm button, and the execution then goes through the surface
/// context's existing command entry point so the normal confirmation/elevation chain applies.
/// </summary>
public sealed class RemoteToolGatewayView : UserControl
{
    private const double TouchTarget = 40;

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly ControlSurfaceViewModel _vm;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _endpoint = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBox _address = new() { MinHeight = TouchTarget, PlaceholderText = "本机 Tailscale IP" };
    private readonly TextBox _port = new() { MinHeight = TouchTarget, Width = 120 };
    private readonly StackPanel _pendingPanel = new() { Spacing = 8 };
    private readonly StackPanel _grantsPanel = new() { Spacing = 8 };
    private readonly StackPanel _catalogPanel = new() { Spacing = 2 };
    private readonly StackPanel _activityPanel = new() { Spacing = 4 };
    private readonly TextBox _deviceName = new() { MinHeight = TouchTarget, PlaceholderText = "手机名称，例如：我的 Pixel" };
    private readonly TextBox _filter = new() { MinHeight = TouchTarget, PlaceholderText = "搜索命令或模块" };
    private readonly CheckBox _allowElevated = new() { Content = "允许该设备请求需要管理员权限的命令（仍需在电脑上逐次确认）" };
    private readonly StackPanel _codeArea = new() { Spacing = 10, IsVisible = false };
    private readonly MptQrCode _qr = new()
    {
        Width = MptQrCode.RecommendedDisplaySize,
        Height = MptQrCode.RecommendedDisplaySize,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _codeContext = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly SelectableTextBlock _code = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas,Menlo,monospace") };
    private readonly Button _primaryGrantButton;
    private string _editingGrantId = "";
    private bool _endpointEdited;
    private bool _attached;

    public RemoteToolGatewayView(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        _vm = new ControlSurfaceViewModel(new MptAvaloniaSurfaceHost(context));
        _primaryGrantButton = Button("创建授权", CreateOrSaveGrantAsync);
        Build();
        // Follow the module's event stream so a new phone request appears without polling, and
        // release it again when the page leaves the visual tree.
        AttachedToVisualTree += async (_, _) =>
        {
            if (_attached) return;
            _attached = true;
            _vm.Attach(_context.SubscribeEvents);
            await GuardAsync(RefreshAsync);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (!_attached) return;
            _attached = false;
            // Detach clears the revealed connection code inside the view model.
            _vm.Detach();
        };
        _vm.PropertyChanged += (_, _) => Render();
    }

    internal ControlSurfaceViewModel ViewModel => _vm;

    private async Task RefreshAsync()
    {
        await _vm.LoadAsync();
        _address.Text = _vm.Address;
        _port.Text = _vm.Port;
        if (!_endpointEdited)
        {
            _address.Text = _vm.Address;
            _port.Text = _vm.Port;
        }

        Render();
    }

    private void Build()
    {
        _address.TextChanged += (_, _) => _endpointEdited = true;
        _port.TextChanged += (_, _) => _endpointEdited = true;

        var listenerCard = Card("监听", [
            _endpoint,
            new TextBlock { Text = "只绑定本机 Tailscale 地址；默认关闭，关闭后不接受任何网络请求。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            Row(_address, _port),
            Row(Button("开启监听 / 应用地址", StartAsync), Button("停止监听", StopAsync))
        ]);

        var pendingCard = Card("待电脑确认", [
            new TextBlock { Text = "手机发起的敏感操作会停在这里；只有你点击某一条的确认按钮，它才会走本机原有的确认与提权链。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            _pendingPanel
        ]);

        var grantCard = Card("设备授权", [
            new TextBlock { Text = "每台手机一份授权：独立访问凭据 + 明确的命令清单 + 是否允许提权。新安装的命令不会自动加入已有授权。", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            _grantsPanel,
            new Separator(),
            _deviceName,
            _allowElevated,
            _filter,
            new ScrollViewer { Content = _catalogPanel, MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            _primaryGrantButton,
            CodeArea()
        ]);

        var activityCard = Card("最近调用与审计", [
            new ScrollViewer { Content = _activityPanel, MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        ]);

        var content = new StackPanel
        {
            Spacing = 16,
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "远程工具访问", FontSize = 24, FontWeight = FontWeight.Bold },
                Row(_status, Button("刷新", RefreshAsync)),
                new TextBlock { Text = "页面跟随工具事件自动更新；手机新发起的操作会立即出现在待确认列表。", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 },
                listenerCard,
                pendingCard,
                grantCard,
                activityCard
            }
        };
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void Render()
    {
        _status.Text = _vm.Status;
        _endpoint.Text = _vm.ListenerRunning ? "正在监听 " + _vm.ListenerEndpoint : "未监听";
        // The code (and its QR symbol) exists only after the user asked to create or show it, and it
        // is dropped again on hide, detach, revocation or when its grant disappears. It is a
        // credential: never put it in an automation name, a tooltip, a log or a window title.
        _codeArea.IsVisible = _vm.HasCreatedCode;
        _qr.Value = _vm.CreatedCode;
        _code.Text = _vm.CreatedCode;
        _codeContext.Text = _vm.HasCreatedCode
            ? "手机扫这个码即可授权给：" + _vm.CreatedCodeContext
            : "";
        _primaryGrantButton.Content = _editingGrantId.Length > 0 ? "保存授权修改" : "创建授权";

        if (_address.Text != _vm.Address && !_address.IsFocused) _address.Text = _vm.Address;
        if (_port.Text != _vm.Port && !_port.IsFocused) _port.Text = _vm.Port;

        _pendingPanel.Children.Clear();
        if (_vm.Pending.Count == 0)
        {
            _pendingPanel.Children.Add(new TextBlock { Text = "没有待确认的操作。", Opacity = 0.7 });
        }
        else
        {
            foreach (var row in _vm.Pending.ToArray()) _pendingPanel.Children.Add(PendingItem(row));
        }

        _grantsPanel.Children.Clear();
        if (_vm.Grants.Count == 0)
        {
            _grantsPanel.Children.Add(new TextBlock { Text = "还没有设备授权。", Opacity = 0.7 });
        }
        else
        {
            foreach (var grant in _vm.Grants.ToArray()) _grantsPanel.Children.Add(GrantItem(grant));
        }

        _catalogPanel.Children.Clear();
        foreach (var command in _vm.VisibleCatalog)
        {
            var check = new CheckBox { Content = command.Display + (command.RequiresElevation ? "（需提权）" : ""), IsChecked = command.Selected, IsEnabled = command.Selectable };
            check.IsCheckedChanged += (_, _) => command.Selected = check.IsChecked == true;
            _catalogPanel.Children.Add(check);
        }

        _activityPanel.Children.Clear();
        foreach (var item in _vm.Activity)
        {
            _activityPanel.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = item.State + " · " + item.Detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap }
                }
            });
        }
    }

    /// <summary>
    /// The revealed connection code: a scannable symbol, which phone it authorizes, and the manual
    /// copy fallback for devices without a camera.
    /// </summary>
    private Control CodeArea()
    {
        _codeArea.Children.Add(new TextBlock
        {
            Text = "连接码（只在本机显示；撤销授权、停止监听或关闭本页后失效）",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        _codeArea.Children.Add(_qr);
        _codeArea.Children.Add(_codeContext);
        _codeArea.Children.Add(_code);
        _codeArea.Children.Add(Row(
            Button("复制连接码", CopyCodeAsync),
            Button("隐藏连接码", () => { _vm.ClearCreatedCode(); Render(); return Task.CompletedTask; })));
        return _codeArea;
    }

    private Control PendingItem(PendingRow row)
    {
        var details = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = row.Summary, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row.Detail, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "命令：" + row.CommandId, Opacity = 0.6, TextWrapping = TextWrapping.Wrap }
            }
        };
        if (row.Claimed)
        {
            // A claimed request is already in the runtime's hands: it can only be reported, never
            // re-approved or rejected from here.
            details.Children.Add(new TextBlock
            {
                Text = "已在本机受理并执行中；手机取消会作用在同一次调用上，结果以运行时返回为准。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            details.Children.Add(new TextBlock
            {
                Text = "尚未受理；点击确认后才会在本机执行。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
            details.Children.Add(Row(
                Button("在本机执行并确认", () => ConfirmAsync(row)),
                Button("拒绝", () => RejectAsync(row))));
        }

        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = details
        };
    }

    private Control GrantItem(GrantRow grant) => new Border
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = grant.DeviceName, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"{grant.CommandCount} 个命令 · {(grant.AllowElevated ? "允许提权" : "不允许提权")} · 活动调用 {grant.ActiveInvocations}",
                    Opacity = 0.75,
                    TextWrapping = TextWrapping.Wrap
                },
                Row(
                    Button("显示连接码", () => ShowCodeAsync(grant)),
                    Button("编辑命令", () => { EditGrant(grant); return Task.CompletedTask; }),
                    Button("撤销授权", () => RevokeAsync(grant)))
            }
        }
    };

    private async Task StartAsync() => await GuardAsync(async () =>
    {
        _vm.Address = _address.Text ?? "";
        _vm.Port = _port.Text ?? "";
        await _vm.StartListenerAsync();
    });

    private async Task StopAsync() => await GuardAsync(async () =>
    {
        await _vm.StopListenerAsync();
    });

    private async Task CreateOrSaveGrantAsync() => await GuardAsync(async () =>
    {
        if (_editingGrantId.Length > 0)
        {
            await _vm.SaveGrantAsync(_editingGrantId, _allowElevated.IsChecked == true);
            _editingGrantId = "";
        }
        else
        {
            _vm.NewDeviceName = _deviceName.Text ?? "";
            _vm.NewAllowElevated = _allowElevated.IsChecked == true;
            await _vm.CreateGrantAsync();
        }
    });

    private void EditGrant(GrantRow grant)
    {
        _editingGrantId = grant.GrantId;
        _deviceName.Text = grant.DeviceName;
        _allowElevated.IsChecked = grant.AllowElevated;
        // The picker shows the commands currently in this grant; saving replaces the list.
        _filter.Text = "";
        _vm.SelectCommands(grant.CommandIds);
        Render();
    }

    private async Task ShowCodeAsync(GrantRow grant) => await GuardAsync(async () =>
    {
        await _vm.ShowCodeAsync(grant.GrantId);
    });

    private async Task RevokeAsync(GrantRow grant) => await GuardAsync(async () =>
    {
        await _vm.RevokeAsync(grant.GrantId);
        if (_editingGrantId == grant.GrantId) _editingGrantId = "";
    });

    private async Task ConfirmAsync(PendingRow row) => await GuardAsync(async () =>
    {
        await _vm.ConfirmAsync(row);
    });

    private async Task RejectAsync(PendingRow row) => await GuardAsync(async () =>
    {
        await _vm.RejectAsync(row);
    });

    private async Task CopyCodeAsync() => await GuardAsync(async () =>
    {
        if (_vm.CreatedCode.Length == 0) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(_vm.CreatedCode);
    });

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _status.Text = MyPowerTools.Abstractions.MptLogRedactor.Redact(ex.Message);
        }
    }

    // ---- small layout helpers ------------------------------------------------------------------

    private static Border Card(string title, IReadOnlyList<Control> children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        foreach (var child in children) panel.Children.Add(child);
        return new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = panel
        };
    }

    private static Control Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, MinHeight = TouchTarget };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("remote-tool-gateway surface action failed: " + ex.Message); }
        };
        return button;
    }
}
