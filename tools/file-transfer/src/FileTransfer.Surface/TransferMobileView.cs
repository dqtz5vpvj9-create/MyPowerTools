using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>
/// The phone presentation of file transfer, following the approved prototype's send flow.
///
/// One step at a time, one primary action per step: choose files → choose a remembered device →
/// progress with cancel → result with retry. Management work (pairing, relay, receive settings) lives
/// in on-demand sheets opened from the header, so a first-run user and a returning user both see a
/// single obvious action instead of a settings form.
///
/// The page owns no transfer state: every value comes from <see cref="TransferCore"/>, which the wide
/// form drives too, so the two presentations cannot disagree. Colour and shape come from the shared
/// MyPowerTools.AvaloniaSdk mobile theme through <c>MptMobile*</c> classes; this file hardcodes none.
/// </summary>
internal sealed partial class TransferMobileView : UserControl, IMptAvaloniaSurfaceActivationHandler
{
    private const double SidePaddingWide = 22;
    private const double SidePaddingNarrow = 18;
    private const double SheetMaxHeight = 560;

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly TransferCore _core;

    // --- step host: every step is rebuilt from fresh controls, so nothing here is reused
    private readonly StackPanel _stepHost = new() { Spacing = 14 };
    private readonly Border _contentCard = MobileUi.Card(new StackPanel { Spacing = 14 });
    private readonly TextBlock _status = MobileUi.Caption("");
    private Button? _headerBack;

    // --- sheets
    private readonly Border _sheetScrim;
    private readonly Border _sheetHost;
    private readonly ScrollViewer _sheetScroll;
    private readonly StackPanel _sheetInner = new() { Spacing = 10 };
    private readonly TextBlock _sheetTitle;
    private readonly Button _sheetClose;
    private readonly StackPanel _pairSheet = null!;
    private readonly StackPanel _relaySheet = null!;
    private readonly StackPanel _receiveSheet = null!;
    private readonly StackPanel _manageSheet = null!;
    private readonly TextBox _pairCode = MobileUi.FieldBox("粘贴电脑上显示的设备连接码");
    private readonly TextBlock _pairPreview = MobileUi.Caption("");
    private readonly TextBox _cloudCode = MobileUi.FieldBox("粘贴电脑上的网盘连接码");
    private readonly TextBlock _cloudPreview = MobileUi.Caption("");
    private readonly TextBox _cloudDirectory = MobileUi.FieldBox("WebDAV 地址，例如 https://…/dav/网盘名/互传文件夹");
    private readonly TextBox _cloudUser = MobileUi.FieldBox("账号（可留空）");
    private readonly TextBox _cloudPassword = MobileUi.FieldBox("密码（留空保留已保存密码）");
    private readonly TextBlock _cloudState = MobileUi.Caption("");
    private readonly TextBlock _receiveState = MobileUi.Caption("");
    private readonly Button _receiveToggle;
    private readonly Button _receiveFolder;

    private double _viewport = 390;
    private bool _attached;
    private bool _sheetOpen;
    private bool _resultShown;

    public TransferMobileView(MptAvaloniaSurfaceContext context, TransferCore core)
    {
        _context = context;
        _core = core;

        // Steps are rebuilt from fresh controls on every render, so no control is re-parented and a
        // half-rendered step can never collide with the previous one. Only the sheet fields below are
        // long-lived, and sheets are opened one at a time.
        _sheetTitle = MobileUi.With(new TextBlock(), MobileUi.Classes.SheetTitle);
        _sheetClose = MobileUi.CloseButton();
        _sheetClose.Click += (_, _) => CloseSheet();
        _receiveToggle = MobileUi.SecondaryButton("开启接收");
        _receiveToggle.Click += async (_, _) => await RunAsync(ToggleReceiveAsync);
        _receiveFolder = MobileUi.QuietButton("选择保存位置");
        _receiveFolder.Click += async (_, _) => await RunAsync(PickReceiveDirectoryAsync);

        // Sheets are built before the header, because the device step links open them.
        _pairSheet = BuildPairSheet();
        _relaySheet = BuildRelaySheet();
        _receiveSheet = BuildReceiveSheet();
        _manageSheet = BuildManageSheet();

        _sheetScroll = new ScrollViewer
        {
            Content = _sheetInner,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _sheetHost = MobileUi.With(new Border { Child = MobileUi.Stack(10, BuildSheetHeader(), _sheetScroll) }, MobileUi.Classes.Sheet);
        _sheetScrim = new Border
        {
            IsVisible = false,
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            Child = _sheetHost
        };
        _sheetScrim.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, _sheetScrim)) CloseSheet(); };

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        layout.Children.Add(header);
        _contentCard.Child = _stepHost;
        var body = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 14, Children = { _contentCard, _status } },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        layout.Children.Add(_sheetScrim);

        MobileUi.With(this, MobileUi.Classes.Root);
        var page = MobileUi.With(new Border { Child = layout }, MobileUi.Classes.Root, MobileUi.Classes.Page);
        MobileUi.EnsureMobileTheme(page);
        // The page follows the host's theme variant, so dark resolves the theme's own dark palette.
        Content = new ThemeVariantScope
        {
            RequestedThemeVariant = string.Equals(context.Theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light,
            Child = page
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !TryHandleBack()) return;
            e.Handled = true;
        };
        ApplyViewport(SidePaddingWide);
        // No render here: Attach() renders once the page is in the tree, so a step is never built
        // twice and no reused control is re-parented before its first tree is detached.
    }

    private Control BuildHeader()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        _headerBack = MobileUi.BackButton("返回");
        _headerBack.Click += (_, _) => TryHandleBack();
        Grid.SetColumn(_headerBack, 0);
        row.Children.Add(_headerBack);
        var title = MobileUi.PageTitle("文件互传");
        title.VerticalAlignment = VerticalAlignment.Center;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var manage = MobileUi.IconButton("MptMobileIconSettings", "连接与接收设置");
        manage.Click += (_, _) => ShowManageSheet();
        Grid.SetColumn(manage, 2);
        row.Children.Add(manage);
        return MobileUi.With(new Border { Child = row }, "MptMobilePageHeader");
    }

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

    // ---- steps ------------------------------------------------------------------------------

    private enum TransferStep
    {
        Files,
        Device,
        Progress,
        Result
    }

    private TransferStep CurrentStep { get; set; } = TransferStep.Files;

    private void SetStep(TransferStep step)
    {
        CurrentStep = step;
        RenderStep();
    }

    /// <summary>Follows the core: a running batch outranks the step the user picked, and a finished
    /// batch shows its result even if nothing moved.</summary>
    private TransferStep StepFor(TransferSnapshot state)
    {
        if (state.Phase == TransferPhase.Transferring) return TransferStep.Progress;
        if (state.HasResult && _resultShown) return TransferStep.Result;
        if (CurrentStep == TransferStep.Progress) return TransferStep.Result;
        return CurrentStep;
    }

    private void RenderStep()
    {
        var state = _core.Snapshot;
        var step = StepFor(state);
        if (_headerBack is not null) _headerBack.IsVisible = step == TransferStep.Device;
        // Every step builds a fresh tree and reuses only controls that are removed with it, so a
        // re-render can never try to add a control that still has a parent.
        _stepHost.Children.Clear();
        switch (step)
        {
            case TransferStep.Files:
                _stepHost.Children.Add(state.Files.Count == 0 ? BuildFilePickerStep() : BuildFileReviewStep(state));
                break;
            case TransferStep.Device:
                _stepHost.Children.Add(BuildDeviceStep(state));
                break;
            case TransferStep.Progress:
                _stepHost.Children.Add(BuildProgressStep(state));
                break;
            case TransferStep.Result:
                _stepHost.Children.Add(BuildResultStep());
                break;
        }
    }

    /// <summary>
    /// Step 1 empty state: the prototype's single "pick a file" action.
    ///
    /// The controls in this step are long-lived fields, so the host is cleared first. A step can be
    /// re-rendered while its previous host still holds the same children (a status change arrives
    /// between construction and first layout), and a control cannot be parented twice.
    /// </summary>
    private Control BuildFilePickerStep()
    {
        var pick = MobileUi.PrimaryButton("选择文件");
        pick.Click += async (_, _) => await PickAsync();
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(MobileUi.PageTitle("想发什么？"));
        panel.Children.Add(MobileUi.Caption("从系统文件或相册里选，也可以在别的应用里分享到 MPT。"));
        panel.Children.Add(pick);
        panel.Children.Add(MobileUi.Note("文件只发给你已配对的设备。"));
        return panel;
    }

    /// <summary>Step 1 with files chosen: review them, then move to the device step.</summary>
    private Control BuildFileReviewStep(TransferSnapshot state)
    {
        var list = new StackPanel { Spacing = 2 };
        foreach (var file in state.Files)
        {
            var captured = file.Path;
            list.Children.Add(MobileUi.FileRow(Kind(file.Name), file.Name,
                $"{TransferCore.Size(file.Length)}{(file.Staged ? " · 已复制到应用待发送" : "")}",
                () => _core.RemoveFile(captured)));
        }
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        var add = MobileUi.SecondaryButton("再选文件");
        add.Click += async (_, _) => await PickAsync();
        Grid.SetColumn(add, 0);
        actions.Children.Add(add);
        var clear = MobileUi.TextButton("清空");
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.Click += (_, _) => _core.ClearFiles();
        Grid.SetColumn(clear, 1);
        actions.Children.Add(clear);

        var cont = MobileUi.PrimaryButton("选择接收设备");
        cont.IsEnabled = state.Files.Count > 0;
        cont.Click += (_, _) => SetStep(TransferStep.Device);
        return MobileUi.Stack(14,
            MobileUi.PageTitle($"{state.Files.Count} 个文件"),
            MobileUi.Caption($"{TransferCore.Size(state.TotalBytes)} · 可以逐个移除，或继续选择接收设备。"),
            MobileUi.ListCard(list),
            actions,
            cont);
    }

    /// <summary>Step 2: the remembered device list plus the one action that starts the transfer.</summary>
    private Control BuildDeviceStep(TransferSnapshot state)
    {
        var rows = new StackPanel { Spacing = 2 };
        foreach (var peer in state.Peers)
        {
            var captured = peer.DeviceId;
            var selected = string.Equals(peer.DeviceId, state.PeerId, StringComparison.Ordinal);
            rows.Children.Add(MobileUi.ListRow("MptMobileIconDesktop", peer.Name, PeerDescription(peer),
                () => { _core.SelectPeer(captured); return Task.CompletedTask; },
                selected ? "已选择" : null));
        }

        var direct = MobileUi.SecondaryButton("设备直连");
        SetActive(direct, state.Route == TransferRoute.Direct);
        direct.Click += (_, _) => _core.SelectRoute(TransferRoute.Direct);
        var relay = MobileUi.SecondaryButton("网盘中转");
        SetActive(relay, state.Route == TransferRoute.Relay);
        relay.IsEnabled = state.RelayConfigured;
        relay.Click += (_, _) => _core.SelectRoute(TransferRoute.Relay);
        var methods = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        Grid.SetColumn(direct, 0);
        methods.Children.Add(direct);
        Grid.SetColumn(relay, 1);
        methods.Children.Add(relay);

        var pairing = MobileUi.SecondaryButton("添加设备");
        pairing.Click += (_, _) => ShowSheet(_pairSheet);
        var send = MobileUi.PrimaryButton(state.Peer is { } target ? $"发送到 {target.Name}" : "发送");
        send.IsEnabled = state.CanSend;
        send.Click += async (_, _) => await RunAsync(() => _core.SendAsync());

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(MobileUi.PageTitle(state.FileLabel));
        body.Children.Add(MobileUi.Caption($"{TransferCore.Size(state.TotalBytes)} · 选择接收设备"));
        body.Children.Add(MobileUi.SectionTitle("发给哪台设备"));
        if (state.Peers.Count > 0) body.Children.Add(MobileUi.ListCard(rows));
        else body.Children.Add(MobileUi.Notice(MobileUi.Caption("还没有配对的设备。先在电脑上打开 MPT 文件互传，复制连接码，再添加到这里。")));
        if (state.Peers.Count > 0 && state.PeerId is null) body.Children.Add(MobileUi.Caption("点一下设备来选择接收方。"));
        body.Children.Add(pairing);
        body.Children.Add(MobileUi.Divider());
        body.Children.Add(MobileUi.FieldLabel("传输方式"));
        body.Children.Add(methods);
        body.Children.Add(MobileUi.Caption(state.Route == TransferRoute.Relay
            ? "文件先上传到中转网盘；发送端无法确认对方是否已领取。"
            : "优先直接传输，接收设备离线时可以改用网盘中转。"));
        body.Children.Add(send);
        return body;
    }

    /// <summary>Step 3: live bytes from the module, with the one action that stops it.</summary>
    private Control BuildProgressStep(TransferSnapshot state)
    {
        var heading = state.Route == TransferRoute.Relay ? "正在存入网盘" : "正在发送";
        var progress = MobileUi.With(new ProgressBar { Minimum = 0, Maximum = 100, Value = state.Progress }, MobileUi.Classes.Progress);
        var percent = MobileUi.With(new TextBlock { Text = state.ProgressText.Length > 0 ? state.ProgressText.Split(' ')[0] : "" }, MobileUi.Classes.CardTitle);
        var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var detail = MobileUi.Caption(state.ProgressText);
        Grid.SetColumn(detail, 0);
        meta.Children.Add(detail);
        Grid.SetColumn(percent, 1);
        meta.Children.Add(percent);

        var cancel = MobileUi.SecondaryButton("取消传输");
        cancel.IsEnabled = state.Busy;
        cancel.Click += async (_, _) => await RunAsync(() => _core.CancelAsync());

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(MobileUi.PageTitle(heading));
        body.Children.Add(MobileUi.CardTitle(state.FileLabel));
        body.Children.Add(progress);
        body.Children.Add(meta);
        body.Children.Add(MobileUi.Notice(MobileUi.Body("传输期间请保持 MPT 运行。取消不会删除已选文件。")));
        body.Children.Add(cancel);
        return body;
    }

    /// <summary>Step 4: the module's own verdict, including every file it reported.</summary>
    private Control BuildResultStep()
    {
        var state = _core.Snapshot;
        var batch = state.Batch;
        var succeeded = state.Phase == TransferPhase.Succeeded;

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(MobileUi.Icon("MptMobileIconCheck",
            succeeded ? [MobileUi.Classes.Icon, MobileUi.Classes.IconSuccessLarge] : [MobileUi.Classes.Icon, MobileUi.Classes.IconAccent]));
        body.Children.Add(MobileUi.PageTitle(batch?.Title ?? (state.Cancelled ? "已取消" : succeeded ? "已完成" : "没有发出去")));
        body.Children.Add(MobileUi.Body(batch?.Detail ?? state.Failure ?? "传输失败，没有送达。可以重试，或换一台设备。"));

        var receipt = new StackPanel { Spacing = 6 };
        foreach (var outcome in batch?.Outcomes ?? [])
            receipt.Children.Add(MobileUi.ReceiptRow(outcome.Name, TransferBatchSummary.StateText(outcome)));
        if (batch is not null)
        {
            receipt.Children.Add(MobileUi.ReceiptRow("文件", $"{batch.Total} 个 · {TransferCore.Size(state.TotalBytes)}"));
            receipt.Children.Add(MobileUi.ReceiptRow("接收设备", batch.Peer.Length > 0 ? batch.Peer : state.Peer?.Name ?? "未选择"));
            receipt.Children.Add(MobileUi.ReceiptRow("传输方式", batch.Route == TransferRoute.Relay ? "网盘中转" : "设备直连"));
        }
        body.Children.Add(MobileUi.ReceiptSurface(receipt));

        // Retry is only offered for files that did not finish; a clean success has nothing to retry.
        if (batch is not null && batch.FailedCount > 0 && _core.HasFailure)
        {
            var retry = MobileUi.PrimaryButton("重试未完成的部分");
            retry.Click += async (_, _) => await RunAsync(() => _core.RetryAsync());
            body.Children.Add(retry);
        }
        else
        {
            var done = MobileUi.PrimaryButton("完成");
            done.Click += (_, _) => { _core.DismissResult(); _core.ClearFiles(); SetStep(TransferStep.Files); };
            body.Children.Add(done);
        }
        if (succeeded)
        {
            var again = MobileUi.SecondaryButton("再发一份");
            again.Click += (_, _) => { _core.DismissResult(); _core.ClearFiles(); SetStep(TransferStep.Files); _ = PickAsync(); };
            body.Children.Add(again);
        }
        return body;
    }

    /// <summary>Applies the plan's viewport rule: 22 dp side padding, 18 dp at 320 dp.</summary>
    public void ApplyViewport(double width)
    {
        if (width > 0) _viewport = width;
        var side = _viewport <= 340 ? SidePaddingNarrow : SidePaddingWide;
        _sheetInner.Margin = new Thickness(side, 0, side, 24);
        // The sheet scrolls inside whatever height is left, so a 320 dp screen never clips content.
        _sheetScroll.MaxHeight = Math.Max(180, Math.Min(SheetMaxHeight, _viewport * 1.15));
    }

    // ---- shared state projection -------------------------------------------------------------

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.Changed += Sync;
        RenderStep();
        Sync();
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.Changed -= Sync;
    }

    /// <summary>Re-renders every control from the shared snapshot. No value is cached in the view.</summary>
    internal void Sync()
    {
        var state = _core.Snapshot;
        _status.Text = state.Status;
        _status.IsVisible = state.Status.Length > 0 && !state.HasResult;

        if (state.HasResult && !_resultShown)
        {
            _resultShown = true;
            // The outcome is the point of the action, so bring it into view once rather than making
            // the user scroll to find out whether the file arrived.
            Dispatcher.UIThread.Post(() => { try { _contentCard.BringIntoView(); } catch (Exception) { } }, DispatcherPriority.Background);
        }
        if (!state.HasResult) _resultShown = false;

        RenderStep();
        SyncReceive();
    }

    private static void SetActive(Button button, bool active) => button.Classes.Set("active", active);

    /// <summary>
    /// Reachability wording comes from the module's own answer. An endpoint that merely has an
    /// address stays "尚未检查"; only a real peer answer becomes "在线".
    /// </summary>
    private static string PeerDescription(TransferPeer peer)
    {
        var address = peer.Address.Length > 0 ? peer.Address + " · " : "";
        return peer.State switch
        {
            "online" => address + "在线",
            "offline" => address + "离线",
            _ => address + "已配对，尚未检查"
        };
    }

    private static string Kind(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return extension.Length is > 0 and <= 4 ? extension : "文件";
    }

    // ---- sheets -----------------------------------------------------------------------------

    private StackPanel BuildPairSheet()
    {
        _pairCode.TextChanged += (_, _) => UpdatePreview(_pairCode, _pairPreview);
        var import = MobileUi.PrimaryButton("添加设备");
        import.Click += async (_, _) => await RunAsync(ImportPairingAsync);
        var copy = MobileUi.SecondaryButton("复制本机连接码");
        copy.Click += async (_, _) => await RunAsync(CopyPairingAsync);

        // Reserved for the platform QR scanner (M7) and the shared MptQrCode display (M1): the code
        // is entered as text today, and this region is where the scan/present pair will live. No
        // button is added here until those capabilities are delivered.
        var codeRegion = MobileUi.Inset(MobileUi.Stack(8,
            MobileUi.FieldLabel("连接码"),
            _pairCode,
            _pairPreview,
            MobileUi.Note("之后可以直接扫电脑上的二维码添加，无需手动输入。")));
        codeRegion.Name = "PairQrRegion";

        return MobileUi.Stack(10,
            MobileUi.Note("在电脑上打开 MPT 文件互传，复制连接码后粘贴到这里。"),
            copy,
            codeRegion,
            import);
    }

    private StackPanel BuildRelaySheet()
    {
        _cloudCode.TextChanged += (_, _) => UpdatePreview(_cloudCode, _cloudPreview);
        var import = MobileUi.PrimaryButton("导入网盘连接码");
        import.Click += async (_, _) => await RunAsync(ImportCloudAsync);
        var test = MobileUi.SecondaryButton("保存并测试");
        test.Click += async (_, _) => await RunAsync(SaveCloudAsync);
        var check = MobileUi.QuietButton("测试网盘连接");
        check.Click += async (_, _) => await RunAsync(() => _core.CheckRelayAsync());
        var export = MobileUi.QuietButton("复制本机网盘连接码");
        export.Click += async (_, _) => await RunAsync(ExportCloudAsync);
        return MobileUi.Stack(10,
            MobileUi.Note("手机不运行 OpenList。先在电脑上连接网盘，再把网盘连接码发到手机导入。"),
            _cloudState,
            _cloudCode,
            _cloudPreview,
            import,
            export,
            MobileUi.Divider(),
            MobileUi.FieldLabel("或直接填写 WebDAV 地址（高级）"),
            _cloudDirectory,
            _cloudUser,
            _cloudPassword,
            test,
            check);
    }

    private StackPanel BuildReceiveSheet() => MobileUi.Stack(10,
        MobileUi.Note("开启后，保持 MPT 运行即可收到直传文件。"),
        _receiveState,
        _receiveToggle,
        _receiveFolder);

    private StackPanel BuildManageSheet()
    {
        var choices = new StackPanel { Spacing = 2 };
        choices.Children.Add(MobileUi.ListRow("MptMobileIconPlus", "添加或管理设备", "用连接码配对另一台设备",
            () => { ShowSheet(_pairSheet); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconCloud", "网盘中转设置", "接收设备离线时先存入网盘",
            () => { ShowSheet(_relaySheet); return Task.CompletedTask; }));
        choices.Children.Add(MobileUi.ListRow("MptMobileIconReceive", "接收与后台", "让别人直接发文件到这台手机",
            () => { ShowSheet(_receiveSheet); return Task.CompletedTask; }));
        return MobileUi.Stack(10, MobileUi.Note("发送之外的管理操作都在这里。"), MobileUi.ListCard(choices));
    }

    private void ShowManageSheet()
    {
        _sheetScroll.Content = _manageSheet;
        _sheetTitle.Text = "连接与接收";
        OpenSheet();
    }

    private void ShowSheet(StackPanel body)
    {
        _sheetScroll.Content = body;
        _sheetTitle.Text = ReferenceEquals(body, _pairSheet) ? "添加设备"
            : ReferenceEquals(body, _relaySheet) ? "网盘中转设置"
            : "接收设置";
        if (ReferenceEquals(body, _pairSheet)) SyncPairSheet();
        if (ReferenceEquals(body, _relaySheet)) SyncRelaySheet();
        if (ReferenceEquals(body, _receiveSheet)) SyncReceive();
        OpenSheet();
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
    }

    /// <summary>True while a sheet owns the page.</summary>
    internal bool IsSheetOpen => _sheetOpen;

    /// <summary>The scrollable step host, exposed so layout tests can measure real padding.</summary>
    internal StackPanel StepHost => _stepHost;

    /// <summary>
    /// Back / Escape, in the order the plan requires: close the sheet first, then step back, then
    /// hand the request back to the host so it can leave the tool.
    /// </summary>
    internal bool TryHandleBack()
    {
        if (_sheetOpen) { CloseSheet(); return true; }
        if (CurrentStep is TransferStep.Device) { SetStep(TransferStep.Files); return true; }
        return false;
    }

    private static void UpdatePreview(TextBox box, TextBlock preview)
    {
        var description = ConnectionCodePreview.Describe(box.Text);
        preview.Text = description ?? "";
        preview.IsVisible = description is not null;
    }

    private void SyncPairSheet() => _pairPreview.IsVisible = _pairPreview.Text is { Length: > 0 };

    private void SyncRelaySheet()
    {
        var state = _core.Snapshot;
        var reachability = state.RelayReachable switch
        {
            true => " · 上次检查可连接",
            false => " · 上次检查连不上",
            _ => " · 尚未检查"
        };
        _cloudState.Text = state.RelayConfigured
            ? $"已配置：{TransferCore.ShortPath(state.RelayDescription)}{reachability}"
            : "还没有网盘。导入电脑上的网盘连接码，或填写 WebDAV 地址。";
        if ((_cloudDirectory.Text ?? "").Length == 0 && state.RelayConfigured) _cloudDirectory.Text = state.RelayDescription;
    }

    private void SyncReceive()
    {
        var receiving = _core.Snapshot.Receiving;
        _receiveToggle.Content = receiving ? "停止接收" : "开启接收";
        _receiveFolder.IsVisible = !OperatingSystem.IsAndroid();
        _receiveState.Text = receiving
            ? OperatingSystem.IsAndroid() ? "接收已开启，收到的文件会发布到系统“下载”目录。" : "接收已开启，文件会保存到收件文件夹。"
            : "接收未开启：别人暂时不能直接发文件到这台手机。";
    }

    // ---- actions -----------------------------------------------------------------------------

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _core.PublishOnUi(_core.Snapshot with { Status = ex.Message }); }
    }

    /// <summary>Real system file picker. Content-URI results are staged into the app's own outbox
    /// first, because a share grant can expire before the transfer reads the file.</summary>
    private async Task PickAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要发送的文件",
            AllowMultiple = true
        });
        if (picked.Count == 0) return;
        var added = 0;
        foreach (var item in picked)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path) { if (_core.AddFile(path)) added++; continue; }
            if (await StageAsync(item) is { } staged && _core.AddStage(staged)) added++;
        }
        _core.PublishOnUi(_core.Snapshot with
        {
            Status = added == 0 ? "没有添加新文件。" : $"已添加 {added} 个文件，接着选择接收设备。"
        });
        if (added > 0) SetStep(TransferStep.Device);
    }

    private async Task<string?> StageAsync(IStorageFile item)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(item.Name);
            var staging = Path.Combine(_core.OutboxRoot, Guid.NewGuid().ToString("N"));
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

    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        var value = (request.ActivationUri ?? "").Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            // The Shell routes a share to one surface, so activation must also land while the phone
            // page is the attached presentation. A share is a real file choice, not a recent list.
            var added = _core.AddFile(uri.LocalPath);
            _core.PublishOnUi(_core.Snapshot with
            {
                Status = added ? $"已从分享添加文件（共 {_core.Files.Count} 个），接着选择接收设备。" : "分享的文件已在待发送列表里。"
            });
            if (added) SetStep(TransferStep.Device);
            return true;
        }
        if (value.StartsWith("mpt://pair/", StringComparison.Ordinal))
        {
            _pairCode.Text = value;
            ShowSheet(_pairSheet);
            _core.PublishOnUi(_core.Snapshot with { Status = "已收到设备连接码，确认后点“添加设备”。" });
            return true;
        }
        if (value.StartsWith("mpt://cloud/", StringComparison.Ordinal))
        {
            _cloudCode.Text = value;
            ShowSheet(_relaySheet);
            _core.PublishOnUi(_core.Snapshot with { Status = "已收到网盘连接码，确认后点“导入连接码”。" });
            return true;
        }
        return false;
    }

    // ---- module work -------------------------------------------------------------------------

    private async Task ToggleReceiveAsync()
    {
        var start = !_core.Snapshot.Receiving;
        await _core.CallAsync(start ? "receive.start" : "receive.stop");
        await _core.RefreshAsync();
        _core.PublishOnUi(_core.Snapshot with { Status = start ? "接收已开启。" : "接收已停止。" });
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
        await _core.CallAsync("configure", new JsonObject { ["receiveDirectory"] = path });
        await _core.RefreshAsync();
        _core.PublishOnUi(_core.Snapshot with { Status = "收件保存路径已更新：" + path });
    }

    private async Task CopyPairingAsync()
    {
        var response = await _core.CallAsync("pairing");
        var code = TransferCore.Str(response, "code") ?? throw new InvalidOperationException("无法生成本机连接码，请先连接 Tailscale 网络。");
        await CopyTextAsync(code);
        _core.PublishOnUi(_core.Snapshot with { Status = "连接码已复制，发给另一台设备导入。" });
    }

    private async Task ImportPairingAsync()
    {
        var code = (_pairCode.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴设备连接码。");
        if (code.StartsWith("mpt://cloud/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是网盘连接码，请在“网盘中转设置”里导入。");
        var result = await _core.CallAsync("pair.import", new JsonObject { ["code"] = code });
        var name = TransferCore.Str(result, "paired");
        _pairCode.Text = "";
        await _core.RefreshAsync();
        var state = _core.Snapshot;
        var match = state.Peers.FirstOrDefault(peer => string.Equals(peer.Name, name, StringComparison.Ordinal));
        if (match is not null) _core.SelectPeer(match.DeviceId);
        CloseSheet();
        SetStep(TransferStep.Device);
        _core.PublishOnUi(_core.Snapshot with { Status = $"设备 {name ?? "已保存"} 已添加，选择它即可发送。" });
    }

    private async Task ImportCloudAsync()
    {
        var code = (_cloudCode.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴网盘连接码。");
        if (code.StartsWith("mpt://pair/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是设备连接码，请在“添加设备”里导入。");
        await _core.CallAsync("cloud.import", new JsonObject { ["code"] = code });
        _cloudCode.Text = "";
        await _core.RefreshAsync();
        SyncRelaySheet();
        _core.PublishOnUi(_core.Snapshot with { Status = "网盘已连接，可以改用网盘中转。" });
    }

    private async Task ExportCloudAsync()
    {
        var response = await _core.CallAsync("cloud.export");
        var code = TransferCore.Str(response, "code") ?? throw new InvalidOperationException("还没有网盘连接码，请先在电脑上连接网盘。");
        await CopyTextAsync(code);
        _core.PublishOnUi(_core.Snapshot with { Status = "网盘连接码已复制，可以在另一台设备导入。" });
    }

    private async Task SaveCloudAsync()
    {
        var directory = (_cloudDirectory.Text ?? "").Trim();
        if (directory.Length == 0) throw new InvalidOperationException("请填写 WebDAV 地址，或直接导入连接码。");
        if (!Uri.TryCreate(directory, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("互传目录请填写 http(s) 地址。");
        var values = new JsonObject { ["webDavUrl"] = uri.AbsoluteUri.TrimEnd('/') };
        if ((_cloudUser.Text ?? "").Trim() is { Length: > 0 } username) values["username"] = username;
        if ((_cloudPassword.Text ?? "").Length > 0) values["password"] = _cloudPassword.Text;
        await _core.CallAsync("configure", values);
        _cloudPassword.Text = "";
        await _core.CheckRelayAsync();
        SyncRelaySheet();
    }

    private async Task CopyTextAsync(string value)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("当前环境不支持剪贴板。");
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(value));
        await clipboard.SetDataAsync(data);
    }
}
