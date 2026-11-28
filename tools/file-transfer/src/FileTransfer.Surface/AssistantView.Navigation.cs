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
    private Button? _chatsTab;
    private Button? _contactsTab;
    private bool _chatOpen;
    private bool _contactsOpen;
    private string _navigationRowsFingerprint = "";
    private readonly StackPanel _conversationList = new() { Spacing = 4, Margin = new Thickness(12, 4, 12, 12) };
    private readonly TextBlock _directoryTitle = new() { Text = "会话", FontSize = 26, LineHeight = 36, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
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
                ? (id is null ? (_core.Snapshot.Identity.PublicRoomState == "public" ? "本服务所有已授权用户可见" : _core.Snapshot.Identity.PublicRoomState.StartsWith("public") ? "正在加入公屏 · 无需连接码" : "私人共享会话 · 可在设置中加入公屏") : "文字、照片和文件")
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
        var add = MobileUi.With(new Button
        {
            Content = MobileUi.Icon("MptMobileIconPlus", MobileUi.Classes.Icon, MobileUi.Classes.IconAccent),
            MinWidth = MobileUi.IconTarget,
            MinHeight = MobileUi.IconTarget
        }, MobileUi.Classes.IconButton, "filled");
        AutomationProperties.SetName(add, "添加设备");
        ToolTip.SetTip(add, "添加设备");
        add.Click += (_, _) => ShowPairSheet();

        var brand = new TextBlock { Text = "MY POWER TOOLS", FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 1.4 };
        brand.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileMutedTextBrush"));
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        titleRow.Children.Add(_directoryTitle);
        Grid.SetColumn(add, 1); titleRow.Children.Add(add);

        _conversationSearch.TextChanged += (_, _) => RefreshConversationNavigation();
        var searchPill = MobileUi.SearchField(_conversationSearch);

        var header = new StackPanel { Spacing = 10, Margin = new Thickness(18, 16, 18, 12), Children =
        { brand, titleRow, searchPill } };

        _chatsTab = BuildDirectoryTabButton("会话");
        _contactsTab = BuildDirectoryTabButton("通讯录");
        var settings = MobileUi.With(new Button
        {
            Content = MobileUi.Icon("MptMobileIconSettings", MobileUi.Classes.Icon, MobileUi.Classes.IconSecondary),
            MinWidth = MobileUi.IconTarget,
            MinHeight = MobileUi.IconTarget
        }, MobileUi.Classes.IconButton);
        AutomationProperties.SetName(settings, "传输设置");
        ToolTip.SetTip(settings, "传输设置");
        _chatsTab.Click += (_, _) => ShowDirectory(false);
        _contactsTab.Click += (_, _) => ShowDirectory(true);
        settings.Click += (_, _) => ShowSetupSheet();

        var tabsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), ColumnSpacing = 8 };
        tabsGrid.Children.Add(_chatsTab);
        Grid.SetColumn(_contactsTab, 1); tabsGrid.Children.Add(_contactsTab);
        Grid.SetColumn(settings, 2); tabsGrid.Children.Add(settings);
        var tabBar = MobileUi.With(new Border { Child = tabsGrid, Padding = new Thickness(12, 8) }, MobileUi.Classes.TabBar);
        UpdateDirectoryTabs();

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        body.Children.Add(header);
        var scroll = new ScrollViewer { Content = _conversationList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); body.Children.Add(scroll);
        Grid.SetRow(tabBar, 2); body.Children.Add(tabBar);
        _directory = new Border { Child = body, BorderThickness = new Thickness(0, 0, 1, 0) };
        _directory.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        _directory.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
        _navigationLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        _navigationLayout.Children.Add(_directory); Grid.SetColumn(chat, 1); _navigationLayout.Children.Add(chat);
        return _navigationLayout;
    }

    private static Button BuildDirectoryTabButton(string label) => new()
    {
        Content = label,
        MinHeight = MobileUi.IconTarget,
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(0),
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(12, 8)
    };

    private void UpdateDirectoryTabs()
    {
        static void Apply(Button? tab, bool active)
        {
            if (tab is null) return;
            if (active)
            {
                tab.Bind(Button.BackgroundProperty, new DynamicResourceExtension("MptMobileAccentSoftBrush"));
                tab.Bind(Button.ForegroundProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
            }
            else
            {
                tab.Background = Brushes.Transparent;
                tab.Bind(Button.ForegroundProperty, new DynamicResourceExtension("MptMobileInactiveTabBrush"));
            }
        }
        Apply(_chatsTab, !_contactsOpen);
        Apply(_contactsTab, _contactsOpen);
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
        UpdateDirectoryTabs();
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

    /// <summary>
    /// Rebuilds the directory rows only when something they display really changed. A sync, an event or
    /// a draft save that leaves the list identical must not replace its controls: rebuilding them
    /// invalidates the layout of the page that is on screen, which is pure work for no visible change.
    /// </summary>
    private void RefreshConversationNavigation()
    {
        if (_directory is null) return;
        var query = (_conversationSearch.Text ?? "").Trim();
        if (_contactsOpen)
        {
            var peers = RememberedDevices().Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            var contactsFingerprint = DirectoryFingerprint(query, peers.Select(peer =>
                $"{peer.DeviceId}\u0003{peer.Name}\u0003{peer.Platform}\u0003{peer.Address}\u0003{peer.Paired}\u0003{peer.Available}\u0003{peer.CanPrivateMessage}"));
            if (contactsFingerprint == _navigationRowsFingerprint) return;
            _navigationRowsFingerprint = contactsFingerprint;
            _conversationList.Children.Clear();
            foreach (var peer in peers)
                _conversationList.Children.Add(DirectoryRow(peer.Name, peer.Available ? "在线 · 文件互传" : "已添加 · 文件互传", "", false, 0,
                    peer.Platform == "android" ? "MptMobileIconPhone" : "MptMobileIconDesktop",
                    () => { ShowContactCard(peer); return Task.CompletedTask; }, tone: "green"));
            if (peers.Length == 0)
            {
                var emptyBadge = MobileUi.IconBadge("MptMobileIconDevices");
                emptyBadge.Width = 52;
                emptyBadge.Height = 52;
                emptyBadge.CornerRadius = new CornerRadius(16);
                emptyBadge.HorizontalAlignment = HorizontalAlignment.Center;
                var emptyTitle = new TextBlock
                {
                    Text = query.Length > 0 ? "没有匹配的设备" : "还没有添加设备",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                };
                var emptyDesc = MobileUi.Caption(query.Length > 0 ? "换个关键词试试，或直接添加新设备。" : "添加一次，以后从通讯录找到设备。");
                emptyDesc.TextAlignment = TextAlignment.Center;
                emptyDesc.HorizontalAlignment = HorizontalAlignment.Center;
                var addBtn = MobileUi.SecondaryButton("添加设备");
                addBtn.HorizontalAlignment = HorizontalAlignment.Center;
                addBtn.MinWidth = 140;
                addBtn.Padding = new Thickness(20, 10);
                addBtn.Click += (_, _) => ShowPairSheet();
                var emptyCard = MobileUi.Card(MobileUi.Stack(10, emptyBadge, emptyTitle, emptyDesc, addBtn));
                emptyCard.Margin = new Thickness(4, 16, 4, 0);
                emptyCard.Padding = new Thickness(20, 24);
                _conversationList.Children.Add(emptyCard);
            }
            return;
        }

        // One pass over the conversations: the same rows feed the fingerprint and the rebuilt list.
        var entries = new List<ConversationEntry>();
        var lines = new List<string>();
        foreach (var entry in ConversationEntries().Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || e.Preview.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(entry);
            var selected = entry.Key == ActiveConversationKey && (_chatOpen || _viewport >= PhoneWidth);
            lines.Add($"{entry.Key}\u0003{entry.Name}\u0003{entry.Preview}\u0003{entry.UpdatedAt?.ToLocalTime().ToString("HH:mm") ?? ""}\u0003{entry.Unread}\u0003{(selected ? '1' : '0')}");
        }

        var fingerprint = DirectoryFingerprint(query, lines);
        if (fingerprint == _navigationRowsFingerprint) return;
        _navigationRowsFingerprint = fingerprint;
        _conversationList.Children.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (i > 0)
            {
                var sep = MobileUi.Divider();
                sep.Margin = new Thickness(70, 0, 12, 0);
                _conversationList.Children.Add(sep);
            }
            var isArchive = entry.Key != SharedConversationKey && entry.DeviceId is null;
            var iconKey = isArchive ? "MptMobileIconClock"
                : entry.DeviceId is null ? "MptMobileIconDevices"
                : "MptMobileIconDesktop";
            var tone = isArchive ? "neutral" : entry.DeviceId is null ? null : "green";
            _conversationList.Children.Add(DirectoryRow(entry.Name, entry.Preview, entry.UpdatedAt?.ToLocalTime().ToString("HH:mm") ?? "",
                entry.Key == ActiveConversationKey && (_chatOpen || _viewport >= PhoneWidth), entry.Unread,
                iconKey, () => OpenConversationAsync(entry), tone));
        }
    }

    /// <summary>What the directory rows display. Anything left out here would leave a stale row behind.</summary>
    private string DirectoryFingerprint(string query, IEnumerable<string> rows) =>
        (_contactsOpen ? "contacts" : "list") + (_chatOpen ? "/open" : "/closed") +
        (_viewport >= PhoneWidth ? "/wide" : "/narrow") + "\u0001" + query + "\u0002" + string.Join("\u0004", rows);

    private static Button DirectoryRow(string name, string preview, string time, bool selected, int unread, string icon, Func<Task> action, string? tone = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Background = Brushes.Transparent };
        var avatar = MobileUi.IconBadge(icon, tone);
        avatar.Width = 46;
        avatar.Height = 46;
        avatar.CornerRadius = new CornerRadius(14);
        grid.Children.Add(avatar);

        var nameBlock = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        nameBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
        var previewBlock = new TextBlock { Text = preview, FontSize = 13, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
        previewBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileSecondaryTextBrush"));
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Children = { nameBlock, previewBlock } };
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var detail = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false };
        if (time.Length > 0)
        {
            var timeBlock = new TextBlock { Text = time, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right };
            timeBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileMutedTextBrush"));
            detail.Children.Add(timeBlock);
        }
        if (unread > 0)
        {
            var badgeText = new TextBlock
            {
                Text = unread > 99 ? "99+" : unread.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileOnAccentBrush"));
            var badge = new Border
            {
                Child = badgeText,
                MinWidth = 20,
                Height = 20,
                Padding = new Thickness(6, 0),
                CornerRadius = new CornerRadius(10),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
            detail.Children.Add(badge);
        }
        Grid.SetColumn(detail, 2); grid.Children.Add(detail);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = grid,
            Padding = new Thickness(12, 12),
            MinHeight = 68,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(14),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center
        };
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
        _newMessages.IsVisible = false;
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

        var avatar = MobileUi.IconBadge(peer.Platform == "android" ? "MptMobileIconPhone" : "MptMobileIconDesktop", "green");
        avatar.Width = 52;
        avatar.Height = 52;
        avatar.CornerRadius = new CornerRadius(16);
        var heroCopy = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = peer.Name, FontSize = 18, LineHeight = 26, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                MobileUi.Caption(peer.PlatformText)
            }
        };
        var statusPill = MobileUi.Pill(peer.Available ? "在线" : peer.Paired ? "已添加" : "未添加", offline: !peer.Available);
        statusPill.VerticalAlignment = VerticalAlignment.Center;
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        heroGrid.Children.Add(avatar);
        Grid.SetColumn(heroCopy, 1); heroGrid.Children.Add(heroCopy);
        Grid.SetColumn(statusPill, 2); heroGrid.Children.Add(statusPill);

        var infoCard = MobileUi.ReceiptSurface(MobileUi.Stack(8,
            MobileUi.ReceiptRow("状态", peer.StateText),
            MobileUi.ReceiptRow("权限", peer.RequiresPairing || !peer.CanPrivateMessage ? "需先添加设备" : "允许文字和文件互传"),
            MobileUi.Caption("设备标识 · " + peer.DeviceId)));

        _sheetScroll.Content = MobileUi.Stack(12, MobileUi.Card(heroGrid), infoCard, send, invite);
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
        if (_core.Snapshot.Identity.PublicRoomState.StartsWith("public")) { ShowPublicRoomSheet(); return; }
        _sheetTitle.Text = "共享会话成员";
        var membersPanel = new StackPanel { Spacing = 0 };
        var selfRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        var selfAvatar = MobileUi.IconBadge("MptMobileIconDevices");
        selfAvatar.Width = 42; selfAvatar.Height = 42; selfAvatar.CornerRadius = new CornerRadius(12);
        selfRow.Children.Add(selfAvatar);
        var selfName = new TextBlock
        {
            Text = CleanDeviceLabel(_core.Snapshot.Identity.DisplayName),
            FontSize = 15,
            LineHeight = 22,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Padding = new Thickness(0, 0, 4, 0)
        };
        selfName.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
        var selfCopy = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                selfName,
                MobileUi.Caption("当前设备")
            }
        };
        Grid.SetColumn(selfCopy, 1); selfRow.Children.Add(selfCopy);
        var selfPill = MobileUi.Pill("本机");
        selfPill.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(selfPill, 2); selfRow.Children.Add(selfPill);
        membersPanel.Children.Add(new Border { Child = selfRow, Padding = new Thickness(14, 12) });

        foreach (var member in _core.Snapshot.Members.Where(m => m.DeviceId != _core.Snapshot.Identity.Id))
        {
            var sep = MobileUi.Divider();
            sep.Margin = new Thickness(68, 0, 14, 0);
            membersPanel.Children.Add(sep);
            membersPanel.Children.Add(DirectoryRow(member.Name, "已加入共享会话", "", false, 0,
                member.Platform == "android" ? "MptMobileIconPhone" : "MptMobileIconDesktop",
                () => { ShowContactCard(member); return Task.CompletedTask; }, tone: "green"));
        }

        var invite = MobileUi.PrimaryButton("邀请设备加入");
        invite.Click += (_, _) => { ShowSheet(_linkSheet); SyncLinkSheet(); };
        var body = MobileUi.Stack(12,
            MobileUi.ListCard(membersPanel),
            MobileUi.Caption("只有明确加入的设备可见此会话。普通配对不会自动加入。"),
            invite);
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
