using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.AvaloniaSdk.Controls;

namespace FileTransfer.Surface;

/// <summary>
/// The file-assistant page: one shared conversation, plus an AirDrop-style "send to a device" picker.
///
/// The first screen is the conversation itself — a text field at the bottom, attachment and image
/// buttons beside it — because "send to myself" needs no device, no mode and no connection setup.
/// Choosing a device is a second, optional entry point that acts on the current attachment, and the
/// first-time setup work (connect my devices, enable offline receiving) lives in sheets that open
/// only when the user asks for them.
///
/// Colour and shape come from the shared MyPowerTools.AvaloniaSdk mobile theme through
/// <c>MptMobile*</c> classes; this file hardcodes none, so light and dark both resolve from the theme.
/// </summary>
internal sealed partial class AssistantView : UserControl, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
{
    private const double SidePaddingWide = 22;
    private const double SidePaddingNarrow = 18;

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
    private readonly StackPanel _thread = new() { Spacing = 12 };
    private readonly StackPanel _pendingRequests = new() { Spacing = 10 };
    private readonly ScrollViewer _threadScroll;
    private readonly StackPanel _emptyState;

    // --- composer
    private readonly TextBox _input;
    private readonly Button _attachFiles;
    private readonly Button _attachImages;
    private readonly Button _send;
    private readonly Button _sendToDevice;
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

    public AssistantView(MptAvaloniaSurfaceContext context, AssistantCore core, TransferCore legacy)
    {
        _context = context;
        _core = core;
        _legacy = legacy;

        _setupButton = MobileUi.IconButton("MptMobileIconSettings", "连接与接收设置");
        _setupButton.Click += (_, _) => ShowSetupSheet();

        _input = MobileUi.With(new TextBox
        {
            PlaceholderText = "写点文字，或添加文件…",
            // Multi-line is allowed, but Enter sends and Shift+Enter breaks the line: the fast path
            // stays one keystroke and a longer note is still possible.
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120
        }, MobileUi.Classes.Field);
        // The text box handles Enter itself, so the shortcut is registered for handled events too,
        // which is the only way the key reaches this page before the control consumes it.
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        // Enablement follows the real text, not a later module refresh: typing must light up 发送.
        _input.TextChanged += (_, _) => SyncComposer();

        _attachFiles = MobileUi.IconButton("MptMobileIconPlus", "添加文件");
        _attachFiles.Click += async (_, _) => await PickAsync(false);
        _attachImages = MobileUi.IconButton("MptMobileIconImage", "添加图片");
        _attachImages.Click += async (_, _) => await PickAsync(true);
        _send = MobileUi.PrimaryButton("发送");
        _send.Click += async (_, _) => await SendAsync(null);
        _sendToDevice = MobileUi.SecondaryButton("发给设备");
        // AirDrop lives on the composer, one tap away, not inside the settings sheet.
        _sendToDevice.Click += (_, _) => ShowDeviceSheet();
        _retrySync = MobileUi.SecondaryButton("重新同步");
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

        _sheetClose = MobileUi.CloseButton();
        _sheetClose.Click += (_, _) => CloseSheet();
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
        _sheetScrim.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, _sheetScrim)) CloseSheet(); };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        layout.Children.Add(header);
        Grid.SetRow(_threadScroll, 1);
        layout.Children.Add(_threadScroll);
        var composer = BuildComposer();
        Grid.SetRow(composer, 2);
        layout.Children.Add(composer);
        // The sheet overlays the whole page: without the span it would only cover the header row and
        // leave the composer visible and tappable underneath.
        Grid.SetRowSpan(_sheetScrim, 3);
        layout.Children.Add(_sheetScrim);

        MobileUi.With(this, MobileUi.Classes.Root);
        var page = MobileUi.With(new Border { Child = layout }, MobileUi.Classes.Root, MobileUi.Classes.Page);
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
        var copy = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _headerTitle, _headerState }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(copy, 0);
        row.Children.Add(copy);
        Grid.SetColumn(_setupButton, 1);
        row.Children.Add(_setupButton);
        return MobileUi.With(new Border { Child = row }, "MptMobilePageHeader");
    }

    private Control BuildComposer()
    {
        // Two icon buttons then the text field, which owns the remaining width. The actions sit on
        // their own line: a four-column row leaves the field a sliver, and stacking the row through
        // the shared adaptive layout hides the field entirely.
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8 };
        Grid.SetColumn(_attachFiles, 0);
        row.Children.Add(_attachFiles);
        Grid.SetColumn(_attachImages, 1);
        row.Children.Add(_attachImages);
        Grid.SetColumn(_input, 2);
        row.Children.Add(_input);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_send, 0);
        actions.Children.Add(_send);
        Grid.SetColumn(_sendToDevice, 1);
        actions.Children.Add(_sendToDevice);

        var attachments = new StackPanel { Spacing = 2, Children = { _attachmentSummary, _attachmentRow } };
        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                attachments,
                row,
                actions,
                _status,
                _retrySync
            }
        };
        _composer = MobileUi.With(new Border { Child = panel }, "MptMobilePageHeader");
        return _composer;
    }

    private Border? _composer;

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
            _emptyTitle.Text = "发给自己，已连接的设备都能看到。";
            _emptyNote.Text = "写一段文字，或用下面的按钮添加文件、图片。另一台设备打开 MPT 后会在同一个会话里看到，点一下就能打开原文件。";
            _emptyConnect.IsVisible = false;
            return;
        }
        _emptyTitle.Text = "先发给自己，也可以连上你的设备。";
        _emptyNote.Text = state.RelayBlocked
            ? "现在只会保存在这台手机上。想在其他设备上看到，先连接我的设备；内容不会经过第三方服务器。"
            : "现在只会保存在这台手机上。连接我的设备后，另一台设备也能在同一个会话里看到。";
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
        choices.Children.Add(MobileUi.ListRow("MptMobileIconDevices", "连接我的设备", "用连接码把另一台设备加入这个会话",
            () => { ShowSheet(_linkSheet); SyncLinkSheet(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconReceive", "接收文件", "允许另一台设备直接发文件到这台手机",
            () => { ShowSheet(_receiveSheet); SyncReceive(); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconSend", "发给设备", "把待发送的内容直接发给某一台设备",
            () => { ShowDeviceSheet(); return Task.CompletedTask; }));
        var advanced = MobileUi.QuietButton("高级设置（IP、端口、WebDAV、经典传输）");
        advanced.Click += (_, _) => _ = OpenAdvancedAsync();
        return MobileUi.Stack(10,
            MobileUi.Note("日常发送不需要打开这里。"),
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

    private StackPanel BuildReceiveSheet() => MobileUi.Stack(10,
        MobileUi.Note("开启后，另一台设备可以直接把文件发到这台手机；需要保持 MPT 在运行。"),
        MobileUi.Note("这不等同于离线收件：接收文件时本机必须在线并有 MPT 在运行，手机不在线时对方无法留文件。"),
        _receiveState,
        _receiveToggle,
        _receiveFolder);

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

    /// <summary>The sheet host, so a test can act on the management rows inside it.</summary>
    internal Border SheetHost => _sheetHost;

    /// <summary>The composer's own send, so a test can drive it without pointer routing.</summary>
    internal Task SendFromComposerAsync(string? targetDeviceId = null) => SendAsync(targetDeviceId);

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

    /// <summary>What the QR region currently hosts, so a test can prove the symbol replaced the hint.</summary>
    internal Control? QrHolderChild => _qrHolder.Child;

    /// <summary>The AirDrop step: the current attachment, then a row of devices. One tap sends.</summary>
    private void ShowDeviceSheet()
    {
        _deviceSheetBody.Children.Clear();
        _deviceSheetBody.Children.Add(MobileUi.Note(_attachments.Count == 0 && (_input.Text ?? "").Trim().Length == 0
            ? "先写点文字或添加文件，再选择要发送的设备。"
            : "选一台设备，立刻发送。第一次由对方确认接收。"));
        if (_pendingForward is { } pending)
            _deviceSheetBody.Children.Add(MobileUi.Note($"转发“{pending.DisplayName}”到这台设备。"));
        _deviceSheetBody.Children.Add(_deviceSheetHint);
        _deviceGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        _deviceSheetBody.Children.Add(_deviceGrid);
        var refresh = MobileUi.SecondaryButton("重新查找设备");
        refresh.Click += async (_, _) => await RunAsync(() => _core.DiscoverAsync());
        _deviceSheetBody.Children.Add(refresh);
        _sheetScroll.Content = _deviceSheetBody;
        _sheetTitle.Text = "发给设备";
        OpenSheet();
        _core.PickerOpen = true;
        // Every open starts one fresh pass, so the list reflects right now rather than the last visit.
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
        _sheetOpen = false;
        _sheetScrim.IsVisible = false;
        // The pending forward is deliberately kept: a device tap closes the sheet and then sends, so
        // clearing it here would make the send fall back to the composer's own draft.
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
        if (!_sheetOpen) return false;
        CloseSheet();
        return true;
    }

    /// <summary>Applies the plan's viewport rule: 22 dp side padding, 18 dp at 320 dp.</summary>
    public void ApplyViewport(double width)
    {
        if (width > 0) _viewport = width;
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
        if (_composer is not null) _composer.Padding = new Thickness(side, 8, side, 10);
        // The composer is never stacked by the shared adaptive layout: stacking a four-column row
        // pushes the text field off screen. Instead the icons keep their 44 dp target, the field
        // takes the remaining width, and the send action moves to its own full-width line on a
        // narrow phone so a long draft still has room.
        // The composer layout is fixed now; only the side padding follows the viewport.
    }

    /// <summary>The width below which the tool uses its phone presentation.</summary>
    internal const double PhoneWidth = 640;

    // ---- state projection -------------------------------------------------------------------

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        // A fresh page gets a fresh lifetime: the previous one was cancelled on detach.
        if (_lifetime.IsCancellationRequested) _lifetime = new CancellationTokenSource();
        _core.Changed += Sync;
        Sync();
    }

    /// <summary>
    /// Leaves the page. Anything this page started is cancelled here — discovery and a camera scan —
    /// while content already handed to the module keeps running: closing the page is not a cancel.
    /// </summary>
    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.Changed -= Sync;
        _core.CancelDiscovery();
        _lifetime.Cancel();
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
        if (_qrValue is not null) _qrValue.Value = null;
    }

    /// <summary>
    /// Re-renders from the module snapshot. The composer is never rebuilt, so text the user is still
    /// typing survives an inspect or a sync arriving mid-sentence; only its enabled state changes.
    /// </summary>
    internal void Sync()
    {
        // A refresh must not fight the user's cursor: remember the caret before any relayout.
        var caret = _input.CaretIndex;
        var state = _core.Snapshot;

        _headerTitle.Text = state.Title;
        _headerState.Text = state.Identity.Linked
            ? $"{state.Identity.DisplayName} · 已连接"
            : state.Identity.Name is { Length: > 0 } name ? name : "只在本机保存";
        _status.Text = state.Status;
        _status.IsVisible = state.Status.Length > 0;
        _retrySync.IsVisible = state.RelayBlocked;
        _setupButton.IsVisible = true;

        RenderPendingRequests(state);
        RenderThread(state);
        RenderDeviceSheet(state);
        RenderAttachments();
        SyncReceive();

        // A conversation reads from the bottom: the newest entry and the composer should be what the
        // user sees, not the oldest entry scrolled to the top.
        if (state.Items.Count != _lastItemCount)
        {
            _lastItemCount = state.Items.Count;
            Dispatcher.UIThread.Post(() => { try { _threadScroll.UpdateLayout(); _threadScroll.ScrollToEnd(); } catch (Exception) { } }, DispatcherPriority.Background);
        }

        _emptyState.IsVisible = state.IsEmpty && !state.HasPendingRequest;
        SyncEmptyState(state);
        _input.IsEnabled = !_core.IsUnsupported;
        SyncComposer();

        // A module refresh must never move the user's cursor; only an out-of-range caret is fixed.
        if (caret <= (_input.Text ?? "").Length) _input.CaretIndex = caret;
    }

    private bool HasComposerContent => (_input.Text ?? "").Trim().Length > 0 || _attachments.Count > 0;

    private bool _sendDisabled;

    /// <summary>
    /// Updates only what the composer's own state decides. It runs on every keystroke, so it must not
    /// touch the text or the caret: the user owns those.
    /// </summary>
    private void SyncComposer()
    {
        _send.IsEnabled = !_core.IsUnsupported && HasComposerContent && !_sendDisabled;
        _sendToDevice.IsEnabled = !_core.IsUnsupported && !_sendDisabled;
        _send.Content = _attachments.Count > 0 ? $"发送 {_attachments.Count} 个文件" : "发送";
        _attachFiles.IsEnabled = !_core.IsUnsupported && !_sendDisabled;
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

    private void RenderThread(AssistantSnapshot state)
    {
        _thread.Children.Clear();
        foreach (var item in state.Items)
            _thread.Children.Add(BuildItemRow(item, state));
    }

    /// <summary>
    /// One conversation entry. A text entry is a plain bubble; an image or file entry is a card with
    /// its real size, its module-reported state and only the actions that state allows.
    /// </summary>
    private Control BuildItemRow(AssistantItem item, AssistantSnapshot state)
    {
        var meta = new List<string>();
        // "From me" is the common case in a session shared with your own devices, so it is not labelled.
        if (item.SenderName is { Length: > 0 } sender && item.SenderDeviceId != state.Identity.Id) meta.Add("来自 " + sender);
        if (item.CreatedAt is { } created) meta.Add(created.ToLocalTime().ToString("MM-dd HH:mm"));
        if (item.StateText is { Length: > 0 } stateText) meta.Add(stateText);
        if (item.ReceiptText is { Length: > 0 } receipts) meta.Add("已保存到 " + receipts);

        if (item.IsText)
        {
            // A text entry is a bubble, but it still carries the same actions as a file: forwarding is
            // how a message reaches one device instead of the whole session.
            var bubble = new StackPanel { Spacing = 4, Children = { MobileUi.Body(item.DisplayName), MobileUi.Caption(string.Join(" · ", meta)) } };
            var textActions = BuildItemActions(item);
            return MobileUi.Card(MobileUi.Stack(10, MobileUi.Inset(bubble), textActions));
        }

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        var glyph = MobileUi.Icon(item.IsImage ? "MptMobileIconImage" : "MptMobileIconClipboard", [MobileUi.Classes.Icon, MobileUi.Classes.IconAccent]);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(glyph, 0);
        head.Children.Add(glyph);
        var title = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { MobileUi.CardTitle(item.DisplayName), MobileUi.Caption(string.Join(" · ", meta)) }
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        if (item.InFlight)
        {
            var percent = MobileUi.With(new TextBlock { Text = $"{item.Progress:F0}%" }, MobileUi.Classes.Meta);
            percent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(percent, 2);
            head.Children.Add(percent);
        }
        else if (item.CanOpen || item.NeedsDownload)
        {
            // "下载并打开" is honest only when the payload is not on this device yet.
            var open = MobileUi.TextButton(item.NeedsDownload ? "下载" : "打开");
            open.VerticalAlignment = VerticalAlignment.Center;
            open.Click += async (_, _) => await RunAsync(() => OpenItemAsync(item));
            Grid.SetColumn(open, 2);
            head.Children.Add(open);
        }

        var children = new List<Control> { head };
        if (item.InFlight)
        {
            var progress = MobileUi.With(new ProgressBar { Minimum = 0, Maximum = 100, Value = item.Progress }, MobileUi.Classes.Progress);
            children.Add(progress);
        }

        children.Add(BuildItemActions(item));
        return MobileUi.Card(MobileUi.Stack(10, [.. children]));
    }

    /// <summary>
    /// The actions one entry allows: retry only after a failure, cancel only while unfinished, and
    /// forward to one device always. Forwarding carries this entry's content, not the composer's.
    /// </summary>
    private Control BuildItemActions(AssistantItem item)
    {
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (item.CanRetry)
        {
            var retry = MobileUi.TextButton("重试");
            retry.Click += async (_, _) => await RunAsync(() => _core.RetryAsync(item.Id));
            actions.Children.Add(retry);
        }
        if (item.CanCancel)
        {
            var cancel = MobileUi.TextButton("取消");
            cancel.Click += async (_, _) => await RunAsync(() => _core.CancelAsync(item.Id));
            actions.Children.Add(cancel);
        }
        var forward = MobileUi.TextButton("转发到设备");
        forward.Click += (_, _) => { _pendingForward = item; ShowDeviceSheet(); };
        actions.Children.Add(forward);
        return actions;
    }

    private void RenderDeviceSheet(AssistantSnapshot state)
    {
        _deviceGrid.Children.Clear();
        _deviceSheetHint.Text = state.Discovery switch
        {
            AssistantDiscoveryState.Searching => "正在查找同一网络里的设备…",
            AssistantDiscoveryState.Completed when state.Devices.Count == 0 => "没有找到设备。确认对方打开了 MPT，或先用连接码加入。",
            AssistantDiscoveryState.Partial => "查找时间到了，列表里是已经应答的设备。可以再查一次。",
            AssistantDiscoveryState.Unsupported => "这台设备当前无法自动查找，请用连接码添加。",
            AssistantDiscoveryState.Failed => "查找设备失败。可以重试，或先用连接码加入。",
            _ => "点一下设备即可发送。"
        };
        _deviceSheetHint.IsVisible = true;

        foreach (var device in state.Devices)
        {
            var captured = device;
            // A compact avatar tile: icon above the name, about a hundred dp tall, so a 390 dp sheet
            // shows two devices side by side and the whole list is comparable at a glance. No second
            // bordered card around the tile.
            var tile = new StackPanel
            {
                Spacing = 4,
                Width = 92,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    MobileUi.Icon(device.Platform == "android" ? "MptMobileIconPhone" : "MptMobileIconDesktop",
                        [MobileUi.Classes.Icon, MobileUi.Classes.IconDevice, MobileUi.Classes.IconLarge]),
                    MobileUi.With(new TextBlock
                    {
                        Text = device.Name,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    }, MobileUi.Classes.CardTitle),
                    MobileUi.With(new TextBlock
                    {
                        Text = device.StateText,
                        TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    }, MobileUi.Classes.Meta)
                }
            };
            // AirDrop behaviour: choosing the device is the send, not the start of a wizard.
            var button = MobileUi.DeviceTile(tile, () => SendAsync(captured.DeviceId));
            button.Width = 92;
            button.MinHeight = 112;
            button.Margin = new Thickness(0, 0, 8, 8);
            button.Padding = new Thickness(6);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetName(button, "发送到 " + device.Name);
            _deviceGrid.Children.Add(button);
        }
    }

    private void RenderAttachments()
    {
        _attachmentRow.Children.Clear();
        foreach (var path in _attachments.ToArray())
        {
            var captured = path;
            var name = Path.GetFileName(path);
            var size = _legacy.IsStaged(path) ? "已复制到应用" : "";
            _attachmentRow.Children.Add(MobileUi.FileRow(Kind(name), name, size, () => RemoveAttachment(captured)));
        }
        _attachmentSummary.Text = _attachments.Count == 0 ? "" : $"待发送 {_attachments.Count} 个文件";
        _attachmentSummary.IsVisible = _attachments.Count > 0;
    }

    private void SyncReceive()
    {
        var receiving = _core.Snapshot.Receiving;
        _receiveToggle.Content = receiving ? "停止接收" : "允许接收";
        _receiveFolder.IsVisible = !OperatingSystem.IsAndroid();
        // The receiving session is the direct receiver: it accepts files while MPT runs, and it does
        // not make this device reachable while it is offline.
        _receiveState.Text = receiving
            ? OperatingSystem.IsAndroid()
                ? "接收已开启，保持 MPT 运行即可收到文件，收到的文件会发布到系统“下载”目录。"
                : "接收已开启，保持 MPT 运行即可收到文件，文件会保存到收件文件夹。"
            : "接收未开启：另一台设备现在发不过来。";
    }

    private void SyncLinkSheet()
    {
        var state = _core.Snapshot;
        _linkState.Text = state.Identity.Linked
            ? $"已连接：{state.Identity.DisplayName}"
            : state.RelayBlocked ? state.RelayText : "还没有连接其他设备。";
    }

    private static string Kind(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return extension.Length is > 0 and <= 4 ? extension : "文件";
    }

    // ---- composer actions -------------------------------------------------------------------

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        // Shift+Enter inserts a line break: a longer note should not be impossible.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (!HasComposerContent || _sendDisabled) return;
        _ = SendAsync(null);
    }

    /// <summary>
    /// Sends what the composer holds, optionally to one device. With no target this is "send to
    /// myself", so the user never has to pick a device for their own devices to see it.
    ///
    /// A forward is a different operation: the entry's *content* is resolved first through
    /// <c>assistant.open</c> (which downloads a file the module has not fetched yet) and the composer
    /// is not involved at all — forwarding must not carry the current draft along.
    /// </summary>
    private async Task SendAsync(string? targetDeviceId)
    {
        if (_sendDisabled) return;

        // Capture what is being sent. Anything the user types or attaches while the module answers
        // belongs to the next message and must survive this send.
        // A forward and a composer send are different operations. Forwarding never carries the draft,
        // and sending never carries a forwarded entry.
        var pending = _pendingForward;
        var text = pending is null ? (_input.Text ?? "").Trim() : "";
        var paths = pending is null ? _attachments.ToArray() : [];
        if (pending is null && text.Length == 0 && paths.Length == 0) return;

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

            // Only the exact content that was sent is cleared; a draft typed while waiting stays.
            if ((_input.Text ?? "").Trim() == text) _input.Text = "";
            foreach (var path in paths) _attachments.Remove(path);
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
        if (targetDeviceId is { Length: > 0 }) return "已交给模块发送，等待对方确认接收。";
        return _core.Snapshot.Relay switch
        {
            AssistantRelayState.Available => "已加入会话，其他设备打开后会看到。",
            AssistantRelayState.Unavailable => "已在本机排队。中转暂时不可用，恢复后会自动继续。",
            AssistantRelayState.Unconfigured => "已在本机排队。启用中转后才会同步到其他设备。",
            _ => "已在本机排队，中转状态尚未确认。"
        };
    }

    private async Task PickAsync(bool imagesOnly)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = imagesOnly ? "选择图片" : "选择文件",
            AllowMultiple = true
        });
        if (picked.Count == 0) return;
        var added = await AddPickedAsync(picked);
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
        Sync();
        return true;
    }

    private void RemoveAttachment(string path)
    {
        _attachments.Remove(path);
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
            await input.CopyToAsync(output);
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
        if (item.NeedsDownload)
        {
            var opened = await _core.OpenAsync(item.Id);
            if (opened.HasText) { ShowTextSheet(item.DisplayName, opened.Text!); return; }
            if (!opened.HasPath)
            {
                _core.PublishOnUi(_core.Snapshot with { Status = "模块没有返回可打开的文件，请稍后重试。" });
                return;
            }
            await LaunchAsync(opened.Path!);
            return;
        }
        if (item.LocalPath is { Length: > 0 } path) await LaunchAsync(path);
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
        _core.PublishOnUi(_core.Snapshot with { Status = start ? "已允许接收，保持 MPT 运行才能收到文件。" : "已停止接收。" });
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
    /// A system share lands here as a local file: it becomes a pending attachment in the conversation
    /// rather than starting a device wizard, which is the whole point of "share to MPT".
    /// </summary>
    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        var value = (request.ActivationUri ?? "").Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var added = AddAttachment(uri.LocalPath);
            _core.PublishOnUi(_core.Snapshot with
            {
                Status = added ? $"已从分享添加 {_attachments.Count} 个文件，点发送即可。" : "分享的文件已在待发送列表里。"
            });
            return true;
        }
        // System "share text" arrives as mypowertools://file-assistant?text=…; the attachment form is
        // a file URI above. Both append to the same composer and never send on their own.
        if (TryReadSharedText(value) is { Length: > 0 } shared)
        {
            var current = _input.Text ?? "";
            _input.Text = current.Length == 0 ? shared : current + "\n" + shared;
            _input.CaretIndex = _input.Text.Length;
            SyncComposer();
            _core.PublishOnUi(_core.Snapshot with { Status = "已从分享添加文字，点发送即可。" });
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
        if (value.StartsWith("mpt://pair/", StringComparison.Ordinal) || value.StartsWith("mpt://cloud/", StringComparison.Ordinal))
        {
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
