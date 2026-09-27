using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Control construction for the phone surface. Everything is built in code (no XAML) so the surface
/// package stays a single DLL with only Avalonia and the shared SDK contracts as dependencies -
/// the same packaging shape the other Android-bundled tool surface uses.
/// </summary>
public sealed partial class MobileNotificationsView
{
    private TextBlock _pageTitle = null!;
    private TextBlock _statusPill = null!;
    private Border _statusPillBorder = null!;
    private TextBlock _statusDetail = null!;
    private Button _syncButton = null!;
    private Button _searchButton = null!;
    private Button _settingsButton = null!;
    private Border _backgroundStrip = null!;
    private TextBlock _backgroundText = null!;
    private TextBlock _backgroundHint = null!;
    private Button _backgroundButton = null!;
    private Border _errorCard = null!;
    private TextBlock _errorText = null!;
    private Button _errorDetailsButton = null!;
    private TextBlock _errorDetails = null!;
    private Border _healthStrip = null!;
    private TextBlock _healthText = null!;
    private Border _searchRow = null!;
    private TextBox _searchBox = null!;
    private CheckBox _searchAll = null!;
    private ScrollViewer _labelStrip = null!;
    private StackPanel _labelPanel = null!;
    private ItemsControl _messageList = null!;
    private TextBlock _emptyText = null!;
    private Border _settingsPanel = null!;
    private TextBlock _settingsFeedback = null!;
    private TextBlock _keyStatus = null!;
    private Button _backgroundButtonInSettings = null!;
    private Button _markReadButton = null!;

    private readonly Dictionary<MobileNotificationLabelViewModel, Button> _labelButtons = [];

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*"),
            Background = Brushes.Transparent
        };

        root.Children.Add(BuildHeader().Row(0));
        root.Children.Add(BuildBackgroundStrip().Row(1));
        root.Children.Add(BuildErrorCard().Row(2));
        root.Children.Add(BuildHealthStrip().Row(3));
        root.Children.Add(BuildSearchRow().Row(4));
        root.Children.Add(BuildLabelStrip().Row(5));

        var listHost = new Grid();
        listHost.Children.Add(BuildMessageList());
        listHost.Children.Add(BuildSettingsPanel());
        root.Children.Add(listHost.Row(6));
        return root;
    }

    private Control BuildHeader()
    {
        var title = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("Text")
        };
        _pageTitle = title;
        var status = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("Text")
        };
        _statusPill = status;
        _statusPillBorder = new Border
        {
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = VerticalAlignment.Center,
            Child = status
        };

        _syncButton = PrimaryButton("立即同步");
        _syncButton.Click += (_, _) => _viewModel.SyncCommand.Execute(null);

        _markReadButton = IconButton("全部已读");
        _markReadButton.Click += (_, _) => _viewModel.MarkReadCommand.Execute(null);

        _searchButton = IconButton("搜索");
        _searchButton.Click += (_, _) => _viewModel.ToggleSearchCommand.Execute(null);

        _settingsButton = IconButton("设置");
        _settingsButton.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(_markReadButton);
        actions.Children.Add(_syncButton);
        actions.Children.Add(_searchButton);
        actions.Children.Add(_settingsButton);

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        heading.Children.Add(title);
        heading.Children.Add(_statusPillBorder);

        _statusDetail = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Muted")
        };

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(heading.Col(0));
        top.Children.Add(actions.Col(1));

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(top);
        header.Children.Add(_statusDetail);

        return new Border
        {
            Padding = new Thickness(14, 12, 14, 10),
            Child = header
        };
    }

    private Control BuildBackgroundStrip()
    {
        _backgroundText = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeight.Medium,
            Foreground = Brush("Text")
        };
        _backgroundHint = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = Brush("Muted")
        };
        _backgroundButton = SecondaryButton("开启后台接收");
        _backgroundButton.Click += (_, _) => _viewModel.ToggleBackgroundCommand.Execute(null);

        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(_backgroundText);
        text.Children.Add(_backgroundHint);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text.Col(0));
        grid.Children.Add(_backgroundButton.Col(1));

        _backgroundStrip = new Border
        {
            Margin = new Thickness(14, 0, 14, 8),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Brush("Card"),
            Child = grid
        };
        return _backgroundStrip;
    }

    private Control BuildErrorCard()
    {
        _errorText = new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Danger")
        };
        _errorDetailsButton = IconButton("查看详情");
        _errorDetailsButton.Click += (_, _) => _viewModel.ToggleErrorDetailsCommand.Execute(null);
        _errorDetails = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = Brush("Muted")
        };
        var fix = IconButton("检查设置");
        fix.Click += (_, _) => _viewModel.OpenSettingsFromErrorCommand.Execute(null);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_errorDetailsButton);
        actions.Children.Add(fix);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(_errorText);
        text.Children.Add(_errorDetails);
        grid.Children.Add(text.Col(0));
        grid.Children.Add(actions.Col(1));

        _errorCard = new Border
        {
            Margin = new Thickness(14, 0, 14, 8),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Brush("DangerSurface"),
            Child = grid
        };
        return _errorCard;
    }

    private Control BuildHealthStrip()
    {
        _healthText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Muted")
        };
        _healthStrip = new Border
        {
            Margin = new Thickness(14, 0, 14, 8),
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            Background = Brush("Card"),
            Child = _healthText
        };
        return _healthStrip;
    }

    private Control BuildSearchRow()
    {
        _searchBox = new TextBox
        {
            PlaceholderText = "搜索通知内容、会话",
            FontSize = 14,
            MinHeight = 44
        };
        _searchBox.Bind(TextBox.TextProperty, new Binding(nameof(MobileNotificationsViewModel.SearchQuery))
        {
            Mode = BindingMode.TwoWay
        });
        _searchAll = new CheckBox { Content = "搜索全部标签", FontSize = 13, MinHeight = 40 };
        _searchAll.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(MobileNotificationsViewModel.SearchAllLabels))
        {
            Mode = BindingMode.TwoWay
        });

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(_searchBox);
        panel.Children.Add(_searchAll);
        _searchRow = new Border
        {
            Margin = new Thickness(14, 0, 14, 8),
            Child = panel
        };
        return _searchRow;
    }

    private Control BuildLabelStrip()
    {
        _labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _labelStrip = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(14, 0, 14, 8),
            Content = _labelPanel
        };
        return _labelStrip;
    }

    private Control BuildMessageList()
    {
        _messageList = new ItemsControl
        {
            ItemsSource = _viewModel.Messages,
            ItemTemplate = new FuncDataTemplate<MobileNotificationCardViewModel>((card, _) => BuildCard(card))
        };
        _emptyText = new TextBlock
        {
            Margin = new Thickness(24, 32, 24, 24),
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Muted")
        };

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(_messageList);
        content.Children.Add(_emptyText);

        return new ScrollViewer
        {
            Padding = new Thickness(14, 0, 14, 18),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
    }

    private Control BuildCard(MobileNotificationCardViewModel card)
    {
        var icon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(card.IconBackground)),
            Child = new TextBlock
            {
                Text = card.IconGlyph,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.Parse(card.IconForeground))
            }
        };

        var label = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationCardViewModel.Label)));
        label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(MobileNotificationCardViewModel.IsUnread))
        {
            Converter = UnreadToBrush
        });

        var time = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("Muted")
        };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationCardViewModel.RelativeTime)));

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(label.Col(0));
        titleRow.Children.Add(time.Col(1));

        var preview = new TextBlock
        {
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brush("Text")
        };
        preview.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationCardViewModel.Preview)));

        var detail = new TextBlock
        {
            FontSize = 14,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Text")
        };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationCardViewModel.Detail)));
        detail.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationCardViewModel.IsExpanded)));

        var meta = new TextBlock
        {
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Muted")
        };
        meta.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationCardViewModel.AbsoluteTime)));
        meta.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationCardViewModel.IsExpanded)));

        var copy = IconButton("复制正文");
        copy.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationCardViewModel.IsExpanded)));
        copy.Click += async (_, _) => await CopyAsync(card.Detail).ConfigureAwait(true);

        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(titleRow);
        text.Children.Add(preview);
        text.Children.Add(detail);
        text.Children.Add(meta);
        text.Children.Add(copy);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon.Col(0));
        grid.Children.Add(new Border { Margin = new Thickness(10, 0, 0, 0), Child = text }.Col(1));

        var unreadDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(6, 4, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("Accent")
        };
        unreadDot.Bind(IsVisibleProperty, new Binding(nameof(MobileNotificationCardViewModel.IsUnread)));

        var cardGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        cardGrid.Children.Add(grid.Col(0));
        cardGrid.Children.Add(unreadDot.Col(1));

        var surface = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Brush("Card"),
            Child = cardGrid
        };
        surface.PointerPressed += (_, _) => card.ToggleExpanded();
        return surface;
    }

    private Control BuildSettingsPanel()
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(SectionTitle("服务器"));
        panel.Children.Add(Field("协议", BuildProtocolBox()));
        panel.Children.Add(Field("服务器地址", BoundBox(nameof(MobileNotificationsViewModel.HostDraft), "message.lixinrui000.cn")));
        panel.Children.Add(Field("端口", BoundBox(nameof(MobileNotificationsViewModel.PortDraft), "8888")));
        panel.Children.Add(Field("频道", BoundBox(nameof(MobileNotificationsViewModel.ChannelDraft), "default")));
        panel.Children.Add(Field("同步间隔（秒）", BoundBox(nameof(MobileNotificationsViewModel.PollIntervalDraft), "5")));

        var save = PrimaryButton("保存服务器设置");
        save.Click += (_, _) => _viewModel.SaveSettingsCommand.Execute(null);
        panel.Children.Add(save);

        panel.Children.Add(SectionTitle("签名密钥"));
        _keyStatus = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_keyStatus);
        var keyBox = new TextBox
        {
            AcceptsReturn = true,
            Height = 120,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "-----BEGIN OPENSSH PRIVATE KEY-----"
        };
        keyBox.Bind(TextBox.TextProperty, new Binding(nameof(MobileNotificationsViewModel.KeyInput))
        {
            Mode = BindingMode.TwoWay
        });
        panel.Children.Add(keyBox);
        var keyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var import = PrimaryButton("导入密钥");
        import.Click += (_, _) => _viewModel.ImportKeyCommand.Execute(null);
        var clearKey = SecondaryButton("清除密钥");
        clearKey.Click += (_, _) => _viewModel.ClearKeyCommand.Execute(null);
        keyActions.Children.Add(import);
        keyActions.Children.Add(clearKey);
        panel.Children.Add(keyActions);
        panel.Children.Add(Hint("私钥只写入系统凭据库（Android Keystore），不会保存在普通文件中，也不会回显。"));

        panel.Children.Add(SectionTitle("后台接收"));
        var backgroundText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        backgroundText.Bind(TextBlock.TextProperty, new Binding(nameof(MobileNotificationsViewModel.BackgroundHint)));
        panel.Children.Add(backgroundText);
        _backgroundButtonInSettings = SecondaryButton("开启后台接收");
        _backgroundButtonInSettings.Click += (_, _) => _viewModel.ToggleBackgroundCommand.Execute(null);
        panel.Children.Add(_backgroundButtonInSettings);

        panel.Children.Add(SectionTitle("通知历史"));
        var clearInbox = SecondaryButton("清空本机通知历史");
        clearInbox.Click += (_, _) => _viewModel.ClearInboxCommand.Execute(null);
        panel.Children.Add(clearInbox);
        panel.Children.Add(Hint("历史与设置保存在应用私有目录；通知内容与桌面端使用同一套签名协议和引用块格式。"));

        _settingsFeedback = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_settingsFeedback);

        var close = SecondaryButton("返回通知列表");
        close.Click += (_, _) => _viewModel.ToggleSettingsCommand.Execute(null);
        panel.Children.Add(close);

        _settingsPanel = new Border
        {
            Padding = new Thickness(14, 4, 14, 18),
            Background = Brush("Page"),
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            }
        };
        return _settingsPanel;
    }

    private Control BuildProtocolBox()
    {
        var box = new ComboBox
        {
            ItemsSource = new[] { "https", "http" },
            MinHeight = 44,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        box.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(MobileNotificationsViewModel.ProtocolDraft))
        {
            Mode = BindingMode.TwoWay
        });
        return box;
    }

    private TextBox BoundBox(string property, string watermark)
    {
        var box = new TextBox
        {
            PlaceholderText = watermark,
            MinHeight = 44,
            FontSize = 14
        };
        box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return box;
    }

    private static Control Field(string label, Control editor)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush("Muted") });
        stack.Children.Add(editor);
        return stack;
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 6, 0, 0),
        Foreground = Brush("Text")
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("Muted")
    };

    private static Button PrimaryButton(string text) => new()
    {
        Content = text,
        MinHeight = 44,
        Padding = new Thickness(16, 8),
        FontWeight = FontWeight.Medium,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static Button SecondaryButton(string text) => new()
    {
        Content = text,
        MinHeight = 44,
        Padding = new Thickness(14, 8),
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private static Button IconButton(string text) => new()
    {
        Content = text,
        MinHeight = 40,
        Padding = new Thickness(12, 6),
        FontSize = 13
    };

    private static IBrush Brush(string role) => MobileNotificationPalette.Current.Brush(role);

    private static readonly IValueConverter UnreadToBrush = new FuncValueConverter<bool, IBrush>(
        unread => unread ? Brush("Accent") : Brush("Text"));
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

/// <summary>
/// Small palette that follows the Shell theme so hard-coded phone colours do not fight the host.
/// </summary>
internal sealed record MobileNotificationPalette(bool Dark)
{
    public static MobileNotificationPalette ForTheme(string? theme) =>
        new(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));

    public static MobileNotificationPalette Current { get; set; } = new(false);

    public IBrush Brush(string role) => new SolidColorBrush(Color.Parse(ColorFor(role)));

    private string ColorFor(string role) => (Dark, role) switch
    {
        (false, "Page") => "#F7F8FA",
        (false, "Card") => "#FFFFFF",
        (false, "Text") => "#111827",
        (false, "Muted") => "#6B7280",
        (false, "Accent") => "#2563EB",
        (false, "Danger") => "#B91C1C",
        (false, "DangerSurface") => "#FEE2E2",
        (true, "Page") => "#111827",
        (true, "Card") => "#1F2937",
        (true, "Text") => "#F9FAFB",
        (true, "Muted") => "#9CA3AF",
        (true, "Accent") => "#60A5FA",
        (true, "Danger") => "#FCA5A5",
        (true, "DangerSurface") => "#7F1D1D",
        _ => "#00000000"
    };
}
