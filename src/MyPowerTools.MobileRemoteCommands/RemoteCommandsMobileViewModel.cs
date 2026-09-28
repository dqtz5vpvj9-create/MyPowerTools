using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>Which bottom sheet the phone page currently shows.</summary>
internal enum MobileSheet
{
    None,
    Run,
    CommandEditor,
    Connection,
    Catalog
}

/// <summary>
/// Phone page state for the Remote Commands Android module.
///
/// The module owns SSH, the host catalog, the host-key store, the secret store and the shared
/// <c>commands.yaml</c>/<c>settings.json</c>/<c>history.json</c> files. This view model only decides what
/// to ask for, keeps the page honest about the result (including "the host key is not confirmed yet"),
/// and never holds a credential after the save call returns.
///
/// The page shape follows the approved prototype: a list of the real catalog commands, one bottom sheet
/// per job (run, edit a command, manage the connection, edit the raw catalog), progress/cancel/retry for
/// a run, and on-demand connection work instead of polling the module.
/// </summary>
internal sealed class RemoteCommandsMobileViewModel : MptObservableViewModel, IDisposable
{
    /// <summary>Matches the module's own output bound so the phone cannot grow the run text without limit.</summary>
    private const int MaxOutputLength = 512 * 1024;

    private const int MaxCommandsFileBytes = 256 * 1024;

    private readonly RemoteCommandsModuleClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _output = new();
    private IDisposable? _events;
    private string? _activeInvocationId;
    private bool _disposed;

    private string _pageTitle = "远程命令";
    private string _pageSubtitle = "正在读取模块状态…";
    private string _statusText = "正在读取模块状态…";
    private string _statusDetail = "";
    private string _feedbackText = "";
    private string _feedbackTone = "info";
    private bool _isBusy;
    private bool _isRunning;
    private bool _runPermissionRequired;

    private IReadOnlyList<MobileCommandDefinition> _commands = [];
    private MobileCommandDefinition? _selectedCommand;
    private string _searchText = "";
    private IReadOnlyList<MobileHostEntry> _hosts = [];
    private string _selectedHost = "";
    private string _defaultHost = "";
    private string _input1 = "";
    private string _input2 = "";
    private bool _showSecondInput;
    private string _outputText = "";
    private string _runStateText = "尚未运行";
    private string _runMessageText = "";
    private string _runEndpointText = "";
    private string _runStage = "";
    private DateTimeOffset? _lastRunAt;
    private MobileRunOutcome? _lastOutcome;
    private string _lastResultCommandLabel = "";
    private string _lastResultAtText = "";

    private MobilePendingHostKey? _pendingHostKey;
    private bool _trustFingerprintAcknowledged;
    private MobileRunRequest? _lastRunRequest;

    private IReadOnlyList<string> _missingAliases = [];
    private IReadOnlyList<MobileHostKeyEntry> _trustedKeys = [];
    private MobileHistorySummary _history = new(0, "", "", "");
    private string _commandsPath = "";
    private string _commandsFileStatus = "";
    private string _commandsError = "";
    private string _catalogError = "";
    private bool _transportAvailable;
    private bool _backgroundAvailable;
    private string _settingsSummary = "";
    private string _dataDirectory = "";
    private MobileHostEntry? _pendingRemoval;
    private MobileHostKeyEntry? _pendingKeyRevocation;
    private bool _historyClearPending;

    private MobileSheet _sheet;
    private bool _discardGuardPending;

    // 命令配置 (editable commands.yaml)
    private string _catalogYaml = "";
    private string _catalogYamlStatus = "";
    private string _catalogSaveMessage = "";
    private bool _catalogDirty;
    private bool _catalogReloadPending;

    // 设置 (editable shared settings)
    private string _settingsDefaultHost = "";
    private string _settingsKnownHosts = "";
    private string _settingsRetention = "500";
    private string _settingsCondaExecutable = "";
    private string _settingsTimeoutMinutes = "30";
    private string _settingsMessage = "";
    private bool _settingsDirty;

    // 添加/编辑命令
    private MobileCommandDefinition? _editingCommand;
    private string _formId = "";
    private string _formLabel = "";
    private string _formCommand = "";
    private string _formDescription = "";
    private bool _formUsesRemoteHost = true;
    private string _formHost = "";
    private string _formInput1Label = "";
    private string _formInput1Placeholder = "";
    private string _formInput2Label = "";
    private string _formInput2Placeholder = "";
    private bool _formShowSecondInput;
    private bool _formAdvanced;
    private string _commandFormMessage = "";
    private bool _commandDeletePending;
    private bool _idEditedByUser;

    // 服务器连接
    private string _formAlias = "";
    private string _formRealHost = "";
    private string _formPort = "22";
    private string _formUsername = "";
    private bool _formUsesPassword = true;
    private string _formPassword = "";
    private string _formPrivateKey = "";
    private string _formPassphrase = "";
    private string _formMessage = "";

    public RemoteCommandsMobileViewModel(MptAvaloniaSurfaceContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _client = new RemoteCommandsModuleClient(context);
    }

    public MptAvaloniaSurfaceContext Context { get; }

    // ---------------------------------------------------------------- page header

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    /// <summary>
    /// Honest connection summary. Nothing here claims "connected": the module has no "ping" command, so
    /// the page reports the configured target and whether its fingerprint is already confirmed, and the
    /// run itself is what establishes the connection.
    /// </summary>
    public string PageSubtitle
    {
        get => _pageSubtitle;
        private set => SetProperty(ref _pageSubtitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set
        {
            if (SetProperty(ref _statusDetail, value))
            {
                OnPropertyChanged(nameof(HasStatusDetail));
            }
        }
    }

    public bool HasStatusDetail => StatusDetail.Length > 0;

    /// <summary>
    /// The status line only appears when it says something the subtitle does not: no managed transport, or
    /// a command catalog the module could not read.
    /// </summary>
    public bool HasStatusPill => !TransportAvailable || CommandsError.Length > 0;

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

    /// <summary>One of <c>info</c>, <c>success</c>, <c>warning</c>, <c>error</c>.</summary>
    public string FeedbackTone
    {
        get => _feedbackTone;
        private set => SetProperty(ref _feedbackTone, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInteract));
                OnPropertyChanged(nameof(CanSaveCommandForm));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanTrustPendingHostKey));
            }
        }
    }

    public bool CanInteract => !IsBusy;

    public bool CanRun => !IsBusy && !IsRunning && SelectedCommand is not null;

    public bool CanCancel => IsRunning;

    /// <summary>Retry repeats the last request with the same inputs; it is never a simulation.</summary>
    public bool CanRetry => !IsBusy && !IsRunning && _lastRunRequest is not null && !CanTrustPendingHostKey;

    /// <summary>True when the host refused the command and is waiting for the user's authorization.</summary>
    public bool RunPermissionRequired
    {
        get => _runPermissionRequired;
        private set
        {
            if (SetProperty(ref _runPermissionRequired, value))
            {
                OnPropertyChanged(nameof(RunPermissionText));
                OnPropertyChanged(nameof(HasRunPermissionNotice));
            }
        }
    }

    public bool HasRunPermissionNotice => RunPermissionRequired;

    public string RunPermissionText =>
        "系统还没有授权这次远程执行。远程命令会在授权后按原有权限机制继续；这里不会绕过授权，也不会假装成功。";

    public bool TransportAvailable
    {
        get => _transportAvailable;
        private set
        {
            if (SetProperty(ref _transportAvailable, value))
            {
                OnPropertyChanged(nameof(TransportWarning));
                OnPropertyChanged(nameof(HasTransportWarning));
            }
        }
    }

    /// <summary>Shown when the device has no managed SSH transport, so "run" is honestly unavailable.</summary>
    public string TransportWarning => TransportAvailable
        ? ""
        : "此设备缺少受管 SSH 传输，远程命令无法执行；本地转换命令仍可使用，命令目录与主机配置仍可查看和编辑。";

    public bool HasTransportWarning => !TransportAvailable;

    public bool BackgroundAvailable
    {
        get => _backgroundAvailable;
        private set
        {
            if (SetProperty(ref _backgroundAvailable, value))
            {
                OnPropertyChanged(nameof(BackgroundText));
            }
        }
    }

    public string BackgroundText => BackgroundAvailable
        ? "执行期间持有系统前台任务，可在通知里停止"
        : "系统未提供前台任务能力，长时间执行可能被系统中断";

    public string CommandsPath
    {
        get => _commandsPath;
        private set => SetProperty(ref _commandsPath, value);
    }

    public string DataDirectory
    {
        get => _dataDirectory;
        private set => SetProperty(ref _dataDirectory, value);
    }

    // ---------------------------------------------------------------- command list

    public IReadOnlyList<MobileCommandDefinition> Commands
    {
        get => _commands;
        private set
        {
            if (SetProperty(ref _commands, value))
            {
                OnPropertyChanged(nameof(HasCommands));
                OnPropertyChanged(nameof(HasNoCommands));
                OnPropertyChanged(nameof(ShowSearch));
                OnPropertyChanged(nameof(VisibleCommands));
                OnPropertyChanged(nameof(HasNoMatches));
                OnPropertyChanged(nameof(CommandListCaption));
            }
        }
    }

    public bool HasCommands => Commands.Count > 0;

    public bool HasNoCommands => Commands.Count == 0;

    /// <summary>The search field only earns its space once the shared catalog is longer than a screenful.</summary>
    public bool ShowSearch => Commands.Count > 4;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(VisibleCommands));
                OnPropertyChanged(nameof(HasNoMatches));
            }
        }
    }

    public IReadOnlyList<MobileCommandDefinition> VisibleCommands
    {
        get
        {
            var query = SearchText.Trim();
            if (query.Length == 0)
            {
                return Commands;
            }

            return Commands
                .Where(command =>
                    command.Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    command.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    command.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    command.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public bool HasNoMatches => HasCommands && VisibleCommands.Count == 0;

    public string CommandListCaption => Commands.Count == 0
        ? "还没有命令"
        : $"{Commands.Count} 条命令 · 来自共享 commands.yaml";

    public string EmptyCommandsText =>
        "共享的 commands.yaml 里还没有命令。点“添加”写下第一条，保存后电脑和手机都会用它。";

    public bool CommandsLoadFailed => _catalogError.Length > 0 || _commandsError.Length > 0;

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

    public string CommandsError
    {
        get => _commandsError;
        private set
        {
            if (SetProperty(ref _commandsError, value))
            {
                OnPropertyChanged(nameof(HasCommandsError));
            }
        }
    }

    public bool HasCommandsError => CommandsError.Length > 0;

    public MobileCommandDefinition? SelectedCommand
    {
        get => _selectedCommand;
        private set
        {
            if (!SetProperty(ref _selectedCommand, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CommandDetailText));
            OnPropertyChanged(nameof(Input1Label));
            OnPropertyChanged(nameof(Input1Placeholder));
            OnPropertyChanged(nameof(Input2Label));
            OnPropertyChanged(nameof(Input2Placeholder));
            OnPropertyChanged(nameof(CanRun));
            OnPropertyChanged(nameof(RunSheetTitle));
            OnPropertyChanged(nameof(RunSheetSubtitle));
            ShowSecondInput = value?.ShowSecondInput == true;
        }
    }

    public string CommandDetailText => SelectedCommand is not { } command
        ? "commands.yaml 中还没有可运行的命令。"
        : $"{command.TypeBadge} · {command.HostText}" +
          (string.IsNullOrWhiteSpace(command.Description) ? "" : $" · {command.Description}");

    public string Input1Label => SelectedCommand?.Input1LabelText ?? "输入 1";

    public string Input1Placeholder => SelectedCommand?.Input1Placeholder ?? "粘贴或输入第一个输入";

    public string Input2Label => SelectedCommand?.Input2LabelText ?? "输入 2";

    public string Input2Placeholder => SelectedCommand?.Input2Placeholder ?? "粘贴或输入第二个输入";

    public string Input1
    {
        get => _input1;
        set => SetProperty(ref _input1, value);
    }

    public string Input2
    {
        get => _input2;
        set => SetProperty(ref _input2, value);
    }

    public bool ShowSecondInput
    {
        get => _showSecondInput;
        set => SetProperty(ref _showSecondInput, value);
    }

    // ---------------------------------------------------------------- sheets

    public MobileSheet Sheet
    {
        get => _sheet;
        private set
        {
            if (!SetProperty(ref _sheet, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSheetOpen));
            OnPropertyChanged(nameof(IsRunSheetOpen));
            OnPropertyChanged(nameof(IsCommandSheetOpen));
            OnPropertyChanged(nameof(IsConnectionSheetOpen));
            OnPropertyChanged(nameof(IsCatalogSheetOpen));
        }
    }

    public bool IsSheetOpen => Sheet != MobileSheet.None;

    public bool IsRunSheetOpen => Sheet == MobileSheet.Run;

    public bool IsCommandSheetOpen => Sheet == MobileSheet.CommandEditor;

    public bool IsConnectionSheetOpen => Sheet == MobileSheet.Connection;

    public bool IsCatalogSheetOpen => Sheet == MobileSheet.Catalog;

    /// <summary>Android back: close the topmost layer and report whether the key was consumed.</summary>
    public bool TryHandleBack()
    {
        if (_discardGuardPending)
        {
            // The first press only warned: this one drops the unsaved edits and closes the sheet.
            _discardGuardPending = false;
            CloseSheet();
            return true;
        }

        // A pending two-tap confirmation is the topmost thing on screen: back disarms it first.
        if (PendingRemoval is not null)
        {
            CancelHostRemoval();
            SetFeedback("已取消删除主机。", "info");
            return true;
        }

        if (PendingKeyRevocation is not null)
        {
            PendingKeyRevocation = null;
            SetFeedback("已取消撤销指纹。", "info");
            return true;
        }

        if (HistoryClearPending)
        {
            CancelHistoryClear();
            SetFeedback("已取消清空历史。", "info");
            return true;
        }

        if (CommandDeletePending)
        {
            CommandDeletePending = false;
            SetFeedback("已取消删除命令。", "info");
            return true;
        }

        if (IsSheetOpen)
        {
            if (SheetGuardNeeded())
            {
                _discardGuardPending = true;
                SetFeedback(Sheet == MobileSheet.Catalog
                    ? "命令配置还有未保存的修改：再按一次返回将放弃。"
                    : "命令表单还有未保存的修改：再按一次返回将放弃。", "warning");
                return true;
            }

            CloseSheet();
            return true;
        }

        return false;
    }

    private bool SheetGuardNeeded() => Sheet switch
    {
        MobileSheet.Catalog => CatalogDirty,
        MobileSheet.CommandEditor => CommandFormDirty,
        _ => false
    };

    public void CloseSheet()
    {
        _discardGuardPending = false;
        Sheet = MobileSheet.None;
        CommandDeletePending = false;
        CatalogReloadPending = false;
    }

    public string SheetTitle => Sheet switch
    {
        MobileSheet.Run => RunSheetTitle,
        MobileSheet.CommandEditor => EditingCommand is null ? "添加远程命令" : "编辑远程命令",
        MobileSheet.Connection => "服务器连接",
        MobileSheet.Catalog => "命令配置",
        _ => ""
    };

    public string SheetSubtitle => Sheet switch
    {
        MobileSheet.Run => RunSheetSubtitle,
        MobileSheet.CommandEditor => "保存在共享 commands.yaml，电脑和手机使用同一份目录。",
        MobileSheet.Connection => "SSH 地址、凭据与默认主机只在这里管理。",
        MobileSheet.Catalog => "直接编辑共享的 commands.yaml，保存由模块校验。",
        _ => ""
    };

    // ---------------------------------------------------------------- run

    public string RunSheetTitle => SelectedCommand?.Label ?? "运行命令";

    public string RunSheetSubtitle => SelectedCommand is not { } command
        ? ""
        : command.IsLocalTransform
            ? "本地转换 · 不经过 SSH"
            : $"{command.TypeBadge} · {TargetHostText}";

    private string TargetHostText
    {
        get
        {
            if (SelectedCommand is { Host.Length: > 0 } fixedHost)
            {
                return $"固定主机 {fixedHost}";
            }

            if (SelectedHost.Length > 0)
            {
                return $"主机 {SelectedHost}";
            }

            return DefaultHost.Length > 0 ? $"默认主机 {DefaultHost}" : "尚未选择主机";
        }
    }

    public IReadOnlyList<MobileHostEntry> Hosts
    {
        get => _hosts;
        private set
        {
            if (SetProperty(ref _hosts, value))
            {
                OnPropertyChanged(nameof(HasHosts));
                OnPropertyChanged(nameof(HasNoHosts));
                OnPropertyChanged(nameof(HostPickerItems));
                OnPropertyChanged(nameof(HostSummaryText));
            }
        }
    }

    public bool HasHosts => Hosts.Count > 0;

    public bool HasNoHosts => Hosts.Count == 0;

    public string HostSummaryText => Hosts.Count == 0
        ? "还没有映射任何主机。手机上没有 ~/.ssh/config，别名必须在这里显式映射到真实地址。"
        : $"{Hosts.Count} 台已映射主机 · 凭据只保存在系统凭据库";

    /// <summary>Alias picker entries: the mapped aliases plus "follow the command / default".</summary>
    public IReadOnlyList<MobileHostChoice> HostPickerItems =>
    [
        new("", DefaultHostChoiceText),
        .. Hosts.Select(host => new MobileHostChoice(host.Alias, host.ToString()))
    ];

    private string DefaultHostChoiceText => string.IsNullOrWhiteSpace(DefaultHost)
        ? "使用命令配置 / 默认主机"
        : $"使用默认主机 {DefaultHost}";

    public string SelectedHost
    {
        get => _selectedHost;
        set
        {
            if (SetProperty(ref _selectedHost, value))
            {
                OnPropertyChanged(nameof(RunSheetSubtitle));
                OnPropertyChanged(nameof(PageSubtitle));
            }
        }
    }

    public string DefaultHost
    {
        get => _defaultHost;
        private set
        {
            if (SetProperty(ref _defaultHost, value))
            {
                OnPropertyChanged(nameof(HostPickerItems));
                OnPropertyChanged(nameof(PageSubtitle));
            }
        }
    }

    public IReadOnlyList<string> MissingAliases
    {
        get => _missingAliases;
        private set
        {
            if (SetProperty(ref _missingAliases, value))
            {
                OnPropertyChanged(nameof(HasMissingAliases));
                OnPropertyChanged(nameof(MissingAliasesText));
            }
        }
    }

    public bool HasMissingAliases => MissingAliases.Count > 0;

    public string MissingAliasesText => MissingAliases.Count == 0
        ? "commands.yaml 与共享设置里的别名都已在手机上映射。"
        : $"以下别名还没有映射到真实地址：{string.Join("、", MissingAliases)}";

    public string OutputText
    {
        get => _outputText;
        private set
        {
            if (SetProperty(ref _outputText, value))
            {
                OnPropertyChanged(nameof(HasOutput));
            }
        }
    }

    public bool HasOutput => OutputText.Length > 0;

    /// <summary>Readable header line for the result block: the configured command, not a fake transcript.</summary>
    public string OutputHeaderText => SelectedCommand is { } command
        ? command.IsLocalTransform
            ? $"$ 本地转换 {command.Command}"
            : $"$ {command.Command}"
        : "";

    public string RunStateText
    {
        get => _runStateText;
        private set => SetProperty(ref _runStateText, value);
    }

    public string RunMessageText
    {
        get => _runMessageText;
        private set
        {
            if (SetProperty(ref _runMessageText, value))
            {
                OnPropertyChanged(nameof(HasRunMessage));
            }
        }
    }

    public bool HasRunMessage => RunMessageText.Length > 0;

    public string RunEndpointText
    {
        get => _runEndpointText;
        private set
        {
            if (SetProperty(ref _runEndpointText, value))
            {
                OnPropertyChanged(nameof(HasRunEndpoint));
            }
        }
    }

    public bool HasRunEndpoint => RunEndpointText.Length > 0;

    public string RunGlyph => _lastOutcome?.Glyph ?? (IsRunning ? "●" : "○");

    /// <summary>One of <c>success</c>, <c>info</c>, <c>warning</c>, <c>error</c>, <c>accent</c>.</summary>
    public string RunTone => _lastOutcome?.Tone ?? (IsRunning ? "accent" : "muted");

    /// <summary>Stage list for the progress block: connection, upload, execution (or one local step).</summary>
    public IReadOnlyList<MobileRunStageItem> RunStages => BuildRunStages();

    public bool HasRunStages => RunStages.Count > 0;

    private IReadOnlyList<MobileRunStageItem> BuildRunStages()
    {
        if (SelectedCommand is not { } command)
        {
            return [];
        }

        if (command.IsLocalTransform)
        {
            var state = IsRunning ? "active" : _lastOutcome is null ? "pending" : _lastOutcome.Succeeded ? "done" : "failed";
            return [new MobileRunStageItem("本地转换", "在手机上执行，输入不会离开设备", state)];
        }

        var stages = new List<MobileRunStageItem>
        {
            new("连接主机", TargetHostText, StageState(0)),
            new("上传输入", "输入会写入远端临时文件，再作为 --file1/--file2 传入", StageState(1)),
            new("执行命令", command.ListCommandText, StageState(2))
        };
        return stages;
    }

    /// <summary>Index of the stage the run is in: 0 connecting, 1 uploading, 2 running.</summary>
    private int CurrentStageIndex => _runStage switch
    {
        RemoteCommandsMobileContract.StageUploading => 1,
        RemoteCommandsMobileContract.StageRunning => 2,
        _ => 0
    };

    private string StageState(int index)
    {
        var current = CurrentStageIndex;
        if (_lastOutcome is { } outcome && !IsRunning)
        {
            if (outcome.Succeeded)
            {
                return "done";
            }

            if (outcome.Cancelled)
            {
                return index < current ? "done" : "pending";
            }

            return index < current ? "done" : index == current ? "failed" : "pending";
        }

        return index < current ? "done" : index == current ? "active" : "pending";
    }

    // ---------------------------------------------------------------- last result

    public bool HasLastResult => _lastOutcome is not null;

    public string LastResultTitle => _lastResultCommandLabel.Length > 0 ? _lastResultCommandLabel : "上次结果";

    public string LastResultMeta => _lastResultAtText.Length > 0
        ? _lastResultAtText
        : History.Count > 0 ? History.LatestTimestamp : "";

    /// <summary>
    /// What the card shows when this page instance has not run anything yet: the module's real history, or
    /// an honest empty state. Never a fabricated sample result.
    /// </summary>
    public string LastResultEmptyText => History.Count > 0
        ? History.LatestText
        : "还没有在这台手机上运行过命令。";

    public bool HasLastResultHistory => !HasLastResult && History.Count > 0;

    public string LastResultStateText => _lastOutcome?.StateText ?? "";

    public string LastResultGlyph => _lastOutcome?.Glyph ?? "○";

    public string LastResultTone => _lastOutcome?.Tone ?? "muted";

    public string LastResultDetailText => _lastOutcome?.DetailText ?? "";

    /// <summary>First few output lines, so the card is readable without opening the full result.</summary>
    public string LastResultPreview
    {
        get
        {
            if (_lastOutcome is not { } outcome)
            {
                return "";
            }

            if (string.IsNullOrWhiteSpace(outcome.Output))
            {
                return string.IsNullOrWhiteSpace(outcome.DetailText) ? "（没有输出）" : outcome.DetailText;
            }

            var lines = outcome.Output
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => line.Trim().Length > 0)
                .Take(6)
                .ToArray();
            var preview = string.Join('\n', lines);
            return preview.Length > 400 ? preview[..400] + "…" : preview;
        }
    }

    // ---------------------------------------------------------------- pending host key

    public MobilePendingHostKey? PendingHostKey
    {
        get => _pendingHostKey;
        private set
        {
            if (SetProperty(ref _pendingHostKey, value))
            {
                OnPropertyChanged(nameof(HasPendingHostKey));
                OnPropertyChanged(nameof(PendingHostKeyEndpoint));
                OnPropertyChanged(nameof(PendingHostKeyReason));
                OnPropertyChanged(nameof(PendingHostKeyDetail));
                OnPropertyChanged(nameof(PendingHostKeyMessage));
                OnPropertyChanged(nameof(CanTrustPendingHostKey));
                OnPropertyChanged(nameof(CanRetry));
            }
        }
    }

    public bool HasPendingHostKey => PendingHostKey is not null;

    public string PendingHostKeyEndpoint => PendingHostKey?.EndpointText ?? "";

    public string PendingHostKeyReason => PendingHostKey?.ReasonText ?? "";

    public string PendingHostKeyDetail => PendingHostKey?.DetailText ?? "";

    public string PendingHostKeyMessage => PendingHostKey is { } pending && !string.IsNullOrWhiteSpace(pending.Message)
        ? pending.Message
        : "确认后只信任这一个指纹；主机以后换钥会再次询问。";

    /// <summary>The user must confirm the exact fingerprint before the accept button enables.</summary>
    public bool TrustFingerprintAcknowledged
    {
        get => _trustFingerprintAcknowledged;
        set
        {
            if (SetProperty(ref _trustFingerprintAcknowledged, value))
            {
                OnPropertyChanged(nameof(CanTrustPendingHostKey));
            }
        }
    }

    public bool CanTrustPendingHostKey =>
        PendingHostKey is { Fingerprint.Length: > 0 } && TrustFingerprintAcknowledged && !IsBusy && !IsRunning;

    // ---------------------------------------------------------------- host keys

    public IReadOnlyList<MobileHostKeyEntry> TrustedKeys
    {
        get => _trustedKeys;
        private set
        {
            if (SetProperty(ref _trustedKeys, value))
            {
                OnPropertyChanged(nameof(HasTrustedKeys));
            }
        }
    }

    public bool HasTrustedKeys => TrustedKeys.Count > 0;

    public MobileHostKeyEntry? PendingKeyRevocation
    {
        get => _pendingKeyRevocation;
        private set
        {
            if (SetProperty(ref _pendingKeyRevocation, value))
            {
                OnPropertyChanged(nameof(HasPendingKeyRevocation));
                OnPropertyChanged(nameof(KeyRevocationPrompt));
            }
        }
    }

    public bool HasPendingKeyRevocation => PendingKeyRevocation is not null;

    public string KeyRevocationPrompt => PendingKeyRevocation is { } key
        ? $"再次点击撤销 {key.EndpointText} 的指纹；下次连接会重新询问。"
        : "";

    // ---------------------------------------------------------------- history

    public MobileHistorySummary History
    {
        get => _history;
        private set
        {
            if (SetProperty(ref _history, value))
            {
                OnPropertyChanged(nameof(HistoryText));
                OnPropertyChanged(nameof(LastResultMeta));
                OnPropertyChanged(nameof(LastResultEmptyText));
                OnPropertyChanged(nameof(HasLastResultHistory));
            }
        }
    }

    public string HistoryText => $"{History.CountText} · {History.LatestText}";

    public bool HistoryClearPending
    {
        get => _historyClearPending;
        private set
        {
            if (SetProperty(ref _historyClearPending, value))
            {
                OnPropertyChanged(nameof(HistoryClearPrompt));
            }
        }
    }

    public string HistoryClearPrompt => HistoryClearPending
        ? "再次点击确认清空手机上的执行历史"
        : "清空执行历史";

    public MobileHostEntry? PendingRemoval
    {
        get => _pendingRemoval;
        private set
        {
            if (SetProperty(ref _pendingRemoval, value))
            {
                OnPropertyChanged(nameof(HasPendingRemoval));
                OnPropertyChanged(nameof(RemovalPrompt));
            }
        }
    }

    public bool HasPendingRemoval => PendingRemoval is not null;

    public string RemovalPrompt => PendingRemoval is { } host
        ? $"再次点击删除 {host.Alias}（同时删除其凭据与已确认指纹）"
        : "";

    public string CommandsFileStatus
    {
        get => _commandsFileStatus;
        private set => SetProperty(ref _commandsFileStatus, value);
    }

    public string SettingsSummary
    {
        get => _settingsSummary;
        private set => SetProperty(ref _settingsSummary, value);
    }

    // ---------------------------------------------------------------- command form

    public MobileCommandDefinition? EditingCommand
    {
        get => _editingCommand;
        private set
        {
            if (SetProperty(ref _editingCommand, value))
            {
                OnPropertyChanged(nameof(IsEditingExistingCommand));
                OnPropertyChanged(nameof(CommandFormTitle));
                OnPropertyChanged(nameof(CommandFormSubtitle));
                OnPropertyChanged(nameof(CommandDeleteLabel));
            }
        }
    }

    public bool IsEditingExistingCommand => EditingCommand is not null;

    public string CommandFormTitle => EditingCommand is { } command
        ? $"编辑 {command.Label}"
        : "添加远程命令";

    public string CommandFormSubtitle => EditingCommand is { } command
        ? $"id {command.Id} · 保存在共享 commands.yaml"
        : "保存在共享 commands.yaml，电脑和手机使用同一份目录。";

    public string CommandDeleteLabel => CommandDeletePending
        ? "再次点击删除该命令"
        : "从 commands.yaml 删除";

    /// <summary>Advanced fields (id, type, host, input hints) stay collapsed on a phone by default.</summary>
    public bool CommandFormAdvanced
    {
        get => _formAdvanced;
        private set
        {
            if (SetProperty(ref _formAdvanced, value))
            {
                OnPropertyChanged(nameof(CommandAdvancedToggleText));
            }
        }
    }

    public string CommandAdvancedToggleText => CommandFormAdvanced ? "收起更多设置" : "更多设置（标识、类型、输入提示）";

    public string FormId
    {
        get => _formId;
        set
        {
            if (SetProperty(ref _formId, value))
            {
                _idEditedByUser = value.Trim().Length > 0;
                OnPropertyChanged(nameof(CanSaveCommandForm));
            }
        }
    }

    public string FormLabel
    {
        get => _formLabel;
        set
        {
            if (SetProperty(ref _formLabel, value))
            {
                if (!_idEditedByUser)
                {
                    _formId = MobileCommandsYamlEditor.SuggestId(value, FormCommand);
                    OnPropertyChanged(nameof(FormId));
                }

                OnPropertyChanged(nameof(CanSaveCommandForm));
            }
        }
    }

    public string FormCommand
    {
        get => _formCommand;
        set
        {
            if (SetProperty(ref _formCommand, value))
            {
                if (!_idEditedByUser)
                {
                    _formId = MobileCommandsYamlEditor.SuggestId(FormLabel, value);
                    OnPropertyChanged(nameof(FormId));
                }

                OnPropertyChanged(nameof(CanSaveCommandForm));
            }
        }
    }

    public string FormDescription
    {
        get => _formDescription;
        set => SetProperty(ref _formDescription, value);
    }

    /// <summary>True for a command that runs on the computer; false for a phone-local transform.</summary>
    public bool FormUsesRemoteHost
    {
        get => _formUsesRemoteHost;
        set
        {
            if (SetProperty(ref _formUsesRemoteHost, value))
            {
                OnPropertyChanged(nameof(FormUsesLocalTransform));
                OnPropertyChanged(nameof(FormTypeHint));
            }
        }
    }

    public bool FormUsesLocalTransform
    {
        get => !_formUsesRemoteHost;
        set => FormUsesRemoteHost = !value;
    }

    public string FormTypeHint => FormUsesRemoteHost
        ? "在电脑上通过 SSH 执行：需要主机映射与凭据。"
        : "在手机上本地转换：命令必须是模块里已有的转换实现，否则执行时会说明缺少实现。";

    public string FormHost
    {
        get => _formHost;
        set => SetProperty(ref _formHost, value);
    }

    public string FormInput1Label
    {
        get => _formInput1Label;
        set => SetProperty(ref _formInput1Label, value);
    }

    public string FormInput1Placeholder
    {
        get => _formInput1Placeholder;
        set => SetProperty(ref _formInput1Placeholder, value);
    }

    public string FormInput2Label
    {
        get => _formInput2Label;
        set => SetProperty(ref _formInput2Label, value);
    }

    public string FormInput2Placeholder
    {
        get => _formInput2Placeholder;
        set => SetProperty(ref _formInput2Placeholder, value);
    }

    public bool FormShowSecondInput
    {
        get => _formShowSecondInput;
        set => SetProperty(ref _formShowSecondInput, value);
    }

    public string CommandFormMessage
    {
        get => _commandFormMessage;
        private set
        {
            if (SetProperty(ref _commandFormMessage, value))
            {
                OnPropertyChanged(nameof(HasCommandFormMessage));
            }
        }
    }

    public bool HasCommandFormMessage => CommandFormMessage.Length > 0;

    public bool CanSaveCommandForm =>
        !IsBusy &&
        FormLabel.Trim().Length > 0 &&
        FormCommand.Trim().Length > 0 &&
        MobileCommandsYamlEditor.IsValidIdentifier(FormId);

    public string CommandFormSaveLabel => EditingCommand is null ? "保存新命令" : "保存修改";

    public bool CommandDeletePending
    {
        get => _commandDeletePending;
        private set
        {
            if (SetProperty(ref _commandDeletePending, value))
            {
                OnPropertyChanged(nameof(CommandDeleteLabel));
            }
        }
    }

    /// <summary>True while the form holds edits that a back press would drop.</summary>
    public bool CommandFormDirty =>
        FormLabel.Trim().Length > 0 ||
        FormCommand.Trim().Length > 0 ||
        FormDescription.Trim().Length > 0 ||
        FormHost.Trim().Length > 0;

    // ---------------------------------------------------------------- catalog sheet

    /// <summary>Editor text for the shared commands.yaml. Editing marks the section dirty.</summary>
    public string CatalogYaml
    {
        get => _catalogYaml;
        set
        {
            if (SetProperty(ref _catalogYaml, value))
            {
                CatalogDirty = true;
                OnPropertyChanged(nameof(CanSaveCatalog));
            }
        }
    }

    public bool CatalogDirty
    {
        get => _catalogDirty;
        private set
        {
            if (SetProperty(ref _catalogDirty, value))
            {
                OnPropertyChanged(nameof(CatalogDirtyText));
                OnPropertyChanged(nameof(CanSaveCatalog));
            }
        }
    }

    public string CatalogDirtyText => CatalogDirty ? "有未保存的修改" : "与模块当前内容一致";

    public string CatalogYamlStatus
    {
        get => _catalogYamlStatus;
        private set => SetProperty(ref _catalogYamlStatus, value);
    }

    public string CatalogSaveMessage
    {
        get => _catalogSaveMessage;
        private set
        {
            if (SetProperty(ref _catalogSaveMessage, value))
            {
                OnPropertyChanged(nameof(HasCatalogSaveMessage));
            }
        }
    }

    public bool HasCatalogSaveMessage => CatalogSaveMessage.Length > 0;

    public bool CanSaveCatalog => !IsBusy && CatalogDirty && CatalogYaml.Trim().Length > 0;

    public bool CatalogReloadPending
    {
        get => _catalogReloadPending;
        private set
        {
            if (SetProperty(ref _catalogReloadPending, value))
            {
                OnPropertyChanged(nameof(CatalogReloadLabel));
            }
        }
    }

    public string CatalogReloadLabel => CatalogReloadPending ? "再次点击放弃修改并重新载入" : "重新载入";

    // ---------------------------------------------------------------- editable settings

    public string SettingsDefaultHost
    {
        get => _settingsDefaultHost;
        set => SetSetting(ref _settingsDefaultHost, value, nameof(SettingsDefaultHost));
    }

    public string SettingsKnownHosts
    {
        get => _settingsKnownHosts;
        set => SetSetting(ref _settingsKnownHosts, value, nameof(SettingsKnownHosts));
    }

    public string SettingsRetention
    {
        get => _settingsRetention;
        set => SetSetting(ref _settingsRetention, value, nameof(SettingsRetention));
    }

    public string SettingsCondaExecutable
    {
        get => _settingsCondaExecutable;
        set => SetSetting(ref _settingsCondaExecutable, value, nameof(SettingsCondaExecutable));
    }

    public string SettingsTimeoutMinutes
    {
        get => _settingsTimeoutMinutes;
        set => SetSetting(ref _settingsTimeoutMinutes, value, nameof(SettingsTimeoutMinutes));
    }

    public bool SettingsDirty
    {
        get => _settingsDirty;
        private set
        {
            if (SetProperty(ref _settingsDirty, value))
            {
                OnPropertyChanged(nameof(SettingsDirtyText));
                OnPropertyChanged(nameof(CanSaveSettings));
            }
        }
    }

    public string SettingsDirtyText => SettingsDirty ? "有未保存的修改" : "已保存";

    public bool CanSaveSettings => SettingsDirty;

    public string SettingsMessage
    {
        get => _settingsMessage;
        private set
        {
            if (SetProperty(ref _settingsMessage, value))
            {
                OnPropertyChanged(nameof(HasSettingsMessage));
            }
        }
    }

    public bool HasSettingsMessage => SettingsMessage.Length > 0;

    /// <summary>All five keys are stored by the module; the surface writes no settings file itself.</summary>
    public string ModuleSettingsHint => "修改完成后点保存，模块会用与电脑相同的校验写入共享设置。";

    // ---------------------------------------------------------------- connection form

    public string FormAlias
    {
        get => _formAlias;
        set => SetProperty(ref _formAlias, value);
    }

    public string FormRealHost
    {
        get => _formRealHost;
        set => SetProperty(ref _formRealHost, value);
    }

    public string FormPort
    {
        get => _formPort;
        set => SetProperty(ref _formPort, value);
    }

    public string FormUsername
    {
        get => _formUsername;
        set => SetProperty(ref _formUsername, value);
    }

    public bool FormUsesPassword
    {
        get => _formUsesPassword;
        set
        {
            if (SetProperty(ref _formUsesPassword, value))
            {
                OnPropertyChanged(nameof(FormUsesPrivateKey));
                OnPropertyChanged(nameof(FormAuthText));
            }
        }
    }

    public bool FormUsesPrivateKey
    {
        get => !_formUsesPassword;
        set => FormUsesPassword = !value;
    }

    public string FormAuthText => FormUsesPassword
        ? "密码只写入系统凭据库，页面不会回显。"
        : "私钥内容只写入系统凭据库，页面不会回显。";

    public string FormPassword
    {
        get => _formPassword;
        set => SetProperty(ref _formPassword, value);
    }

    public string FormPrivateKey
    {
        get => _formPrivateKey;
        set => SetProperty(ref _formPrivateKey, value);
    }

    public string FormPassphrase
    {
        get => _formPassphrase;
        set => SetProperty(ref _formPassphrase, value);
    }

    public string FormMessage
    {
        get => _formMessage;
        private set
        {
            if (SetProperty(ref _formMessage, value))
            {
                OnPropertyChanged(nameof(HasFormMessage));
            }
        }
    }

    public bool HasFormMessage => FormMessage.Length > 0;

    // ---------------------------------------------------------------- lifecycle

    public void Activate()
    {
        _events ??= _client.Subscribe(OnModuleEvent);
        _ = RefreshAsync();
    }

    public void Deactivate()
    {
        _events?.Dispose();
        _events = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Deactivate();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    // ---------------------------------------------------------------- refresh

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RefreshCoreAsync().ConfigureAwait(true);
            SetFeedback("", "info");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = "读取状态失败";
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var state = await _client.StatusAsync(_lifetime.Token).ConfigureAwait(true);
        ApplyState(state);

        var catalog = await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true);
        Commands = catalog.Commands;
        CatalogError = catalog.Error;
        if (CommandsPath.Length == 0)
        {
            CommandsPath = catalog.CommandsPath;
        }

        ApplyCatalogText(catalog);
        SelectDefaultCommand();

        ApplyHosts(await _client.HostsAsync(_lifetime.Token).ConfigureAwait(true));
        TrustedKeys = await _client.HostKeysAsync(_lifetime.Token).ConfigureAwait(true);
        History = await _client.HistoryAsync(_lifetime.Token).ConfigureAwait(true);
        RefreshCommandsFileStatus();
    }

    /// <summary>Cheap slice refresh used by module events: no polling, only what the event changed.</summary>
    private async Task RefreshCatalogAsync()
    {
        try
        {
            var catalog = await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true);
            Commands = catalog.Commands;
            CatalogError = catalog.Error;
            ApplyCatalogText(catalog);
            SelectDefaultCommand();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback($"刷新命令目录失败：{exception.Message}", "warning");
        }
    }

    private void ApplyState(MobileModuleState state)
    {
        CurrentState = state;
        DefaultHost = state.DefaultHost;
        TransportAvailable = state.TransportAvailable;
        BackgroundAvailable = state.BackgroundAvailable;
        DataDirectory = state.DataDirectory;
        if (!string.IsNullOrWhiteSpace(state.CommandsPath))
        {
            CommandsPath = state.CommandsPath;
        }

        CommandsError = state.CommandsError;
        StatusText = state.TransportAvailable
            ? $"{state.CommandCount} 条命令 · {state.Hosts.Count} 台主机 · 受管 SSH 就绪"
            : "受管 SSH 不可用 · 本地转换仍可使用";
        StatusDetail = state.LastRunSummary;
        PageTitle = state.HasActiveRun && !IsRunning ? "远程命令 · 有调用在执行" : "远程命令";
        PageSubtitle = BuildConnectionSummary(state);

        if (state.HasActiveRun && !IsRunning)
        {
            // Another surface (or a previous page instance) started a run; report it without polling.
            RunPermissionRequired = false;
            RunStateText = $"模块里还有调用在执行（阶段 {Or(state.ActiveStage, "未知")}）";
            RunEndpointText = "";
        }

        if (!SettingsDirty)
        {
            ApplySettingsDraft(state);
        }
        else
        {
            SettingsCondaExecutable = state.CondaExecutable;
            SettingsTimeoutMinutes = state.CommandTimeoutMinutes.ToString();
        }

        SettingsSummary =
            $"共享 settings.json：默认主机 {Or(state.DefaultHost, "未设置")} · 别名 {state.KnownHostAliases.Count} 个 · " +
            $"历史保留 {state.HistoryRetention} 条 · 超时 {state.CommandTimeoutMinutes} 分钟 · CONDA_EXE {Or(state.CondaExecutable, "未设置")}";
    }

    /// <summary>
    /// Never reports "connected": the module has no connectivity probe, so the page states the configured
    /// target and whether its fingerprint is confirmed. The run establishes the connection on demand.
    /// </summary>
    private string BuildConnectionSummary(MobileModuleState state)
    {
        if (!state.TransportAvailable)
        {
            return "此设备没有受管 SSH 传输 · 只能使用本地转换命令";
        }

        var parts = new List<string>();
        if (state.Hosts.Count == 0)
        {
            parts.Add("还没有配置主机");
        }
        else
        {
            parts.Add(DefaultHost.Length > 0 ? $"默认主机 {DefaultHost}" : "未设置默认主机");
            parts.Add($"{state.Hosts.Count} 台已映射");
        }

        if (SelectedCommand is { IsLocalTransform: false } command)
        {
            var alias = command.Host.Length > 0 ? command.Host : SelectedHost.Length > 0 ? SelectedHost : DefaultHost;
            if (alias.Length > 0)
            {
                parts.Add(FingerprintState(alias, state));
            }
        }

        parts.Add("连接在运行时按需建立");
        return string.Join(" · ", parts);
    }

    private string FingerprintState(string alias, MobileModuleState state)
    {
        var host = state.Hosts.FirstOrDefault(entry =>
            string.Equals(entry.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (host is null)
        {
            return $"{alias} 尚未映射真实地址";
        }

        var trusted = state.TrustedHostKeys.Any(key =>
            string.Equals(key.Host, host.Host, StringComparison.OrdinalIgnoreCase) && key.Port == host.Port);
        return trusted ? $"{alias} 指纹已确认" : $"{alias} 首次连接需确认指纹";
    }

    private void ApplyHosts(MobileHostsSnapshot snapshot)
    {
        Hosts = snapshot.Hosts;
        if (!string.IsNullOrWhiteSpace(SelectedHost) &&
            Hosts.All(host => !string.Equals(host.Alias, SelectedHost, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedHost = "";
        }

        // Aliases referenced by commands.yaml or the shared settings, but not mapped on the phone.
        var known = new HashSet<string>(Hosts.Select(host => host.Alias), StringComparer.OrdinalIgnoreCase);
        var missing = snapshot.MissingAliases
            .Concat(CurrentState?.KnownHostAliases ?? [])
            .Concat(Commands.Where(command => command.Host.Length > 0).Select(command => command.Host))
            .Where(alias => !known.Contains(alias))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(alias => alias, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        MissingAliases = missing;
        if (snapshot.LoadFailed)
        {
            SetFeedback("主机映射文件无法读取，已按空目录继续。", "warning");
        }
    }

    private MobileModuleState? CurrentState { get; set; }

    private void SelectDefaultCommand()
    {
        // A refresh replaces the records, so the selected command must be re-bound to the new instance:
        // otherwise the run sheet would keep showing the command text from before an edit.
        if (SelectedCommand is { } current &&
            Commands.FirstOrDefault(command => string.Equals(command.Id, current.Id, StringComparison.OrdinalIgnoreCase))
                is { } refreshed)
        {
            SelectedCommand = refreshed;
            return;
        }

        SelectedCommand = Commands.FirstOrDefault(command =>
                              string.Equals(command.Id, _lastSelectedCommandId, StringComparison.OrdinalIgnoreCase))
                          ?? Commands.FirstOrDefault();
    }

    private string? _lastSelectedCommandId;

    // ---------------------------------------------------------------- actions: run

    /// <summary>Opens the run sheet for one catalog command (a tap on a list row).</summary>
    public void OpenRunSheet(MobileCommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        _lastSelectedCommandId = command.Id;
        SelectedCommand = command;
        _lastOutcome = null;
        _lastResultCommandLabel = "";
        _lastResultAtText = "";
        RunMessageText = "";
        RunPermissionRequired = false;
        PendingHostKey = null;
        TrustFingerprintAcknowledged = false;
        _runStage = "";
        OutputText = "";
        _output.Clear();

        // Reopening the same command keeps the inputs of the last attempt; a different command starts clean.
        if (_lastRunRequest is { } request && string.Equals(request.CommandId, command.Id, StringComparison.Ordinal))
        {
            SelectedHost = request.Host;
            Input1 = request.Input1;
            Input2 = request.Input2;
            ShowSecondInput = request.SecondInput;
        }
        else
        {
            SelectedHost = "";
            Input1 = "";
            Input2 = "";
            ShowSecondInput = command.ShowSecondInput;
        }

        RunStateText = command.IsLocalTransform ? "尚未运行（本地转换）" : "尚未运行";
        RunEndpointText = "";
        Sheet = MobileSheet.Run;
        NotifyRunChanged();
    }

    /// <summary>Reopens the sheet for the previous result, without starting a new run.</summary>
    public void OpenLastResultSheet()
    {
        if (_lastOutcome is null)
        {
            return;
        }

        Sheet = MobileSheet.Run;
    }

    public void SelectCommand(MobileCommandDefinition? command)
    {
        if (command is null)
        {
            return;
        }

        OpenRunSheet(command);
    }

    public async Task RunAsync()
    {
        if (IsRunning || SelectedCommand is not { } command)
        {
            return;
        }

        if (!TransportAvailable && !command.IsLocalTransform)
        {
            SetFeedback(TransportWarning, "warning");
            return;
        }

        if (!command.IsLocalTransform && !HasRunnableHost(command))
        {
            RunStateText = "缺少主机";
            RunMessageText = "这条命令需要 SSH 主机：请先在“服务器连接”里映射别名并保存凭据。";
            RunPermissionRequired = false;
            SetFeedback("还没有可用于执行的主机映射。", "warning");
            Sheet = MobileSheet.Run;
            RequestConnectionSheet();
            return;
        }

        var invocationId = Guid.NewGuid().ToString("N");
        _activeInvocationId = invocationId;
        _lastSelectedCommandId = command.Id;
        _lastRunRequest = new MobileRunRequest(command.Id, SelectedHost, Input1, Input2, ShowSecondInput);
        _output.Clear();
        OutputText = "";
        RunMessageText = "";
        RunPermissionRequired = false;
        PendingHostKey = null;
        TrustFingerprintAcknowledged = false;
        _lastOutcome = null;
        _runStage = command.IsLocalTransform ? "" : RemoteCommandsMobileContract.StageConnecting;
        RunStateText = command.IsLocalTransform ? "正在本地转换…" : "正在连接主机…";
        RunEndpointText = command.IsLocalTransform ? "本地转换（不经过 SSH）" : TargetHostText;
        IsRunning = true;
        Sheet = MobileSheet.Run;
        NotifyRunChanged();

        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
            try
            {
                var outcome = await _client.RunAsync(
                    command.Id,
                    SelectedHost,
                    Input1,
                    Input2,
                    ShowSecondInput,
                    invocationId,
                    _lifetime.Token).ConfigureAwait(true);
                ApplyOutcome(command, outcome);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            _lastOutcome = new MobileRunOutcome(
                RemoteCommandsMobileContract.StateCancelled, "已取消", null, "", "", "", OutputText, null, "");
            RunStateText = "已取消";
        }
        catch (MobileModuleException exception)
        {
            RunPermissionRequired = exception.PermissionRequired;
            _lastOutcome = new MobileRunOutcome(
                RemoteCommandsMobileContract.StateFailed, exception.Message, null, "", "", "", OutputText, null, "");
            RunStateText = exception.PermissionRequired ? "等待系统授权" : "执行失败";
            RunMessageText = exception.Message;
            SetFeedback(exception.Message, exception.PermissionRequired ? "warning" : "error");
        }
        catch (Exception exception)
        {
            _lastOutcome = new MobileRunOutcome(
                RemoteCommandsMobileContract.StateFailed, exception.Message, null, "", "", "", OutputText, null, "");
            RunStateText = "执行失败";
            RunMessageText = exception.Message;
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            _activeInvocationId = null;
            IsRunning = false;
            _lastResultCommandLabel = command.Label;
            _lastRunAt = DateTimeOffset.Now;
            _lastResultAtText = _lastRunAt.Value.ToString("HH:mm");
            NotifyRunChanged();
            await RefreshAfterRunAsync().ConfigureAwait(true);
        }
    }

    private bool HasRunnableHost(MobileCommandDefinition command)
    {
        if (command.Host.Length > 0)
        {
            return Hosts.Any(host => string.Equals(host.Alias, command.Host, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedHost.Length > 0)
        {
            return true;
        }

        return DefaultHost.Length > 0 && Hosts.Count > 0;
    }

    /// <summary>Retries the last request with exactly the same inputs; the module sees a new invocation.</summary>
    public async Task RetryAsync()
    {
        if (_lastRunRequest is not { } request || IsRunning || IsBusy)
        {
            return;
        }

        var command = Commands.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, request.CommandId, StringComparison.OrdinalIgnoreCase));
        if (command is null)
        {
            SetFeedback("上次运行的命令已不在 commands.yaml 中。", "warning");
            return;
        }

        SelectedCommand = command;
        SelectedHost = request.Host;
        Input1 = request.Input1;
        Input2 = request.Input2;
        ShowSecondInput = request.SecondInput;
        PendingHostKey = null;
        TrustFingerprintAcknowledged = false;
        await RunAsync().ConfigureAwait(true);
    }

    private void ApplyOutcome(MobileCommandDefinition command, MobileRunOutcome outcome)
    {
        // The terminal payload always carries the bounded full output; streamed lines are reconciled
        // against it so a dropped event cannot leave the page with a partial log.
        if (!string.IsNullOrEmpty(outcome.Output))
        {
            _output.Clear();
            _output.Append(outcome.Output);
            OutputText = Trim(_output);
        }

        _lastOutcome = outcome;
        RunPermissionRequired = false;
        RunStateText = outcome.StateText;
        RunMessageText = outcome.DetailText;
        RunEndpointText = command.IsLocalTransform
            ? "本地转换（不经过 SSH）"
            : string.IsNullOrWhiteSpace(outcome.DeviceText) ? RunEndpointText : $"执行设备：{outcome.DeviceText}";

        if (outcome.PendingHostKey is { } pending)
        {
            PendingHostKey = pending with { Port = pending.Port ?? ResolvePort(pending.Host) };
            RunStateText = "等待确认主机密钥";
            _runStage = RemoteCommandsMobileContract.StageConnecting;
            SetFeedback("主机密钥尚未确认，请核对指纹后确认。", "warning");
            return;
        }

        PendingHostKey = null;
        _runStage = outcome.Succeeded ? RemoteCommandsMobileContract.StageRunning : _runStage;
        SetFeedback(
            outcome.Succeeded ? "执行完成" : outcome.Cancelled ? "已取消" : outcome.DetailText,
            outcome.Succeeded ? "success" : outcome.Cancelled ? "info" : "error");
    }

    private async Task RefreshAfterRunAsync()
    {
        try
        {
            History = await _client.HistoryAsync(_lifetime.Token).ConfigureAwait(true);
            ApplyHosts(await _client.HostsAsync(_lifetime.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback($"执行后刷新失败：{exception.Message}", "warning");
        }
    }

    public async Task CancelAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            var cancelled = await _client.CancelAsync(_activeInvocationId, _lifetime.Token).ConfigureAwait(true);
            SetFeedback(cancelled ? "已请求取消执行。" : "没有正在执行的调用。", cancelled ? "info" : "warning");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
        }
    }

    /// <summary>
    /// Confirms the single pending fingerprint, then repeats the interrupted run with the same inputs.
    /// This is the only path that writes a trusted key, and it always uses the exact SHA256 the module
    /// reported for this host.
    /// </summary>
    public async Task TrustPendingHostKeyAndRunAsync()
    {
        if (PendingHostKey is not { } pending || !CanTrustPendingHostKey)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.AcceptHostKeyAsync(
                pending.Host,
                pending.Port ?? ResolvePort(pending.Host),
                pending.HostKeyName,
                pending.Fingerprint,
                _lifetime.Token).ConfigureAwait(true);
            TrustedKeys = await _client.HostKeysAsync(_lifetime.Token).ConfigureAwait(true);
            PendingHostKey = null;
            TrustFingerprintAcknowledged = false;
            SetFeedback($"已确认 {pending.EndpointText} 的指纹。", "success");

            if (_lastRunRequest is { } request)
            {
                SelectedHost = request.Host;
                Input1 = request.Input1;
                Input2 = request.Input2;
                ShowSecondInput = request.SecondInput;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await RunAsync().ConfigureAwait(true);
    }

    public void DismissPendingHostKey()
    {
        PendingHostKey = null;
        TrustFingerprintAcknowledged = false;
        NotifyRunChanged();
        SetFeedback("未确认主机密钥，执行保持中断。", "info");
    }

    // ---------------------------------------------------------------- actions: command form

    public void OpenCommandForm(MobileCommandDefinition? command)
    {
        EditingCommand = command;
        CommandDeletePending = false;
        CommandFormMessage = "";
        _idEditedByUser = command is not null;
        if (command is null)
        {
            var draft = MobileCommandsYamlEditor.NewDraft();
            _formId = "";
            FormLabel = "";
            FormCommand = "";
            FormDescription = draft.Description;
            FormUsesRemoteHost = true;
            FormHost = "";
            FormInput1Label = draft.Input1Label;
            FormInput1Placeholder = draft.Input1Placeholder;
            FormInput2Label = draft.Input2Label;
            FormInput2Placeholder = draft.Input2Placeholder;
            FormShowSecondInput = false;
            CommandFormAdvanced = false;
        }
        else
        {
            FormId = command.Id;
            FormLabel = command.Label;
            FormCommand = command.Command;
            FormDescription = command.Description;
            FormUsesRemoteHost = !command.IsLocalTransform;
            FormHost = command.Host;
            FormInput1Label = command.Input1Label;
            FormInput1Placeholder = command.Input1Placeholder;
            FormInput2Label = command.Input2Label;
            FormInput2Placeholder = command.Input2Placeholder;
            FormShowSecondInput = command.ShowSecondInput;
            CommandFormAdvanced = true;
        }

        _idEditedByUser = command is not null;
        Sheet = MobileSheet.CommandEditor;
        NotifyCommandFormChanged();
    }

    /// <summary>Opens the form editor for the command currently loaded in the run sheet.</summary>
    public void EditSelectedCommand()
    {
        if (SelectedCommand is { } command)
        {
            OpenCommandForm(command);
        }
    }

    public void ToggleCommandFormAdvanced() => CommandFormAdvanced = !CommandFormAdvanced;

    public void CancelCommandForm()
    {
        CommandDeletePending = false;
        CloseSheet();
    }

    /// <summary>
    /// Writes the form into the shared commands.yaml through the module. The document text is produced
    /// here (preserving every other entry) and validated by the module with the shipped parser, so a
    /// rejected edit keeps the user's input and reports the parser's own message.
    /// </summary>
    public async Task SaveCommandFormAsync()
    {
        if (IsBusy)
        {
            CommandFormMessage = "正在刷新，请稍后再保存。";
            return;
        }

        var draft = new MobileCommandDraft(
            FormId.Trim(),
            FormLabel.Trim(),
            FormCommand.Trim(),
            FormDescription.Trim(),
            FormUsesRemoteHost ? "shell" : "py",
            FormHost.Trim(),
            FormInput1Label.Trim(),
            FormInput1Placeholder.Trim(),
            FormInput2Label.Trim(),
            FormInput2Placeholder.Trim(),
            FormShowSecondInput);

        var originalId = EditingCommand?.Id;
        var yaml = await CurrentCatalogYamlAsync().ConfigureAwait(true);
        if (yaml is null)
        {
            CommandFormMessage = "无法读取当前 commands.yaml，请先重新载入命令目录。";
            SetFeedback(CommandFormMessage, "error");
            return;
        }

        var written = originalId is null
            ? MobileCommandsYamlEditor.TryAdd(yaml, draft, out var updated, out var error)
            : MobileCommandsYamlEditor.TryUpdate(yaml, originalId, draft, out updated, out error);
        if (!written)
        {
            CommandFormMessage = error;
            SetFeedback(error, "error");
            return;
        }

        var saved = await SaveCatalogTextAsync(updated).ConfigureAwait(true);
        if (!saved.Success)
        {
            CommandFormMessage = saved.Error;
            SetFeedback(saved.Error, "error");
            return;
        }

        CommandFormMessage = originalId is null
            ? $"已添加 {draft.Label}。"
            : $"已更新 {draft.Label}。";
        SetFeedback(CommandFormMessage, "success");
        EditingCommand = null;
        CloseSheet();
    }

    public void RequestCommandRemoval()
    {
        if (EditingCommand is null)
        {
            return;
        }

        CommandDeletePending = true;
        CommandFormMessage = $"再次点击将删除 {EditingCommand.Label}；电脑上的同一份目录也会少掉这条命令。";
        OnPropertyChanged(nameof(CommandDeleteLabel));
    }

    public async Task ConfirmCommandRemovalAsync()
    {
        if (EditingCommand is not { } command || !CommandDeletePending || IsBusy)
        {
            return;
        }

        var yaml = await CurrentCatalogYamlAsync().ConfigureAwait(true);
        if (yaml is null)
        {
            CommandFormMessage = "无法读取当前 commands.yaml，请先重新载入命令目录。";
            return;
        }

        if (!MobileCommandsYamlEditor.TryRemove(yaml, command.Id, out var updated, out var error))
        {
            CommandFormMessage = error;
            SetFeedback(error, "error");
            return;
        }

        var saved = await SaveCatalogTextAsync(updated).ConfigureAwait(true);
        if (!saved.Success)
        {
            CommandFormMessage = saved.Error;
            SetFeedback(saved.Error, "error");
            return;
        }

        CommandDeletePending = false;
        EditingCommand = null;
        SetFeedback($"已删除命令 {command.Label}。", "success");
        CloseSheet();
    }

    private void NotifyCommandFormChanged()
    {
        OnPropertyChanged(nameof(CanSaveCommandForm));
        OnPropertyChanged(nameof(CommandFormSaveLabel));
        OnPropertyChanged(nameof(CommandFormTitle));
        OnPropertyChanged(nameof(CommandFormSubtitle));
    }

    // ---------------------------------------------------------------- actions: connection

    public void OpenConnectionSheet()
    {
        FormMessage = "";
        Sheet = MobileSheet.Connection;
    }

    public void RequestConnectionSheet() => Sheet = MobileSheet.Connection;

    public async Task SaveHostAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var alias = FormAlias.Trim();
        var host = FormRealHost.Trim();
        var username = FormUsername.Trim();
        if (alias.Length == 0 || host.Length == 0 || username.Length == 0)
        {
            FormMessage = "别名、真实主机与用户名都必须填写。";
            return;
        }

        if (!int.TryParse(FormPort.Trim(), out var port) || port is < 1 or > 65535)
        {
            FormMessage = "端口必须是 1-65535 之间的整数。";
            return;
        }

        var existing = Hosts.FirstOrDefault(entry =>
            string.Equals(entry.Alias, alias, StringComparison.OrdinalIgnoreCase));
        var credentialProvided = FormUsesPassword
            ? !string.IsNullOrEmpty(FormPassword)
            : !string.IsNullOrWhiteSpace(FormPrivateKey);
        if (!credentialProvided && existing?.CredentialConfigured != true)
        {
            FormMessage = FormUsesPassword
                ? "请填写密码：它只写入系统凭据库，不会保存到设置文件。"
                : "请粘贴 OpenSSH 私钥内容：它只写入系统凭据库。";
            return;
        }

        IsBusy = true;
        FormMessage = "";
        try
        {
            var snapshot = await _client.AddHostAsync(
                alias,
                host,
                port,
                username,
                FormUsesPassword ? RemoteCommandsMobileContract.AuthPassword : RemoteCommandsMobileContract.AuthPrivateKey,
                FormUsesPassword ? FormPassword : null,
                FormUsesPassword ? null : FormPrivateKey,
                FormUsesPassword ? null : FormPassphrase,
                _lifetime.Token).ConfigureAwait(true);
            ApplyHosts(snapshot);

            // Credential fields never stay on the page after the module stored them.
            FormPassword = "";
            FormPrivateKey = "";
            FormPassphrase = "";
            FormMessage = $"已保存 {alias} → {username}@{host}:{port}。凭据只在系统凭据库中。";
            SetFeedback($"已保存主机 {alias}。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            FormMessage = exception.Message;
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Config import: pre-fills the mapping form from an alias the shared settings or commands.yaml references.</summary>
    public void PrefillHost(string alias)
    {
        var existing = Hosts.FirstOrDefault(entry =>
            string.Equals(entry.Alias, alias, StringComparison.OrdinalIgnoreCase));
        FormAlias = alias;
        FormRealHost = existing?.Host ?? "";
        FormPort = (existing?.Port ?? RemoteCommandsMobileContract.DefaultPort).ToString();
        FormUsername = existing?.Username ?? "";
        FormUsesPassword = existing is null ||
                           string.Equals(existing.Auth, RemoteCommandsMobileContract.AuthPassword, StringComparison.OrdinalIgnoreCase);
        FormMessage = existing is null
            ? $"请填写 {alias} 的真实地址与账号；MPT 不会猜测地址，也不会自动信任主机密钥。"
            : $"正在更新 {alias}；留空凭据字段表示沿用系统凭据库里的现有值。";
        SetFeedback($"正在为 {alias} 编辑映射。", "info");
        Sheet = MobileSheet.Connection;
    }

    public void ClearHostForm()
    {
        FormAlias = "";
        FormRealHost = "";
        FormPort = RemoteCommandsMobileContract.DefaultPort.ToString();
        FormUsername = "";
        FormPassword = "";
        FormPrivateKey = "";
        FormPassphrase = "";
        FormMessage = "";
    }

    public void RequestHostRemoval(MobileHostEntry host)
    {
        PendingRemoval = host;
        SetFeedback(RemovalPrompt, "warning");
    }

    public void CancelHostRemoval() => PendingRemoval = null;

    public async Task ConfirmHostRemovalAsync()
    {
        if (PendingRemoval is not { } host || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.RemoveHostAsync(host.Alias, _lifetime.Token).ConfigureAwait(true);
            ApplyHosts(await _client.HostsAsync(_lifetime.Token).ConfigureAwait(true));
            TrustedKeys = await _client.HostKeysAsync(_lifetime.Token).ConfigureAwait(true);
            PendingRemoval = null;
            SetFeedback($"已删除主机 {host.Alias} 及其凭据与指纹。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Revoking trust is a two-tap action: one tap only arms the confirmation.</summary>
    public void RequestHostKeyRevocation(MobileHostKeyEntry key)
    {
        PendingKeyRevocation = key;
        SetFeedback(KeyRevocationPrompt, "warning");
    }

    public async Task RevokeHostKeyAsync(MobileHostKeyEntry key)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.RevokeHostKeyAsync(key.Host, key.Port, _lifetime.Token).ConfigureAwait(true);
            TrustedKeys = await _client.HostKeysAsync(_lifetime.Token).ConfigureAwait(true);
            PendingKeyRevocation = null;
            SetFeedback($"已撤销 {key.EndpointText} 的指纹，下次连接会重新询问。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RequestHistoryClear()
    {
        HistoryClearPending = true;
        SetFeedback(HistoryClearPrompt, "warning");
    }

    public void CancelHistoryClear() => HistoryClearPending = false;

    public async Task ConfirmHistoryClearAsync()
    {
        if (!HistoryClearPending || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.ClearHistoryAsync(_lifetime.Token).ConfigureAwait(true);
            History = await _client.HistoryAsync(_lifetime.Token).ConfigureAwait(true);
            HistoryClearPending = false;
            SetFeedback("已清空手机上的执行历史。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- actions: settings

    private void ApplySettingsDraft(MobileModuleState state)
    {
        _settingsDefaultHost = state.DefaultHost;
        _settingsKnownHosts = string.Join('\n', state.KnownHostAliases);
        _settingsRetention = state.HistoryRetention.ToString();
        _settingsCondaExecutable = state.CondaExecutable;
        _settingsTimeoutMinutes = state.CommandTimeoutMinutes.ToString();
        NotifySettingsDraftChanged();
    }

    /// <summary>Drops local settings edits and restores what the module currently has.</summary>
    public void DiscardSettingsEdits()
    {
        SettingsDirty = false;
        if (CurrentState is { } state)
        {
            ApplySettingsDraft(state);
        }

        SettingsMessage = "已还原为已保存的设置。";
    }

    private void SetSetting(ref string field, string value, string propertyName)
    {
        // The property name must be passed in: SetProperty's [CallerMemberName] would report
        // "SetSetting", leaving the two-way binding of that field without a source notification.
        if (SetProperty(ref field, value, propertyName))
        {
            SettingsDirty = true;
            OnPropertyChanged(nameof(CanSaveSettings));
        }
    }

    /// <summary>
    /// Pushes what the user currently sees in the five settings controls into the draft before a save.
    /// The click path therefore never depends on a binding write-back having happened, so the visible
    /// draft and the saved draft cannot drift apart.
    /// </summary>
    public void CommitSettingsDraft(
        string defaultHost,
        string knownHosts,
        string retention,
        string condaExecutable,
        string timeoutMinutes)
    {
        SettingsDefaultHost = defaultHost;
        SettingsKnownHosts = knownHosts;
        SettingsRetention = retention;
        SettingsCondaExecutable = condaExecutable;
        SettingsTimeoutMinutes = timeoutMinutes;
    }

    /// <summary>Re-asserts the draft to the view; used after a rejected save so both sides stay identical.</summary>
    private void NotifySettingsDraftChanged()
    {
        OnPropertyChanged(nameof(SettingsDefaultHost));
        OnPropertyChanged(nameof(SettingsKnownHosts));
        OnPropertyChanged(nameof(SettingsRetention));
        OnPropertyChanged(nameof(SettingsCondaExecutable));
        OnPropertyChanged(nameof(SettingsTimeoutMinutes));
    }

    /// <summary>
    /// Saves all five settings through <c>remote-commands-android.settings.update</c>. The module owns
    /// validation and persistence (the same path the Shell uses), so this surface never touches
    /// settings.json. A rejection keeps the draft exactly as typed and reports the module's message.
    /// </summary>
    public async Task SaveSettingsAsync()
    {
        if (IsBusy)
        {
            // Never drop the click silently: the previous message would look like a repeated failure.
            SettingsMessage = "正在刷新，请稍后再保存。";
            SetFeedback(SettingsMessage, "info");
            return;
        }

        var host = SettingsDefaultHost.Trim();
        if (host.Length > 0 && !RemoteCommandsStore.IsValidHost(host))
        {
            SettingsMessage = "默认主机别名不合法：只允许字母、数字、点、下划线和连字符。";
            return;
        }

        if (!int.TryParse(SettingsRetention.Trim(), out var retention) || retention is < 10 or > 5000)
        {
            SettingsMessage = "历史保留条数必须是 10-5000 之间的整数。";
            return;
        }

        var aliases = SettingsKnownHosts
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var invalidAlias = aliases.FirstOrDefault(alias => !RemoteCommandsStore.IsValidHost(alias));
        if (invalidAlias is not null)
        {
            SettingsMessage = $"别名 '{invalidAlias}' 不合法：只允许字母、数字、点、下划线和连字符。";
            return;
        }

        if (!int.TryParse(SettingsTimeoutMinutes.Trim(), out var timeout) || timeout is < 1 or > 1440)
        {
            SettingsMessage = "单次执行超时必须是 1-1440 分钟之间的整数。";
            return;
        }

        IsBusy = true;
        try
        {
            var values = new JsonObject
            {
                ["defaultHost"] = host,
                ["knownHosts"] = string.Join('\n', aliases),
                ["historyRetention"] = retention,
                ["condaExecutable"] = SettingsCondaExecutable.Trim(),
                ["commandTimeoutMinutes"] = timeout
            };

            await _client.UpdateSettingsAsync(values, _lifetime.Token).ConfigureAwait(true);

            // Only a complete save marks the draft clean; a rejected value keeps every edit on screen.
            SettingsDirty = false;
            await RefreshCoreAsync().ConfigureAwait(true);
            SettingsMessage = "设置已保存。";
            SetFeedback(SettingsMessage, "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SettingsMessage = $"保存设置失败（草稿已保留）：{exception.Message}";
            SetFeedback(exception.Message, "error");
            NotifySettingsDraftChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- actions: catalog

    /// <summary>
    /// Loads the editor from the module payload, falling back to the canonical file it named. An async
    /// status refresh must not silently discard unsaved YAML: only an explicit reload or a successful
    /// save replaces the draft (<paramref name="force"/>).
    /// </summary>
    private void ApplyCatalogText(MobileCatalogSnapshot catalog, bool force = false)
    {
        if (CatalogDirty && !force)
        {
            RefreshCommandsFileStatus();
            return;
        }

        var text = catalog.YamlText;
        if (text.Length == 0 && CommandsPath.Length > 0 && System.IO.File.Exists(CommandsPath))
        {
            try
            {
                var info = new FileInfo(CommandsPath);
                if (info.Length <= MaxCommandsFileBytes)
                {
                    text = System.IO.File.ReadAllText(CommandsPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CatalogYamlStatus = $"无法读取 commands.yaml：{exception.Message}";
            }
        }

        if (text.Length == 0)
        {
            text = RemoteCommandsYaml.DefaultCommandsYaml;
        }

        _catalogYaml = text;
        CatalogDirty = false;
        OnPropertyChanged(nameof(CatalogYaml));
        OnPropertyChanged(nameof(CanSaveCatalog));
        RefreshCommandsFileStatus();
    }

    /// <summary>
    /// The document the form editors start from. The module payload wins; otherwise the canonical file
    /// the module named is read. Returns null when neither is available, so a form edit can refuse
    /// instead of inventing a document.
    /// </summary>
    private async Task<string?> CurrentCatalogYamlAsync()
    {
        try
        {
            var catalog = await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true);
            if (catalog.YamlText.Length > 0)
            {
                return catalog.YamlText;
            }

            var path = catalog.CommandsPath.Length > 0 ? catalog.CommandsPath : CommandsPath;
            if (path.Length == 0)
            {
                return null;
            }

            if (!System.IO.File.Exists(path))
            {
                // A fresh install: the user is creating the first entry, so an empty document is honest.
                return "";
            }

            var info = new FileInfo(path);
            return info.Length <= MaxCommandsFileBytes ? System.IO.File.ReadAllText(path) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or MobileModuleException)
        {
            SetFeedback($"读取 commands.yaml 失败：{exception.Message}", "error");
            return null;
        }
    }

    /// <summary>Saves document text through the module and refreshes the catalog from the module again.</summary>
    private async Task<(bool Success, string Error)> SaveCatalogTextAsync(string content)
    {
        IsBusy = true;
        try
        {
            await _client.SaveCatalogAsync(content, _lifetime.Token).ConfigureAwait(true);
            var catalog = await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true);
            Commands = catalog.Commands;
            CatalogError = catalog.Error;
            ApplyCatalogText(catalog, force: true);
            SelectDefaultCommand();
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "保存已取消。");
        }
        catch (Exception exception)
        {
            // Validation errors are reported verbatim; the editor keeps the user's text.
            return (false, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenCatalogSheet()
    {
        CatalogSaveMessage = "";
        CatalogReloadPending = false;
        Sheet = MobileSheet.Catalog;
    }

    /// <summary>Drops local edits and reloads the catalog text the module/file currently holds.</summary>
    public async Task ReloadCatalogYamlAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (CatalogDirty && !CatalogReloadPending)
        {
            CatalogReloadPending = true;
            CatalogSaveMessage = "有未保存的修改：再次点击将放弃这些修改并重新载入。";
            return;
        }

        CatalogReloadPending = false;
        try
        {
            ApplyCatalogText(await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true), force: true);
            CatalogSaveMessage = "";
            SetFeedback("已重新载入 commands.yaml。", "info");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetFeedback(exception.Message, "error");
        }
    }

    /// <summary>Imports text (clipboard paste) into the editor; saving still goes through the module.</summary>
    public void ImportCatalogText(string text)
    {
        if (text.Length == 0)
        {
            CatalogSaveMessage = "剪贴板里没有文本。";
            return;
        }

        CatalogYaml = text;
        CatalogSaveMessage = "已粘贴命令配置，保存后生效。";
    }

    /// <summary>
    /// Saves through <c>remote-commands-android.catalog.save</c>: the module validates the document with
    /// the shipped parser and writes the canonical file, so an invalid edit is rejected here and the
    /// user's text stays in the editor.
    /// </summary>
    public async Task SaveCatalogAsync()
    {
        if (!CanSaveCatalog)
        {
            return;
        }

        var saved = await SaveCatalogTextAsync(CatalogYaml).ConfigureAwait(true);
        if (saved.Success)
        {
            CatalogSaveMessage = $"已保存 commands.yaml（{Commands.Count} 个命令）。";
            SetFeedback("命令配置已保存。", "success");
            return;
        }

        CatalogSaveMessage = $"保存失败：{saved.Error}";
        SetFeedback(saved.Error, "error");
    }

    /// <summary>
    /// Validates the shared commands.yaml with the shipped parser, read-only. The module remains the
    /// only writer; this exists so a broken file is explained on the phone instead of silently listing
    /// zero commands.
    /// </summary>
    public void RefreshCommandsFileStatus()
    {
        var path = CommandsPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            CommandsFileStatus = "命令配置尚未加载。";
            return;
        }

        try
        {
            if (!System.IO.File.Exists(path))
            {
                CommandsFileStatus = "尚未创建命令配置，保存后即可使用。";
                return;
            }

            var info = new FileInfo(path);
            if (info.Length > MaxCommandsFileBytes)
            {
                CommandsFileStatus = $"commands.yaml 过大（{info.Length} 字节），未在手机上校验。";
                return;
            }

            var text = System.IO.File.ReadAllText(path);
            CommandsFileStatus = RemoteCommandsYaml.TryValidate(text, out var error)
                ? $"commands.yaml 校验通过（{Commands.Count} 个命令，{info.LastWriteTime:yyyy-MM-dd HH:mm} 更新）"
                : $"commands.yaml 校验失败：{error}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CommandsFileStatus = $"无法读取 commands.yaml：{exception.Message}";
        }
    }

    // ---------------------------------------------------------------- module events

    private void OnModuleEvent(MptSurfaceEvent surfaceEvent)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnModuleEvent(surfaceEvent));
            return;
        }

        var payload = surfaceEvent.Payload;
        var invocationId = RemoteCommandsMobileJson.Text(payload, "invocationId");
        var ownRun = string.Equals(invocationId, _activeInvocationId, StringComparison.Ordinal);
        if (invocationId.Length > 0 && !ownRun)
        {
            // A host that does not let the page own the invocation id also hides it from the stream; while
            // this page has exactly one run in flight (the module allows one active run), its events still
            // belong to that run. Otherwise only the invocation this page started owns the output pane.
            if (_client.OwnsInvocationId || !IsRunning)
            {
                return;
            }
        }

        switch (surfaceEvent.Type)
        {
            case RemoteCommandsMobileContract.EventCommandOutput:
                foreach (var line in ReadOutputLines(payload))
                {
                    AppendOutput(line);
                }

                break;

            case RemoteCommandsMobileContract.EventRunStage:
                ApplyStage(RemoteCommandsMobileJson.Text(payload, "stage"));
                break;

            case RemoteCommandsMobileContract.EventRunStarted:
                IsRunning = true;
                _runStage = RemoteCommandsMobileContract.StageConnecting;
                RunStateText = $"已开始：{RemoteCommandsMobileJson.Text(payload, "label")}";
                RunEndpointText = RemoteCommandsMobileJson.Text(payload, "resolvedHost") is { Length: > 0 } resolved
                    ? $"执行设备：{resolved}"
                    : RunEndpointText;
                NotifyRunChanged();
                break;

            case RemoteCommandsMobileContract.EventHostKeyPending:
                var pending = RemoteCommandsMobileJson.Pending(payload, payload);
                PendingHostKey = pending with { Port = pending.Port ?? ResolvePort(pending.Host) };
                TrustFingerprintAcknowledged = false;
                RunStateText = "等待确认主机密钥";
                NotifyRunChanged();
                break;

            case RemoteCommandsMobileContract.EventRunFinished:
                RunStateText = RemoteCommandsMobileJson.Text(payload, "state") switch
                {
                    RemoteCommandsMobileContract.StateSucceeded => "运行完成",
                    RemoteCommandsMobileContract.StateCancelled => "已取消",
                    RemoteCommandsMobileContract.StateHostKeyRequired => "等待确认主机密钥",
                    _ => "运行结束"
                };
                NotifyRunChanged();
                break;

            // Event-driven refresh: the module tells the page when shared state changed, so the page
            // never has to poll it.
            case RemoteCommandsMobileContract.EventCatalogSaved:
                _ = RefreshCatalogAsync();
                SetFeedback(EventMessage(payload, "命令目录已在模块里更新。"), "info");
                break;

            case RemoteCommandsMobileContract.EventSettingsUpdated:
                SetFeedback(EventMessage(payload, "设置已在模块里更新。"), "info");
                break;

            case RemoteCommandsMobileContract.EventHostUpdated:
            case RemoteCommandsMobileContract.EventHostRemoved:
            case RemoteCommandsMobileContract.EventHostKeyAccepted:
            case RemoteCommandsMobileContract.EventHostKeyRevoked:
            case RemoteCommandsMobileContract.EventHistoryCleared:
                SetFeedback(RemoteCommandsMobileJson.Text(payload, "message"), "info");
                break;
        }
    }

    private static string EventMessage(System.Text.Json.Nodes.JsonObject payload, string fallback) =>
        RemoteCommandsMobileJson.Text(payload, "message") is { Length: > 0 } message ? message : fallback;

    private void ApplyStage(string stage)
    {
        _runStage = stage;
        RunStateText = stage switch
        {
            RemoteCommandsMobileContract.StageConnecting => "正在连接主机…",
            RemoteCommandsMobileContract.StageUploading => "正在上传输入…",
            RemoteCommandsMobileContract.StageRunning => "远端命令执行中…",
            _ => $"执行中（{Or(stage, "未知阶段")}）"
        };
        NotifyRunChanged();
    }

    private void NotifyRunChanged()
    {
        OnPropertyChanged(nameof(RunGlyph));
        OnPropertyChanged(nameof(RunTone));
        OnPropertyChanged(nameof(RunStages));
        OnPropertyChanged(nameof(HasRunStages));
        OnPropertyChanged(nameof(HasLastResult));
        OnPropertyChanged(nameof(LastResultTitle));
        OnPropertyChanged(nameof(LastResultMeta));
        OnPropertyChanged(nameof(LastResultEmptyText));
        OnPropertyChanged(nameof(HasLastResultHistory));
        OnPropertyChanged(nameof(LastResultStateText));
        OnPropertyChanged(nameof(LastResultGlyph));
        OnPropertyChanged(nameof(LastResultTone));
        OnPropertyChanged(nameof(LastResultDetailText));
        OnPropertyChanged(nameof(LastResultPreview));
        OnPropertyChanged(nameof(OutputHeaderText));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanCancel));
    }

    private static IEnumerable<string> ReadOutputLines(JsonObject payload)
    {
        // The module publishes single lines on the run stream and batches them for the event pump.
        if (payload.TryGetPropertyValue("lines", out var node) && node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item?.GetValue<string>() is { } line)
                {
                    yield return line;
                }
            }

            yield break;
        }

        if (RemoteCommandsMobileJson.Text(payload, "line") is { Length: > 0 } single)
        {
            yield return single;
        }
    }

    private void AppendOutput(string line)
    {
        _output.AppendLine(line);
        OutputText = Trim(_output);
    }

    private static string Trim(StringBuilder builder)
    {
        if (builder.Length <= MaxOutputLength)
        {
            return builder.ToString();
        }

        var text = builder.ToString(builder.Length - MaxOutputLength, MaxOutputLength);
        builder.Clear();
        builder.Append("[已截断到最近 ").Append(MaxOutputLength).AppendLine(" 字符]");
        builder.Append(text);
        return builder.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private int ResolvePort(string host) =>
        Hosts.FirstOrDefault(entry =>
            string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Alias, host, StringComparison.OrdinalIgnoreCase))?.Port
        ?? RemoteCommandsMobileContract.DefaultPort;

    private void SetFeedback(string message, string tone)
    {
        FeedbackText = message;
        FeedbackTone = tone;
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed record MobileRunRequest(string CommandId, string Host, string Input1, string Input2, bool SecondInput);
}

/// <summary>One entry of the run-time host picker.</summary>
internal sealed record MobileHostChoice(string Alias, string Display)
{
    public override string ToString() => Display;
}
