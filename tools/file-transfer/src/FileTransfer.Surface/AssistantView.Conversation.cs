using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.AvaloniaSdk.Controls;

namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private readonly Dictionary<string, Bitmap> _imagePreviews = [];
    private string? _targetDeviceId;
    private string _targetName = "文件传输助手";
    private int _lastRenderedCount = -1;
    private readonly Button _newMessages = new() { Content = "新消息 ↓", IsVisible = false, MinHeight = 44 };
    private readonly Dictionary<string, (string Version, Control Row)> _messageRows = [];
    private readonly TextBox _pairCode = MobileUi.FieldBox("粘贴对方的连接码");
    private readonly TextBlock _pairPreview = MobileUi.Caption("");
    private string? _previewedPairCode;
    private Button? _pairConfirm;

    internal string? SelectedTargetDeviceId => _targetDeviceId;
    internal string SelectedConversationName => _targetName;

    private void ShowAttachmentSheet()
    {
        _sheetTitle.Text = "添加附件";
        _sheetScroll.Content = MobileUi.Stack(0,
            MobileUi.ListRow("MptMobileIconClipboard", "文件", "从设备选择文件", async () => { CloseSheet(); await PickAsync(false); }),
            MobileUi.ListRow("MptMobileIconImage", "照片", "选择图片", async () => { CloseSheet(); await PickAsync(true); }));
        OpenSheet();
    }

    private IEnumerable<AssistantDevice> RememberedDevices()
    {
        // The local inspect cache is authoritative for pairing. Discovery may be slow or unavailable.
        var cache = _legacy.Snapshot.Peers.Select(p => new AssistantDevice(p.DeviceId, p.Name, p.Address, "", true, p.IsOnline));
        return cache.Concat(_core.Snapshot.Devices.Where(p => p.Paired))
            .DistinctBy(p => p.DeviceId);
    }

    private void RenderTargets(AssistantSnapshot state)
    {
        if (_sheetTitle.Text != "发给谁") return;
        RenderConversationChoices();
    }

    private void ShowPairSheet()
    {
        _sheetTitle.Text = "添加设备";
        _pairConfirm = MobileUi.PrimaryButton("确认添加");
        _pairConfirm.IsEnabled = false;

        _pairConfirm.Click += async (_, _) => await RunAsync(ConfirmPairAsync);
        var preview = MobileUi.SecondaryButton("预览设备");
        preview.Click += async (_, _) => await RunAsync(PreviewPairAsync);
        var scan = MobileUi.SecondaryButton("扫描二维码") ;
        scan.IsVisible = _context.ScanConnectionCodeAsync is not null;
        scan.Click += async (_, _) => await RunAsync(async () =>
        {
            var code = await _context.ScanConnectionCodeAsync!(_lifetime.Token);
            if (!_sheetOpen || _sheetTitle.Text != "添加设备" || string.IsNullOrWhiteSpace(code)) return;
            _pairCode.Text = code;
            await PreviewPairAsync();
        });
        var export = MobileUi.QuietButton("显示我的文件互传码");
        export.Click += async (_, _) => await RunAsync(ShowMyPairCodeAsync);
        _sheetScroll.Content = MobileUi.Stack(8, MobileUi.Caption("仅允许文件互传，不共享自己的会话或电脑控制权限。"), scan,
            _pairCode, _pairPreview, preview, _pairConfirm, export);
        OpenSheet();
    }

    private async Task ShowMyPairCodeAsync()
    {
        var answer = await _legacy.CallAsync("pairing");
        var code = answer["code"]?.GetValue<string>() ?? "";
        var qr = new MptQrCode { Value = code, Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center };
        var copy = MobileUi.SecondaryButton("复制连接码");
        copy.Click += async (_, _) => await RunAsync(() => CopyTextAsync(code));
        _sheetTitle.Text = "我的文件互传码";
        _sheetScroll.Content = MobileUi.Stack(8, MobileUi.Caption("仅允许向这台设备发送文件和文字"), qr, copy);
    }

    private async Task PreviewPairAsync()
    {
        var code = (_pairCode.Text ?? "").Trim();
        try
        {
            var result = await _legacy.CallAsync("pair.preview", new JsonObject { ["code"] = code });
            if (!_sheetOpen || _sheetTitle.Text != "添加设备" || (_pairCode.Text ?? "").Trim() != code) return;
            _pairPreview.Text = (result["name"]?.GetValue<string>() ?? result["deviceName"]?.GetValue<string>() ?? "另一台设备") + " · 允许文件互传";
            _previewedPairCode = code;
            _pairConfirm!.IsEnabled = true;
        }
        catch
        {
            _pairPreview.Text = "无法识别此连接码，请检查是否完整，或请对方重新分享。";
            _previewedPairCode = null;
            _pairConfirm!.IsEnabled = false;
        }
    }

    private async Task ConfirmPairAsync()
    {
        var code = (_pairCode.Text ?? "").Trim();
        if (code != _previewedPairCode) return;
        if (_pairConfirm is not null) _pairConfirm.IsEnabled = false;
        try
        {
            var result = await _legacy.CallAsync("pair.import", new JsonObject { ["code"] = code });
            await _legacy.RefreshAsync();
            var id = result["deviceId"]?.GetValue<string>();
            var peer = _legacy.Snapshot.Peers.FirstOrDefault(p => p.DeviceId == id);
            if (peer is not null) ShowContactCard(new AssistantDevice(peer.DeviceId, peer.Name, peer.Address, "", true, peer.IsOnline));
            else ShowDeviceSheet();
            _pairCode.Text = "";
            _pairPreview.Text = "";
            _previewedPairCode = null;
        }
        catch
        {
            _pairPreview.Text = "添加未完成，请重试或请对方重新分享连接码。";
            if (_pairConfirm is not null) _pairConfirm.IsEnabled = code == _previewedPairCode;
        }
    }

    private void RenderCompactThread(AssistantSnapshot state)
    {
        var ids = state.Items.Select(i => i.Id).ToHashSet();
        foreach (var obsolete in _messageRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _thread.Children.Remove(_messageRows[obsolete].Row); _messageRows.Remove(obsolete); }
        for (var index = 0; index < state.Items.Count; index++)
        {
            var item = state.Items[index];
            var version = _viewport.ToString(System.Globalization.CultureInfo.InvariantCulture) + JsonSerializer.Serialize(item);
            if (!_messageRows.TryGetValue(item.Id, out var current) || current.Version != version)
            {
                var row = CompactRow(item, state);
                if (current.Row is not null) _thread.Children.Remove(current.Row);
                _messageRows[item.Id] = (version, row);
                _thread.Children.Insert(Math.Min(index, _thread.Children.Count), row);
            }
        }
    }

    private Control CompactRow(AssistantItem item, AssistantSnapshot state)
    {
        var mine = item.SenderDeviceId == state.Identity.Id;
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44") };
        Control body;
        if (item.IsText)
            body = new TextBlock { Text = item.Text ?? item.DisplayName, FontSize = 15, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var fileText = new StackPanel { Spacing = 3, Children =
            {
                new TextBlock { Text = item.DisplayName, FontSize = 14, FontWeight = FontWeight.SemiBold, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis },
                MobileUi.Caption(item.SizeText + (item.NeedsDownload ? " · 点按下载" : " · 点按打开"))
            }};
            Control fileBody = fileText;
            if (item.IsImage && PreviewImage(item.LocalPath, 160) is { } image)
                fileBody = new StackPanel { Spacing = 6, Children = { image, fileText } };
            var file = new Button { Content = fileBody, Padding = new Thickness(0), MinHeight = 44, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(file, "打开 " + item.DisplayName);
            file.Click += async (_, _) => await RunAsync(() => OpenItemAsync(item));
            body = file;
        }
        var chatWidth = _viewport >= PhoneWidth ? Math.Max(280, _viewport - 300) : _viewport;
        var available = Math.Min(560, Math.Max(220, chatWidth * .82));
        body.MaxWidth = available - 64;
        var bubbleWidth = Math.Min(available, 290);
        content.Children.Add(body);
        var more = new Button { Content = MobileUi.Icon("MptMobileIconDots", [MobileUi.Classes.Icon]), Width = 44, MinHeight = 44, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(more, "消息操作 " + item.DisplayName);
        more.Click += (_, _) => ShowMessageMenu(item);
        Grid.SetColumn(more, 1);
        content.Children.Add(more);
        var bubble = new Border { Child = content, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(14) };
        bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension(mine ? "MptMobileAccentSoftBrush" : "MptMobileCardBrush"));
        var status = item.State == AssistantItemState.Queued && mine && item.TargetDeviceId is null ? "已保存到本机" : item.TransferStateText;
        var meta = MobileUi.Caption(mine ? status : item.SenderName + " · " + status);
        meta.FontSize = 12;
        meta.Margin = new Thickness(4, 2, 4, 0);
        meta.TextAlignment = mine ? TextAlignment.Right : TextAlignment.Left;
        var row = new StackPanel { Spacing = 0, Width = bubbleWidth, MaxWidth = available,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left, Children = { bubble, meta } };
        return row;
    }

    private void ShowMessageMenu(AssistantItem item)
    {
        _sheetTitle.Text = "消息操作";
        var actions = new StackPanel { Spacing = 2 };
        void Add(string title, Func<Task> action)
        {
            var button = MobileUi.QuietButton(title);
            button.Click += async (_, _) => await RunAsync(action);
            actions.Children.Add(button);
        }
        if (item.IsText) Add("复制文字", async () => { await CopyTextAsync(item.Text ?? ""); CloseSheet(); });
        Add("转发", () => { _pendingForward = item; ShowDeviceSheet(); return Task.CompletedTask; });
        if (item.IsFile) Add("保存副本", () => SaveCopyAsync(item));
        if (item.CanRetry) Add("重试", async () => { CloseSheet(); await _core.RetryAsync(item.Id); });
        if (item.CanCancel) Add("取消发送", async () => { CloseSheet(); await _core.CancelAsync(item.Id); });
        Add("查看详情", () =>
        {
            _sheetTitle.Text = "消息详情";
            _sheetScroll.Content = MobileUi.Stack(8, MobileUi.Body(item.DisplayName), MobileUi.Caption(item.CreatedAt?.ToLocalTime().ToString("g") ?? ""),
                MobileUi.Caption(item.TransferStateText), MobileUi.Caption(item.ReceiptText.Length > 0 ? "已保存到 " + item.ReceiptText : "尚无其他设备接收回执"),
                MobileUi.Caption(item.ErrorExplanation));
            return Task.CompletedTask;
        });
        _sheetScroll.Content = actions;
        OpenSheet();
    }

    private void ShowForwardConfirmation()
    {
        var send = MobileUi.PrimaryButton("确认转发");
        send.Click += async (_, _) => await RunAsync(() => SendAsync(_targetDeviceId));
        _sheetTitle.Text = "转发给 " + _targetName;
        _sheetScroll.Content = MobileUi.Stack(8, MobileUi.Body(_pendingForward!.DisplayName), send);
        OpenSheet();
    }

    private async Task SaveCopyAsync(AssistantItem item)
    {
        var opened = await _core.OpenAsync(item.Id);
        if (!opened.HasPath) return;
        var target = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "保存副本", SuggestedFileName = item.Name });
        if (target is null) return;
        await using var source = File.OpenRead(opened.Path!);
        await using var destination = await target.OpenWriteAsync();
        await source.CopyToAsync(destination, _lifetime.Token);
        CloseSheet();
    }

    private Image? PreviewImage(string? path, double height)
    {
        if (path is null || !File.Exists(path)) return null;
        if (!_imagePreviews.TryGetValue(path, out var bitmap))
        {
            try
            {
                using var stream = File.OpenRead(path);
                bitmap = Bitmap.DecodeToWidth(stream, 320);
                _imagePreviews[path] = bitmap;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or NullReferenceException) { return null; }
        }
        return new Image { Source = bitmap, Height = height, MaxWidth = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private static string FriendlyStatus(string status)
    {
        if (status.Contains("http", StringComparison.OrdinalIgnoreCase) || status.Contains("Connection", StringComparison.OrdinalIgnoreCase))
            return "操作未完成，请重试。连接详情可在诊断中查看。";
        return status;
    }
}
