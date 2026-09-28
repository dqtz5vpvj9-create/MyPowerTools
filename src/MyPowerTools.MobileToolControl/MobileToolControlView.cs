using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl;

/// <summary>
/// The 电脑工具 page for phones.
///
/// It lists the imported computers, reads the selected computer's real tool/command catalog through the
/// <c>mobile-tool-control</c> module, presents understandable actions for Input Monitor / ScreenEase /
/// Paste Image, builds a touch form from the real command parameters for everything else, and reports
/// the invocation lifecycle (progress, result, cancel, error, waiting for the computer user to confirm)
/// exactly as the computer returned it.
///
/// The page never contacts a computer itself, never fabricates a status, and never runs a background
/// scan: a refresh is either an explicit user action or a short-lived status read of an invocation the
/// user started, and it stops when that invocation ends or the page is left.
/// </summary>
internal sealed partial class MobileToolControlView :
    UserControl,
    IMptAvaloniaSurfaceActivationHandler,
    IMptAvaloniaSurfaceBackHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly MobileToolControlViewModel _viewModel;

    // header / feedback
    private readonly TextBlock _pageTitle;
    private readonly TextBlock _pageSubtitle;
    private readonly TextBlock _feedbackText;

    // device list page
    private Border? _pageFrame;
    private readonly StackPanel _deviceListPage;
    private readonly Border _emptyCard;
    private readonly StackPanel _deviceRows;
    private readonly Button _refreshButton;
    private readonly Button _importButton;

    // device page
    private readonly StackPanel _devicePage;
    private readonly Button _backButton;
    private readonly TextBlock _deviceName;
    private readonly Border _deviceStatePill;
    private readonly TextBlock _deviceStateText;
    private readonly TextBlock _deviceSubtitle;
    private readonly TextBlock _deviceEndpoint;
    private readonly Button _checkButton;
    private readonly Button _catalogRefreshButton;
    private readonly Button _removeButton;
    private readonly Border _catalogErrorNotice;
    private readonly TextBlock _catalogErrorText;
    private readonly TextBlock _catalogCaption;
    private readonly StackPanel _priorityRows;
    private readonly Border _priorityCard;
    private readonly StackPanel _otherGroupsPanel;
    private readonly Border _otherCard;
    private readonly StackPanel _toolRows;
    private readonly Border _toolsCard;
    private readonly Border _emptyCatalogCard;

    // busy
    private readonly Border _busyPanel;
    private readonly TextBlock _busyText;

    // import sheet
    private readonly Border _importOverlay;
    private readonly TextBox _importCodeBox;
    private readonly Button _importParseButton;
    private readonly Button _importScanButton;
    private readonly Border _importPreviewPanel;
    private readonly TextBlock _importPreviewTitle;
    private readonly SelectableTextBlock _importPreviewDetail;
    private readonly TextBlock _importWarning;
    private readonly TextBlock _importErrorText;
    private readonly Button _confirmImportButton;

    // parameter sheet
    private readonly Border _parameterOverlay;
    private readonly TextBlock _parameterTitle;
    private readonly TextBlock _parameterSubtitle;
    private readonly Border _parameterSafetyNotice;
    private readonly TextBlock _parameterSafetyText;
    private readonly StackPanel _parameterFieldsPanel;
    private readonly Button _runButton;

    // invocation sheet
    private readonly Border _invocationOverlay;
    private readonly TextBlock _invocationCommand;
    private readonly Border _invocationStatePill;
    private readonly TextBlock _invocationStateText;
    private readonly TextBlock _invocationMessage;
    private readonly ProgressBar _invocationProgress;
    private readonly TextBlock _invocationProgressCaption;
    private readonly TextBlock _invocationCancelNote;
    private readonly Border _invocationResultCard;
    private readonly TextBlock _invocationResultTitle;
    private readonly TextBlock _invocationResultDetail;
    private readonly Button _detailsButton;
    private readonly Button _cancelInvocationButton;
    private readonly Button _closeInvocationButton;

    // tool detail sheet
    private readonly Border _toolOverlay;
    private readonly TextBlock _toolSheetTitle;
    private readonly TextBlock _toolSheetDetail;
    private readonly StackPanel _toolSheetRows;
    private readonly Border _toolSheetEmpty;
    private readonly Button _toolSheetCloseButton;

    // technical details
    private readonly Border _detailsOverlay;
    private readonly SelectableTextBlock _detailsText;
    private readonly Button _detailsCloseButton;

    public MobileToolControlView(MptAvaloniaSurfaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _viewModel = new MobileToolControlViewModel(context);
        DataContext = _viewModel;

        _pageTitle = MobileToolControlTheme.PageTitle(_viewModel.PageTitle);
        _pageSubtitle = MobileToolControlTheme.Caption(_viewModel.PageSubtitle);
        _feedbackText = MobileToolControlTheme.Caption("");
        _feedbackText.IsVisible = false;

        _deviceRows = Stack(0);
        _emptyCard = BuildEmptyCard();
        _refreshButton = MobileToolControlTheme.QuietButton("刷新本机记录");
        _refreshButton.Click += async (_, _) => await _viewModel.RefreshDevicesAsync().ConfigureAwait(true);
        _importButton = MobileToolControlTheme.PrimaryButton("导入电脑连接码");
        _importButton.Click += (_, _) => _viewModel.OpenImportSheet();
        _deviceListPage = BuildDeviceListPage();

        _backButton = MobileToolControlTheme.QuietButton("← 返回设备列表");
        _backButton.HorizontalAlignment = HorizontalAlignment.Left;
        _backButton.Click += (_, _) => BackToList();
        _deviceName = MobileToolControlTheme.PageTitle("");
        _deviceStateText = MobileToolControlTheme.Text(MobileToolControlTheme.PillTextClass);
        (_deviceStatePill, _deviceStateText) = Pill("", "muted");
        _deviceSubtitle = MobileToolControlTheme.Caption("");
        // The MptMobileMono class is tuned for the dark command surface, so the address uses the
        // caption colour plus the mono font token instead of an unreadable dark-on-light monospace.
        _deviceEndpoint = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _deviceEndpoint.Classes.Add(MobileToolControlTheme.CaptionClass);
        _deviceEndpoint.Bind(
            TextBlock.FontFamilyProperty,
            new DynamicResourceExtension(MobileToolControlTheme.MonoFontFamilyKey));

        _checkButton = MobileToolControlTheme.SecondaryButton("检查电脑连接");
        _checkButton.Click += async (_, _) => await _viewModel.CheckDeviceAsync().ConfigureAwait(true);
        _catalogRefreshButton = MobileToolControlTheme.SecondaryButton("刷新工具目录");
        _catalogRefreshButton.Click += async (_, _) => await _viewModel.LoadCatalogAsync(refresh: true).ConfigureAwait(true);
        _removeButton = MobileToolControlTheme.QuietButton("移除这台电脑");
        _removeButton.Click += async (_, _) => await _viewModel.RemoveDeviceAsync().ConfigureAwait(true);

        _catalogErrorText = MobileToolControlTheme.Caption("");
        _catalogErrorNotice = Notice("", warning: true);
        MobileToolControlTheme.SetClass(_catalogErrorText, MobileToolControlTheme.WarningTextClass, true);
        _catalogErrorNotice.Child = _catalogErrorText;
        _catalogCaption = MobileToolControlTheme.Caption("");
        _priorityRows = Stack(0);
        _priorityCard = MobileToolControlTheme.Card(MobileToolControlTheme.ListCardClass);
        _priorityCard.Child = _priorityRows;
        _otherGroupsPanel = Stack(10);
        _otherCard = MobileToolControlTheme.Card(MobileToolControlTheme.ListCardClass);
        _otherCard.Child = _otherGroupsPanel;
        _toolRows = Stack(0);
        _toolsCard = MobileToolControlTheme.Card(MobileToolControlTheme.ListCardClass);
        _toolsCard.Child = _toolRows;
        _emptyCatalogCard = BuildEmptyCatalogCard();
        _devicePage = BuildDevicePage();

        _busyText = MobileToolControlTheme.Caption("");
        _busyPanel = BuildBusyPanel();

        (_importOverlay, var importBody) = Sheet("导入电脑连接码", CloseSheets);
        _importCodeBox = Field("粘贴 mpt://control/… 或电脑上显示的连接码", "", multiline: true);
        _importCodeBox.TextChanged += (_, _) => _viewModel.ImportCode = _importCodeBox.Text ?? "";
        _importParseButton = MobileToolControlTheme.SecondaryButton("解析连接码");
        _importParseButton.Click += async (_, _) => await RunCommandAsync(_viewModel.ImportCodeCommand).ConfigureAwait(true);
        // The QR scanner is a host capability; when it is absent the button does not exist and the
        // user still has the paste/parse path.
        _importScanButton = MobileToolControlTheme.SecondaryButton("扫描电脑上的二维码");
        _importScanButton.IsVisible = context.ScanConnectionCodeAsync is not null;
        _importScanButton.Click += async (_, _) => await ScanConnectionCodeAsync().ConfigureAwait(true);
        _importPreviewTitle = MobileToolControlTheme.SectionTitle("");
        _importPreviewDetail = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _importPreviewDetail.Classes.Add(MobileToolControlTheme.CaptionClass);
        var previewStack = Stack(6);
        previewStack.Children.Add(_importPreviewTitle);
        previewStack.Children.Add(_importPreviewDetail);
        _importPreviewPanel = Panel(previewStack);
        _importPreviewPanel.IsVisible = false;
        _importWarning = MobileToolControlTheme.Caption("");
        _importErrorText = MobileToolControlTheme.Caption("");
        _importErrorText.IsVisible = false;
        _confirmImportButton = MobileToolControlTheme.PrimaryButton("确认导入");
        _confirmImportButton.Click += async (_, _) => await RunCommandAsync(_viewModel.ConfirmImportCommand).ConfigureAwait(true);
        BuildImportSheet(importBody);

        (_parameterOverlay, var parameterBody) = Sheet("执行电脑命令", CloseSheets);
        _parameterTitle = MobileToolControlTheme.SectionTitle("");
        _parameterSubtitle = MobileToolControlTheme.Caption("");
        _parameterSafetyText = MobileToolControlTheme.Caption("");
        _parameterSafetyNotice = Notice("");
        _parameterSafetyNotice.Child = _parameterSafetyText;
        _parameterFieldsPanel = Stack(12);
        _runButton = MobileToolControlTheme.PrimaryButton("发送到电脑");
        _runButton.Click += async (_, _) => await RunCommandAsync(_viewModel.RunCommand).ConfigureAwait(true);
        BuildParameterSheet(parameterBody);

        Border invocationOverlay = null!;
        (invocationOverlay, var invocationBody) = Sheet("调用", () => _viewModel.CloseInvocationSheet());
        _invocationOverlay = invocationOverlay;
        _invocationCommand = MobileToolControlTheme.Caption("");
        _invocationStateText = MobileToolControlTheme.Text(MobileToolControlTheme.PillTextClass);
        (_invocationStatePill, _invocationStateText) = Pill("", "accent");
        _invocationMessage = MobileToolControlTheme.Body("");
        _invocationProgress = new ProgressBar { IsIndeterminate = true };
        _invocationProgress.Classes.Add(MobileToolControlTheme.ProgressClass);
        _invocationProgressCaption = MobileToolControlTheme.Caption("");
        _invocationCancelNote = MobileToolControlTheme.Caption("");
        _invocationResultTitle = MobileToolControlTheme.SectionTitle("");
        _invocationResultDetail = MobileToolControlTheme.Caption("");
        var resultStack = Stack(6);
        resultStack.Children.Add(_invocationResultTitle);
        resultStack.Children.Add(_invocationResultDetail);
        _invocationResultCard = Panel(resultStack);
        _invocationResultCard.IsVisible = false;
        _detailsButton = MobileToolControlTheme.QuietButton("查看技术详情");
        _detailsButton.Click += (_, _) => _viewModel.OpenDetails(BuildDetails());
        _cancelInvocationButton = MobileToolControlTheme.SecondaryButton("取消这次调用");
        _cancelInvocationButton.Click += async (_, _) => await RunCommandAsync(_viewModel.CancelInvocationCommand).ConfigureAwait(true);
        _closeInvocationButton = MobileToolControlTheme.PrimaryButton("完成");
        _closeInvocationButton.Click += (_, _) => _viewModel.CloseInvocationSheet();
        BuildInvocationSheet(invocationBody);

        Border toolOverlay = null!;
        (toolOverlay, var toolBody) = Sheet("工具详情", () => _viewModel.CloseToolSheet());
        _toolOverlay = toolOverlay;
        _toolSheetTitle = MobileToolControlTheme.SectionTitle("");
        _toolSheetDetail = MobileToolControlTheme.Caption("");
        _toolSheetRows = Stack(0);
        _toolSheetEmpty = MobileToolControlTheme.Card();
        var emptyStack = Stack(6);
        emptyStack.Children.Add(MobileToolControlTheme.Body("这台电脑没有为这个工具声明命令。"));
        emptyStack.Children.Add(MobileToolControlTheme.Caption(
            "工具仍然存在于电脑目录里；是否授权由电脑端决定，手机不会猜测它的能力。"));
        _toolSheetEmpty.Child = emptyStack;
        _toolSheetCloseButton = MobileToolControlTheme.SecondaryButton("关闭");
        _toolSheetCloseButton.Click += (_, _) => _viewModel.CloseToolSheet();
        BuildToolSheet(toolBody);

        Border detailsOverlay = null!;
        (detailsOverlay, var detailsBody) = Sheet("技术详情", () => _viewModel.CloseDetails());
        _detailsOverlay = detailsOverlay;
        _detailsText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _detailsCloseButton = MobileToolControlTheme.SecondaryButton("关闭");
        _detailsCloseButton.Click += (_, _) => _viewModel.CloseDetails();
        // The raw wire payload belongs on the SDK command surface, where the mono token is readable.
        var detailsSurface = new Border { Child = _detailsText };
        detailsSurface.Classes.Add(MobileToolControlTheme.CommandOutputClass);
        detailsBody.Children.Add(new ScrollViewer
        {
            Content = detailsSurface,
            MaxHeight = 420,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        detailsBody.Children.Add(_detailsCloseButton);

        Content = BuildRoot();

        _viewModel.PropertyChanged += (_, _) => UpdateChrome();
        AttachedToVisualTree += (_, _) =>
        {
            TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnRootKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _viewModel.Activate();
            UpdateChrome();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnRootKeyDown);
            _viewModel.Deactivate();
        };

        UpdateChrome();
    }

    internal MobileToolControlViewModel ViewModel => _viewModel;

    /// <summary>
    /// Page-level back. Android's back key is routed by the host, so the Shell can call this before it
    /// leaves the tool page: <see langword="true"/> means the page consumed the key.
    /// </summary>
    public bool TryHandleBack() => _viewModel.TryHandleBack();

    /// <summary>
    /// External activation. Two link forms are supported:
    /// <list type="bullet">
    /// <item><c>mpt://control/&lt;base64url-json&gt;</c> — a desktop connection code. It is handed to the
    /// existing <c>import.preview</c> and the confirmation sheet is opened; nothing is stored and no
    /// command runs before the user confirms. The code is never logged.</item>
    /// <item><c>mypowertools://device-tool?device=&lt;grantId&gt;[&amp;tool=&lt;toolId&gt;][&amp;command=&lt;commandId&gt;][&amp;run=1]</c> —
    /// locates the imported computer and preselects a tool or command <b>only</b> when the computer's
    /// real catalog offers it.</item>
    /// </list>
    /// An external link never executes anything: <c>run</c> is accepted for compatibility and
    /// deliberately ignored, so a web page or another app cannot trigger an action on the computer.
    /// The user has to press the page's own button.
    /// </summary>
    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TryReadControlCode(request.ActivationUri, out var code))
        {
            await _viewModel.RefreshDevicesAsync(announce: false).ConfigureAwait(true);
            var previewed = await _viewModel.OpenImportFromCodeAsync(code).ConfigureAwait(true);
            // The log line never contains the code or the token.
            _context.Log(new MptSurfaceLogEntry(
                "info",
                previewed
                    ? "已解析电脑工具连接码，等待用户确认导入。"
                    : "电脑工具连接码无法解析，导入面板已显示原因。",
                DateTimeOffset.Now));
            return previewed;
        }

        var parsed = ParseActivationUri(request.ActivationUri);
        if (parsed is null)
        {
            return false;
        }

        var (deviceId, toolId, commandId, _) = parsed.Value;
        // The page may be activated before the first device read finished; the list is local state, so
        // reading it here costs no network call.
        await _viewModel.RefreshDevicesAsync(announce: false).ConfigureAwait(true);
        var device = _viewModel.Devices.FirstOrDefault(item =>
            string.Equals(item.DeviceId, deviceId, StringComparison.Ordinal));
        if (device is null)
        {
            _context.Log(new MptSurfaceLogEntry(
                "warning",
                $"激活目标设备未导入：{deviceId}",
                DateTimeOffset.Now));
            return false;
        }

        await _viewModel.OpenDeviceAsync(device).ConfigureAwait(true);
        if (commandId.Length > 0)
        {
            var row = _viewModel.FindRow(toolId, commandId);
            if (row is not null)
            {
                _viewModel.OpenCommand(row);
                return true;
            }

            _context.Log(new MptSurfaceLogEntry(
                "warning",
                $"电脑目录里没有这个命令，改为打开工具：{commandId}",
                DateTimeOffset.Now));
        }

        if (toolId.Length > 0 && _viewModel.HasTool(toolId))
        {
            // A tool-only activation opens that tool's detail sheet instead of stopping at the catalog.
            _viewModel.OpenTool(toolId);
            return true;
        }

        // Device-only activation: the computer's catalog is now on screen.
        return commandId.Length == 0 && toolId.Length == 0;
    }

    /// <summary>
    /// Reads a connection code with the host's scanner and hands it to the same preview/confirm path a
    /// pasted code uses. The scanner result is never logged, and nothing is stored before the user
    /// confirms.
    /// </summary>
    private async Task ScanConnectionCodeAsync()
    {
        var scan = _context.ScanConnectionCodeAsync;
        if (scan is null)
        {
            _viewModel.ReportImportFailure("这台设备没有可用的扫码能力，请粘贴连接码。");
            return;
        }

        string? code;
        try
        {
            code = await scan(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // The scanner's own error text is shown; the code (if any) is never logged.
            _viewModel.ReportImportFailure("扫码没有完成：" + exception.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            _viewModel.ReportImportFailure("没有识别到连接码，请重试或改为粘贴。");
            return;
        }

        await _viewModel.OpenImportFromCodeAsync(code).ConfigureAwait(true);
        UpdateChrome();
    }

    /// <summary>
    /// Reads a <c>mpt://control/…</c> activation code. The value is returned exactly as it arrived and
    /// is never written to a log, an error message or a command result.
    /// </summary>
    internal static bool TryReadControlCode(string? uri, out string code)
    {
        code = "";
        var text = uri?.Trim() ?? "";
        if (text.Length < MobileToolControlContract.ControlCodePrefix.Length ||
            !text.StartsWith(MobileToolControlContract.ControlCodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        code = text;
        return true;
    }

    internal static (string DeviceId, string ToolId, string CommandId, bool Run)? ParseActivationUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) ||
            !Uri.TryCreate(uri.Trim(), UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, "mypowertools", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Host, "device-tool", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = parsed.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => Uri.UnescapeDataString(pair[1]),
                StringComparer.OrdinalIgnoreCase);

        var device = query.GetValueOrDefault(MobileToolControlContract.ActivationDevice, "");
        if (device.Length == 0)
        {
            return null;
        }

        return (
            device,
            query.GetValueOrDefault(MobileToolControlContract.ActivationTool, ""),
            query.GetValueOrDefault(MobileToolControlContract.ActivationCommand, ""),
            query.GetValueOrDefault(MobileToolControlContract.ActivationRun, "") is "1" or "true");
    }

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        var vm = _viewModel;

        _pageTitle.Text = vm.PageTitle;
        _pageSubtitle.Text = vm.PageSubtitle;
        _feedbackText.Text = vm.FeedbackText;
        _feedbackText.IsVisible = vm.HasFeedback;
        MobileToolControlTheme.ApplyTextTone(_feedbackText, vm.FeedbackTone);

        _deviceListPage.IsVisible = vm.IsDeviceListPageOpen;
        _devicePage.IsVisible = vm.IsDevicePageOpen;

        // device list
        _emptyCard.IsVisible = !vm.HasDevices;
        _deviceRows.IsVisible = vm.HasDevices;
        _refreshButton.IsEnabled = !vm.IsBusy;
        _importButton.IsEnabled = !vm.IsBusy;
        RebuildDeviceRows();

        // device page
        _deviceName.Text = vm.DeviceTitle;
        _deviceStateText.Text = vm.DeviceStateLabel;
        _deviceStatePill.IsVisible = vm.HasSelectedDevice;
        MobileToolControlTheme.StylePill(
            _deviceStatePill,
            _deviceStateText,
            vm.SelectedDevice?.IsReachable == true ? "success" : "muted");
        _deviceSubtitle.Text = vm.DeviceSubtitle;
        _deviceEndpoint.Text = vm.DeviceEndpoint;
        _checkButton.IsEnabled = vm.HasSelectedDevice && !vm.IsBusy;
        _catalogRefreshButton.IsEnabled = vm.HasSelectedDevice && !vm.IsBusy;
        _removeButton.IsEnabled = vm.HasSelectedDevice && !vm.IsBusy;

        _catalogErrorText.Text = vm.CatalogError;
        _catalogErrorNotice.IsVisible = vm.HasCatalogError;
        _catalogCaption.Text = vm.CatalogCaption;
        _catalogCaption.IsVisible = vm.HasCatalog;

        _priorityCard.IsVisible = vm.HasPriorityActions;
        _otherCard.IsVisible = vm.HasOtherCommands;
        _emptyCatalogCard.IsVisible = vm.ShowCatalogEmpty;
        RebuildActionRows();
        RebuildOtherGroups();
        RebuildToolRows();

        // busy
        _busyPanel.IsVisible = vm.IsBusy;
        _busyText.Text = vm.BusyText;

        // import sheet
        _importOverlay.IsVisible = vm.IsImportSheetOpen;
        if (_importCodeBox.Text != vm.ImportCode)
        {
            _importCodeBox.Text = vm.ImportCode;
        }

        _importParseButton.IsEnabled = !vm.IsBusy;
        _importPreviewPanel.IsVisible = vm.HasImportPreview;
        _importPreviewTitle.Text = vm.ImportPreviewTitle;
        _importPreviewDetail.Text = vm.ImportPreviewDetail;
        _importWarning.Text = vm.ImportPreviewWarning;
        _importWarning.IsVisible = vm.HasImportPreview;
        _importErrorText.Text = vm.ImportError;
        _importErrorText.IsVisible = vm.HasImportError;
        MobileToolControlTheme.ApplyTextTone(_importErrorText, "warning");
        _confirmImportButton.IsEnabled = vm.CanConfirmImport;

        // parameter sheet
        _parameterOverlay.IsVisible = vm.IsParameterSheetOpen;
        _parameterTitle.Text = vm.ParameterSheetTitle;
        _parameterSubtitle.Text = vm.ParameterSheetSubtitle;
        _parameterSubtitle.IsVisible = vm.ParameterSheetSubtitle.Length > 0;
        _parameterSafetyText.Text = vm.ParameterSafetyText;
        _parameterSafetyNotice.IsVisible = vm.ParameterSafetyText.Length > 0;
        _runButton.IsEnabled = vm.CanConfirmRun;
        RebuildParameterFields();

        // invocation sheet
        _invocationOverlay.IsVisible = vm.IsInvocationSheetOpen && vm.HasInvocation;
        _invocationCommand.Text = vm.Invocation is null ? "" : CommandTitle(vm.Invocation.CommandId);
        _invocationStateText.Text = vm.InvocationStateText;
        MobileToolControlTheme.StylePill(_invocationStatePill, _invocationStateText, vm.InvocationTone);
        _invocationMessage.Text = vm.InvocationMessage;
        _invocationMessage.IsVisible = vm.InvocationMessage.Length > 0;
        _invocationProgress.IsVisible = vm.Invocation is { Terminal: false };
        _invocationProgressCaption.Text = vm.InvocationProgressText;
        _invocationCancelNote.Text = vm.InvocationCancelNote;
        _invocationCancelNote.IsVisible = vm.HasInvocationCancelNote;
        MobileToolControlTheme.ApplyTextTone(_invocationCancelNote, "warning");
        _invocationResultCard.IsVisible = vm.InvocationResultTitle.Length > 0;
        _invocationResultTitle.Text = vm.InvocationResultTitle;
        _invocationResultDetail.Text = vm.InvocationResultDetail;
        _invocationResultDetail.IsVisible = vm.HasInvocationResultDetail;
        _detailsButton.IsEnabled = vm.HasDetails;
        _cancelInvocationButton.IsEnabled = vm.CanCancelInvocation;
        _cancelInvocationButton.IsVisible = vm.CanCancelInvocation;
        _closeInvocationButton.IsVisible = vm.Invocation?.Terminal ?? true;
        UpdateDetailsPreview();

        // tool detail sheet
        _toolOverlay.IsVisible = vm.IsToolSheetOpen;
        _toolSheetTitle.Text = vm.ToolSheetTitle;
        _toolSheetDetail.Text = vm.ToolSheetDetail;
        _toolSheetEmpty.IsVisible = vm.ShowToolSheetEmpty;
        RebuildToolSheetRows();

        // technical details
        _detailsOverlay.IsVisible = vm.IsDetailsOpen;
        _detailsText.Text = vm.DetailsText;
    }

    private string CommandTitle(string commandId)
    {
        var command = _viewModel.Catalog?.FindCommand(commandId);
        if (command is null)
        {
            return commandId;
        }

        return MobileToolControlSemantics.Describe(command).Action;
    }

    private void UpdateDetailsPreview()
    {
        if (!_viewModel.HasDetails)
        {
            return;
        }

        _detailsText.Text = _viewModel.DetailsText;
    }

    private string BuildDetails()
    {
        var invocation = _viewModel.Invocation;
        if (invocation is null)
        {
            return "";
        }

        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["deviceId"] = invocation.DeviceId,
            ["invocationId"] = invocation.InvocationId,
            ["commandId"] = invocation.CommandId,
            ["state"] = invocation.State,
            ["message"] = invocation.Message,
            ["terminal"] = invocation.Terminal,
            ["cancelAccepted"] = invocation.CancelAccepted,
            ["result"] = invocation.Result.DeepClone()
        };
        return payload.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private void RebuildDeviceRows()
    {
        var signature = string.Join('|', _viewModel.Devices.Select(device =>
            $"{device.DeviceId}:{device.DeviceName}:{device.LastState}:{device.CredentialConfigured}"));
        if (signature == _deviceRowSignature)
        {
            return;
        }

        _deviceRowSignature = signature;
        _deviceRows.Children.Clear();
        foreach (var device in _viewModel.Devices)
        {
            var trailing = device.CredentialConfigured ? device.StateLabel : "凭据缺失";
            var button = Row("▣", device.DeviceName, device.Subtitle, trailing, () => _ = _viewModel.OpenDeviceAsync(device));
            _deviceRows.Children.Add(button);
        }
    }

    private void RebuildActionRows()
    {
        var rows = _viewModel.PriorityActions;
        var signature = string.Join('|', rows.Select(row => $"{row.Command.CommandId}:{row.Command.Allowed}"));
        if (signature == _actionRowSignature)
        {
            return;
        }

        _actionRowSignature = signature;
        _priorityRows.Children.Clear();
        AddActionRows(_priorityRows, rows);
    }

    private void RebuildOtherGroups()
    {
        var groups = _viewModel.OtherGroups;
        var signature = string.Join('|', groups.Select(group =>
            $"{group.ModuleId}:{string.Join(',', group.Actions.Select(action => action.Command.CommandId + action.Command.Allowed))}"));
        if (signature == _otherGroupSignature)
        {
            return;
        }

        _otherGroupSignature = signature;
        _otherGroupsPanel.Children.Clear();
        foreach (var group in groups)
        {
            var panel = Stack(0);
            panel.Children.Add(MobileToolControlTheme.Caption(group.Title));
            AddActionRows(panel, group.Actions);
            _otherGroupsPanel.Children.Add(panel);
        }
    }

    private void RebuildToolRows()
    {
        var tools = _viewModel.Tools;
        var signature = string.Join('|', tools.Select(tool => $"{tool.ToolId}:{tool.State}:{tool.Availability}"));
        if (signature == _toolRowSignature)
        {
            return;
        }

        _toolRowSignature = signature;
        _toolRows.Children.Clear();
        foreach (var tool in tools)
        {
            var title = MobileToolControlSemantics.ToolTitle(tool.ToolId, tool.Title.Length > 0 ? tool.Title : tool.ToolId);
            var purpose = MobileToolControlSemantics.ToolPurpose(tool.ToolId, tool.Description);
            var state = tool.State.Length > 0 ? tool.State : tool.Availability;
            var commands = _viewModel.Catalog?.CommandsFor(tool.ToolId) ?? [];
            var allowedCount = commands.Count(command => command.Allowed);
            var subtitle = commands.Count == 0
                ? purpose
                : $"{purpose} · 已授权 {allowedCount}/{commands.Count} 条命令";
            var toolId = tool.ToolId;
            _toolRows.Children.Add(Row("◆", title, subtitle, state, () => _viewModel.OpenTool(toolId)));
        }
    }

    private void RebuildToolSheetRows()
    {
        var rows = _viewModel.ToolSheetActions;
        var signature = string.Join('|', rows.Select(row => $"{row.Command.CommandId}:{row.Command.Allowed}"));
        if (signature == _toolSheetSignature)
        {
            return;
        }

        _toolSheetSignature = signature;
        _toolSheetRows.Children.Clear();
        AddActionRows(_toolSheetRows, rows);
    }

    private void RebuildParameterFields()
    {
        var fields = _viewModel.ParameterFields;
        var signature = string.Join('|', fields.Select(field => field.Id));
        if (signature == _parameterSignature)
        {
            return;
        }

        _parameterSignature = signature;
        _parameterFieldsPanel.Children.Clear();
        AddFieldList(_parameterFieldsPanel, fields);
    }

    private void OpenCommand(MobileToolCommandItem command)
    {
        var catalog = _viewModel.Catalog;
        var toolTitle = catalog?.Tools.FirstOrDefault(tool =>
            string.Equals(tool.ToolId, command.ModuleId, StringComparison.Ordinal))?.Title ?? command.ModuleId;
        _viewModel.OpenCommand(new MobileToolActionRow(
            command,
            MobileToolControlSemantics.Describe(command).Action,
            command.Subtitle,
            MobileToolControlSemantics.Describe(command).Intent,
            MobileToolControlSemantics.ToolTitle(command.ModuleId, toolTitle)));
    }

    private void BackToList()
    {
        if (_viewModel.TryHandleBack())
        {
            UpdateChrome();
        }
    }

    private void CloseSheets()
    {
        _viewModel.CloseSheets();
        UpdateChrome();
    }

    private async Task RunCommandAsync(System.Windows.Input.ICommand command)
    {
        if (command is MptAsyncRelayCommand relay)
        {
            await relay.ExecuteAsync().ConfigureAwait(true);
            UpdateChrome();
        }
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        if (_viewModel.TryHandleBack())
        {
            args.Handled = true;
            UpdateChrome();
        }
    }


    private string _deviceRowSignature = "";
    private string _actionRowSignature = "";
    private string _otherGroupSignature = "";
    private string _toolRowSignature = "";
    private string _toolSheetSignature = "";
    private string _parameterSignature = "";
}
