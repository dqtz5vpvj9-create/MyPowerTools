using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private sealed record ConversationEntry(string Key, string Name, string? DeviceId, string Preview,
        DateTimeOffset? UpdatedAt, int Unread);
    private Grid? _navigationLayout;
    private Border? _directory;
    private Button? _chatBack;
    private bool _chatOpen;
    private bool _contactsOpen;
    private readonly StackPanel _conversationList = new() { Spacing = 3, Margin = new Thickness(9, 0) };
    private readonly TextBlock _directoryTitle = new() { Text = "会话", FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBox _conversationSearch = MobileUi.FieldBox("搜索会话");
    private readonly List<string> _sharedFiles = [];
    private readonly List<string> _sharedTexts = [];
    private static string DeviceConversationKey(string id) => "device:" + id;
    private string ItemConversationKey(AssistantItem item) => item.ConversationKey.Length > 0
        ? item.ConversationKey : item.TargetDeviceId is { Length: > 0 } id ? DeviceConversationKey(id) : SharedConversationKey;

    private AssistantSnapshot CurrentConversationSnapshot() => _core.Snapshot with
    {
        Items = _core.Snapshot.Items.Where(i => ItemConversationKey(i) == ActiveConversationKey).ToArray()
    };

    private IEnumerable<ConversationEntry> ConversationEntries()
    {
        var items = _core.Snapshot.Items;
        ConversationEntry Entry(string key, string name, string? id)
        {
            var messages = items.Where(i => ItemConversationKey(i) == key).ToArray();
            var latest = messages.LastOrDefault();
            var draft = key == ActiveConversationKey ? _input.Text ?? "" : ConversationDraftText(key);
            var preview = draft.Length > 0 ? "[草稿] " + draft.Replace('\n', ' ') : latest is null
                ? (id is null ? "已加入设备的共享会话" : "文字、照片和文件")
                : latest.IsText ? latest.Text ?? "" : "[文件] " + latest.DisplayName;
            var readAt = ConversationReadAt(key);
            var unread = messages.Count(i => i.SenderDeviceId != _core.Snapshot.Identity.Id && i.CreatedAt is { } at && (readAt is null || at > readAt));
            return new(key, name, id, preview, latest?.CreatedAt, unread);
        }
        yield return Entry(SharedConversationKey, "文件传输助手", null);
        var peers = RememberedDevices().ToDictionary(p => p.DeviceId);
        var privateKeys = items.Select(ItemConversationKey).Where(k => k.StartsWith("device:", StringComparison.Ordinal))
            .Concat(peers.Keys.Select(DeviceConversationKey))
            .Concat(ConversationDraftKeys.Where(k => k.StartsWith("device:", StringComparison.Ordinal)))
            .Concat(ActiveConversationKey.StartsWith("device:", StringComparison.Ordinal) ? [ActiveConversationKey] : []).Distinct();
        foreach (var entry in privateKeys.Select(key =>
        {
            var id = key[7..];
            var name = peers.TryGetValue(id, out var peer) ? peer.Name
                : items.LastOrDefault(i => ItemConversationKey(i) == key && i.SenderDeviceId == id)?.SenderName ?? ConversationDraftTargetName(key) ?? (key == ActiveConversationKey ? _targetName : id);
            return Entry(key, name, id);
        }).OrderByDescending(e => e.UpdatedAt)) yield return entry;
        foreach (var key in items.Select(ItemConversationKey).Where(k => k != SharedConversationKey && !k.StartsWith("device:", StringComparison.Ordinal)).Distinct())
            yield return Entry(key, key == "history" ? "历史记录" : "以前的共享会话", null);
    }

    private Control BuildConversationNavigation(Grid chat)
    {
        var add = MobileUi.IconButton("MptMobileIconPlus", "添加设备");
        add.Click += (_, _) => ShowPairSheet();
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,44") };
        titleRow.Children.Add(_directoryTitle);
        Grid.SetColumn(add, 1); titleRow.Children.Add(add);
        _conversationSearch.TextChanged += (_, _) => RefreshConversationNavigation();
        var header = new StackPanel { Spacing = 14, Margin = new Thickness(20, 22, 20, 16), Children =
        { new TextBlock { Text = "MY POWER TOOLS", FontSize = 10, Opacity = .45 }, titleRow, _conversationSearch } };
        var chats = MobileUi.TextButton("会话");
        var contacts = MobileUi.TextButton("通讯录");
        var settings = MobileUi.IconButton("MptMobileIconSettings", "传输设置");
        chats.Click += (_, _) => ShowDirectory(false);
        contacts.Click += (_, _) => ShowDirectory(true);
        settings.Click += (_, _) => ShowSetupSheet();
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,44"), Margin = new Thickness(10, 6) };
        tabs.Children.Add(chats); Grid.SetColumn(contacts, 1); tabs.Children.Add(contacts);
        Grid.SetColumn(settings, 2); tabs.Children.Add(settings);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(header);
        var scroll = new ScrollViewer { Content = _conversationList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll); Grid.SetRow(tabs, 2); body.Children.Add(tabs);
        _directory = new Border { Child = body, BorderThickness = new Thickness(0, 0, 1, 0) };
        _directory.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        _directory.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
        _navigationLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        _navigationLayout.Children.Add(_directory); Grid.SetColumn(chat, 1); _navigationLayout.Children.Add(chat);
        return _navigationLayout;
    }

    private void DismissSheet()
    {
        if (_sheetTitle.Text == "分享到会话") { _sharedFiles.Clear(); _sharedTexts.Clear(); }
        CloseSheet();
    }

    private void MarkVisibleConversationRead()
    {
        if (!_attached || (!_chatOpen && _viewport < PhoneWidth)) return;
        var latest = CurrentConversationSnapshot().Items.Where(i => i.SenderDeviceId != _core.Snapshot.Identity.Id)
            .Select(i => i.CreatedAt).DefaultIfEmpty().Max();
        if (latest is { } at && (ConversationReadAt(ActiveConversationKey) is not { } read || read < at))
            _ = MarkConversationReadAsync(ActiveConversationKey, at);
    }

    private void ShowDirectory(bool contacts)
    {
        _contactsOpen = contacts;
        _directoryTitle.Text = contacts ? "通讯录" : "会话";
        _conversationSearch.PlaceholderText = contacts ? "搜索设备" : "搜索会话";
        _conversationSearch.Text = "";
        ReturnToConversationList();
        RefreshConversationNavigation();
    }

    private void ReturnToConversationList()
    {
        _chatOpen = false;
        _ = SaveDraftAsync();
        UpdateNavigationLayout();
        RefreshConversationNavigation();
    }

    private void UpdateNavigationLayout()
    {
        if (_navigationLayout is null || _directory is null || _conversationColumn is null) return;
        var phone = _viewport < PhoneWidth;
        _navigationLayout.ColumnDefinitions = new ColumnDefinitions(phone ? "*" : "300,*");
        Grid.SetColumn(_conversationColumn, phone ? 0 : 1);
        _directory.IsVisible = !phone || !_chatOpen;
        _conversationColumn.IsVisible = !phone || _chatOpen;
        if (_chatBack is not null) _chatBack.IsVisible = phone;
    }

    private void RefreshConversationNavigation()
    {
        if (_directory is null) return;
        _conversationList.Children.Clear();
        var query = (_conversationSearch.Text ?? "").Trim();
        if (_contactsOpen)
        {
            foreach (var peer in RememberedDevices().Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                _conversationList.Children.Add(DirectoryRow(peer.Name, "已添加 · 文件互传", "", false, 0,
                    peer.Platform == "android" ? "MptMobileIconPhone" : "MptMobileIconDesktop", () => { ShowContactCard(peer); return Task.CompletedTask; }));
            if (_conversationList.Children.Count == 0) _conversationList.Children.Add(MobileUi.Note("添加一次，以后从通讯录找到设备。"));
            return;
        }
        foreach (var entry in ConversationEntries().Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)))
            _conversationList.Children.Add(DirectoryRow(entry.Name, entry.Preview, entry.UpdatedAt?.ToLocalTime().ToString("HH:mm") ?? "",
                entry.Key == ActiveConversationKey && (_chatOpen || _viewport >= PhoneWidth), entry.Unread,
                entry.DeviceId is null ? "MptMobileIconDevices" : "MptMobileIconDesktop", () => OpenConversationAsync(entry)));
    }

    private static Button DirectoryRow(string name, string preview, string time, bool selected, int unread, string icon, Func<Task> action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto"), ColumnSpacing = 12 };
        var glyph = MobileUi.Icon(icon, [MobileUi.Classes.Icon]);
        glyph.HorizontalAlignment = HorizontalAlignment.Center; glyph.VerticalAlignment = VerticalAlignment.Center;
        var avatar = new Border { Width = 43, Height = 43, CornerRadius = new CornerRadius(13), Child = glyph };
        avatar.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileAccentSoftBrush"));
        grid.Children.Add(avatar);
        var text = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children =
        { new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
          new TextBlock { Text = preview, FontSize = 12, Opacity = .6, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis } } };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        var detail = new StackPanel { Spacing = 7, Children = { new TextBlock { Text = time, FontSize = 10, Opacity = .5 } } };
        if (unread > 0) detail.Children.Add(new TextBlock { Text = unread.ToString(), FontSize = 11, Foreground = Brushes.DodgerBlue, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(detail, 2); grid.Children.Add(detail);
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Content = grid, Padding = new Thickness(12, 14), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (selected) button.Bind(Button.BackgroundProperty, new DynamicResourceExtension("MptMobileAccentSoftBrush"));
        else button.Background = Brushes.Transparent;
        AutomationProperties.SetName(button, "打开会话 " + name);
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task OpenConversationAsync(ConversationEntry entry)
    {
        await SwitchConversationAsync(entry.Key, entry.DeviceId, entry.Name);
        _chatOpen = true;
        _lastItemCount = -1;
        _lastRenderedCount = -1;
        _messageRows.Clear(); _thread.Children.Clear();
        CloseSheet(); UpdateNavigationLayout(); Sync(); RestoreConversationScroll();
        var lastIncoming = CurrentConversationSnapshot().Items.Where(i => i.SenderDeviceId != _core.Snapshot.Identity.Id).Select(i => i.CreatedAt).DefaultIfEmpty().Max();
        if (lastIncoming is { } at) await MarkConversationReadAsync(entry.Key, at);
        RefreshConversationNavigation();
    }

    private void ShowContactCard(AssistantDevice peer)
    {
        _sheetTitle.Text = "设备名片";
        var send = MobileUi.PrimaryButton("发消息");
        send.Click += async (_, _) =>
        {
            if (peer.RequiresPairing || !peer.CanPrivateMessage) { ShowPairSheet(); return; }
            await RunAsync(() => OpenConversationAsync(new(DeviceConversationKey(peer.DeviceId), peer.Name, peer.DeviceId, "", null, 0)));
        };
        var invite = MobileUi.QuietButton("邀请加入文件传输助手");
        invite.Click += (_, _) =>
        {
            _sheetTitle.Text = "邀请 " + peer.Name;
            var confirm = MobileUi.PrimaryButton("显示共享会话邀请");
            confirm.Click += (_, _) => { ShowSheet(_linkSheet); SyncLinkSheet(); };
            _sheetScroll.Content = MobileUi.Stack(12, MobileUi.Body("加入后，这台设备能查看文件传输助手中的历史和后续文字、文件。"),
                MobileUi.Caption("此操作会显示邀请，对方确认加入后才获得权限。普通文件互传不受影响。"), confirm);
        };
        _sheetScroll.Content = MobileUi.Stack(16, MobileUi.PageTitle(peer.Name), MobileUi.Caption(peer.PlatformText),
            MobileUi.Caption("设备标识 · " + peer.DeviceId), MobileUi.Caption(peer.RequiresPairing || !peer.CanPrivateMessage ? "请先添加设备，取得私聊互传权限。" : "已允许文字和文件互传"), send, invite);
        OpenSheet();
    }

    private void ShowConversationDetails()
    {
        if (_targetDeviceId is { } id)
        {
            var peer = RememberedDevices().FirstOrDefault(p => p.DeviceId == id);
            if (peer is not null) ShowContactCard(peer);
            return;
        }
        _sheetTitle.Text = "共享会话成员";
        var body = MobileUi.Stack(10, MobileUi.Note("只有明确加入的设备可见此会话。普通配对不会加入。"));
        body.Children.Add(MobileUi.Caption(_core.Snapshot.Identity.DisplayName + " · 本机"));
        foreach (var member in _core.Snapshot.Members.Where(m => m.DeviceId != _core.Snapshot.Identity.Id))
            body.Children.Add(DirectoryRow(member.Name, "已加入共享会话", "", false, 0, "MptMobileIconDevices", () => { ShowContactCard(member); return Task.CompletedTask; }));
        var invite = MobileUi.PrimaryButton("邀请设备加入");
        invite.Click += (_, _) => { ShowSheet(_linkSheet); SyncLinkSheet(); };
        body.Children.Add(invite);
        _sheetScroll.Content = body; OpenSheet();
    }

    private void ShowConversationChoiceForShare()
    {
        _sheetTitle.Text = "分享到会话";
        _sheetScroll.Content = _deviceSheetBody;
        RenderConversationChoices(); OpenSheet();
    }

    private void RenderConversationChoices()
    {
        _deviceSheetBody.Children.Clear();
        foreach (var entry in ConversationEntries().Where(e => e.Key == SharedConversationKey || e.DeviceId is not null))
            _deviceSheetBody.Children.Add(DirectoryRow(entry.Name, entry.Preview, "", false, 0, "MptMobileIconDevices", () => ChooseConversationAsync(entry)));
    }

    private async Task ChooseConversationAsync(ConversationEntry entry)
    {
        var forward = _pendingForward;
        await OpenConversationAsync(entry);
        if (forward is not null) { _pendingForward = forward; ShowForwardConfirmation(); return; }
        if (_sharedFiles.Count == 0 && _sharedTexts.Count == 0) return;
        foreach (var path in _sharedFiles) AddAttachment(path);
        foreach (var shared in _sharedTexts) _input.Text = string.IsNullOrEmpty(_input.Text) ? shared : _input.Text + "\n" + shared;
        _sharedFiles.Clear(); _sharedTexts.Clear();
        DraftChanged(); SyncComposer();
        await SaveDraftAsync();
    }
}
