using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl;

/// <summary>One row of the imported-computer list.</summary>
internal sealed record MobileToolDeviceRow(MobileToolDeviceItem Device);

/// <summary>One action row: the command plus its understandable wording.</summary>
internal sealed record MobileToolActionRow(
    MobileToolCommandItem Command,
    string Action,
    string Hint,
    MobileToolIntent Intent,
    string ToolTitle)
{
    public bool IsRead => Intent == MobileToolIntent.Read;

    public bool IsAllowed => Command.Allowed;

    public string Badge => Command.Allowed ? "" : "未授权";
}

/// <summary>A group of commands that are not part of the three priority tools.</summary>
internal sealed record MobileToolCommandGroup(string ModuleId, string Title, IReadOnlyList<MobileToolActionRow> Actions);

/// <summary>How one catalog parameter is rendered and encoded on the wire.</summary>
internal enum MobileToolParameterKind
{
    String,
    Integer,
    Number,
    Boolean,
    /// <summary>An <c>object</c>/<c>array</c> parameter: advanced JSON text, not a default JSON page.</summary>
    Json,
    /// <summary>A parameter whose type embeds its choices (<c>enum:a|b</c> or <c>a|b</c>).</summary>
    Choice
}

/// <summary>
/// One field of the parameter form, built from a real catalog parameter. The value is encoded with
/// the parameter's declared type, so a module that asked for an integer never receives a string.
/// </summary>
internal sealed class MobileToolParameterField
{
    private string _value;
    private string _error = "";

    public MobileToolParameterField(MobileToolParameterItem parameter)
    {
        Parameter = parameter ?? throw new ArgumentNullException(nameof(parameter));
        _value = parameter.DefaultValue;
        BooleanValue = string.Equals(parameter.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
        Kind = Classify(parameter.Type);
        Choices = ReadChoices(parameter.Type);
    }

    public MobileToolParameterItem Parameter { get; }

    public string Id => Parameter.Id;

    public string Label => Parameter.Required ? Parameter.Label + "（必填）" : Parameter.Label;

    public MobileToolParameterKind Kind { get; }

    public IReadOnlyList<string> Choices { get; }

    public string Value
    {
        get => _value;
        set
        {
            var text = value ?? "";
            if (string.Equals(_value, text, StringComparison.Ordinal))
            {
                return;
            }

            _value = text;
            Error = "";
            OnValueChanged?.Invoke();
        }
    }

    /// <summary>Raised when the value changes outside the editor control (prefill, retry, tests).</summary>
    public Action? OnValueChanged { get; set; }

    public bool BooleanValue { get; set; }

    /// <summary>Field-level validation message, shown next to the control; never sent to the computer.</summary>
    public string Error
    {
        get => _error;
        set
        {
            _error = value ?? "";
            OnErrorChanged?.Invoke();
        }
    }

    public bool HasError => Error.Length > 0;

    public Action? OnErrorChanged { get; set; }

    public bool IsBoolean => Kind == MobileToolParameterKind.Boolean;

    public bool IsNumber => Kind is MobileToolParameterKind.Integer or MobileToolParameterKind.Number;

    public bool IsChoice => Kind == MobileToolParameterKind.Choice;

    public bool IsJson => Kind == MobileToolParameterKind.Json;

    public string TypeLabel => Parameter.Type.Length > 0 ? Parameter.Type : "string";

    public string Placeholder => Kind switch
    {
        MobileToolParameterKind.Integer => Parameter.DefaultValue.Length > 0 ? $"整数，默认 {Parameter.DefaultValue}" : "整数",
        MobileToolParameterKind.Number => Parameter.DefaultValue.Length > 0 ? $"数字，默认 {Parameter.DefaultValue}" : "数字",
        MobileToolParameterKind.Json => "JSON（对象或数组）",
        _ => Parameter.DefaultValue.Length > 0 ? $"默认 {Parameter.DefaultValue}" : ""
    };

    /// <summary>
    /// Validates and encodes this field. <see cref="MobileToolParameterKind.Integer"/> becomes a JSON
    /// integer, <see cref="MobileToolParameterKind.Number"/> a JSON number, booleans a JSON boolean and
    /// JSON parameters a parsed object/array. A value that does not match the declared type sets
    /// <see cref="Error"/> and returns <see langword="false"/> so the call is never submitted.
    /// </summary>
    public bool TryEncode(out JsonNode? encoded)
    {
        encoded = null;
        Error = "";
        if (IsBoolean)
        {
            encoded = JsonValue.Create(BooleanValue);
            return true;
        }

        var text = Value.Trim();
        if (text.Length == 0)
        {
            if (!Parameter.Required)
            {
                return true;
            }

            Error = "必填参数不能为空。";
            return false;
        }

        switch (Kind)
        {
            case MobileToolParameterKind.Integer:
                if (!long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture, out var integer))
                {
                    Error = "请填写整数（例如 800），不要把小数或文字发给电脑。";
                    return false;
                }

                encoded = JsonValue.Create(integer);
                return true;
            case MobileToolParameterKind.Number:
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number))
                {
                    Error = "请填写数字（例如 0.5）。";
                    return false;
                }

                encoded = JsonValue.Create(number);
                return true;
            case MobileToolParameterKind.Json:
                try
                {
                    encoded = JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    Error = "请填写合法的 JSON 对象或数组。";
                    return false;
                }

                if (encoded is not (JsonObject or JsonArray))
                {
                    Error = "这个参数需要 JSON 对象或数组。";
                    encoded = null;
                    return false;
                }

                return true;
            default:
                encoded = JsonValue.Create(text);
                return true;
        }
    }

    private static MobileToolParameterKind Classify(string? type)
    {
        var value = (type ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return MobileToolParameterKind.String;
        }

        if (value.Contains("bool", StringComparison.Ordinal))
        {
            return MobileToolParameterKind.Boolean;
        }

        if (value.Contains('|') || value.StartsWith("enum", StringComparison.Ordinal) ||
            value.StartsWith("choice", StringComparison.Ordinal) || value.StartsWith("select", StringComparison.Ordinal))
        {
            return MobileToolParameterKind.Choice;
        }

        if (value.Contains("int", StringComparison.Ordinal) || value.Contains("long", StringComparison.Ordinal))
        {
            return MobileToolParameterKind.Integer;
        }

        if (value.Contains("number", StringComparison.Ordinal) || value.Contains("float", StringComparison.Ordinal) ||
            value.Contains("double", StringComparison.Ordinal) || value.Contains("decimal", StringComparison.Ordinal))
        {
            return MobileToolParameterKind.Number;
        }

        if (value.Contains("object", StringComparison.Ordinal) || value.Contains("array", StringComparison.Ordinal) ||
            value.Contains("json", StringComparison.Ordinal))
        {
            return MobileToolParameterKind.Json;
        }

        return MobileToolParameterKind.String;
    }

    private static IReadOnlyList<string> ReadChoices(string? type)
    {
        var value = type ?? "";
        var separator = value.IndexOf(':');
        var list = separator >= 0 ? value[(separator + 1)..] : value;
        if (!list.Contains('|'))
        {
            return [];
        }

        return list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
/// The 电脑工具 page: imported computers, the computer's real tool and command catalog, understandable
/// actions for Input Monitor / ScreenEase / Paste Image, a parameter form built from the real command
/// parameters, and the invocation lifecycle (progress, result, cancel, error, waiting for the computer
/// user to confirm).
///
/// The page owns no network and no credential: every effect is a module command. It refreshes only
/// while it is visible and only for the invocation the user started, and it stops as soon as that
/// invocation reaches a terminal state or the page is left.
/// </summary>
internal sealed class MobileToolControlViewModel : MptObservableViewModel
{
    private readonly MobileToolControlModuleClient _client;
    private readonly DispatcherTimer? _progressTimer;
    private CancellationTokenSource? _pendingWork;
    // Cancelled when the page is left; every status read is linked to it, so a late answer can never
    // arrive after the page is gone.
    private CancellationTokenSource? _pageLifetime;
    private int _statusReadInFlight;
    private bool _active;

    private IReadOnlyList<MobileToolDeviceItem> _devices = [];
    private MobileToolDeviceItem? _selectedDevice;
    private MobileToolCatalogSnapshot? _catalog;
    private bool _isDevicePageOpen;
    private bool _isBusy;
    private string _busyText = "";
    private string _feedbackText = "";
    private string _feedbackTone = "neutral";
    private string _catalogError = "";
    private bool _isImportSheetOpen;
    private string _importCode = "";
    private MobileToolImportPreview? _importPreview;
    private string _importError = "";
    private bool _isParameterSheetOpen;
    private MobileToolCommandItem? _pendingCommand;
    private IReadOnlyList<MobileToolParameterField> _parameterFields = [];
    private MobileToolInvocationSnapshot? _invocation;
    private bool _isInvocationSheetOpen;
    private bool _isToolSheetOpen;
    private MobileToolToolItem? _toolSheetTool;
    private IReadOnlyList<MobileToolActionRow> _toolSheetActions = [];
    private bool _isDetailsOpen;
    private string _detailsText = "";
    private bool _canCancelInvocation;
    private string _cancelNote = "";

    public MobileToolControlViewModel(MptAvaloniaSurfaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _client = new MobileToolControlModuleClient(context);
        PageTitle = "电脑工具";
        PageSubtitle = "用手机查看并使用已授权电脑上的工具";

        ImportCodeCommand = new MptAsyncRelayCommand(PreviewImportAsync, () => !IsBusy, "tool-control.import.preview");
        ConfirmImportCommand = new MptAsyncRelayCommand(ConfirmImportAsync, () => CanConfirmImport, "tool-control.import.confirm");
        CloseSheetCommand = new MptAsyncRelayCommand(() => { CloseSheets(); return Task.CompletedTask; }, null, "tool-control.close-sheet");
        RunCommand = new MptAsyncRelayCommand(ConfirmRunAsync, () => PendingCommand is not null && !IsBusy, "tool-control.invoke");
        CancelInvocationCommand = new MptAsyncRelayCommand(CancelInvocationAsync, () => CanCancelInvocation, "tool-control.invoke.cancel");
        ShowDetailsCommand = new MptAsyncRelayCommand(
            () => { IsDetailsOpen = true; return Task.CompletedTask; },
            () => DetailsText.Length > 0,
            "tool-control.details");
        CloseDetailsCommand = new MptAsyncRelayCommand(() => { IsDetailsOpen = false; return Task.CompletedTask; }, null, "tool-control.details.close");

        if (Dispatcher.UIThread.CheckAccess())
        {
            _progressTimer = new DispatcherTimer { Interval = MobileToolControlContract.ProgressRefreshInterval };
            _progressTimer.Tick += async (_, _) => await PollInvocationAsync().ConfigureAwait(true);
        }
    }

    // ---------------------------------------------------------------- state

    public string PageTitle { get; }

    public string PageSubtitle { get; }

    public IReadOnlyList<MobileToolDeviceItem> Devices
    {
        get => _devices;
        private set
        {
            if (SetProperty(ref _devices, value))
            {
                OnPropertyChanged(nameof(HasDevices));
                OnPropertyChanged(nameof(ShowEmptyState));
            }
        }
    }

    public bool HasDevices => Devices.Count > 0;

    public bool ShowEmptyState => Devices.Count == 0 && !IsBusy;

    public MobileToolDeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(HasSelectedDevice));
                OnPropertyChanged(nameof(DeviceTitle));
                OnPropertyChanged(nameof(DeviceStateLabel));
                OnPropertyChanged(nameof(DeviceSubtitle));
                OnPropertyChanged(nameof(DeviceEndpoint));
            }
        }
    }

    public bool HasSelectedDevice => SelectedDevice is not null;

    public string DeviceTitle => SelectedDevice?.DeviceName ?? "";

    public string DeviceStateLabel => SelectedDevice?.StateLabel ?? "";

    public string DeviceSubtitle => SelectedDevice?.Subtitle ?? "";

    public string DeviceEndpoint => SelectedDevice?.Endpoint ?? "";

    public bool IsDevicePageOpen
    {
        get => _isDevicePageOpen;
        private set
        {
            if (SetProperty(ref _isDevicePageOpen, value))
            {
                OnPropertyChanged(nameof(IsDeviceListPageOpen));
            }
        }
    }

    public bool IsDeviceListPageOpen => !IsDevicePageOpen;

    public MobileToolCatalogSnapshot? Catalog
    {
        get => _catalog;
        private set
        {
            if (SetProperty(ref _catalog, value))
            {
                OnPropertyChanged(nameof(HasCatalog));
                OnPropertyChanged(nameof(CatalogCaption));
                OnPropertyChanged(nameof(Tools));
                OnPropertyChanged(nameof(PriorityActions));
                OnPropertyChanged(nameof(OtherGroups));
                OnPropertyChanged(nameof(HasPriorityActions));
                OnPropertyChanged(nameof(HasOtherCommands));
                OnPropertyChanged(nameof(ShowCatalogLoading));
                OnPropertyChanged(nameof(ShowCatalogEmpty));
            }
        }
    }

    public bool HasCatalog => Catalog is not null;

    public bool ShowCatalogLoading => IsBusy && Catalog is null;

    public bool ShowCatalogEmpty => Catalog is not null && Catalog.Commands.Count == 0 && Catalog.Tools.Count == 0;

    public string CatalogCaption
    {
        get
        {
            if (Catalog is null)
            {
                return "";
            }

            var device = Catalog.DeviceName.Length > 0 ? Catalog.DeviceName : DeviceTitle;
            var suffix = Catalog.FromCache ? " · 缓存" : "";
            return $"{device} · {Catalog.Commands.Count} 条命令{suffix}";
        }
    }

    public IReadOnlyList<MobileToolToolItem> Tools => Catalog?.Tools ?? [];

    public IReadOnlyList<MobileToolActionRow> PriorityActions
    {
        get
        {
            if (Catalog is null)
            {
                return [];
            }

            var (priority, _) = MobileToolControlSemantics.Split(Catalog.Commands);
            return priority.Select(command => ToRow(command, Catalog)).ToArray();
        }
    }

    public IReadOnlyList<MobileToolCommandGroup> OtherGroups
    {
        get
        {
            if (Catalog is null)
            {
                return [];
            }

            var (_, others) = MobileToolControlSemantics.Split(Catalog.Commands);
            return MobileToolControlSemantics
                .GroupByTool(others, Catalog.Tools)
                .Select(group => new MobileToolCommandGroup(
                    group.ModuleId,
                    MobileToolControlSemantics.ToolTitle(group.ModuleId, group.Title),
                    group.Commands.Select(command => ToRow(command, Catalog)).ToArray()))
                .ToArray();
        }
    }

    public bool HasPriorityActions => PriorityActions.Count > 0;

    public bool HasOtherCommands => OtherGroups.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(BusyText));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowCatalogLoading));
                NotifyCommands();
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    public string FeedbackText
    {
        get => _feedbackText;
        private set
        {
            if (SetProperty(ref _feedbackText, value))
            {
                OnPropertyChanged(nameof(HasFeedback));
            }
        }
    }

    public bool HasFeedback => FeedbackText.Length > 0;

    public string FeedbackTone
    {
        get => _feedbackTone;
        private set => SetProperty(ref _feedbackTone, value);
    }

    public string CatalogError
    {
        get => _catalogError;
        private set
        {
            if (SetProperty(ref _catalogError, value))
            {
                OnPropertyChanged(nameof(HasCatalogError));
            }
        }
    }

    public bool HasCatalogError => CatalogError.Length > 0;

    // ---------------------------------------------------------------- import sheet

    public bool IsImportSheetOpen
    {
        get => _isImportSheetOpen;
        private set => SetProperty(ref _isImportSheetOpen, value);
    }

    public string ImportCode
    {
        get => _importCode;
        set
        {
            if (SetProperty(ref _importCode, value))
            {
                ImportPreview = null;
                ImportError = "";
                NotifyCommands();
            }
        }
    }

    public MobileToolImportPreview? ImportPreview
    {
        get => _importPreview;
        private set
        {
            if (SetProperty(ref _importPreview, value))
            {
                OnPropertyChanged(nameof(HasImportPreview));
                OnPropertyChanged(nameof(CanConfirmImport));
                OnPropertyChanged(nameof(ImportPreviewTitle));
                OnPropertyChanged(nameof(ImportPreviewDetail));
                OnPropertyChanged(nameof(ImportPreviewWarning));
                NotifyCommands();
            }
        }
    }

    public bool HasImportPreview => ImportPreview is { Ok: true };

    public bool CanConfirmImport => ImportPreview is { Ok: true } && !IsBusy;

    public string ImportPreviewTitle => ImportPreview is { Ok: true } preview
        ? $"是这台电脑吗？{preview.DeviceName}"
        : "";

    public string ImportPreviewDetail => ImportPreview is { Ok: true } preview
        ? $"{preview.Endpoint}\n授权 ID：{preview.GrantId}"
        : "";

    public string ImportPreviewWarning => ImportPreview is { Ok: true, AlreadyImported: true }
        ? "这台电脑已经导入过；继续会用它新生成的凭据替换本机保存的凭据。"
        : "凭据只会保存到本机系统凭据库，不会显示在页面、日志或导出里。";

    public string ImportError
    {
        get => _importError;
        private set
        {
            if (SetProperty(ref _importError, value))
            {
                OnPropertyChanged(nameof(HasImportError));
            }
        }
    }

    public bool HasImportError => ImportError.Length > 0;

    // ---------------------------------------------------------------- parameter sheet

    public bool IsParameterSheetOpen
    {
        get => _isParameterSheetOpen;
        private set => SetProperty(ref _isParameterSheetOpen, value);
    }

    public MobileToolCommandItem? PendingCommand
    {
        get => _pendingCommand;
        private set
        {
            if (SetProperty(ref _pendingCommand, value))
            {
                OnPropertyChanged(nameof(ParameterSheetTitle));
                OnPropertyChanged(nameof(ParameterSheetSubtitle));
                OnPropertyChanged(nameof(HasParameterFields));
                OnPropertyChanged(nameof(ParameterSafetyText));
                OnPropertyChanged(nameof(CanConfirmRun));
                NotifyCommands();
            }
        }
    }

    public string ParameterSheetTitle => PendingCommand is null
        ? ""
        : MobileToolControlSemantics.Describe(PendingCommand).Action;

    public string ParameterSheetSubtitle => PendingCommand?.Subtitle ?? "";

    public IReadOnlyList<MobileToolParameterField> ParameterFields
    {
        get => _parameterFields;
        private set
        {
            if (SetProperty(ref _parameterFields, value))
            {
                OnPropertyChanged(nameof(HasParameterFields));
            }
        }
    }

    public bool HasParameterFields => ParameterFields.Count > 0;

    public string ParameterSafetyText
    {
        get
        {
            if (PendingCommand is null)
            {
                return "";
            }

            var parts = new List<string>();
            if (PendingCommand.SafetyLabel.Length > 0)
            {
                parts.Add(PendingCommand.SafetyLabel);
            }

            if (PendingCommand.RequiresElevation)
            {
                parts.Add("需要电脑端管理员权限");
            }

            if (PendingCommand.NotAllowedReason.Length > 0 && !PendingCommand.Allowed)
            {
                parts.Add(PendingCommand.NotAllowedText);
            }

            return string.Join(" · ", parts);
        }
    }

    public bool CanConfirmRun => PendingCommand is { Allowed: true } && !IsBusy;

    // ---------------------------------------------------------------- invocation

    public MobileToolInvocationSnapshot? Invocation
    {
        get => _invocation;
        private set
        {
            if (SetProperty(ref _invocation, value))
            {
                OnPropertyChanged(nameof(HasInvocation));
                OnPropertyChanged(nameof(InvocationStateText));
                OnPropertyChanged(nameof(InvocationMessage));
                OnPropertyChanged(nameof(InvocationResultTitle));
                OnPropertyChanged(nameof(InvocationResultDetail));
                OnPropertyChanged(nameof(HasInvocationResultDetail));
                OnPropertyChanged(nameof(InvocationTone));
                OnPropertyChanged(nameof(InvocationProgressText));
            }
        }
    }

    public bool HasInvocation => Invocation is not null;

    /// <summary>
    /// Chinese label for the wire state. Only <c>pending-confirmation</c> becomes 等待电脑确认; every
    /// other unknown state is shown verbatim so the page never claims a state the computer did not send.
    /// </summary>
    public string InvocationStateText => Invocation is null ? "" : StateLabel(Invocation.State);

    public static string StateLabel(string state) => state switch
    {
        MobileToolControlContract.StateQueued => "已接收",
        MobileToolControlContract.StatePending => "已接收",
        MobileToolControlContract.StateAccepted => "已接收",
        MobileToolControlContract.StateRunning => "电脑执行中",
        // Both spellings mean the same wire state; the gateway sends awaiting-confirmation.
        MobileToolControlContract.StateAwaitingConfirmation => "等待电脑确认",
        MobileToolControlContract.StatePendingConfirmation => "等待电脑确认",
        MobileToolControlContract.StateClaimed => "电脑已受理，正在本机执行",
        MobileToolControlContract.StateCancelling => "正在取消，等待最终结果",
        MobileToolControlContract.StateSucceeded => "已完成",
        MobileToolControlContract.StateFailed => "执行失败",
        MobileToolControlContract.StateCancelled => "已取消",
        MobileToolControlContract.StateRejected => "电脑拒绝了这次调用",
        MobileToolControlContract.StatePermissionRequired => "需要电脑端授权",
        "" => "状态未知",
        _ => state
    };

    public string InvocationMessage => Invocation?.Message ?? "";

    public string InvocationTone => Invocation?.State switch
    {
        MobileToolControlContract.StateSucceeded => "success",
        MobileToolControlContract.StateFailed or MobileToolControlContract.StateRejected => "danger",
        MobileToolControlContract.StateCancelled => "muted",
        MobileToolControlContract.StatePermissionRequired => "danger",
        MobileToolControlContract.StateAwaitingConfirmation or MobileToolControlContract.StatePendingConfirmation => "warning",
        MobileToolControlContract.StateCancelling => "warning",
        _ => "accent"
    };

    public string InvocationProgressText => Invocation is null
        ? ""
        : Invocation.Terminal
            ? "调用已结束"
            : "电脑执行中，页面正在按需读取状态";

    public string InvocationResultTitle
    {
        get
        {
            if (Invocation is null)
            {
                return "";
            }

            if (Invocation.ResultSummary.Length > 0)
            {
                return Invocation.ResultSummary;
            }

            if (Invocation.ResultErrorMessage.Length > 0)
            {
                return Invocation.ResultErrorMessage;
            }

            return Invocation.Message;
        }
    }

    public string InvocationResultDetail
    {
        get
        {
            if (Invocation is null)
            {
                return "";
            }

            var parts = new List<string>();
            if (Invocation.ResultErrorCode.Length > 0)
            {
                parts.Add($"错误代码：{Invocation.ResultErrorCode}");
            }

            if (Invocation.ResultState.Length > 0 && !string.Equals(Invocation.ResultState, Invocation.State, StringComparison.Ordinal))
            {
                parts.Add($"结果状态：{Invocation.ResultState}");
            }

            if (Invocation.Retryable)
            {
                parts.Add("可以重试");
            }

            if (Invocation.CancelAccepted && !Invocation.Terminal)
            {
                parts.Add("电脑已收到取消请求，正在停止");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasInvocationResultDetail => InvocationResultDetail.Length > 0;

    public bool IsInvocationSheetOpen
    {
        get => _isInvocationSheetOpen;
        private set => SetProperty(ref _isInvocationSheetOpen, value);
    }

    public bool CanCancelInvocation
    {
        get => _canCancelInvocation;
        private set
        {
            if (SetProperty(ref _canCancelInvocation, value))
            {
                NotifyCommands();
            }
        }
    }

    /// <summary>
    /// What the computer answered to the cancel request: refused, accepted (still waiting for the real
    /// outcome) or already finished. A 2xx cancel response alone never produces "已取消".
    /// </summary>
    public string InvocationCancelNote
    {
        get => _cancelNote;
        private set
        {
            if (SetProperty(ref _cancelNote, value))
            {
                OnPropertyChanged(nameof(HasInvocationCancelNote));
            }
        }
    }

    public bool HasInvocationCancelNote => InvocationCancelNote.Length > 0;

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        private set => SetProperty(ref _isDetailsOpen, value);
    }

    public string DetailsText
    {
        get => _detailsText;
        private set
        {
            if (SetProperty(ref _detailsText, value))
            {
                OnPropertyChanged(nameof(HasDetails));
                NotifyCommands();
            }
        }
    }

    public bool HasDetails => DetailsText.Length > 0;

    // ---------------------------------------------------------------- tool detail sheet

    public bool IsToolSheetOpen
    {
        get => _isToolSheetOpen;
        private set => SetProperty(ref _isToolSheetOpen, value);
    }

    public MobileToolToolItem? ToolSheetTool
    {
        get => _toolSheetTool;
        private set
        {
            if (SetProperty(ref _toolSheetTool, value))
            {
                OnPropertyChanged(nameof(ToolSheetTitle));
                OnPropertyChanged(nameof(ToolSheetDetail));
            }
        }
    }

    public string ToolSheetTitle => ToolSheetTool is null
        ? ""
        : MobileToolControlSemantics.ToolTitle(
            ToolSheetTool.ToolId,
            ToolSheetTool.Title.Length > 0 ? ToolSheetTool.Title : ToolSheetTool.ToolId);

    /// <summary>
    /// Real tool facts only: the state/availability the computer reported and how many of its commands
    /// this grant authorizes. No statistic is inferred from the tool's name.
    /// </summary>
    public string ToolSheetDetail
    {
        get
        {
            if (ToolSheetTool is null || Catalog is null)
            {
                return "";
            }

            var commands = Catalog.CommandsFor(ToolSheetTool.ToolId);
            var allowed = commands.Count(command => command.Allowed);
            var purpose = MobileToolControlSemantics.ToolPurpose(ToolSheetTool.ToolId, ToolSheetTool.Description);
            var state = ToolSheetTool.State.Length > 0 ? ToolSheetTool.State : ToolSheetTool.Availability;
            var authorization = commands.Count == 0
                ? "这台电脑没有为它声明命令"
                : $"已授权 {allowed}/{commands.Count} 条命令";
            return purpose + "\n状态：" + state + " · " + authorization;
        }
    }

    public IReadOnlyList<MobileToolActionRow> ToolSheetActions
    {
        get => _toolSheetActions;
        private set
        {
            if (SetProperty(ref _toolSheetActions, value))
            {
                OnPropertyChanged(nameof(HasToolSheetActions));
                OnPropertyChanged(nameof(ShowToolSheetEmpty));
            }
        }
    }

    public bool HasToolSheetActions => ToolSheetActions.Count > 0;

    public bool ShowToolSheetEmpty => ToolSheetActions.Count == 0;

    /// <summary>Opens one tool's detail sheet from the real catalog; unknown tool ids are ignored.</summary>
    public void OpenTool(string toolId)
    {
        var catalog = Catalog;
        if (catalog is null)
        {
            return;
        }

        var tool = catalog.Tools.FirstOrDefault(item =>
            string.Equals(item.ToolId, toolId, StringComparison.Ordinal));
        if (tool is null)
        {
            Feedback("电脑目录里没有这个工具。", "warning");
            return;
        }

        ToolSheetTool = tool;
        ToolSheetActions = catalog.CommandsFor(toolId).Select(command => ToRow(command, catalog)).ToArray();
        IsToolSheetOpen = true;
    }

    public void CloseToolSheet() => IsToolSheetOpen = false;

    // ---------------------------------------------------------------- commands

    public ICommand ImportCodeCommand { get; }

    public ICommand ConfirmImportCommand { get; }

    public ICommand CloseSheetCommand { get; }

    public ICommand RunCommand { get; }

    public ICommand CancelInvocationCommand { get; }

    public ICommand ShowDetailsCommand { get; }

    public ICommand CloseDetailsCommand { get; }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Called when the page becomes visible. It refreshes the local device list only: no computer is
    /// contacted until the user asks for a catalog, an action or a check.
    /// </summary>
    public async void Activate()
    {
        _active = true;
        _pageLifetime?.Cancel();
        _pageLifetime = new CancellationTokenSource();
        await RefreshDevicesAsync(announce: false).ConfigureAwait(true);

        // Coming back to the page resumes the state reads for an invocation that is still running.
        if (Invocation is { Terminal: false })
        {
            _progressTimer?.Start();
        }
    }

    /// <summary>
    /// Called when the page is left. Every short-lived refresh stops here: the timer is stopped, the
    /// in-flight status read is cancelled, and a late answer is ignored. A submit that is already on
    /// its way is deliberately not cancelled - the computer may already have admitted that
    /// invocation id, and the page must be able to pick it up again on return.
    /// </summary>
    public void Deactivate()
    {
        _active = false;
        _progressTimer?.Stop();
        var lifetime = _pageLifetime;
        _pageLifetime = null;
        lifetime?.Cancel();
    }

    /// <summary>
    /// Page-level back. The Shell calls this before it leaves the tool page:
    /// <see langword="true"/> means the page consumed the key (a sheet was closed, or the device page
    /// went back to the device list).
    /// </summary>
    public bool TryHandleBack()
    {
        if (IsDetailsOpen)
        {
            IsDetailsOpen = false;
            return true;
        }

        if (IsToolSheetOpen)
        {
            IsToolSheetOpen = false;
            return true;
        }

        if (IsParameterSheetOpen)
        {
            CloseSheets();
            return true;
        }

        if (IsImportSheetOpen)
        {
            CloseSheets();
            return true;
        }

        if (IsInvocationSheetOpen)
        {
            IsInvocationSheetOpen = false;
            return true;
        }

        if (IsDevicePageOpen)
        {
            IsDevicePageOpen = false;
            CloseSheets();
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- operations

    public Task RefreshDevicesAsync(bool announce = true) =>
        RunBusyAsync(token => RefreshDevicesCoreAsync(token, announce), "正在读取本机记录…");

    /// <summary>
    /// Reads the local device list. It never contacts a computer: this is the module's own record of
    /// imported computers. Other operations call this core helper so they do not re-enter the busy
    /// guard.
    /// </summary>
    private async Task RefreshDevicesCoreAsync(CancellationToken token, bool announce)
    {
        var devices = await _client.DevicesAsync(token).ConfigureAwait(true);
        Devices = devices;
        var removed = SelectedDevice is not null &&
                      devices.All(device => !string.Equals(device.DeviceId, SelectedDevice.DeviceId, StringComparison.Ordinal));
        if (removed)
        {
            SelectedDevice = null;
            Catalog = null;
            IsDevicePageOpen = false;
        }
        else if (SelectedDevice is not null)
        {
            SelectedDevice = devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, SelectedDevice.DeviceId, StringComparison.Ordinal));
        }

        if (announce)
        {
            Feedback("已刷新本机保存的电脑列表。", "neutral");
        }
    }

    public Task OpenDeviceAsync(MobileToolDeviceItem device)
    {
        ArgumentNullException.ThrowIfNull(device);
        SelectedDevice = device;
        IsDevicePageOpen = true;
        Catalog = null;
        CatalogError = "";
        Feedback("", "neutral");
        return LoadCatalogAsync(refresh: false);
    }

    public Task LoadCatalogAsync(bool refresh) => RunBusyAsync(async token =>
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            var catalog = await _client.CatalogAsync(device.DeviceId, device.DeviceName, refresh, token).ConfigureAwait(true);
            Catalog = catalog;
            CatalogError = "";
            SelectedDevice = Devices.FirstOrDefault(item =>
                string.Equals(item.DeviceId, device.DeviceId, StringComparison.Ordinal)) ?? device;
        }
        catch (MobileToolControlModuleException exception)
        {
            Catalog = null;
            CatalogError = exception.IsAuthorizationLost
                ? exception.Message + "（请重新导入电脑上的连接码）"
                : exception.Message;
            Feedback(exception.Message, exception.IsUnreachable ? "warning" : "danger");
        }
    }, refresh ? "正在刷新电脑工具目录…" : "正在读取电脑工具目录…");

    public Task CheckDeviceAsync() => RunBusyAsync(async token =>
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            var check = await _client.CheckDeviceAsync(device.DeviceId, token).ConfigureAwait(true);
            Feedback(
                check.Reachable
                    ? $"{check.DeviceName} 已响应，共 {check.CommandCount} 条命令。"
                    : "电脑没有响应。",
                check.Reachable ? "success" : "warning");
            await RefreshDevicesCoreAsync(token, announce: false).ConfigureAwait(true);
        }
        catch (MobileToolControlModuleException exception)
        {
            Feedback(exception.Message, "warning");
            await RefreshDevicesCoreAsync(token, announce: false).ConfigureAwait(true);
        }
    }, "正在检查电脑连接…");

    public Task RemoveDeviceAsync() => RunBusyAsync(async token =>
    {
        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        await _client.RemoveDeviceAsync(device.DeviceId, token).ConfigureAwait(true);
        SelectedDevice = null;
        Catalog = null;
        IsDevicePageOpen = false;
        await RefreshDevicesCoreAsync(token, announce: false).ConfigureAwait(true);
        Feedback($"已移除 {device.DeviceName} 及其授权凭据。", "neutral");
    }, "正在移除电脑…");

    public void OpenImportSheet()
    {
        ImportCode = "";
        ImportPreview = null;
        ImportError = "";
        IsImportSheetOpen = true;
    }

    /// <summary>
    /// Shows an import problem (a scan that returned nothing, a scanner failure) without inventing a
    /// device and without echoing any code content.
    /// </summary>
    public void ReportImportFailure(string message)
    {
        ImportPreview = null;
        IsImportSheetOpen = true;
        ImportError = string.IsNullOrWhiteSpace(message) ? "没有识别到连接码。" : message;
    }

    /// <summary>
    /// Opens the import sheet for a connection code that arrived from outside the page (system link or
    /// the QR scanner) and runs the existing preview. The code is only held in memory for the
    /// confirmation; it is never logged, and nothing is stored and no command runs before the user
    /// presses 确认导入.
    /// </summary>
    public async Task<bool> OpenImportFromCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !code.Trim().StartsWith(MobileToolControlContract.ControlCodePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        OpenImportSheet();
        ImportCode = code.Trim();
        await ((MptAsyncRelayCommand)ImportCodeCommand).ExecuteAsync().ConfigureAwait(true);
        return HasImportPreview;
    }

    public void OpenDetails(string text)
    {
        DetailsText = text;
        IsDetailsOpen = text.Length > 0;
    }

    public void CloseSheets()
    {
        IsImportSheetOpen = false;
        IsParameterSheetOpen = false;
    }

    /// <summary>Closes the invocation sheet through the view model so it cannot reappear on the next refresh.</summary>
    public void CloseInvocationSheet() => IsInvocationSheetOpen = false;

    /// <summary>Closes the technical-details layer through the view model.</summary>
    public void CloseDetails() => IsDetailsOpen = false;

    private Task PreviewImportAsync() => RunBusyAsync(async token =>
    {
        ImportPreview = null;
        ImportError = "";
        try
        {
            var preview = await _client.PreviewImportAsync(ImportCode, token).ConfigureAwait(true);
            if (!preview.Ok)
            {
                ImportError = preview.Error;
                return;
            }

            ImportPreview = preview;
        }
        catch (MobileToolControlModuleException exception)
        {
            ImportError = exception.Message;
        }
    }, "正在解析连接码…");

    private Task ConfirmImportAsync() => RunBusyAsync(async token =>
    {
        try
        {
            var devices = await _client.ConfirmImportAsync(ImportCode, token).ConfigureAwait(true);
            Devices = devices;
            var imported = devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, ImportPreview?.GrantId, StringComparison.Ordinal));
            IsImportSheetOpen = false;
            ImportCode = "";
            ImportPreview = null;
            Feedback(
                imported is null ? "已保存这台电脑。" : $"已导入 {imported.DeviceName}。",
                "success");
        }
        catch (MobileToolControlModuleException exception)
        {
            ImportError = exception.Message;
        }
    }, "正在保存凭据…");

    public void OpenCommand(MobileToolActionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.Command.Allowed)
        {
            Feedback(row.Command.NotAllowedText, "warning");
            return;
        }

        PendingCommand = row.Command;
        ParameterFields = row.Command.Parameters.Select(parameter => new MobileToolParameterField(parameter)).ToArray();
        InvocationCancelNote = "";
        IsToolSheetOpen = false;
        IsParameterSheetOpen = true;
    }

    private async Task ConfirmRunAsync()
    {
        var command = PendingCommand;
        var device = SelectedDevice;
        if (command is null || device is null || !command.Allowed)
        {
            return;
        }

        // Encode every field with its declared type; a value that does not match stays on screen
        // with a field-level error and nothing is submitted.
        var args = new JsonObject();
        MobileToolParameterField? invalid = null;
        foreach (var field in ParameterFields)
        {
            if (!field.TryEncode(out var encoded))
            {
                invalid ??= field;
                continue;
            }

            if (encoded is not null)
            {
                args[field.Id] = encoded;
            }
        }

        if (invalid is not null)
        {
            Feedback($"请检查「{invalid.Parameter.Label}」：{invalid.Error}", "warning");
            return;
        }

        IsParameterSheetOpen = false;
        await RunBusyAsync(async token =>
        {
            var invocation = await _client
                .InvokeAsync(device.DeviceId, command.CommandId, args, Guid.NewGuid().ToString("N"), token)
                .ConfigureAwait(true);
            ApplyInvocation(invocation, openSheet: true);
        }, "正在把调用发给电脑…").ConfigureAwait(true);
    }

    private Task CancelInvocationAsync() => RunBusyAsync(async token =>
    {
        var invocation = Invocation;
        if (invocation is null)
        {
            return;
        }

        try
        {
            var updated = await _client.CancelAsync(invocation.DeviceId, invocation.InvocationId, token).ConfigureAwait(true);
            ApplyInvocation(updated, openSheet: true);
            // The HTTP call succeeding is not the answer; the computer's own document is. An accepted
            // cancel can arrive together with a terminal state (a pending confirmation is cancelled
            // locally), and that means the cancel succeeded - not that the call had already finished.
            if (updated.CancelAccepted)
            {
                InvocationCancelNote = updated.Terminal
                    ? "电脑已取消这次调用，最终状态以电脑返回为准。"
                    : "电脑已接受取消请求，仍在等待最终结果。";
                Feedback(
                    updated.Terminal ? "电脑已取消这次调用。" : "取消请求已被电脑接受，结果以电脑返回为准。",
                    "neutral");
            }
            else if (updated.Terminal)
            {
                InvocationCancelNote = "调用已经结束，电脑没有需要取消的执行。";
                Feedback("调用已经结束。", "muted");
            }
            else
            {
                InvocationCancelNote = updated.Message.Length > 0
                    ? "电脑没有接受取消请求：" + updated.Message
                    : "电脑没有接受取消请求，调用仍在继续。";
                Feedback(InvocationCancelNote, "warning");
            }
        }
        catch (MobileToolControlModuleException exception)
        {
            InvocationCancelNote = "取消请求没有送达电脑：" + exception.Message;
            Feedback(exception.Message, exception.IsUnreachable ? "warning" : "danger");
        }
    }, "正在请求取消…");

    /// <summary>One visible status read; the page's timer calls this, tests call it directly.</summary>
    internal Task PollOnceAsync() => PollInvocationAsync();

    private async Task PollInvocationAsync()
    {
        var invocation = Invocation;
        if (invocation is null || invocation.Terminal || !_active)
        {
            _progressTimer?.Stop();
            CanCancelInvocation = false;
            return;
        }

        // Exactly one status read at a time: a slow computer must not let the 1.2 s timer stack
        // requests, and a stale answer must not overwrite a newer invocation.
        if (Interlocked.CompareExchange(ref _statusReadInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(
                _pageLifetime?.Token ?? CancellationToken.None);
            work.CancelAfter(TimeSpan.FromSeconds(20));
            var updated = await _client
                .InvocationStatusAsync(invocation.DeviceId, invocation.InvocationId, work.Token)
                .ConfigureAwait(true);

            var current = Invocation;
            if (current is null ||
                !string.Equals(current.DeviceId, invocation.DeviceId, StringComparison.Ordinal) ||
                !string.Equals(current.InvocationId, invocation.InvocationId, StringComparison.Ordinal))
            {
                // The user moved to another device or started another call while this read was in
                // flight; the answer belongs to the previous one and is dropped.
                return;
            }

            ApplyInvocation(updated, openSheet: false);
        }
        catch (OperationCanceledException)
        {
            // The page was left (or the read timed out); the next visible poll resumes.
        }
        catch (MobileToolControlModuleException exception)
        {
            if (exception.Retryable || exception.IsUnreachable)
            {
                // Keep the last known state and say so instead of pretending the call ended.
                Feedback("暂时读不到调用状态：" + exception.Message, "warning");
                return;
            }

            _progressTimer?.Stop();
            CanCancelInvocation = false;
            Feedback(exception.Message, "danger");
        }
        finally
        {
            Interlocked.Exchange(ref _statusReadInFlight, 0);
        }
    }

    private void ApplyInvocation(MobileToolInvocationSnapshot invocation, bool openSheet)
    {
        var isNewInvocation = Invocation is null ||
                              !string.Equals(Invocation.InvocationId, invocation.InvocationId, StringComparison.Ordinal) ||
                              !string.Equals(Invocation.DeviceId, invocation.DeviceId, StringComparison.Ordinal);
        Invocation = invocation;
        if (isNewInvocation)
        {
            InvocationCancelNote = "";
        }

        if (openSheet)
        {
            IsInvocationSheetOpen = true;
        }

        // A cancel that the runtime accepted keeps the call non-terminal until the real outcome
        // arrives; asking again would only repeat the request, so the button is hidden.
        CanCancelInvocation = !invocation.Terminal && !invocation.CancelAccepted;
        if (invocation.CancelAccepted && !invocation.Terminal && InvocationCancelNote.Length == 0)
        {
            InvocationCancelNote = "电脑已接受取消请求，仍在等待最终结果。";
        }
        if (invocation.Terminal)
        {
            _progressTimer?.Stop();
            Feedback(
                StateLabel(invocation.State) + (invocation.Message.Length > 0 ? "：" + invocation.Message : ""),
                InvocationTone);
            return;
        }

        if (_active)
        {
            _progressTimer?.Start();
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Resolves a command for an activation. The catalog's tool→command relationship is used as it
    /// really is (<c>ToolId</c> and <c>ModuleId</c> are not assumed to be equal): a command matches when
    /// its module is the tool, when the tool's own module owns it, or when its id is prefixed by the
    /// tool id.
    /// </summary>
    internal MobileToolCommandItem? FindCommand(string toolId, string commandId)
    {
        var catalog = Catalog;
        if (catalog is null || commandId.Length == 0)
        {
            return null;
        }

        var tool = catalog.Tools.FirstOrDefault(item =>
            string.Equals(item.ToolId, toolId, StringComparison.Ordinal));
        return catalog.Commands.FirstOrDefault(command =>
            string.Equals(command.CommandId, commandId, StringComparison.Ordinal) &&
            (toolId.Length == 0 ||
             string.Equals(command.ModuleId, toolId, StringComparison.Ordinal) ||
             (tool is not null && string.Equals(command.ModuleId, tool.ModuleId, StringComparison.Ordinal)) ||
             command.CommandId.StartsWith(toolId + ".", StringComparison.Ordinal)));
    }

    /// <summary>The action row for an activation target, or <see langword="null"/> when the catalog has none.</summary>
    internal MobileToolActionRow? FindRow(string toolId, string commandId)
    {
        var catalog = Catalog;
        var command = FindCommand(toolId, commandId);
        return command is null || catalog is null ? null : ToRow(command, catalog);
    }

    /// <summary>True when the catalog offers a tool with this id (or a command module of that name).</summary>
    internal bool HasTool(string toolId)
    {
        var catalog = Catalog;
        return catalog is not null &&
               (catalog.Tools.Any(tool => string.Equals(tool.ToolId, toolId, StringComparison.Ordinal)) ||
                catalog.Commands.Any(command => string.Equals(command.ModuleId, toolId, StringComparison.Ordinal)));
    }

    private MobileToolActionRow ToRow(MobileToolCommandItem command, MobileToolCatalogSnapshot catalog)
    {
        var (action, hint, intent) = MobileToolControlSemantics.Describe(command);
        var toolTitle = catalog.Tools.FirstOrDefault(tool =>
            string.Equals(tool.ToolId, command.ModuleId, StringComparison.Ordinal))?.Title ?? command.ModuleId;
        return new MobileToolActionRow(
            command,
            action,
            hint,
            intent,
            MobileToolControlSemantics.ToolTitle(command.ModuleId, toolTitle));
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action, string busyText)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        BusyText = busyText;
        var source = new CancellationTokenSource();
        _pendingWork = source;
        try
        {
            await action(source.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The page was left while the call was running; the module call was cancelled too.
        }
        catch (MobileToolControlModuleException exception)
        {
            Feedback(exception.Message, exception.IsUnreachable ? "warning" : "danger");
        }
        catch (Exception exception)
        {
            Feedback("操作失败：" + exception.Message, "danger");
        }
        finally
        {
            if (ReferenceEquals(_pendingWork, source))
            {
                _pendingWork = null;
            }

            source.Dispose();
            IsBusy = false;
        }
    }

    private void Feedback(string text, string tone)
    {
        FeedbackTone = tone;
        FeedbackText = text;
    }

    private void NotifyCommands()
    {
        (ImportCodeCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ConfirmImportCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RunCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
        (CancelInvocationCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ShowDetailsCommand as MptAsyncRelayCommand)?.NotifyCanExecuteChanged();
    }
}
