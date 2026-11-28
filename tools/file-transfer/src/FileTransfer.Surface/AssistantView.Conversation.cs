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
    private string? _previewingPairCode;
    private Button? _pairConfirm;

    internal string? SelectedTargetDeviceId => _targetDeviceId;
    internal string SelectedConversationName => _targetName;

    private void ShowAttachmentSheet()
    {
        _sheetTitle.Text = "添加附件";
        _sheetScroll.Content = MobileUi.GroupedListCard(
            MobileUi.ListRow("MptMobileIconFile", "文件", "从设备选择文档、安装包或任意文件", async () => { CloseSheet(); await PickAsync(false); }),
            MobileUi.ListRow("MptMobileIconImage", "照片", "从相册选择图片", async () => { CloseSheet(); await PickAsync(true); }, tone: "green"));
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
        if (_sheetTitle.Text is not ("发给谁" or "分享到会话")) return;
        RenderConversationChoices();
    }

    private void ShowPairSheet()
    {
        _sheetTitle.Text = "添加设备";
        _pairPreview.Text = "";
        _pairPreview.IsVisible = false;
        _previewedPairCode = null;
        _pairConfirm = MobileUi.PrimaryButton("确认添加");
        _pairConfirm.IsEnabled = false;
        _pairConfirm.MinHeight = 44;
        _pairConfirm.Click += async (_, _) => await RunAsync(ConfirmPairAsync);

        var preview = MobileUi.SecondaryButton("预览设备");
        preview.MinHeight = 44;
        preview.Click += async (_, _) => await RunAsync(PreviewPairAsync);

        var scan = MobileUi.SecondaryButton("扫描二维码");
        scan.IsVisible = _context.ScanConnectionCodeAsync is not null;
        scan.Click += async (_, _) => await RunAsync(async () =>
        {
            var code = await _context.ScanConnectionCodeAsync!(_lifetime.Token);
            if (!_sheetOpen || _sheetTitle.Text != "添加设备") return;
            if (string.IsNullOrWhiteSpace(code)) { ShowSheetFeedback("已取消扫码。"); return; }
            _pairCode.Text = code.Trim();
            await PreviewPairAsync();
        });

        var actionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        Grid.SetColumn(preview, 0); actionRow.Children.Add(preview);
        Grid.SetColumn(_pairConfirm, 1); actionRow.Children.Add(_pairConfirm);

        var codeCard = MobileUi.Inset(MobileUi.Stack(10,
            MobileUi.FieldLabel("对方设备的连接码"),
            _pairCode,
            _pairPreview,
            actionRow));

        var export = MobileUi.QuietButton("显示我的文件互传码");
        export.Click += async (_, _) => await RunAsync(async () =>
        {
            export.IsEnabled = false;
            try { await ShowMyPairCodeAsync(); }
            finally { export.IsEnabled = true; }
        });

        _sheetScroll.Content = MobileUi.Stack(12,
            MobileUi.Caption("仅允许文件互传，不共享自己的会话或电脑控制权限。"),
            scan,
            codeCard,
            MobileUi.Divider(),
            export);
        OpenSheet();
    }

    private async Task ShowMyPairCodeAsync()
    {
        ClearSheetFeedback();
        var answer = await _legacy.CallAsync("pairing");
        var code = answer["code"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("暂时无法生成本机文件互传码，请稍后重试。");
        var qr = new MptQrCode { Value = code, Width = 228, Height = 228, HorizontalAlignment = HorizontalAlignment.Center };
        var qrFrame = new Border
        {
            Child = qr,
            Background = Brushes.White,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var copy = MobileUi.SecondaryButton("复制连接码");
        copy.Click += async (_, _) => await RunAsync(async () =>
        {
            await CopyTextAsync(code);
            ShowSheetFeedback("文件互传码已复制，发给对方设备粘贴或扫码即可。");
        });
        _sheetTitle.Text = "我的文件互传码";
        _sheetScroll.Content = MobileUi.Stack(12,
            MobileUi.Caption("仅允许向这台设备发送文件和文字"),
            qrFrame,
            copy);
    }

    private async Task PreviewPairAsync()
    {
        var code = (_pairCode.Text ?? "").Trim();
        if (code.Length == 0)
        {
            _pairPreview.Text = "请先粘贴对方的连接码，或点上方扫描二维码。";
            _pairPreview.IsVisible = true;
            _previewedPairCode = null;
            if (_pairConfirm is not null) _pairConfirm.IsEnabled = false;
            return;
        }
        if (code == _previewingPairCode || code == _previewedPairCode) return;
        _previewingPairCode = code;
        try
        {
            var result = await _legacy.CallAsync("pair.preview", new JsonObject { ["code"] = code });
            if (!_sheetOpen || _sheetTitle.Text != "添加设备" || (_pairCode.Text ?? "").Trim() != code) return;
            _pairPreview.Text = "已识别：" + (result["name"]?.GetValue<string>() ?? result["deviceName"]?.GetValue<string>() ?? "另一台设备") + " · 允许文件互传";
            _pairPreview.IsVisible = true;
            _previewedPairCode = code;
            if (_pairConfirm is not null) _pairConfirm.IsEnabled = true;
            ClearSheetFeedback();
        }
        catch
        {
            _pairPreview.Text = "无法识别此连接码，请检查是否完整，或请对方重新分享。";
            _pairPreview.IsVisible = true;
            _previewedPairCode = null;
            if (_pairConfirm is not null) _pairConfirm.IsEnabled = false;
        }
        finally
        {
            if (_previewingPairCode == code) _previewingPairCode = null;
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
            _pairPreview.IsVisible = false;
            _previewedPairCode = null;
        }
        catch (Exception ex)
        {
            _pairPreview.Text = ex.Message.Length > 0 ? ex.Message : "添加未完成，请重试或请对方重新分享连接码。";
            _pairPreview.IsVisible = true;
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
        var chatWidth = _viewport >= PhoneWidth ? Math.Max(280, _viewport - 300) : _viewport;
        var sideAllowance = _viewport <= 340 ? 28 : 36;
        var available = Math.Min(560, Math.Max(210, Math.Min(chatWidth - sideAllowance, chatWidth * .88)));
        var bubbleMaxWidth = Math.Max(136, available - 48);

        Control body;
        if (item.IsText)
        {
            var textBlock = new TextBlock
            {
                Text = item.Text ?? item.DisplayName,
                FontSize = 15,
                LineHeight = 22,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = Math.Max(104, bubbleMaxWidth - 26)
            };
            textBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(mine ? "MptMobileOnAccentBrush" : "MptMobileTextBrush"));
            body = textBlock;
        }
        else
        {
            var captionText = item.SizeText.Length > 0
                ? item.SizeText + (item.NeedsDownload ? " · 点按下载" : " · 点按打开")
                : (item.NeedsDownload ? "点按下载" : "点按打开");
            var nameBlock = new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                MaxLines = 2,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
            var detailBlock = MobileUi.Caption(captionText);
            detailBlock.FontSize = 12;
            detailBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            var fileText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { nameBlock, detailBlock } };

            if (item.State is AssistantItemState.Sending or AssistantItemState.Downloading)
            {
                var progressBar = MobileUi.With(new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = item.Progress,
                    IsIndeterminate = item.Size <= 0,
                    Height = 4,
                    MinHeight = 4,
                    Margin = new Thickness(0, 4, 0, 0)
                }, MobileUi.Classes.Progress);
                fileText.Children.Add(progressBar);
            }

            var fileHeader = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                ColumnSpacing = 10,
                MaxWidth = Math.Max(120, bubbleMaxWidth - 22)
            };
            var badge = BuildFileExtensionBadge(item.DisplayName, item.IsImage);
            fileHeader.Children.Add(badge);
            Grid.SetColumn(fileText, 1);
            fileHeader.Children.Add(fileText);

            Control fileBody = fileHeader;
            if (item.IsImage && item.LocalPath is { Length: > 0 } previewPath &&
                !previewPath.StartsWith("content://", StringComparison.OrdinalIgnoreCase) &&
                PreviewImage(previewPath, 150) is { } image)
            {
                image.MaxWidth = Math.Max(120, bubbleMaxWidth - 22);
                var clippedImage = new Border
                {
                    Child = image,
                    CornerRadius = new CornerRadius(10),
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                fileBody = new StackPanel { Spacing = 6, MaxWidth = Math.Max(120, bubbleMaxWidth - 22), Children = { clippedImage, fileHeader } };
            }

            var file = new Button
            {
                Content = fileBody,
                Padding = new Thickness(0),
                MinHeight = 44,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MaxWidth = Math.Max(120, bubbleMaxWidth - 22)
            };
            AutomationProperties.SetName(file, "打开 " + item.DisplayName);
            file.Click += async (_, _) => await RunAsync(() => OpenItemAsync(item));
            body = file;
        }

        var bubble = new Border
        {
            Child = body,
            Padding = item.IsText ? new Thickness(13, 8) : new Thickness(11, 8),
            CornerRadius = mine ? new CornerRadius(18, 18, 5, 18) : new CornerRadius(18, 18, 18, 5),
            MaxWidth = bubbleMaxWidth,
            MinWidth = item.IsText ? 52 : Math.Min(210, bubbleMaxWidth),
            BorderThickness = mine && item.IsText ? new Thickness(0) : new Thickness(1)
        };
        if (mine && item.IsText)
        {
            bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
        }
        else
        {
            bubble.Bind(Border.BackgroundProperty, new DynamicResourceExtension(mine ? "MptMobileAccentSoftBrush" : "MptMobileCardBrush"));
            bubble.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("MptMobileCardBorderBrush"));
        }

        var moreIcon = MobileUi.Icon("MptMobileIconDots", MobileUi.Classes.Icon, MobileUi.Classes.IconMuted);
        moreIcon.Opacity = 0.7;
        var more = new Button
        {
            Content = moreIcon,
            Width = 44,
            Height = 44,
            MinWidth = 44,
            MinHeight = 44,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(22),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(more, "消息操作 " + item.DisplayName);
        ToolTip.SetTip(more, "更多操作");
        more.Click += (_, _) => ShowMessageMenu(item);

        var bubbleLine = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(mine ? "44,Auto" : "Auto,44"),
            ColumnSpacing = 2,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        if (mine)
        {
            Grid.SetColumn(more, 0); bubbleLine.Children.Add(more);
            Grid.SetColumn(bubble, 1); bubbleLine.Children.Add(bubble);
        }
        else
        {
            Grid.SetColumn(bubble, 0); bubbleLine.Children.Add(bubble);
            Grid.SetColumn(more, 1); bubbleLine.Children.Add(more);
        }

        var status = item.TransferStateText;
        var metaText = new TextBlock
        {
            Text = mine ? status : item.SenderName + " · " + status,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(110, available - (item.CreatedAt is not null ? 90 : 52)),
            VerticalAlignment = VerticalAlignment.Center
        };
        metaText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(
            item.State == AssistantItemState.Failed ? "MptMobileWarningBrush" : "MptMobileMutedTextBrush"));

        var metaRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = mine ? new Thickness(46, 2, 4, 0) : new Thickness(4, 2, 46, 0)
        };
        metaRow.Children.Add(metaText);
        if (item.CreatedAt is { } createdAt)
        {
            var timeBlock = new TextBlock
            {
                Text = createdAt.ToLocalTime().ToString("HH:mm"),
                FontSize = 10,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center
            };
            timeBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileMutedTextBrush"));
            metaRow.Children.Add(timeBlock);
        }

        return new StackPanel
        {
            Spacing = 0,
            MaxWidth = available,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Children = { bubbleLine, metaRow }
        };
    }

    private static Border BuildFileExtensionBadge(string fileName, bool isImage)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        var (label, bgKey, fgKey) = ext switch
        {
            "APK" => ("APK", "MptMobileSuccessSoftBrush", "MptMobileSuccessBrush"),
            "PDF" => ("PDF", "MptMobileOrangeSoftBrush", "MptMobileOrangeBrush"),
            "ZIP" or "7Z" or "RAR" or "TAR" or "GZ" => (ext.Length <= 3 ? ext : "ZIP", "MptMobileFileThumbArchiveBackgroundBrush", "MptMobileFileThumbArchiveTextBrush"),
            "PNG" or "JPG" or "JPEG" or "WEBP" or "GIF" or "BMP" or "HEIC" => ("IMG", "MptMobileFileThumbPhotoBackgroundBrush", "MptMobileFileThumbPhotoTextBrush"),
            "MP4" or "MOV" or "MKV" or "AVI" or "MP3" or "WAV" or "FLAC" => (ext, "MptMobilePurpleSoftBrush", "MptMobilePurpleBrush"),
            "DOC" or "DOCX" or "XLS" or "XLSX" or "PPT" or "PPTX" or "TXT" or "MD" or "JSON" => (ext.Length <= 4 ? ext : ext[..3], "MptMobileAccentSoftBrush", "MptMobileAccentBrush"),
            _ when isImage => ("IMG", "MptMobileFileThumbPhotoBackgroundBrush", "MptMobileFileThumbPhotoTextBrush"),
            _ => (ext.Length is > 0 and <= 4 ? ext : "FILE", "MptMobileFileThumbBackgroundBrush", "MptMobileFileThumbTextBrush")
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(fgKey));
        var badge = new Border
        {
            Width = 36,
            Height = 38,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = text
        };
        badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension(bgKey));
        return badge;
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
        // Check only when the menu is opened; a deleted received copy can be fetched again.
        var isExternalUri = item.LocalPath is { Length: > 0 } lp && lp.StartsWith("content://", StringComparison.OrdinalIgnoreCase);
        var missingReceivedFile = item.IsIncoming && item.IsFile && item.State == AssistantItemState.Available && !isExternalUri && !File.Exists(item.LocalPath);
        if (item.CanRetry || missingReceivedFile)
            Add(missingReceivedFile ? "重新下载" : item.IsIncoming ? "重试接收" : "重试", async () => { CloseSheet(); await _core.RetryAsync(item.Id); });
        if (item.CanCancel) Add(item.IsIncoming ? "取消接收" : "取消发送", async () => { CloseSheet(); await _core.CancelAsync(item.Id); });
        Add("查看详情", () =>
        {
            _sheetTitle.Text = "消息详情";
            var rows = new StackPanel { Spacing = 8 };
            rows.Children.Add(MobileUi.ReceiptRow("状态", item.TransferStateText));
            if (item.SizeText.Length > 0) rows.Children.Add(MobileUi.ReceiptRow("大小", item.SizeText));
            if (item.CreatedAt is { } created) rows.Children.Add(MobileUi.ReceiptRow("时间", created.ToLocalTime().ToString("g")));
            rows.Children.Add(MobileUi.ReceiptRow("接收回执", item.ReceiptText.Length > 0 ? "已保存到 " + item.ReceiptText : "尚无其他设备接收回执"));
            var detailStack = MobileUi.Stack(10,
                MobileUi.CardTitle(item.DisplayName),
                MobileUi.ReceiptSurface(rows));
            if (item.ErrorExplanation.Length > 0)
                detailStack.Children.Add(MobileUi.With(MobileUi.Notice(MobileUi.Caption(item.ErrorExplanation)), "warning"));
            _sheetScroll.Content = detailStack;
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
        var fileName = item.Name ?? Path.GetFileName(opened.Path!);
        var extension = Path.GetExtension(fileName);
        var target = await TopLevel.GetTopLevel(this)!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存副本",
            SuggestedFileName = fileName,
            FileTypeChoices = extension.Length == 0 ? null :
                [new FilePickerFileType(extension.TrimStart('.').ToUpperInvariant() + " 文件") { Patterns = ["*" + extension] }]
        });
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
