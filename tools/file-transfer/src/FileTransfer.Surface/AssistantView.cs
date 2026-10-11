using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.AvaloniaSdk.Controls;

namespace FileTransfer.Surface;

/// <summary>Conversation navigation and the shared native chat surface.</summary>
internal sealed partial class AssistantView : UserControl, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
{
    private const double SidePaddingWide = 16;
    private const double SidePaddingNarrow = 12;

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly AssistantCore _core;
    private readonly TransferCore _legacy;

    /// <summary>Raised when the user asks for the classic transfer form (IP, port, WebDAV, batch send).</summary>
    public event EventHandler? AdvancedRequested;

    // --- header
    private readonly TextBlock _headerTitle = MobileUi.PageTitle("文件助手");
    private readonly TextBlock _headerState = MobileUi.Caption("");
    private readonly Button _setupButton;

    // --- thread
    private readonly StackPanel _thread = new() { Spacing = 10 };
    private readonly StackPanel _pendingRequests = new() { Spacing = 10 };
    private readonly ScrollViewer _threadScroll;
    private readonly StackPanel _emptyState;

    // --- composer
    private readonly TextBox _input;
    private readonly Button _attachFiles;
    private readonly Button _attachImages;
    private readonly Button _send;
    private readonly StackPanel _attachmentRow = new() { Spacing = 2 };
    private readonly TextBlock _attachmentSummary = MobileUi.Caption("");
    private readonly List<string> _attachments = [];
    private readonly TextBlock _status = MobileUi.Caption("");
    private readonly Button _retrySync;

    // --- sheets
    private readonly Border _sheetScrim;
    private readonly Border _sheetHost;
    private readonly ScrollViewer _sheetScroll;
    private readonly StackPanel _sheetInner = new() { Spacing = 10 };
    private readonly TextBlock _sheetTitle = MobileUi.With(new TextBlock(), MobileUi.Classes.SheetTitle);
    private readonly Button _sheetClose;
    private readonly StackPanel _deviceSheetBody = new() { Spacing = 10 };
    private readonly WrapPanel _deviceGrid = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _deviceSheetHint = MobileUi.Caption("");
    private readonly StackPanel _setupSheet;
    private readonly StackPanel _linkSheet;
    private readonly StackPanel _receiveSheet;
    private Button _scanButton = null!;
    private Button _copyLink = null!;
    private Button _refreshLink = null!;
    private MptQrCode? _qrValue;

    /// <summary>
    /// Work this page started (a camera scan, a link preview) is bound to the page's current
    /// attachment, not to the object's lifetime. Detach cancels it and the next attach renews it, so
    /// reopening the tool does not inherit a cancelled token and silently drop every later callback.
    /// </summary>
    private CancellationTokenSource _lifetime = new();
    private readonly TextBox _linkCode = MobileUi.FieldBox("粘贴连接码");
    private readonly TextBlock _linkPreview = MobileUi.Caption("");
    private readonly TextBlock _linkState = MobileUi.Caption("");
    private readonly TextBlock _receiveState = MobileUi.Caption("");
    private readonly Button _receiveToggle;
    private readonly Button _receiveFolder;
    private readonly Border _qrHolder;

    private double _viewport = 390;
    private bool _attached;
    private bool _sheetOpen;
    private AssistantItem? _pendingForward;
    private int _lastItemCount = -1;
    private bool _followThreadEnd = true;
    // Set by a scroll notification, performed once after layout: hiding the cue resizes the composer
    // row, which is the viewport the notification came from.
    private bool _hideNewMessagesCue;
    private bool _followQueued;
    // The offset the follow decision was made from. The queued job runs one dispatcher turn later, so
    // it must be able to tell "content grew under a following view" (the offset did not move) from
    // "the view moved while this job was pending" (the offset did move) without asking the extent,
    // which grows on its own.
    private double _followQueueOffset;

    public AssistantView(MptAvaloniaSurfaceContext context, AssistantCore core, TransferCore legacy)
    {
        _context = context;
        _core = core;
        _legacy = legacy;

        _setupButton = MobileUi.IconButton("MptMobileIconDots", "会话详情");
        ((Control)_setupButton.Content!).Width = 20;
        ((Control)_setupButton.Content!).Height = 20;
        _setupButton.Click += (_, _) => ShowConversationDetails();

        _input = MobileUi.With(new TextBox
        {
            PlaceholderText = "写点文字，或添加文件…",
            // Multi-line is allowed, but Enter sends and Shift+Enter breaks the line: the fast path
            // stays one keystroke and a longer note is still possible.
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 104,
            FontSize = 15,
            MinHeight = 44,
            Padding = new Thickness(10, 8)
        }, MobileUi.Classes.Field);
        // The text box handles Enter itself, so the shortcut is registered for handled events too,
        // which is the only way the key reaches this page before the control consumes it.
        AutomationProperties.SetName(_input, "消息内容");
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // Enablement follows the real text, not a later module refresh: typing must light up 发送.
        _input.TextChanged += (_, _) => { SyncComposer(); DraftChanged(); RefreshConversationNavigation(); };
        _pairCode.TextChanged += (_, _) => { if (_pairConfirm is not null) _pairConfirm.IsEnabled = _previewedPairCode is not null && _pairCode.Text == _previewedPairCode; };

        _attachFiles = MobileUi.IconButton("MptMobileIconPlus", "添加文件");
        _attachFiles.Click += (_, _) => ShowAttachmentSheet();
        _attachImages = MobileUi.IconButton("MptMobileIconImage", "添加图片");
        _attachImages.Click += async (_, _) => await PickAsync(true);
        _send = MobileUi.PrimaryButton("发送");
        _send.Click += async (_, _) => await RunAsync(() => SendAsync(_targetDeviceId));
        _retrySync = MobileUi.TextButton("重新同步");
        _retrySync.HorizontalAlignment = HorizontalAlignment.Right;
        _retrySync.Click += async (_, _) => await RunAsync(() => _core.SyncAsync());

        _emptyState = BuildEmptyState();
        // Every level stretches to the viewport width: a wide card inside a scroll viewer that does
        // not scroll horizontally would otherwise overflow and hide its trailing content.
        _thread.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pendingRequests.HorizontalAlignment = HorizontalAlignment.Stretch;
        _emptyState.HorizontalAlignment = HorizontalAlignment.Stretch;
        _threadScroll = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { _pendingRequests, _emptyState, _thread }
            },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        _threadScroll.ScrollChanged += (_, e) =>
        {
            // Extent and viewport settle during layout, after a snapshot rebuilt the rows.
            // Keep following the latest entry through those size changes, including receipt text
            // wrapping and the Android keyboard changing the viewport.
            //
            // A ScrollChanged notification is raised from inside the ScrollViewer's own arrange pass.
            // Anything written here that is a layout input of this viewport -- the offset itself and
            // the composer row that the "new messages" cue lives in -- would invalidate the pass that
            // produced the notification. The decision is therefore recorded here and performed once,
            // after layout, by QueueFollowThreadEnd.
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (AtThreadEnd())
                {
                    _followThreadEnd = true;
                    _hideNewMessagesCue = true;
                }
                if (_followThreadEnd) QueueFollowThreadEnd();
            }
            else if (e.OffsetDelta.Y != 0)
            {
                ConversationScrollChanged();
                // A person scrolling into history owns that position until another message arrives.
                _followThreadEnd = AtThreadEnd();
            }
        };

        _sheetClose = MobileUi.CloseButton();
        _sheetClose.Click += (_, _) => DismissSheet();
        _receiveToggle = MobileUi.SecondaryButton("允许接收");
        _receiveToggle.Click += async (_, _) => await RunAsync(ToggleReceiveAsync);
        _receiveFolder = MobileUi.QuietButton("选择收件保存位置");
        _receiveFolder.Click += async (_, _) => await RunAsync(PickReceiveDirectoryAsync);
        _receiveState = MobileUi.Caption("");
        _qrHolder = MobileUi.Inset(new Grid { Children = { CenterCaption("正在准备连接码…") } });
        _qrHolder.HorizontalAlignment = HorizontalAlignment.Center;

        _setupSheet = BuildSetupSheet();
        _linkSheet = BuildLinkSheet();
        _receiveSheet = BuildReceiveSheet();

        // The content scrolls inside a bounded height, so a long sheet never runs past the screen and a
        // short one stays a sheet instead of a full-page panel.
        _sheetScroll = new ScrollViewer
        {
            Content = _sheetInner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = VerticalAlignment.Top
        };
        _sheetHost = MobileUi.With(new Border
        {
            Child = MobileUi.Stack(10, BuildSheetHeader(), _sheetScroll),
            // A bottom sheet sizes to its content and sits on the bottom edge. Without this it
            // stretches to the whole page and paints an empty white panel over the conversation.
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxHeight = SheetMaxHeight
        }, MobileUi.Classes.Sheet);
        _sheetScrim = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            Child = _sheetHost
        };
        _sheetScrim.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, _sheetScrim)) DismissSheet(); };

        // The header, thread and composer share one bounded column, so a desktop window shows a
        // readable conversation instead of text stretched the full width of the monitor.
        _conversationColumn = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        _conversationColumn.Children.Add(header);
        Grid.SetRow(_threadScroll, 1);
        _conversationColumn.Children.Add(_threadScroll);
        var composer = BuildComposer();
        Grid.SetRow(composer, 2);
        _conversationColumn.Children.Add(composer);

        var layout = new Grid { RowDefinitions = new RowDefinitions("*") };
        layout.Children.Add(BuildConversationNavigation(_conversationColumn));
        // The sheet overlays the whole page: without the span it would only cover the header row and
        // leave the composer visible and tappable underneath.
        Grid.SetRowSpan(_sheetScrim, 3);
        layout.Children.Add(_sheetScrim);

        MobileUi.With(this, MobileUi.Classes.Root);
        var page = MobileUi.With(new Border { Child = layout }, MobileUi.Classes.Root, MobileUi.Classes.Page);
        // The tool carries its own copy of the mobile styles, so it renders correctly on a desktop
        // host that never loads the mobile theme.
        MobileUi.EnsureMobileTheme(this);
        _pageRoot = page;
        Content = new ThemeVariantScope
        {
            RequestedThemeVariant = string.Equals(context.Theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light,
            Child = page
        };

        // Dropping files onto the conversation is the desktop half of the same feature set.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
        AddHandler(DragDrop.DropEvent, (_, e) => OnDrop(e));

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !TryHandleBack()) return;
            e.Handled = true;
        };
        ApplyViewport(SidePaddingWide);
        Sync();
    }

    private Control BuildHeader()
    {
        _headerTitle.FontSize = 18;
        _headerTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _headerTitle.VerticalAlignment = VerticalAlignment.Center;
        _setupButton.Width = 44;
        _setupButton.Height = 44;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,44"), Height = 56 };
        Grid.SetColumn(_headerTitle, 1);
        row.Children.Add(_headerTitle);
        Grid.SetColumn(_setupButton, 2);
        row.Children.Add(_setupButton);
        _chatBack = MobileUi.TextButton("‹");
        _chatBack.Width = 44;
        _chatBack.Height = 44;
        _chatBack.FontSize = 28;
        _chatBack.Padding = new Thickness(0);
        _chatBack.HorizontalContentAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(_chatBack, "返回会话列表");
        _chatBack.Click += (_, _) => ReturnToConversationList();
        row.Children.Add(_chatBack);
        return row;
    }

    private Control BuildComposer()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,Auto"), ColumnSpacing = 6 };
        _attachFiles.Width = 44;
        _attachFiles.Height = 44;
        AutomationProperties.SetName(_attachFiles, "添加附件");
        row.Children.Add(_attachFiles);
        Grid.SetColumn(_input, 1);
        row.Children.Add(_input);
        _send.MinHeight = 44;
        _send.MinWidth = 56;
        _send.Padding = new Thickness(10, 8);
        _send.FontSize = 14;
        Grid.SetColumn(_send, 2);
        row.Children.Add(_send);
        var attachments = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 2, Children = { _attachmentSummary, _attachmentRow } },
            MaxHeight = 96, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _newMessages.Click += (_, _) => { _followThreadEnd = true; _newMessages.IsVisible = false; _threadScroll.ScrollToEnd(); };
        var panel = new StackPanel { Spacing = 4, Children = { _newMessages, attachments, row, _status } };
        _composer = new Border { Child = panel, Padding = new Thickness(8), BorderThickness = new Thickness(0, 1, 0, 0) };
        _composer.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        _composer.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
        return _composer;
    }

    private Border? _composer;
    private Border? _sheetGrabber;
    private Grid? _conversationColumn;
    private Border? _pageRoot;

    private StackPanel BuildEmptyState()
    {
        var connect = MobileUi.QuietButton("连接我的设备");
        connect.Click += (_, _) => ShowSetupSheet();
        _emptyConnect = connect;
        return MobileUi.Stack(10,
            _emptyTitle,
            _emptyNote,
            connect);
    }

    private readonly TextBlock _emptyTitle = MobileUi.EmptyTitle("");
    private readonly TextBlock _emptyNote = MobileUi.Note("");
    private Button _emptyConnect = null!;

    /// <summary>
    /// The empty state only promises what this device can currently do. Without a linked session the
    /// entry stays local, and the page says that instead of claiming every device will see it.
    /// </summary>
    private void SyncEmptyState(AssistantSnapshot state)
    {
        if (state.Identity.Linked)
        {
            _emptyTitle.Text = "会话已在关联设备间同步。";
            _emptyNote.Text = "写一段文字，或用下面的按钮添加文件、图片。另一台设备打开 MPT 后会在同一个会话里看到，点一下就能打开原文件。";
            _emptyConnect.IsVisible = false;
            return;
        }
        _emptyTitle.Text = "内容保存在这台设备";
        _emptyTitle.FontSize = 12;
        _emptyNote.Text = "尚未关联其他设备，内容暂时只在本机可见。";
        _emptyNote.FontSize = 12;
        _emptyConnect.IsVisible = true;
    }

    private static Control CenterCaption(string text)
    {
        var block = MobileUi.Caption(text);
        block.TextAlignment = TextAlignment.Center;
        block.HorizontalAlignment = HorizontalAlignment.Center;
        return block;
    }

    // ---- sheets -----------------------------------------------------------------------------

    private Control BuildSheetHeader()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(_sheetTitle, 0);
        row.Children.Add(_sheetTitle);
        Grid.SetColumn(_sheetClose, 1);
        row.Children.Add(_sheetClose);
        var grabber = MobileUi.With(new Border { HorizontalAlignment = HorizontalAlignment.Center }, MobileUi.Classes.SheetGrabber);
        _sheetGrabber = grabber;
        return MobileUi.Stack(10, grabber, row);
    }

    /// <summary>
    /// The tallest a sheet may grow before its body scrolls instead. It is tall enough for the
    /// connection sheet's 240 px QR symbol plus its actions, while still leaving the conversation
    /// visible above.
    /// </summary>
    private const double SheetMaxHeight = 520;

    private StackPanel BuildSetupSheet()
    {
        var choices = new StackPanel { Spacing = 2 };
        choices.Children.Add(MobileUi.ListRow("MptMobileIconReceive", "空间管理", "清理已接收的文件，保留聊天记录", () => RunAsync(ShowStorageAsync)));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconCloud", "我的网盘", "登录网盘，让这台设备按需使用它中转",
            () => { OpenCloudAccounts(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconDevices", "连接我的设备", "用连接码把另一台设备加入这个会话",
            () => { ShowSheet(_linkSheet); SyncLinkSheet(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconReceive", "接收文件", "允许另一台设备直接发文件到这台设备",
            () => { ShowSheet(_receiveSheet); SyncReceive(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconSend", "设备私聊", "打开与一台设备的独立会话",
            () => { ShowDeviceSheet(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconPulse", "连接诊断", "查看最近的连接检查结果", () =>
        {
            _sheetTitle.Text = "连接诊断";
            _sheetScroll.Content = MobileUi.Stack(8, MobileUi.Body(_core.Snapshot.DiscoveryMessage), MobileUi.Body(_core.Snapshot.RelayMessage));
            OpenSheet();
            return Task.CompletedTask;
        }));
        var advanced = MobileUi.QuietButton("更多设置");
        advanced.Click += (_, _) => _ = OpenAdvancedAsync();
        return MobileUi.Stack(10,
            MobileUi.Note("管理设备连接与文件接收。"),
            MobileUi.ListCard(choices),
            MobileUi.Divider(),
            advanced);
    }

    /// <summary>
    /// The connection sheet puts this device's code first, because that is what the sheet is for: the
    /// symbol has to be fully on screen without scrolling, or scanning it means dragging the sheet.
    /// Everything explanatory is one compact line, the code actions share one row, and the manual
    /// paste path stays below as the fallback step.
    /// </summary>
    private StackPanel BuildLinkSheet()
    {
        _scanButton = MobileUi.SecondaryButton("扫描二维码");
        // The host owns the camera. Without a scanner the action is not shown at all, so there is
        // never a scan button that cannot scan, and manual paste stays available below.
        _scanButton.IsVisible = _context.ScanConnectionCodeAsync is not null;
        _scanButton.Click += async (_, _) => await RunAsync(ScanAsync);
        _copyLink = MobileUi.TextButton("复制连接码");
        _copyLink.Click += async (_, _) => await RunAsync(CopyMyLinkAsync);
        _refreshLink = MobileUi.TextButton("刷新连接码");
        _refreshLink.Click += async (_, _) => await RunAsync(ShowMyLinkAsync);
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        Grid.SetColumn(_refreshLink, 1);
        actions.Children.Add(_refreshLink);
        Grid.SetColumn(_copyLink, 2);
        actions.Children.Add(_copyLink);

        var preview = MobileUi.SecondaryButton("查看连接码内容");
        preview.Click += async (_, _) => await RunAsync(PreviewLinkAsync);
        var import = MobileUi.PrimaryButton("加入这台设备");
        import.Click += async (_, _) => await RunAsync(ImportLinkAsync);

        return MobileUi.Stack(8,
            MobileUi.Caption("加入后可查看此共享会话的历史和后续文字、文件。请只邀请你信任的设备；对方需确认加入。"),
            _linkState,
            _qrHolder,
            actions,
            _scanButton,
            MobileUi.Divider(),
            MobileUi.FieldLabel("对方的连接码"),
            _linkCode,
            _linkPreview,
            preview,
            import);
    }

    private StackPanel BuildReceiveSheet()
    {
        var pairCode = MobileUi.SecondaryButton("显示我的文件互传码");
        pairCode.Click += async (_, _) => await RunAsync(ShowMyPairCodeAsync);
        return MobileUi.Stack(10,
            MobileUi.Note("开启后自动接收消息和不超过 15 MB 的附件；更大的文件需点按下载。"),
            MobileUi.Note("离线时文件会等待你回来；重新打开 MPT 后继续接收。"),
            pairCode,
            _receiveState,
            _receiveToggle,
            _receiveFolder);
    }

    private void ShowSetupSheet()
    {
        _sheetScroll.Content = _setupSheet;
        _sheetTitle.Text = "连接与接收";
        OpenSheet();
    }

    private void ShowSheet(StackPanel body)
    {
        _sheetScroll.Content = body;
        _sheetTitle.Text = ReferenceEquals(body, _linkSheet) ? "连接我的设备"
            : ReferenceEquals(body, _receiveSheet) ? "接收文件"
            : "发给设备";
        OpenSheet();
        // The connection sheet fetches its code as part of opening. The fetch starts after the sheet is
        // marked open, and it is remembered so an awaiting caller joins it rather than asking twice.
        if (ReferenceEquals(body, _linkSheet) && _pendingLinkExport is null)
            _pendingLinkExport = ShowMyLinkAsync();
    }

    /// <summary>Opens the device picker for the current composer content. Used by the page and tests.</summary>
    internal void OpenDevicePicker() => ShowDeviceSheet();

    /// <summary>Opens the connection sheet. Used by the page and tests.</summary>
    internal void OpenSetup() => ShowSetupSheet();

    /// <summary>Opens the "connect my devices" sheet directly.</summary>
    internal void OpenLinkSheet()
    {
        ShowSheet(_linkSheet);
        SyncLinkSheet();
    }

    /// <summary>Opens the connection sheet and resolves once its on-open code fetch has finished.</summary>
    internal async Task OpenLinkSheetForTestAsync()
    {
        OpenLinkSheet();
        if (_pendingLinkExport is { } pending) await pending;
    }

    private Task? _pendingLinkExport;

    /// <summary>The open sheet's title, so a test can tell which sheet is showing.</summary>
    internal string SheetTitle => _sheetTitle.Text ?? "";

    /// <summary>The conversation thread panel, so a test can act on an entry's own actions.</summary>
    internal StackPanel ThreadPanel => _thread;

    /// <summary>The thread's scroll viewer, so a test can assert the follow/restore position it owns.</summary>
    internal ScrollViewer ThreadScroll => _threadScroll;

    /// <summary>The sheet host, so a test can act on the management rows inside it.</summary>
    internal Border SheetHost => _sheetHost;

    /// <summary>The composer's own send, so a test can drive it without pointer routing.</summary>
    internal Task SendFromComposerAsync(string? targetDeviceId = null) => SendAsync(targetDeviceId ?? _targetDeviceId);

    /// <summary>The scan action, so a test can exercise it without pointer routing.</summary>
    internal Task ScanForTestAsync() => ScanAsync();

    /// <summary>The confirm action of the connection sheet, driven directly by a test.</summary>
    internal Task ImportLinkForTestAsync() => ImportLinkAsync();

    /// <summary>The "show my connection code" action, driven directly by a test.</summary>
    internal Task ExportLinkForTestAsync() => ShowMyLinkAsync();

    /// <summary>The scroll viewport of the open sheet, so a test can prove the symbol fits un-scrolled.</summary>
    internal ScrollViewer SheetScroll => _sheetScroll;

    /// <summary>The code currently in the connection sheet, empty once it was cleared.</summary>
    internal string LinkCodeText => _linkCode.Text ?? "";

    /// <summary>The decoded preview currently shown, empty once it was cleared.</summary>
    internal string LinkPreviewText => _linkPreview.Text ?? "";

    /// <summary>The rendered QR value, null once the sheet closed.</summary>
    internal string? QrValue => _qrValue?.Value;

    /// <summary>The created QR control, null before the user asks for the code.</summary>
    internal MptQrCode? QrSymbol => _qrValue;

    /// <summary>The QR symbol's size, from the shared control's own default.</summary>
    internal double QrSize => _qrValue?.Width ?? MptQrCode.RecommendedDisplaySize;

    /// <summary>True when this page loaded the mobile theme into its own styles.</summary>
    internal bool HasScopedMobileTheme => Styles.OfType<StyleInclude>().Any(include =>
            include.Source?.AbsolutePath.Contains("MptMobileTheme.axaml", StringComparison.Ordinal) == true);

    /// <summary>The bounded conversation column's measured size, for the desktop layout assertions.</summary>
    internal Rect ConversationColumnBounds => _conversationColumn?.Bounds ?? default;

    /// <summary>Where the conversation column starts inside a host window.</summary>
    internal Point? ConversationOriginIn(Visual host) =>
        _conversationColumn?.TranslatePoint(default, host);

    /// <summary>What the QR region currently hosts, so a test can prove the symbol replaced the hint.</summary>
    internal Control? QrHolderChild => _qrHolder.Child;

    /// <summary>Chooses a conversation; forwarding still requires a separate confirmation.</summary>
    private void ShowDeviceSheet()
    {
        _sheetTitle.Text = "发给谁";
        _sheetScroll.Content = _deviceSheetBody;
        _core.PickerOpen = true;
        RenderTargets(_core.Snapshot);
        OpenSheet();
        _ = RunAsync(() => _core.DiscoverAsync());
    }

    private void OpenSheet()
    {
        _sheetOpen = true;
        _sheetScrim.IsVisible = true;
    }

    internal void CloseSheet()
    {
        if (!_sheetOpen) return;
        CloseCloudAccounts();
        _sheetOpen = false;
        _pendingForward = null;
        _sheetScrim.IsVisible = false;
        // A bounded discovery run belongs to the open picker; closing the sheet ends it and marks the
        // picker closed, so a late answer cannot publish a stale list or an error.
        _core.PickerOpen = false;
        // Credential work belongs to the open sheet: a scan or preview that lands after closing must
        // not reveal, copy or import anything.
        CancelCredentialWork();
        ClearCredentialSurface();
        _pendingLinkExport = null;
    }

    /// <summary>Cancels this sheet's credential round-trips and re-arms the lifetime for the next open.</summary>
    private void CancelCredentialWork()
    {
        _lifetime.Cancel();
        _lifetime = new CancellationTokenSource();
    }

    internal bool IsSheetOpen => _sheetOpen;

    /// <summary>Number of files waiting in the composer. Exposed for tests and for the Shell's badge.</summary>
    internal int AttachmentCount => _attachments.Count;

    /// <summary>
    /// Back / Escape: close the sheet first, then hand the request to the host. The conversation is
    /// the root of this tool, so it declines rather than navigating away from the user's own draft.
    /// </summary>
    public bool TryHandleBack()
    {
        if (!IsEffectivelyVisible || TopLevel.GetTopLevel(this) is null) return false;
        if (_sheetOpen && _cloudAccounts?.TryHandleBack() == true) return true;
        if (_sheetOpen) { DismissSheet(); return true; }
        if (_chatOpen && _viewport < PhoneWidth) { ReturnToConversationList(); return true; }
        return false;
    }

    /// <summary>Applies the plan's viewport rule: 22 dp side padding, 18 dp at 320 dp.</summary>
    public void ApplyViewport(double width)
    {
        if (width > 0) _viewport = width;
        if (_messageRows.Count > 0) RenderCompactThread(CurrentConversationSnapshot());
        var desktop = _viewport >= PhoneWidth;
        if (_pageRoot is not null)
        {
            if (desktop) _pageRoot.Padding = new Thickness(0);
            else _pageRoot.Padding = new Thickness(0);
        }
        if (_sheetGrabber is not null) _sheetGrabber.IsVisible = !desktop;
        var side = _viewport <= 340 ? SidePaddingNarrow : SidePaddingWide;
        if (_viewport <= 340) _sheetHost.Padding = new Thickness(16, 10, 16, 30);
        else _sheetHost.ClearValue(Border.PaddingProperty);
        // The sheet owns its own inset: a phone window already has a margin, so a second wide one
        // would leave the device tiles too little room to sit two across.
        _sheetInner.Margin = new Thickness(_viewport <= 340 ? 8 : 10, 0, _viewport <= 340 ? 8 : 10, 20);
        // The sheet host owns the outer bound; the scroll gets what is left under the header.
        _sheetScroll.MaxHeight = Math.Max(160, SheetMaxHeight - 96);
        _thread.Margin = new Thickness(side, 8, side, 8);
        _pendingRequests.Margin = new Thickness(side, 8, side, 0);
        _emptyState.Margin = new Thickness(side, 0, side, 0);
        if (_composer is not null) _composer.Padding = new Thickness(8);
        // A desktop window is wide, so the conversation keeps a readable column and the sheet becomes a
        // bounded dialog rather than a full-width panel.
        if (_conversationColumn is not null)
        {
            _conversationColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
            _conversationColumn.Width = double.NaN;
            _conversationColumn.MaxWidth = double.PositiveInfinity;
        }
        _sheetHost.MaxWidth = _viewport < PhoneWidth ? double.PositiveInfinity : DialogMaxWidth;
        _sheetHost.HorizontalAlignment = _viewport < PhoneWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        _sheetHost.VerticalAlignment = _viewport < PhoneWidth ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        _sheetHost.CornerRadius = _viewport < PhoneWidth ? new CornerRadius(24, 24, 0, 0) : new CornerRadius(24);
        UpdateNavigationLayout();
        // The composer is never stacked by the shared adaptive layout: stacking a four-column row
        // pushes the text field off screen. Instead the icons keep their 44 dp target, the field
        // takes the remaining width, and the send action moves to its own full-width line on a
        // narrow phone so a long draft still has room.
        // The composer layout is fixed now; only the side padding follows the viewport.
    }

    /// <summary>The width below which the tool uses its phone presentation.</summary>
    internal const double PhoneWidth = 640;

    /// <summary>The widest a desktop dialog grows, so it reads as a dialog and not as a page.</summary>
    internal const double DialogMaxWidth = 520;

    // ---- state projection -------------------------------------------------------------------

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        // A fresh page gets a fresh lifetime: the previous one was cancelled on detach.
        if (_lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        _ = EnsureDraftLoadedAsync();
        _core.Changed += Sync;
        _legacy.Changed += Sync;
        _followThreadEnd = true;
        Sync();
    }

    /// <summary>
    /// Leaves the page. Anything this page started is cancelled here — discovery and a camera scan —
    /// while content already handed to the module keeps running: closing the page is not a cancel.
    /// </summary>
    internal void Detach()
    {
        if (!_attached) return;
        CloseCloudAccounts();
        _attached = false;
        _draftSaveTimer?.Stop();
        _ = SaveDraftAsync();
        _core.Changed -= Sync;
        _legacy.Changed -= Sync;
        _core.CancelDiscovery();
        _lifetime.Cancel();
        foreach (var image in _imagePreviews.Values) image.Dispose();
        _imagePreviews.Clear();
        _messageRows.Clear();
        _thread.Children.Clear();
        // Credentials never survive leaving the page: a late scan or preview callback is void.
        ClearCredentialSurface();
    }

    /// <summary>
    /// Drops the connection-code text, its preview and the rendered QR value. Called when the sheet
    /// closes and when the page detaches, so a credential cannot reappear or be acted on later.
    /// </summary>
    private void ClearCredentialSurface()
    {
        _linkCode.Text = "";
        _linkPreview.Text = "";
        _pairCode.Text = "";
        _pairPreview.Text = "";
        _previewedPairCode = null;
        if (_sheetTitle.Text == "我的文件互传码") _sheetScroll.Content = null;
        if (_qrValue is not null) _qrValue.Value = null;
    }

    /// <summary>True when the thread has nothing left below the current offset.</summary>
    private bool AtThreadEnd() =>
        _threadScroll.Extent.Height <= _threadScroll.Viewport.Height + 1 ||
        _threadScroll.Offset.Y >= _threadScroll.Extent.Height - _threadScroll.Viewport.Height - 1;

    /// <summary>
    /// Performs the follow-the-newest work outside the layout pass that asked for it. Both writes --
    /// hiding the "new messages" cue (it lives in the composer row, so it changes this viewport) and
    /// moving the offset -- are layout inputs of the ScrollViewer, and a ScrollChanged notification is
    /// raised from inside its arrange. One posted job per layout pass keeps that from re-entering.
    /// </summary>
    private void QueueFollowThreadEnd()
    {
        if (_followQueued) return;
        _followQueued = true;
        _followQueueOffset = _threadScroll.Offset.Y;
        Dispatcher.UIThread.Post(
            () =>
            {
                _followQueued = false;
                if (!_attached || !_followThreadEnd) return;
                // Only a moved offset means the view itself moved while this job was pending, and only
                // then does the reader's own position win. Content that grew (an appended entry, receipt
                // wrapping, a finished image decode) or a viewport that changed (the keyboard) moves the
                // end, not the reader, and must keep following exactly as it did when this ran inline.
                if (_threadScroll.Offset.Y != _followQueueOffset && !AtThreadEnd()) return;
                if (_hideNewMessagesCue)
                {
                    _hideNewMessagesCue = false;
                    _newMessages.IsVisible = false;
                }
                // Write the finite end instead of ScrollToEnd's open-ended target, and only when the
                // view is really not there: a follow that is already at the end must not touch the
                // ScrollViewer's offset at all.
                var end = Math.Max(0, _threadScroll.Extent.Height - _threadScroll.Viewport.Height);
                if (Math.Abs(_threadScroll.Offset.Y - end) > 0.5) _threadScroll.Offset = new Vector(0, end);
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Re-renders from the module snapshot. The composer is never rebuilt, so text the user is still
    /// typing survives an inspect or a sync arriving mid-sentence; only its enabled state changes.
    /// </summary>
    internal void Sync()
    {
        // A refresh must not fight the user's cursor: remember the caret before any relayout.
        var caret = _input.CaretIndex;
        var state = CurrentConversationSnapshot();
        RefreshConversationNavigation();
        MarkVisibleConversationRead();
        if (state.Items.Count != _lastItemCount)
        {
            _lastItemCount = state.Items.Count;
            if (_lastRenderedCount < 0) _followThreadEnd = true;
            else if (!_followThreadEnd) _newMessages.IsVisible = true;
            _lastRenderedCount = state.Items.Count;
        }

        _headerTitle.Text = ActiveConversationKey == "history" ? "历史记录" : _targetName;
        _headerState.Text = state.Identity.Linked
            ? $"{state.Identity.DisplayName} · 已连接"
            : state.Identity.Name is { Length: > 0 } name ? name : "只在本机保存";
        _status.Text = FriendlyStatus(state.Status);
        _status.IsVisible = _status.Text.Length > 0;
        _retrySync.IsVisible = false;
        _setupButton.IsVisible = true;

        RenderPendingRequests(state);
        _pendingRequests.IsVisible = ActiveConversationKey == SharedConversationKey;
        RenderThread(state);
        RenderDeviceSheet(state);
        RenderAttachments();
        SyncReceive();

        // A conversation reads from the bottom: the newest entry and the composer should be what the
        // user sees, not the oldest entry scrolled to the top.
        if (_followThreadEnd) _threadScroll.ScrollToEnd();

        _emptyState.IsVisible = !state.Identity.Linked && _targetDeviceId is null && !state.HasPendingRequest;
        SyncEmptyState(state);
        _composer!.IsVisible = ActiveConversationKey == SharedConversationKey || ActiveConversationKey.StartsWith("device:", StringComparison.Ordinal);
        _input.IsEnabled = !_core.IsUnsupported;
        SyncComposer();

        // A module refresh must never move the user's cursor; only an out-of-range caret is fixed.
        if (caret <= (_input.Text ?? "").Length) _input.CaretIndex = caret;
    }

    private bool HasComposerContent => (_input.Text ?? "").Trim().Length > 0 || _attachments.Count > 0;

    private bool _sendDisabled;
    private bool _preparingAttachments;

    /// <summary>
    /// Updates only what the composer's own state decides. It runs on every keystroke, so it must not
    /// touch the text or the caret: the user owns those.
    /// </summary>
    private void SyncComposer()
    {
        _send.IsEnabled = !_core.IsUnsupported && _targetUsable && HasComposerContent && !_sendDisabled && !_preparingAttachments;
        _send.Content = "发送";
        _attachFiles.IsEnabled = !_core.IsUnsupported && !_sendDisabled && !_preparingAttachments;
        _attachImages.IsEnabled = !_core.IsUnsupported && !_sendDisabled;
    }

    private void RenderPendingRequests(AssistantSnapshot state)
    {
        _pendingRequests.Children.Clear();
        _pendingRequests.IsVisible = state.HasPendingRequest;
        foreach (var request in state.PendingRequests)
            _pendingRequests.Children.Add(BuildRequestCard(request));
    }

    /// <summary>
    /// An inbound request is explicit: nothing is received until the user answers, and "remember"
    /// is a separate, opt-in choice rather than the default.
    /// </summary>
    private Control BuildRequestCard(AssistantPendingRequest request)
    {
        var remember = MobileUi.With(new CheckBox { Content = "记住这台设备" }, "MptMobileCheck");
        var accept = MobileUi.PrimaryButton("接收");
        accept.MinHeight = 48;
        accept.Click += async (_, _) => await RunAsync(() => _core.RespondAsync(request.RequestId, true, remember.IsChecked == true));
        var reject = MobileUi.SecondaryButton("拒绝");
        reject.MinHeight = 48;
        reject.Click += async (_, _) => await RunAsync(() => _core.RespondAsync(request.RequestId, false, false));
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        Grid.SetColumn(accept, 0);
        actions.Children.Add(accept);
        Grid.SetColumn(reject, 1);
        actions.Children.Add(reject);
        return MobileUi.Card(MobileUi.Stack(10,
            MobileUi.SectionTitle($"{request.Name} 想发送给你"),
            MobileUi.Body(request.ItemText),
            remember,
            actions));
    }

    private void RenderThread(AssistantSnapshot state) => RenderCompactThread(state);

    private void RenderDeviceSheet(AssistantSnapshot state) => RenderTargets(state);

    private void RenderAttachments()
    {
        _attachmentRow.Children.Clear();
        foreach (var path in _attachments.ToArray())
        {
            var captured = path;
            var name = Path.GetFileName(path);
            var size = File.Exists(path) ? AssistantItem.FormatSize(new FileInfo(path).Length) : "文件不可用";
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,44"), ColumnSpacing = 8 };
            if (Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" && PreviewImage(path, 40) is { } preview) row.Children.Add(preview);
            var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children =
            {
                new TextBlock { Text = name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }, MobileUi.Caption(size)
            }};
            Grid.SetColumn(copy, 1); row.Children.Add(copy);
            var remove = MobileUi.IconButton("MptMobileIconClose", "移除 " + name);
            remove.Width = 44; remove.Height = 44; remove.IsEnabled = !_sendDisabled;
            remove.Click += (_, _) => RemoveAttachment(captured);
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            _attachmentRow.Children.Add(row);
        }
        _attachmentSummary.Text = _attachments.Count == 0 ? "" : $"待发送 {_attachments.Count} 个文件";
        _attachmentSummary.IsVisible = _attachments.Count > 0;
    }

    private void SyncReceive()
    {
        var receiving = _core.Snapshot.Receiving;
        _receiveToggle.Content = receiving ? "停止接收" : "允许接收";
        _receiveFolder.IsVisible = !OperatingSystem.IsAndroid();
        // This is the user's receiving preference, independent of any individual network listener.
        _receiveState.Text = receiving
            ? OperatingSystem.IsAndroid()
                ? "已开启接收。大于 15 MB 的附件会等待你确认下载。"
                : "已开启接收。大于 15 MB 的附件会等待你确认下载。"
            : "已暂停自动接收。";
    }

    private void SyncLinkSheet()
    {
        var state = _core.Snapshot;
        _linkState.Text = state.Identity.Linked
            ? $"已连接：{state.Identity.DisplayName}"
            : "用另一台设备扫描，即可共享你的文件助手。";
    }

    private static string Kind(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return extension.Length is > 0 and <= 4 ? extension : "文件";
    }

    // ---- composer actions -------------------------------------------------------------------

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))) return;
        e.Handled = true;
        if (HasComposerContent && !_sendDisabled) _ = RunAsync(() => SendAsync(_targetDeviceId));
    }

    /// <summary>
    /// Sends the current conversation's draft within its fixed shared or private scope.
    ///
    /// A forward is a different operation: the entry's *content* is resolved first through
    /// <c>assistant.open</c> (which downloads a file the module has not fetched yet) and the composer
    /// is not involved at all — forwarding must not carry the current draft along.
    /// </summary>
    private async Task SendAsync(string? targetDeviceId)
    {
        if (_sendDisabled || !_targetUsable || (ActiveConversationKey != SharedConversationKey && !ActiveConversationKey.StartsWith("device:", StringComparison.Ordinal))) return;

        // Capture what is being sent. Anything the user types or attaches while the module answers
        // belongs to the next message and must survive this send.
        // A forward and a composer send are different operations. Forwarding never carries the draft,
        // and sending never carries a forwarded entry.
        var originConversation = ActiveConversationKey;
        var pending = _pendingForward;
        var text = pending is null ? (_input.Text ?? "").Trim() : "";
        var paths = pending is null ? _attachments.ToArray() : [];
        if (pending is null && text.Length == 0 && paths.Length == 0) return;

        _followThreadEnd = true;
        _newMessages.IsVisible = false;
        _sendDisabled = true;
        SyncComposer();
        try
        {
            if (pending is not null)
            {
                _pendingForward = null;
                await ForwardAsync(pending.Id, targetDeviceId);
                return;
            }

            var accepted = await _core.SendAsync(text, paths, targetDeviceId);
            if (accepted.Count == 0) return;

            await ClearSentDraftAsync(originConversation, text, paths);
            _core.PublishOnUi(_core.Snapshot with { Status = SendStatus(targetDeviceId) });
        }
        finally
        {
            _sendDisabled = false;
            CloseSheet();
            SyncComposer();
        }
    }

    /// <summary>
    /// Resolves one conversation entry to real content and sends that to the chosen target. The
    /// module's own <c>assistant.open</c> is what turns an item id into a text value or a local path,
    /// so the send command never receives an item id in its paths.
    /// </summary>
    private async Task ForwardAsync(string itemId, string? targetDeviceId)
    {
        var opened = await _core.OpenAsync(itemId);
        if (opened.HasText)
        {
            var accepted = await _core.SendAsync(opened.Text, null, targetDeviceId);
            if (accepted.Count > 0) _core.PublishOnUi(_core.Snapshot with { Status = SendStatus(targetDeviceId) });
            return;
        }
        if (!opened.HasPath)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "这条内容还取不到文件，请稍后重试。" });
            return;
        }
        var sent = await _core.SendAsync(null, [opened.Path!], targetDeviceId);
        if (sent.Count > 0) _core.PublishOnUi(_core.Snapshot with { Status = SendStatus(targetDeviceId) });
    }

    /// <summary>
    /// The honest result line. Accepting a send only means the module persisted it locally, so the
    /// wording follows the relay's own state instead of promising that other devices already see it.
    /// </summary>
    private string SendStatus(string? targetDeviceId)
    {
        if (targetDeviceId is { Length: > 0 }) return "";
        return "";
    }

    private async Task PickAsync(bool imagesOnly)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = imagesOnly ? "选择图片" : "选择文件",
            AllowMultiple = true,
            FileTypeFilter = imagesOnly ? [FilePickerFileTypes.ImageAll] : null
        });
        if (picked.Count == 0) return;
        _preparingAttachments = true;
        SyncComposer();
        _core.PublishOnUi(_core.Snapshot with { Status = "正在准备附件…" });
        int added;
        try { added = await AddPickedAsync(picked); }
        finally { _preparingAttachments = false; SyncComposer(); }
        _core.PublishOnUi(_core.Snapshot with { Status = added == 0 ? "没有添加新内容。" : $"已添加 {added} 个，点发送。" });
    }

    /// <summary>Adds real files the user picked. Content URIs are copied into the module's own
    /// outbox-class staging area, because a share grant can expire before the send reads it.</summary>
    internal async Task<int> AddPickedAsync(IReadOnlyList<IStorageFile> files)
    {
        var added = 0;
        foreach (var item in files)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path) { if (AddAttachment(path)) added++; continue; }
            if (await StageAsync(item) is { } staged && AddAttachment(staged)) added++;
        }
        Sync();
        return added;
    }

    internal bool AddAttachment(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (_attachments.Any(existing => string.Equals(existing, path, StringComparison.Ordinal))) return false;
        _attachments.Add(path);
        DraftChanged();
        Sync();
        return true;
    }

    private void RemoveAttachment(string path)
    {
        _attachments.Remove(path);
        DraftChanged();
        // A staged copy is ours to reclaim; a file the user picked is never deleted.
        _legacy.RemoveFile(path);
        Sync();
    }

    private void ClearAttachments() => _attachments.Clear();

    private async Task<string?> StageAsync(IStorageFile item)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(item.Name);
            var staging = Path.Combine(_legacy.OutboxRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var local = Path.Combine(staging, name);
            await using var input = await item.OpenReadAsync();
            await using var output = File.Create(local);
            await input.CopyToAsync(output, _lifetime.Token);
            return local;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "无法读取所选文件：" + ex.Message });
            return null;
        }
    }

    private void OnDrop(DragEventArgs e)
    {
        var added = 0;
        foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
        {
            if (item.TryGetLocalPath() is not { Length: > 0 } path) continue;
            if (Directory.Exists(path)) continue;
            if (AddAttachment(path)) added++;
        }
        _core.PublishOnUi(_core.Snapshot with { Status = added == 0 ? "没有添加新内容。" : $"已添加 {added} 个，点发送。" });
    }

    /// <summary>Paste: the clipboard can carry a file (desktop) or text (both).</summary>
    /// <summary>Paste: the clipboard can carry files (desktop) or text (both).</summary>
    internal async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var files = await clipboard.TryGetFilesAsync();
            var added = 0;
            foreach (var file in files ?? [])
                if (file.TryGetLocalPath() is { Length: > 0 } path && AddAttachment(path)) added++;
            if (added > 0)
            {
                _core.PublishOnUi(_core.Snapshot with { Status = $"已从剪贴板添加 {added} 个文件，点发送。" });
                return;
            }
            var text = await clipboard.TryGetTextAsync();
            if (text is { Length: > 0 })
            {
                // Appending keeps whatever the user had already typed.
                _input.Text = (_input.Text ?? "") + text;
                _input.CaretIndex = (_input.Text ?? "").Length;
                _core.PublishOnUi(_core.Snapshot with { Status = "已粘贴文本，点发送。" });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "无法读取剪贴板。" });
        }
    }

    private async Task OpenItemAsync(AssistantItem item)
    {
        // The module owns downloaded payloads and can recover a received copy deleted outside MPT.
        // A path retained in the current snapshot is not proof that the file still exists.
        var opened = await _core.OpenAsync(item.Id);
        if (opened.HasText) { ShowTextSheet(item.DisplayName, opened.Text!); return; }
        if (!opened.HasPath)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "文件暂时无法打开，请稍后重试。" });
            return;
        }
        await LaunchAsync(opened.Path!);
    }

    private void ShowTextSheet(string title, string text)
    {
        _sheetScroll.Content = MobileUi.Stack(10, MobileUi.Body(text));
        _sheetTitle.Text = title;
        OpenSheet();
    }

    /// <summary>
    /// Opens real local content with the platform viewer. The host's own delegate wins when it
    /// provides one (a mobile host grants the viewer temporary read access that way); a desktop host
    /// falls back to <see cref="TopLevel.Launcher"/>. The page never claims a file was opened when no
    /// viewer ran, so there is no "saved, therefore opened" shortcut.
    /// </summary>
    private async Task LaunchAsync(string path)
    {
        var result = await FileOpenStrategy.OpenAsync(
            _context.OpenFileAsync,
            TopLevel.GetTopLevel(this),
            path,
            _lifetime.Token);
        if (result.Opened) return;
        _core.PublishOnUi(_core.Snapshot with { Status = result.Message });
    }

    // ---- setup actions ----------------------------------------------------------------------

    private async Task ToggleReceiveAsync()
    {
        var start = !_core.Snapshot.Receiving;
        await _legacy.CallAsync(start ? "receive.start" : "receive.stop");
        await _core.RefreshAsync();
        _core.PublishOnUi(_core.Snapshot with { Status = start ? "已开启自动接收。" : "已暂停自动接收。" });
        SyncReceive();
    }

    private async Task PickReceiveDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择收件文件夹", AllowMultiple = false });
        if (folders.Count == 0) return;
        if (folders[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "该位置无法直接写入，请选择设备上的本地文件夹。" });
            return;
        }
        await _legacy.CallAsync("configure", new JsonObject { ["receiveDirectory"] = path });
        await _core.RefreshAsync();
        _core.PublishOnUi(_core.Snapshot with { Status = "收件保存位置已更新。" });
    }

    /// <summary>
    /// Shows this device's connection code, including as a QR symbol. The code is a credential, so it
    /// is only fetched when the user actually opens this sheet and asks for it.
    /// </summary>
    private async Task ShowMyLinkAsync()
    {
        var token = _lifetime.Token;
        var code = await _core.ExportLinkAsync();
        // The user may have closed the sheet while the module was answering; a credential must not
        // appear after that.
        if (token.IsCancellationRequested || !_sheetOpen) return;
        ShowQrSymbol(code);
        _core.PublishOnUi(_core.Snapshot with { Status = "连接码已显示，可在另一台设备上扫描，或用“复制连接码”。" });
    }

    /// <summary>
    /// Renders the symbol at the largest size the open sheet can show without scrolling, capped at the
    /// shared control's recommendation. The symbol carries its own quiet zone, so the full square has
    /// to be inside the visible area for a camera to read it.
    /// </summary>
    private void ShowQrSymbol(string code)
    {
        var side = MptQrCode.RecommendedDisplaySize;
        _qrValue ??= new MptQrCode { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _qrValue.Width = side;
        _qrValue.Height = side;
        _qrValue.Value = code;
        // The placeholder has to give way to the symbol, otherwise the sheet shows the hint that says
        // the code is not displayed yet while the code is already resolved.
        if (!ReferenceEquals(_qrHolder.Child, _qrValue)) _qrHolder.Child = _qrValue;
        _qrHolder.Padding = new Thickness(6);
    }

    /// <summary>Copying is the user's separate, explicit choice; showing the code never copies it.</summary>
    private async Task CopyMyLinkAsync()
    {
        var token = _lifetime.Token;
        var code = await _core.ExportLinkAsync();
        if (token.IsCancellationRequested || !_sheetOpen) return;
        await CopyTextAsync(code);
        _core.PublishOnUi(_core.Snapshot with { Status = "连接码已复制，在另一台设备上导入即可。" });
    }

    /// <summary>
    /// One camera scan through the host's scanner, then the module's preview. A scan never joins on
    /// its own: the preview is shown and the user confirms with 加入这台设备.
    /// </summary>
    private async Task ScanAsync()
    {
        if (_context.ScanConnectionCodeAsync is not { } scan)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "当前设备没有可用的扫码能力，请手动粘贴连接码。" });
            return;
        }
        var token = _lifetime.Token;
        var code = await scan(token);
        // The sheet may have been closed while the camera was open: then nothing is revealed.
        if (token.IsCancellationRequested || !_sheetOpen) return;
        if (code is not { Length: > 0 })
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "已取消扫码。" });
            return;
        }
        // Same path as a pasted code: preview first, import only when the user confirms.
        _linkCode.Text = code;
        await PreviewLinkAsync();
        _core.PublishOnUi(_core.Snapshot with { Status = "已识别连接码，确认后点“加入这台设备”。" });
    }

    private async Task PreviewLinkAsync()
    {
        var token = _lifetime.Token;
        var code = (_linkCode.Text ?? "").Trim();
        var preview = await _core.PreviewLinkAsync(code);
        if (token.IsCancellationRequested || !_sheetOpen) return;
        _linkPreview.Text = preview;
    }

    private async Task ImportLinkAsync()
    {
        await _core.ImportLinkAsync((_linkCode.Text ?? "").Trim());
        _linkCode.Text = "";
        _linkPreview.Text = "";
        SyncLinkSheet();
    }

    /// <summary>
    /// Opens the classic transfer form. It stays reachable for IP/port/WebDAV work and for hosts whose
    /// module has not shipped the assistant commands yet, without being the page's main screen.
    /// </summary>
    private Task OpenAdvancedAsync()
    {
        AdvancedRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task CopyTextAsync(string value)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境不支持剪贴板。");
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(value));
        await clipboard.SetDataAsync(data);
    }

    // ---- activation -------------------------------------------------------------------------

    /// <summary>
    /// A system share waits for a conversation choice before merging into that draft. It never sends.
    /// </summary>
    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        // A share may arrive before the page's initial inspect finishes. Its choices must use the
        // real shared namespace, never capture the temporary "shared" key from an empty snapshot.
        if (_core.Snapshot.Identity.Id.Length == 0) await _core.RefreshAsync();
        await EnsureDraftLoadedAsync();
        var value = (request.ActivationUri ?? "").Trim();
        if (value.StartsWith("mpt://pair/", StringComparison.Ordinal))
        {
            ShowPairSheet();
            _pairCode.Text = value;
            await PreviewPairAsync();
            return true;
        }
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            _sharedFiles.Add(uri.LocalPath);
            ShowConversationChoiceForShare();
            return true;
        }
        // System "share text" arrives as mypowertools://file-assistant?text=…; the attachment form is
        // a file URI above. Both append to the same composer and never send on their own.
        if (TryReadSharedText(value) is { Length: > 0 } shared)
        {
            _sharedTexts.Add(shared);
            ShowConversationChoiceForShare();
            return true;
        }
        if (value.StartsWith("mpt://assistant/", StringComparison.Ordinal))
        {
            // A connection entry only previews: joining stays the user's confirmation.
            _linkCode.Text = value;
            ShowSheet(_linkSheet);
            SyncLinkSheet();
            await PreviewLinkAsync();
            _core.PublishOnUi(_core.Snapshot with { Status = "已收到连接码，确认后点“加入这台设备”。" });
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the text out of the system share URI. Only the documented shape is accepted, so an
    /// unrelated link cannot inject content into the composer.
    /// </summary>
    private static string? TryReadSharedText(string value)
    {
        if (!value.StartsWith("mypowertools://file-assistant", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) continue;
            if (!pair.AsSpan(0, separator).Equals("text", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = pair[(separator + 1)..].Replace('+', ' ');
            try { return Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Runs one user action and reports its failure. It does not serialise actions against each
    /// other: a discovery pass in the picker must not block typing or a send. The composer and each
    /// item guard their own re-entry.
    /// </summary>
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _core.PublishOnUi(_core.Snapshot with { Status = ex.Message }); }
    }
}
