using System.Text;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Phone page state for the Remote Commands Android module.
///
/// The module owns SSH, the host catalog, the host-key store, the secret store and the shared
/// <c>commands.yaml</c>/<c>settings.json</c>/<c>history.json</c> files. This view model only decides what
/// to ask for, keeps the page honest about the result (including "the host key is not confirmed yet"),
/// and never holds a credential after the save call returns.
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
    private string _statusText = "正在读取模块状态…";
    private string _statusDetail = "";
    private string _feedbackText = "";
    private string _feedbackTone = "info";
    private bool _isBusy;
    private bool _isRunning;

    private IReadOnlyList<MobileCommandDefinition> _commands = [];
    private MobileCommandDefinition? _selectedCommand;
    private IReadOnlyList<MobileHostEntry> _hosts = [];
    private string _selectedHost = "";
    private string _defaultHost = "";
    private string _input1 = "";
    private string _input2 = "";
    private bool _showSecondInput;
    private string _outputText = "";
    private string _runStateText = "尚未执行";
    private string _runMessageText = "";
    private string _runEndpointText = "";

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
    private bool _historyClearPending;

    // 命令配置 (editable commands.yaml)
    private string _catalogYaml = "";
    private string _catalogYamlStatus = "";
    private string _catalogSaveMessage = "";
    private bool _catalogEditorExpanded;
    private bool _catalogDirty;

    // 设置 (editable shared settings + module preferences)
    private string _settingsDefaultHost = "";
    private string _settingsKnownHosts = "";
    private string _settingsRetention = "500";
    private string _settingsCondaExecutable = "";
    private string _settingsTimeoutMinutes = "30";
    private string _settingsMessage = "";
    private bool _settingsDirty;

    private string _formAlias = "";
    private string _formHost = "";
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

    // ---------------------------------------------------------------- header

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string FeedbackText
    {
        get => _feedbackText;
        private set => SetProperty(ref _feedbackText, value);
    }

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
            }
        }
    }

    public bool CanInteract => !IsBusy;

    public bool CanRun => !IsBusy && !IsRunning && SelectedCommand is not null;

    public bool CanCancel => IsRunning;

    public bool TransportAvailable
    {
        get => _transportAvailable;
        private set
        {
            if (SetProperty(ref _transportAvailable, value))
            {
                OnPropertyChanged(nameof(TransportWarning));
                OnPropertyChanged(nameof(CanRun));
            }
        }
    }

    /// <summary>Shown when the device has no managed SSH transport, so "run" is honestly unavailable.</summary>
    public string TransportWarning => TransportAvailable
        ? ""
        : "此设备缺少受管 SSH 传输，运行已停用；命令目录与主机配置仍可查看和编辑。";

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

    // ---------------------------------------------------------------- catalog / inputs

    public IReadOnlyList<MobileCommandDefinition> Commands
    {
        get => _commands;
        private set
        {
            if (SetProperty(ref _commands, value))
            {
                OnPropertyChanged(nameof(HasCommands));
            }
        }
    }

    public bool HasCommands => Commands.Count > 0;

    public MobileCommandDefinition? SelectedCommand
    {
        get => _selectedCommand;
        set
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

    // ---------------------------------------------------------------- hosts

    public IReadOnlyList<MobileHostEntry> Hosts
    {
        get => _hosts;
        private set
        {
            if (SetProperty(ref _hosts, value))
            {
                OnPropertyChanged(nameof(HasHosts));
                OnPropertyChanged(nameof(HostPickerItems));
            }
        }
    }

    public bool HasHosts => Hosts.Count > 0;

    /// <summary>Alias picker entries: the mapped aliases plus "follow the command / default".</summary>
    public IReadOnlyList<MobileHostChoice> HostPickerItems =>
    [
        new("", DefaultHostChoiceText),
        .. Hosts.Select(host => new MobileHostChoice(host.Alias, host.ToString()))
    ];

    private string DefaultHostChoiceText => string.IsNullOrWhiteSpace(DefaultHost)
        ? "使用命令配置的主机"
        : $"使用默认主机 {DefaultHost}";

    public string SelectedHost
    {
        get => _selectedHost;
        set => SetProperty(ref _selectedHost, value);
    }

    public string DefaultHost
    {
        get => _defaultHost;
        private set
        {
            if (SetProperty(ref _defaultHost, value))
            {
                OnPropertyChanged(nameof(HostPickerItems));
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
        PendingHostKey is { Fingerprint.Length: > 0 } && TrustFingerprintAcknowledged && !IsBusy;

    // ---------------------------------------------------------------- run result

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
        private set => SetProperty(ref _runEndpointText, value);
    }

    // ---------------------------------------------------------------- history / settings

    public MobileHistorySummary History
    {
        get => _history;
        private set
        {
            if (SetProperty(ref _history, value))
            {
                OnPropertyChanged(nameof(HistoryText));
            }
        }
    }

    public string HistoryText => $"{History.CountText} · {History.LatestText}";

    public string CommandsPath
    {
        get => _commandsPath;
        private set => SetProperty(ref _commandsPath, value);
    }

    /// <summary>Read-only validation of the shared commands.yaml, using the shipped parser.</summary>
    public string CommandsFileStatus
    {
        get => _commandsFileStatus;
        private set => SetProperty(ref _commandsFileStatus, value);
    }

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

    public string SettingsSummary
    {
        get => _settingsSummary;
        private set => SetProperty(ref _settingsSummary, value);
    }

    public string DataDirectory
    {
        get => _dataDirectory;
        private set => SetProperty(ref _dataDirectory, value);
    }

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

    // ---------------------------------------------------------------- commands.yaml editor

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

    public bool CatalogEditorExpanded
    {
        get => _catalogEditorExpanded;
        set
        {
            if (SetProperty(ref _catalogEditorExpanded, value))
            {
                OnPropertyChanged(nameof(CatalogEditorToggleText));
            }
        }
    }

    public string CatalogEditorToggleText => CatalogEditorExpanded ? "收起命令配置" : "展开命令配置（编辑 commands.yaml）";

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

    public string CatalogDirtyText => CatalogDirty ? "有未保存的修改" : "已保存";

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

    /// <summary>Drops local edits and reloads the catalog text the module/file currently holds.</summary>
    public async Task ReloadCatalogYamlAsync()
    {
        if (IsBusy)
        {
            return;
        }

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

        IsBusy = true;
        try
        {
            await _client.SaveCatalogAsync(CatalogYaml, _lifetime.Token).ConfigureAwait(true);
            // The module is the authority again after a successful write.
            var catalog = await _client.CatalogAsync(_lifetime.Token).ConfigureAwait(true);
            Commands = catalog.Commands;
            CatalogError = catalog.Error;
            ApplyCatalogText(catalog, force: true);
            SelectDefaultCommand();
            CatalogSaveMessage = $"已保存 commands.yaml（{Commands.Count} 个命令）。";
            SetFeedback("命令配置已保存。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Validation errors are reported verbatim; the editor keeps the user's text.
            CatalogSaveMessage = $"保存失败：{exception.Message}";
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

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

    /// <summary>All five keys are stored by the module; the surface writes no settings file itself.</summary>
    public string ModuleSettingsHint =>
        "修改完成后，点保存使设置生效。";

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
            var values = new System.Text.Json.Nodes.JsonObject
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

    // ---------------------------------------------------------------- host form

    public string FormAlias
    {
        get => _formAlias;
        set => SetProperty(ref _formAlias, value);
    }

    public string FormHost
    {
        get => _formHost;
        set => SetProperty(ref _formHost, value);
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

    // ---------------------------------------------------------------- actions

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

        var hosts = await _client.HostsAsync(_lifetime.Token).ConfigureAwait(true);
        ApplyHosts(hosts);

        TrustedKeys = await _client.HostKeysAsync(_lifetime.Token).ConfigureAwait(true);
        History = await _client.HistoryAsync(_lifetime.Token).ConfigureAwait(true);
        RefreshCommandsFileStatus();
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
        SettingsSummary =
            $"默认主机 {Or(state.DefaultHost, "未设置")} · 历史保留 {state.HistoryRetention} 条 · " +
            $"超时 {state.CommandTimeoutMinutes} 分钟 · CONDA_EXE {Or(state.CondaExecutable, "未设置")}";
        StatusDetail = string.IsNullOrWhiteSpace(state.LastRunSummary) ? BackgroundText : state.LastRunSummary;
        PageTitle = string.IsNullOrWhiteSpace(state.ActiveInvocationId)
            ? "远程命令"
            : "远程命令 · 正在执行";
        if (!string.IsNullOrWhiteSpace(state.ActiveInvocationId) && !IsRunning)
        {
            IsRunning = true;
            RunStateText = $"正在执行（阶段 {Or(state.ActiveStage, "未知")}）";
        }

        StatusText = state.TransportAvailable
            ? $"受管 SSH 就绪 · {state.CommandCount} 个命令 · {state.Hosts.Count} 台主机"
            : "受管 SSH 不可用";

        // Status is the authority for stored settings; an in-progress edit is never overwritten.
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
        if (SelectedCommand is { } current && Commands.Any(command => command.Id == current.Id))
        {
            return;
        }

        SelectedCommand = Commands.FirstOrDefault(command =>
                              string.Equals(command.Id, _lastSelectedCommandId, StringComparison.OrdinalIgnoreCase))
                          ?? Commands.FirstOrDefault();
    }

    private string? _lastSelectedCommandId;

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

        var invocationId = Guid.NewGuid().ToString("N");
        _activeInvocationId = invocationId;
        _lastSelectedCommandId = command.Id;
        _lastRunRequest = new MobileRunRequest(command.Id, SelectedHost, Input1, Input2, ShowSecondInput);
        _output.Clear();
        OutputText = "";
        RunMessageText = "";
        RunEndpointText = string.IsNullOrWhiteSpace(SelectedHost) ? command.HostText : SelectedHost;
        RunStateText = command.IsLocalTransform ? "正在本地转换…" : "正在连接并执行…";
        PendingHostKey = null;
        TrustFingerprintAcknowledged = false;
        IsRunning = true;

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
            RunStateText = "已取消";
        }
        catch (Exception exception)
        {
            RunStateText = "执行失败";
            RunMessageText = exception.Message;
            SetFeedback(exception.Message, "error");
        }
        finally
        {
            _activeInvocationId = null;
            IsRunning = false;
            await RefreshAfterRunAsync().ConfigureAwait(true);
        }
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

        RunStateText = outcome.ResultText;
        RunMessageText = outcome.Message;
        RunEndpointText = command.IsLocalTransform
            ? "本地转换（不经过 SSH）"
            : string.IsNullOrWhiteSpace(outcome.EndpointText) ? RunEndpointText : outcome.EndpointText;

        if (outcome.PendingHostKey is { } pending)
        {
            PendingHostKey = pending with { Port = pending.Port ?? ResolvePort(pending.Host) };
            RunStateText = "等待确认主机密钥";
            SetFeedback("主机密钥尚未确认，请核对指纹后确认。", "warning");
            return;
        }

        PendingHostKey = null;
        SetFeedback(
            outcome.Succeeded ? "执行完成" : outcome.Cancelled ? "已取消" : outcome.Message,
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
        SetFeedback("未确认主机密钥，执行保持中断。", "info");
    }

    public async Task SaveHostAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var alias = FormAlias.Trim();
        var host = FormHost.Trim();
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
        FormAlias = alias;
        FormHost = "";
        FormPort = RemoteCommandsMobileContract.DefaultPort.ToString();
        FormUsername = "";
        FormUsesPassword = true;
        FormMessage = $"请填写 {alias} 的真实地址与账号；MPT 不会猜测地址，也不会自动信任主机密钥。";
        SetFeedback($"正在为 {alias} 添加映射。", "info");
    }

    public void ClearHostForm()
    {
        FormAlias = "";
        FormHost = "";
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
        if (invocationId.Length > 0 && !string.Equals(invocationId, _activeInvocationId, StringComparison.Ordinal))
        {
            // Only the invocation this page started owns the output pane.
            return;
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
                var stage = RemoteCommandsMobileJson.Text(payload, "stage");
                RunStateText = stage switch
                {
                    "uploading" => "正在上传输入文件…",
                    "running" => "远端命令执行中…",
                    _ => $"执行中（{Or(stage, "未知阶段")}）"
                };
                break;

            case RemoteCommandsMobileContract.EventRunStarted:
                IsRunning = true;
                RunStateText = $"已开始：{RemoteCommandsMobileJson.Text(payload, "label")}";
                RunEndpointText = RemoteCommandsMobileJson.Text(payload, "resolvedHost") is { Length: > 0 } resolved
                    ? resolved
                    : RunEndpointText;
                break;

            case RemoteCommandsMobileContract.EventHostKeyPending:
                var pending = RemoteCommandsMobileJson.Pending(payload, payload);
                PendingHostKey = pending with { Port = pending.Port ?? ResolvePort(pending.Host) };
                TrustFingerprintAcknowledged = false;
                RunStateText = "等待确认主机密钥";
                break;

            case RemoteCommandsMobileContract.EventRunFinished:
                RunStateText = RemoteCommandsMobileJson.Text(payload, "state") switch
                {
                    RemoteCommandsMobileContract.StateSucceeded => "执行成功",
                    RemoteCommandsMobileContract.StateCancelled => "已取消",
                    RemoteCommandsMobileContract.StateHostKeyRequired => "等待确认主机密钥",
                    _ => "执行结束"
                };
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

    private static IEnumerable<string> ReadOutputLines(System.Text.Json.Nodes.JsonObject payload)
    {
        // The module publishes single lines on the run stream and batches them for the event pump.
        if (payload.TryGetPropertyValue("lines", out var node) && node is System.Text.Json.Nodes.JsonArray array)
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
