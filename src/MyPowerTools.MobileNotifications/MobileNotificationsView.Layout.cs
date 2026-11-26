using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Control construction for the phone surface, following the approved prototype
/// (<c>.lavish/mpt-mobile/index.html</c> + <c>app.css</c>): the compact page header with real status,
/// the "值得你看一眼。" heading, the label filter strip, day-grouped notification rows inside one
/// rounded list card, the quiet notice, the settings page and a single bottom sheet for the full
/// message and for destructive confirmation.
///
/// Everything is built in code (no XAML) and every control carries the real
/// <c>MyPowerTools.AvaloniaSdk</c> mobile classes (<c>Themes/MptMobile*.axaml</c>), so colours,
/// typography, touch metrics, hover/pressed states and light/dark switching come from the shared
/// theme. Local values are limited to layout (structure, spacing, alignment) and to the per-message
/// icon badge colours that are data, not theme.
/// </summary>
public sealed partial class MobileNotificationsView
{
    private readonly Dictionary<MobileNotificationLabelViewModel, Button> _labelButtons = [];
    private Control? _sheetInvoker;

    private Border _pageFrame = null!;
    private Grid _pageRoot = null!;
    private Border _headerBorder = null!;

    private TextBlock _pageTitle = null!;
    private TextBlock _statusPill = null!;
    private Border _statusPillBorder = null!;
    private TextBlock _statusDetail = null!;
    private TextBlock _headingTitle = null!;
    private TextBlock _headingSubtitle = null!;
    private Button _searchButton = null!;
    private Button _settingsButton = null!;

    private Border _searchRow = null!;
    private TextBox _searchBox = null!;
    private CheckBox _searchAll = null!;

    private ScrollViewer _labelStrip = null!;
    private StackPanel _labelPanel = null!;

    private Border _errorCard = null!;
    private TextBlock _errorText = null!;
    private TextBlock _errorHint = null!;
    private TextBlock _errorDetails = null!;
    private Button _errorActionButton = null!;
    private Button _errorDetailsButton = null!;

    private Border _keyWarningCard = null!;
    private Border _healthStrip = null!;
    private TextBlock _healthText = null!;

    private ItemsControl _groupList = null!;
    private Border _emptyCard = null!;
    private Button _emptyActionButton = null!;

    private Border _inboxPanel = null!;
    private Border _settingsPanel = null!;
    private TextBlock _settingsTitle = null!;
    private Button _settingsBackButton = null!;
    private TextBlock _settingsFeedback = null!;
    private TextBlock _keyStatus = null!;
    private TextBox _keyBox = null!;
    private Border _keyCard = null!;
    private ToggleSwitch _backgroundSwitch = null!;
    private TextBlock _backgroundState = null!;
    private TextBlock _backgroundHint = null!;
    private TextBlock _endpointText = null!;

    private Border _sheetOverlay = null!;
    private Border _sheetCard = null!;
    private StackPanel _detailPanel = null!;
    private StackPanel _clearPanel = null!;
    private TextBlock _detailTitle = null!;
    private TextBlock _detailMeta = null!;
    private TextBlock _detailBody = null!;
    private TextBlock _detailPosition = null!;
    private Button _detailPreviousButton = null!;
    private Button _detailNextButton = null!;
    private Button _detailReadButton = null!;

    private Control BuildLayout()
    {
        _pageRoot = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
            Background = Brushes.Transparent
        };
        _pageRoot.Classes.Add(MobileNotificationTheme.RootClass);
        _pageRoot.Children.Add(BuildPageHeader().Row(0));
        _pageRoot.Children.Add(BuildSearchPanel().Row(1));
        _pageRoot.Children.Add(BuildLabelStrip().Row(2));

        var body = new Grid();
        _inboxPanel = BuildInboxPanel();
        _settingsPanel = BuildSettingsPanel();
        body.Children.Add(_inboxPanel);
        body.Children.Add(_settingsPanel);
        _pageRoot.Children.Add(body.Row(3));

        _pageFrame = new Border { Child = _pageRoot, Name = "PageFrame" };
        _pageFrame.Classes.Add(MobileNotificationTheme.PageClass);
        MobileNotificationTheme.ApplyBackground(_pageFrame, MobileNotificationTheme.BackgroundBrushKey);
        _pageFrame.SizeChanged += (_, _) => ApplyPageWidth();

        var host = new Grid();
        host.Children.Add(_pageFrame);
        host.Children.Add(BuildSheetOverlay());
        return host;
    }

    // ---------------------------------------------------------------- header

    private Control BuildPageHeader()
    {
        _pageTitle = MobileNotificationTheme.Meta("远程通知");

        var (pill, pillText) = MobileNotificationTheme.StatusPill();
        _statusPillBorder = pill;
        _statusPill = pillText;

        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        identity.Children.Add(_pageTitle);
        identity.Children.Add(pill);

        _searchButton = MobileNotificationTheme.IconButton("搜索");
        _searchButton.Click += (_, _) => _viewModel.ToggleSearchCommand.Execute(null);
        _searchButton.Name = "SearchToggle";
        _settingsButton = MobileNotificationTheme.IconButton("设置");
        _settingsButton.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);
        _settingsButton.Name = "SettingsToggle";

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        actions.Children.Add(_searchButton);
        actions.Children.Add(_settingsButton);

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(identity.Col(0));
        top.Children.Add(actions.Col(1));

        _headingTitle = MobileNotificationTheme.PageTitle();
        _headingTitle.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.HeadingTitle)));
        _headingSubtitle = MobileNotificationTheme.PageSubtitle();
        _headingSubtitle.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.HeadingSubtitle)));

        _statusDetail = MobileNotificationTheme.Caption();

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(top);
        header.Children.Add(_headingTitle);
        header.Children.Add(_headingSubtitle);
        header.Children.Add(_statusDetail);

        _headerBorder = new Border { Child = header, Name = "InboxHeader" };
        _headerBorder.Classes.Add(MobileNotificationTheme.PageHeaderClass);
        return _headerBorder;
    }

    // ---------------------------------------------------------------- search + filters

    private Control BuildSearchPanel()
    {
        _searchBox = new TextBox { PlaceholderText = "搜索通知内容、会话或来源", Name = "SearchBox" };
        _searchBox.Classes.Add(MobileNotificationTheme.SearchClass);
        _searchBox.Bind(TextBox.TextProperty, new Binding(nameof(MobileNotificationsViewModel.SearchQuery))
        {
            Mode = BindingMode.TwoWay
        });

        _searchAll = MobileNotificationTheme.Check("搜索全部标签（含 Claude Task）");
        _searchAll.Name = "SearchAllLabels";
        _searchAll.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MobileNotificationsViewModel.SearchAllLabels))
        {
            Mode = BindingMode.TwoWay
        });

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(_searchBox);
        panel.Children.Add(_searchAll);

        _searchRow = MobileNotificationTheme.Card(MobileNotificationTheme.SearchBoxClass);
        _searchRow.Margin = new Thickness(0, 6, 0, 2);
        _searchRow.Child = panel;
        _searchRow.Name = "SearchPanel";
        return _searchRow;
    }

    private Control BuildLabelStrip()
    {
        _labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _labelStrip = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 6, 0, 2),
            Content = _labelPanel,
            Name = "LabelStrip"
        };
        return _labelStrip;
    }

    // ---------------------------------------------------------------- inbox

    private Border BuildInboxPanel()
    {
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(BuildErrorCard());
        content.Children.Add(BuildKeyWarningCard());
        content.Children.Add(BuildHealthStrip());
        content.Children.Add(BuildGroupList());
        content.Children.Add(BuildEmptyCard());
        content.Children.Add(BuildNotice());
        content.Children.Add(BuildNoticeSettingsButton());

        return new Border
        {
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 2, 0, 24),
                Content = content
            },
            Name = "InboxPanel"
        };
    }

    private Control BuildErrorCard()
    {
        _errorText = MobileNotificationTheme.Body();
        _errorText.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.ErrorText)));

        _errorHint = MobileNotificationTheme.Caption();
        _errorHint.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.ErrorHint)));
        _errorHint.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.HasErrorHint)));

        _errorDetails = MobileNotificationTheme.Caption();
        _errorDetails.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.ErrorDetails)));
        _errorDetails.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.IsErrorDetailsVisible)));

        _errorActionButton = MobileNotificationTheme.SecondaryButton("");
        _errorActionButton.Bind(ContentControl.ContentProperty, new Binding(nameof(MobileNotificationsViewModel.ErrorActionText)));
        _errorActionButton.Click += (_, _) => _viewModel.ErrorActionCommand.Execute(null);
        _errorActionButton.Name = "ErrorAction";

        _errorDetailsButton = MobileNotificationTheme.QuietButton("");
        _errorDetailsButton.Bind(ContentControl.ContentProperty, new Binding(nameof(MobileNotificationsViewModel.ErrorDetailsActionText)));
        _errorDetailsButton.Click += (_, _) => _viewModel.ToggleErrorDetailsCommand.Execute(null);
        _errorDetailsButton.Name = "ErrorDetails";

        var actions = new StackPanel { Spacing = 2, MinWidth = 128 };
        actions.Children.Add(_errorActionButton);
        actions.Children.Add(_errorDetailsButton);

        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(_errorText);
        text.Children.Add(_errorHint);
        text.Children.Add(_errorDetails);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(text.Col(0));
        grid.Children.Add(actions.Col(1));

        _errorCard = MobileNotificationTheme.Card(MobileNotificationTheme.NoticeClass);
        _errorCard.Classes.Add(MobileNotificationTheme.NoticeWarningClass);
        _errorCard.Child = grid;
        _errorCard.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.HasError)));
        _errorCard.Name = "ErrorCard";
        return _errorCard;
    }

    private Control BuildKeyWarningCard()
    {
        var text = MobileNotificationTheme.Caption();
        text.Classes.Add(MobileNotificationTheme.WarningTextClass);
        text.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.KeySetupText)));

        var action = MobileNotificationTheme.SecondaryButton("");
        action.Bind(ContentControl.ContentProperty, new Binding(nameof(MobileNotificationsViewModel.KeySetupActionText)));
        action.Click += (_, _) => _viewModel.OpenKeySettingsCommand.Execute(null);
        action.MinWidth = 132;
        action.Name = "KeySetupAction";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(text.Col(0));
        grid.Children.Add(action.Col(1));

        _keyWarningCard = MobileNotificationTheme.Card(MobileNotificationTheme.NoticeClass);
        _keyWarningCard.Child = grid;
        _keyWarningCard.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.ShowsKeySetupWarning)));
        _keyWarningCard.Name = "KeyWarningCard";
        return _keyWarningCard;
    }

    private Control BuildHealthStrip()
    {
        _healthText = MobileNotificationTheme.Caption();
        _healthText.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.HealthText)));

        _healthStrip = MobileNotificationTheme.Card();
        _healthStrip.Child = _healthText;
        _healthStrip.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.HasHealthText)));
        _healthStrip.Name = "HealthStrip";
        return _healthStrip;
    }

    private Control BuildGroupList()
    {
        _groupList = new ItemsControl
        {
            ItemsSource = _viewModel.MessageGroups,
            ItemTemplate = new FuncDataTemplate<MobileNotificationDayGroupViewModel>((group, _) => BuildDayGroup(group))
        };
        _groupList.Name = "NotificationGroups";
        return _groupList;
    }

    private Control BuildDayGroup(MobileNotificationDayGroupViewModel group)
    {
        var headingRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headingRow.Classes.Add(MobileNotificationTheme.SectionHeaderClass);
        headingRow.Children.Add(MobileNotificationTheme.SectionTitle(group.Title).Col(0));
        if (group.IsFirst)
        {
            var markRead = MobileNotificationTheme.TextButton("全部已读");
            markRead.HorizontalAlignment = HorizontalAlignment.Right;
            markRead.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.HasUnread))
            {
                Source = _viewModel
            });
            markRead.Click += (_, _) => _viewModel.MarkReadCommand.Execute(null);
            markRead.Name = "MarkAllRead";
            headingRow.Children.Add(markRead.Col(1));
        }

        var rows = new StackPanel();
        var cards = group.Cards.ToList();
        for (var index = 0; index < cards.Count; index++)
        {
            if (index > 0)
            {
                rows.Children.Add(MobileNotificationTheme.RowDivider());
            }

            rows.Children.Add(BuildRow(cards[index]));
        }

        var card = MobileNotificationTheme.Card(MobileNotificationTheme.ListCardClass);
        card.Child = rows;

        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(headingRow);
        section.Children.Add(card);
        return section;
    }

    private Control BuildRow(MobileNotificationCardViewModel card)
    {
        // Icon badge colours are message data (shipped RemoteNotificationMessageViewModel), not theme.
        var icon = new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(card.IconBackground)),
            Child = new TextBlock
            {
                Text = card.IconGlyph,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(card.IconForeground))
            }
        };
        icon.Classes.Add(MobileNotificationTheme.IconBoxClass);

        var title = MobileNotificationTheme.RowTitle(card.Title);
        title.TextTrimming = TextTrimming.CharacterEllipsis;

        var meta = MobileNotificationTheme.RowSubtitle(card.Subtitle);

        var preview = MobileNotificationTheme.Caption(card.Preview);
        preview.MaxLines = 2;
        preview.TextTrimming = TextTrimming.CharacterEllipsis;

        var unreadDot = MobileNotificationTheme.UnreadDot();
        unreadDot.Margin = new Thickness(8, 6, 0, 0);
        unreadDot.VerticalAlignment = VerticalAlignment.Top;

        var copy = new StackPanel { Spacing = 4 };
        copy.Children.Add(title);
        copy.Children.Add(meta);
        copy.Children.Add(preview);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon.Col(0));
        grid.Children.Add(new Border { Margin = new Thickness(11, 0, 0, 0), Child = copy }.Col(1));
        grid.Children.Add(unreadDot.Col(2));

        var row = new Button
        {
            Content = grid,
            Name = $"NotificationRow_{card.Id}"
        };
        row.Classes.Add(MobileNotificationTheme.ListRowClass);
        row.Click += (_, _) =>
        {
            _sheetInvoker = row;
            _viewModel.ShowDetail(card);
        };

        // Rows are rebuilt with their group, but a card can change while visible (mark-all-read,
        // relative-time tick), so keep the stateful bits in sync.
        card.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MobileNotificationCardViewModel.IsUnread):
                    unreadDot.IsVisible = card.IsUnread;
                    break;
                case nameof(MobileNotificationCardViewModel.RelativeTime):
                case nameof(MobileNotificationCardViewModel.Subtitle):
                    meta.Text = card.Subtitle;
                    break;
            }
        };
        unreadDot.IsVisible = card.IsUnread;

        return row;
    }

    private Control BuildEmptyCard()
    {
        var glyph = new TextBlock
        {
            Text = "○",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        glyph.Classes.Add(MobileNotificationTheme.PageTitleClass);

        var title = MobileNotificationTheme.Text(MobileNotificationTheme.EmptyTitleClass);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        title.TextAlignment = TextAlignment.Center;
        title.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.EmptyTitle)));

        var description = MobileNotificationTheme.Caption();
        description.TextAlignment = TextAlignment.Center;
        description.HorizontalAlignment = HorizontalAlignment.Center;
        description.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.EmptyText)));

        _emptyActionButton = MobileNotificationTheme.SecondaryButton("");
        _emptyActionButton.Bind(ContentControl.ContentProperty, new Binding(nameof(MobileNotificationsViewModel.EmptyActionText)));
        _emptyActionButton.Click += (_, _) => _viewModel.EmptyActionCommand.Execute(null);
        _emptyActionButton.Name = "EmptyAction";

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(glyph);
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(_emptyActionButton);

        _emptyCard = MobileNotificationTheme.Card();
        _emptyCard.Margin = new Thickness(0, 8, 0, 0);
        _emptyCard.Child = panel;
        _emptyCard.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.ShowsEmptyState)));
        _emptyCard.Name = "EmptyState";
        return _emptyCard;
    }

    private Control BuildNotice()
    {
        var noticeText = MobileNotificationTheme.Caption();
        noticeText.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.NoticeText)));

        var notice = MobileNotificationTheme.Card(MobileNotificationTheme.NoticeClass);
        notice.Child = noticeText;
        notice.Name = "Notice";
        return notice;
    }

    private Control BuildNoticeSettingsButton()
    {
        var button = MobileNotificationTheme.QuietButton("通知设置");
        button.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);
        button.Name = "NoticeSettings";
        return button;
    }

    // ---------------------------------------------------------------- settings

    private Border BuildSettingsPanel()
    {
        var content = new StackPanel { Spacing = 12 };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Classes.Add(MobileNotificationTheme.PageHeaderClass);
        _settingsTitle = MobileNotificationTheme.PageTitle(_viewModel.SettingsTitle);
        header.Children.Add(_settingsTitle.Col(0));
        _settingsBackButton = MobileNotificationTheme.BackButton("返回");
        _settingsBackButton.HorizontalAlignment = HorizontalAlignment.Right;
        _settingsBackButton.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);
        _settingsBackButton.Name = "SettingsBack";
        header.Children.Add(_settingsBackButton.Col(1));
        content.Children.Add(header);

        content.Children.Add(BuildServerCard());
        content.Children.Add(BuildKeyCard());
        content.Children.Add(BuildBackgroundCard());
        content.Children.Add(BuildHistoryCard());

        _settingsFeedback = MobileNotificationTheme.Caption();
        _settingsFeedback.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.SettingsFeedback)));
        _settingsFeedback.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationsViewModel.HasSettingsFeedback)));
        content.Children.Add(_settingsFeedback);

        var close = MobileNotificationTheme.QuietButton("返回通知列表");
        close.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);
        close.Name = "SettingsClose";
        content.Children.Add(close);

        return new Border
        {
            Background = Brushes.Transparent,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0, 2, 0, 24),
                Content = content
            },
            IsVisible = false,
            Name = "SettingsPanel"
        };
    }

    private Control BuildServerCard()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(MobileNotificationTheme.SectionTitle("服务器"));

        // The raw endpoint lives here, not in the inbox header: the list keeps only friendly state.
        _endpointText = MobileNotificationTheme.Caption();
        _endpointText.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.EndpointText)));
        panel.Children.Add(_endpointText);

        panel.Children.Add(Field("协议", BuildProtocolBox()));
        panel.Children.Add(Field("服务器地址", BoundBox(nameof(MobileNotificationsViewModel.HostDraft), "message.example.com")));
        panel.Children.Add(Field("端口", BoundBox(nameof(MobileNotificationsViewModel.PortDraft), "8888")));
        panel.Children.Add(Field("频道", BoundBox(nameof(MobileNotificationsViewModel.ChannelDraft), "default")));
        panel.Children.Add(Field("同步间隔（秒）", BoundBox(nameof(MobileNotificationsViewModel.PollIntervalDraft), "5")));

        var save = MobileNotificationTheme.PrimaryButton("保存服务器设置");
        save.Click += (_, _) => _viewModel.SaveSettingsCommand.Execute(null);
        save.Name = "SaveServerSettings";
        panel.Children.Add(save);

        return SettingsCard(panel);
    }

    private Control BuildKeyCard()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(MobileNotificationTheme.SectionTitle("签名密钥"));

        _keyStatus = MobileNotificationTheme.Caption();
        _keyStatus.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.KeyStatusText)));
        panel.Children.Add(_keyStatus);

        panel.Children.Add(MobileNotificationTheme.Caption(
            "在电脑端复制 OpenSSH Ed25519 私钥内容（通常是 ~/.ssh/id_ed25519），粘贴到下面导入。"));

        _keyBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 120,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----",
            Name = "SigningKeyBox"
        };
        _keyBox.Classes.Add(MobileNotificationTheme.FieldClass);
        _keyBox.Bind(TextBox.TextProperty, new Binding(nameof(MobileNotificationsViewModel.KeyInput))
        {
            Mode = BindingMode.TwoWay
        });
        panel.Children.Add(_keyBox);

        var import = MobileNotificationTheme.PrimaryButton("导入密钥");
        import.Click += (_, _) => _viewModel.ImportKeyCommand.Execute(null);
        import.Name = "ImportKey";
        var clear = MobileNotificationTheme.SecondaryButton("清除密钥");
        clear.Click += (_, _) => _viewModel.ClearKeyCommand.Execute(null);
        clear.Name = "ClearKey";

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actions.Children.Add(import.Col(0));
        actions.Children.Add(clear.Col(1));
        panel.Children.Add(actions);

        panel.Children.Add(MobileNotificationTheme.Caption(
            "私钥只写入系统凭据库（Android Keystore），不会保存在普通文件中，也不会回显。"));

        _keyCard = SettingsCard(panel);
        _keyCard.Name = "KeyCard";
        return _keyCard;
    }

    private Control BuildBackgroundCard()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(MobileNotificationTheme.SectionTitle("后台接收"));

        _backgroundSwitch = MobileNotificationTheme.Switch();
        _backgroundSwitch.OnContent = "开启";
        _backgroundSwitch.OffContent = "关闭";
        _backgroundSwitch.Name = "BackgroundSwitch";
        _backgroundSwitch.Bind(ToggleSwitch.IsCheckedProperty, new Binding(nameof(MobileNotificationsViewModel.BackgroundSwitchOn))
        {
            Mode = BindingMode.TwoWay
        });
        _backgroundSwitch.Bind(IsEnabledProperty, new Binding(nameof(MobileNotificationsViewModel.CanToggleBackground)));

        var label = MobileNotificationTheme.Body("应用退到后台后继续接收");
        var switchGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        switchGrid.Children.Add(label.Col(0));
        switchGrid.Children.Add(_backgroundSwitch.Col(1));
        panel.Children.Add(switchGrid);

        _backgroundState = MobileNotificationTheme.Caption();
        _backgroundState.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.BackgroundStateText)));
        panel.Children.Add(_backgroundState);

        _backgroundHint = MobileNotificationTheme.Caption();
        _backgroundHint.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.BackgroundHint)));
        panel.Children.Add(_backgroundHint);

        return SettingsCard(panel);
    }

    private Control BuildHistoryCard()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(MobileNotificationTheme.SectionTitle("通知历史"));

        var count = MobileNotificationTheme.Body();
        count.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.CountText)));
        panel.Children.Add(count);

        var clear = MobileNotificationTheme.SecondaryButton("清空本机通知历史");
        clear.Click += (_, _) => _viewModel.OpenClearHistoryCommand.Execute(null);
        clear.Name = "ClearHistory";
        panel.Children.Add(clear);

        panel.Children.Add(MobileNotificationTheme.Caption(
            "历史保存在应用私有目录；通知内容与桌面端使用同一套签名协议和引用块格式。"));

        return SettingsCard(panel);
    }

    private static Border SettingsCard(Control content)
    {
        var card = MobileNotificationTheme.Card();
        card.Child = content;
        return card;
    }

    private Control BuildProtocolBox()
    {
        var box = MobileNotificationTheme.ComboBox();
        box.ItemsSource = new[] { "https", "http" };
        box.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(MobileNotificationsViewModel.ProtocolDraft))
        {
            Mode = BindingMode.TwoWay
        });
        return box;
    }

    private TextBox BoundBox(string property, string watermark)
    {
        var box = MobileNotificationTheme.Field(watermark);
        box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static Control Field(string label, Control editor)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(MobileNotificationTheme.FieldLabel(label));
        stack.Children.Add(editor);
        return stack;
    }

    // ---------------------------------------------------------------- sheets

    private Control BuildSheetOverlay()
    {
        _sheetOverlay = new Border
        {
            IsVisible = false,
            ZIndex = 40,
            Name = "SheetOverlay",
            Child = null
        };
        _sheetOverlay.Classes.Add(MobileNotificationTheme.OverlayClass);
        _sheetOverlay.PointerPressed += (_, args) =>
        {
            // Tapping the scrim closes the sheet, like the prototype overlay.
            if (ReferenceEquals(args.Source, _sheetOverlay))
            {
                CloseSheetFromView();
            }
        };

        _detailPanel = BuildDetailSheetContent();
        _clearPanel = BuildClearSheetContent();

        var grabber = MobileNotificationTheme.SheetGrabber();

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(grabber);
        content.Children.Add(_detailPanel);
        content.Children.Add(_clearPanel);

        _sheetCard = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            },
            Name = "SheetCard"
        };
        _sheetCard.Classes.Add(MobileNotificationTheme.SheetClass);

        _sheetOverlay.Child = _sheetCard;
        _sheetOverlay.SizeChanged += (_, _) =>
            _sheetCard.MaxHeight = Math.Max(320, _sheetOverlay.Bounds.Height * 0.92);
        return _sheetOverlay;
    }

    private StackPanel BuildDetailSheetContent()
    {
        _detailTitle = MobileNotificationTheme.SheetTitle();

        _detailMeta = MobileNotificationTheme.Caption();
        _detailMeta.Margin = new Thickness(0, 6, 0, 0);

        var heading = new StackPanel();
        heading.Children.Add(_detailTitle);
        heading.Children.Add(_detailMeta);

        var close = MobileNotificationTheme.CloseButton("✕");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Click += (_, _) => CloseSheetFromView();
        close.Name = "DetailClose";

        var headRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headRow.Children.Add(heading.Col(0));
        headRow.Children.Add(close.Col(1));

        _detailBody = MobileNotificationTheme.Body();
        _detailBody.Margin = new Thickness(0, 4, 0, 0);

        var bodyCard = MobileNotificationTheme.Card(MobileNotificationTheme.NoticeClass);
        bodyCard.Child = _detailBody;

        _detailPosition = MobileNotificationTheme.Meta();
        _detailPreviousButton = MobileNotificationTheme.SecondaryButton("← 上一条");
        _detailPreviousButton.Click += (_, _) => _viewModel.ShowDetailPreviousCommand.Execute(null);
        _detailPreviousButton.Name = "DetailPrevious";
        _detailNextButton = MobileNotificationTheme.SecondaryButton("下一条 →");
        _detailNextButton.Click += (_, _) => _viewModel.ShowDetailNextCommand.Execute(null);
        _detailNextButton.Name = "DetailNext";

        var positionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        positionRow.Children.Add(_detailPosition.Col(0));
        positionRow.Children.Add(_detailPreviousButton.Col(1));
        positionRow.Children.Add(_detailNextButton.Col(2));

        _detailReadButton = MobileNotificationTheme.PrimaryButton("标为已读");
        _detailReadButton.Click += (_, _) => _viewModel.MarkDetailReadCommand.Execute(null);
        _detailReadButton.Name = "DetailMarkRead";

        var copy = MobileNotificationTheme.SecondaryButton("复制正文");
        copy.Click += async (_, _) =>
        {
            if (_viewModel.Detail is { } detail)
            {
                await CopyAsync(detail.Body).ConfigureAwait(true);
            }
        };
        copy.Name = "DetailCopy";

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        actions.Children.Add(_detailReadButton.Col(0));
        actions.Children.Add(copy.Col(1));

        var panel = new StackPanel { Spacing = 14, IsVisible = false, Name = "DetailSheet" };
        panel.Children.Add(headRow);
        panel.Children.Add(bodyCard);
        panel.Children.Add(positionRow);
        panel.Children.Add(actions);
        return panel;
    }

    private StackPanel BuildClearSheetContent()
    {
        var title = MobileNotificationTheme.SheetTitle("清空本机通知历史？");
        var description = MobileNotificationTheme.Caption(
            "只会删除这台手机上保存的通知历史，不会影响电脑端记录，也不会修改服务器设置。");

        var confirm = MobileNotificationTheme.PrimaryButton("清空");
        confirm.Click += (_, _) => _viewModel.ClearInboxCommand.Execute(null);
        confirm.Name = "ConfirmClearHistory";
        var cancel = MobileNotificationTheme.QuietButton("取消");
        cancel.Click += (_, _) => _viewModel.CancelClearHistoryCommand.Execute(null);
        cancel.Name = "CancelClearHistory";

        var panel = new StackPanel { Spacing = 14, IsVisible = false, Name = "ClearSheet" };
        panel.Children.Add(title);
        panel.Children.Add(description);
        panel.Children.Add(confirm);
        panel.Children.Add(cancel);
        return panel;
    }

    /// <summary>Closes whichever sheet is open and restores focus to the row that opened it.</summary>
    private void CloseSheetFromView()
    {
        _viewModel.CloseSheets();
        RestoreSheetInvokerFocus();
    }

    private void RestoreSheetInvokerFocus()
    {
        var invoker = _sheetInvoker;
        _sheetInvoker = null;
        if (invoker is not null)
        {
            Dispatcher.UIThread.Post(() => invoker.Focus(), DispatcherPriority.Input);
        }
    }

    // ---------------------------------------------------------------- layout helpers

    /// <summary>
    /// Prototype page padding is 22dp and 18dp on a 320dp-wide phone; the SDK theme ships both as
    /// <c>MptMobilePage</c> / <c>MptMobilePageNarrow</c>, so this only toggles the class.
    /// </summary>
    private void ApplyPageWidth()
    {
        var width = _pageFrame.Bounds.Width;
        var narrow = width > 0 && width < 340;
        MobileNotificationTheme.SetClass(_pageFrame, MobileNotificationTheme.PageNarrowClass, narrow);
    }
}

internal static class MobileNotificationControlExtensions
{
    public static T Row<T>(this T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    public static T Col<T>(this T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
