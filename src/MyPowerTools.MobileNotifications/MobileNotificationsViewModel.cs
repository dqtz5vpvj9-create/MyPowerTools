using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.RemoteNotifications.Configuration;
using RemoteNotifications.Surface.Services;
using RemoteNotifications.Surface.ViewModels;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Phone-first view state for Remote Notifications on Android.
///
/// It reads the same persisted inbox the desktop product writes and the same shipped
/// <see cref="RemoteNotificationsLegacyStore"/> / <see cref="RemoteNotificationSettingsStore"/> /
/// <see cref="RemoteNotificationMessageViewModel"/> sources, but drives every action through module
/// commands (<c>sync-now</c>, <c>polling.start/stop</c>, <c>configure</c>, key import/clear,
/// <c>inbox.clear</c>) because the Android build has no service unit and must not poll on its own:
/// the module owns the single signed pull loop and the tray notifications.
///
/// Everything the page shows is real: history comes from the shared inbox file, status and errors
/// come from the module, background state changes only after the module reports it, and a missing
/// signing key is offered as a configuration step instead of a simulated success.
/// </summary>
public sealed class MobileNotificationsViewModel : MptObservableViewModel, IDisposable
{
    private const string ClaudeTaskLabel = "Claude Task";
    private const string AllLabelsFilter = RemoteNotificationsLegacyStore.FilterAll;

    /// <summary>Module error code that means "the user must grant or configure something first".</summary>
    public const string PermissionRequiredCode = "MPT_PERMISSION_REQUIRED";

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly RemoteNotificationsLegacyStore _store;
    private readonly RemoteNotificationSettingsStore _settingsStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MobileNotificationCardViewModel> _all = [];
    private readonly HashSet<string> _unreadLabels = new(StringComparer.Ordinal);

    private IDisposable? _events;
    private IReadOnlyList<string> _knownLabels = [];
    private string? _filterLabel;
    private string _searchQuery = "";
    private bool _isSearchVisible;
    private bool _searchAllLabels;
    private bool _isClaudeTaskVisible;
    private bool _isSettingsVisible;
    private bool _isBusy;
    private bool _isErrorDetailsVisible;
    private bool _isClearConfirmVisible;
    private string _connectionState = "starting";
    private string _lastPoll = "";
    private string _lastError = "";
    private string _lastErrorCode = "";
    private Func<Task>? _retry;
    private string _syncResult = "";
    private string _serverText = "";
    private bool _keyConfigured;
    private bool _backgroundActive;
    private bool _backgroundSwitchOn;
    private bool _backgroundAvailable;
    private bool _backgroundRequested;
    private string _healthText = "";
    private string _settingsFeedback = "";
    private string _settingsFeedbackState = "idle";
    private string _keyInput = "";
    private string _protocolDraft = RemoteNotificationSettings.DefaultProtocol;
    private string _hostDraft = RemoteNotificationSettings.DefaultHost;
    private string _portDraft = RemoteNotificationSettings.DefaultPort.ToString(CultureInfo.InvariantCulture);
    private string _channelDraft = RemoteNotificationSettings.DefaultChannel;
    private string _pollIntervalDraft = RemoteNotificationSettings.DefaultPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
    private MobileNotificationDetailViewModel? _detail;
    private RemoteNotificationSessionPosition? _detailPosition;
    private bool _disposed;

    public MobileNotificationsViewModel(
        MptAvaloniaSurfaceContext context,
        RemoteNotificationsLegacyStore store,
        RemoteNotificationSettingsStore settingsStore)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        Messages = [];
        MessageGroups = [];
        Labels = [];
        SyncCommand = new MptAsyncRelayCommand(() => SyncAsync(), () => !IsBusy, "remote-notifications.sync");
        ToggleSettingsCommand = new MptAsyncRelayCommand(() =>
        {
            IsSettingsVisible = !IsSettingsVisible;
            if (IsSettingsVisible)
            {
                LoadDrafts();
                IsSearchVisible = false;
                CloseSheets();
            }

            return Task.CompletedTask;
        });
        ToggleSearchCommand = new MptAsyncRelayCommand(() =>
        {
            IsSearchVisible = !IsSearchVisible;
            if (!IsSearchVisible)
            {
                SearchQuery = "";
            }

            return Task.CompletedTask;
        });
        ToggleBackgroundCommand = new MptAsyncRelayCommand(
            () => SetBackgroundAsync(!BackgroundActive),
            () => !IsBusy && CanToggleBackground);
        SaveSettingsCommand = new MptAsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        ImportKeyCommand = new MptAsyncRelayCommand(ImportKeyAsync, () => !IsBusy);
        ClearKeyCommand = new MptAsyncRelayCommand(ClearKeyAsync, () => !IsBusy);
        ClearInboxCommand = new MptAsyncRelayCommand(ClearInboxAsync, () => !IsBusy);
        MarkReadCommand = new MptAsyncRelayCommand(() =>
        {
            MarkAllAsRead();
            return Task.CompletedTask;
        });
        ToggleErrorDetailsCommand = new MptAsyncRelayCommand(() =>
        {
            IsErrorDetailsVisible = !IsErrorDetailsVisible;
            return Task.CompletedTask;
        });
        OpenSettingsFromErrorCommand = new MptAsyncRelayCommand(() => OpenSettingsAsync());
        OpenKeySettingsCommand = new MptAsyncRelayCommand(() => OpenSettingsAsync(requestKeySection: true));
        EmptyActionCommand = new MptAsyncRelayCommand(EmptyActionAsync);
        ErrorActionCommand = new MptAsyncRelayCommand(ErrorActionAsync);
        CloseDetailCommand = new MptAsyncRelayCommand(() =>
        {
            CloseSheets();
            return Task.CompletedTask;
        });
        ShowDetailPreviousCommand = new MptAsyncRelayCommand(
            () => { ShowAdjacentDetail(-1); return Task.CompletedTask; },
            () => CanShowDetailPrevious);
        ShowDetailNextCommand = new MptAsyncRelayCommand(
            () => { ShowAdjacentDetail(1); return Task.CompletedTask; },
            () => CanShowDetailNext);
        MarkDetailReadCommand = new MptAsyncRelayCommand(() =>
        {
            if (Detail is { } detail)
            {
                SetRead(detail.Card, isRead: true);
            }

            return Task.CompletedTask;
        });
        OpenClearHistoryCommand = new MptAsyncRelayCommand(() =>
        {
            Detail = null;
            _detailPosition = null;
            IsClearConfirmVisible = true;
            return Task.CompletedTask;
        });
        CancelClearHistoryCommand = new MptAsyncRelayCommand(() =>
        {
            IsClearConfirmVisible = false;
            return Task.CompletedTask;
        });

        LoadSettingsDrafts();
        ReloadSnapshot(markNewUnread: false);
    }

    /// <summary>Raised when the user asked for the signing-key section; the view scrolls it into view.</summary>
    public event Action? KeySectionRequested;

    // ---------------------------------------------------------------- collections

    /// <summary>Cards after label, Claude Task and search filtering, newest first.</summary>
    public ObservableCollection<MobileNotificationCardViewModel> Messages { get; }

    /// <summary>Same cards, grouped into the real 今天 / 昨天 / date buckets the list renders.</summary>
    public ObservableCollection<MobileNotificationDayGroupViewModel> MessageGroups { get; }

    /// <summary>Horizontal label strip: 全部, one chip per known label, then Claude Task.</summary>
    public ObservableCollection<MobileNotificationLabelViewModel> Labels { get; }

    public ICommand SyncCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ToggleSearchCommand { get; }
    public ICommand ToggleBackgroundCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ImportKeyCommand { get; }
    public ICommand ClearKeyCommand { get; }
    public ICommand ClearInboxCommand { get; }
    public ICommand MarkReadCommand { get; }
    public ICommand ToggleErrorDetailsCommand { get; }
    public ICommand OpenSettingsFromErrorCommand { get; }
    public ICommand OpenKeySettingsCommand { get; }
    public ICommand EmptyActionCommand { get; }
    public ICommand ErrorActionCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ShowDetailPreviousCommand { get; }
    public ICommand ShowDetailNextCommand { get; }
    public ICommand MarkDetailReadCommand { get; }
    public ICommand OpenClearHistoryCommand { get; }
    public ICommand CancelClearHistoryCommand { get; }

    // ---------------------------------------------------------------- view state

    public string Title => "远程通知";
    public string Subtitle => "与桌面端相同的签名通知，保存在本机私有目录。";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(SyncButtonText));
                NotifyCommandStates();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public string SyncButtonText => IsBusy ? "同步中…" : "立即同步";

    private void NotifyCommandStates()
    {
        foreach (var command in new[]
                 {
                     SyncCommand, ToggleBackgroundCommand, SaveSettingsCommand, ImportKeyCommand,
                     ClearKeyCommand, ClearInboxCommand, ShowDetailPreviousCommand, ShowDetailNextCommand
                 })
        {
            if (command is MptAsyncRelayCommand relay)
            {
                relay.NotifyCanExecuteChanged();
            }
        }
    }

    public string ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => ConnectionState switch
    {
        "running" => "同步中",
        "ok" => "已连接",
        "idle" => "空闲",
        "auth" => "需要签名密钥",
        "error" => "同步异常",
        _ => "准备中"
    };

    public string LastPoll
    {
        get => _lastPoll;
        private set
        {
            if (SetProperty(ref _lastPoll, value))
            {
                OnPropertyChanged(nameof(LastPollText));
            }
        }
    }

    public string LastPollText => string.IsNullOrWhiteSpace(LastPoll) ? "尚未同步" : $"上次同步 {LastPoll}";

    public string SyncResult
    {
        get => _syncResult;
        private set => SetProperty(ref _syncResult, value);
    }

    public string ServerText
    {
        get => _serverText;
        private set
        {
            if (SetProperty(ref _serverText, value))
            {
                OnPropertyChanged(nameof(EndpointText));
            }
        }
    }

    public string CountText => _all.Count == 0 ? "暂无通知" : $"共 {_all.Count} 条通知";

    /// <summary>Real configured endpoint, shown on the connection settings page (never in the list).</summary>
    public string EndpointText => string.IsNullOrWhiteSpace(ServerText) ? "当前端点：尚未配置" : $"当前端点：{ServerText}";

    /// <summary>Prototype page heading and its real subtitle (unread count / empty prompt).</summary>
    public string HeadingTitle => "值得你看一眼。";

    public string HeadingSubtitle => UnreadCount > 0
        ? $"来自设备的 {UnreadCount} 条新消息。"
        : _all.Count == 0
            ? "还没有收到通知，点“立即同步”检查一次。"
            : "所有消息都已读完。";

    public int UnreadCount => _all.Count(card => card.IsUnread);

    public bool HasUnread => UnreadCount > 0;

    public bool ShowsEmptyState => Messages.Count == 0;

    public string EmptyTitle => SearchQuery.Trim().Length > 0
        ? "换个词试试"
        : IsClaudeTaskVisible
            ? "还没有 Claude Task 通知"
            : _filterLabel is not null
                ? $"没有“{_filterLabel}”标签的通知"
                : "还没有收到通知";

    public string EmptyText => SearchQuery.Trim().Length > 0
        ? $"没有匹配“{SearchQuery.Trim()}”的通知。"
        : "电脑端发来通知后，会自动出现在这里。";

    public string EmptyActionText => SearchQuery.Trim().Length > 0 || _filterLabel is not null || IsClaudeTaskVisible
        ? "查看全部通知"
        : "立即同步";

    public string NoticeText => "只提醒你关心的事。来源、频道与同步间隔都能在“通知设置”里调整，历史保存在本机。";

    public bool HasError => _lastError.Length > 0 || ConnectionState is "error" or "auth";
    public string ErrorText => _lastError.Length > 0 ? _lastError : "最近一次同步未成功。";

    /// <summary>Actionable next step for the current failure; never a bare "failed" message.</summary>
    public string ErrorHint => _lastErrorCode == PermissionRequiredCode
        ? KeyConfigured
            ? "开启后台接收时系统会请求通知权限；授予后即可重新开启。"
            : "签名密钥决定能否与电脑端建立可信同步，导入后即可重试。"
        : _lastErrorCode == "MPT_VALIDATION_FAILED"
            ? "请检查服务器地址、端口与频道。"
            : "";

    public bool HasErrorHint => ErrorHint.Length > 0;

    public string ErrorDetails => $"服务器：{ServerText}\n上次同步：{LastPollText}\n\n{_lastError}";
    public bool IsErrorDetailsVisible
    {
        get => _isErrorDetailsVisible;
        private set
        {
            if (SetProperty(ref _isErrorDetailsVisible, value))
            {
                OnPropertyChanged(nameof(ErrorDetailsActionText));
            }
        }
    }

    public string ErrorDetailsActionText => IsErrorDetailsVisible ? "收起详情" : "查看详情";

    public string ErrorActionText => _lastErrorCode == PermissionRequiredCode
        ? KeyConfigured ? "重试" : "去配置签名密钥"
        : "打开通知设置";

    public bool HasErrorAction => HasError;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? ""))
            {
                OnPropertyChanged(nameof(HasSearchQuery));
                RefreshVisible();
            }
        }
    }

    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        private set => SetProperty(ref _isSearchVisible, value);
    }

    public bool SearchAllLabels
    {
        get => _searchAllLabels;
        set
        {
            if (SetProperty(ref _searchAllLabels, value))
            {
                RefreshVisible();
            }
        }
    }

    public bool IsClaudeTaskVisible
    {
        get => _isClaudeTaskVisible;
        private set
        {
            if (SetProperty(ref _isClaudeTaskVisible, value))
            {
                OnPropertyChanged(nameof(PageTitle));
            }
        }
    }

    public string PageTitle => IsClaudeTaskVisible ? "Claude Task" : "远程通知";

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set
        {
            if (SetProperty(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsInboxVisible));
                OnPropertyChanged(nameof(SettingsTitle));
            }
        }
    }

    public bool IsInboxVisible => !IsSettingsVisible;
    public string SettingsTitle => "通知设置";

    public string HealthText
    {
        get => _healthText;
        private set
        {
            if (SetProperty(ref _healthText, value))
            {
                OnPropertyChanged(nameof(HasHealthText));
            }
        }
    }

    public bool HasHealthText => HealthText.Length > 0;

    // ---------------------------------------------------------------- detail sheet

    public MobileNotificationDetailViewModel? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
                OnPropertyChanged(nameof(IsDetailVisible));
                OnPropertyChanged(nameof(IsSheetVisible));
                OnPropertyChanged(nameof(CanShowDetailPrevious));
                OnPropertyChanged(nameof(CanShowDetailNext));
                NotifyCommandStates();
            }
        }
    }

    public bool HasDetail => Detail is not null;

    public bool IsDetailVisible => Detail is not null;

    public bool CanShowDetailPrevious => _detailPosition is { Index: > 0 };
    public bool CanShowDetailNext => _detailPosition is { } position && position.Index < position.Count - 1;

    /// <summary>Shows the full message (quoted block included) in the phone bottom sheet.</summary>
    public void ShowDetail(MobileNotificationCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        _detailPosition = RemoteNotificationSessionChain.Resolve(OrderedRecords(), card.Message.Source);
        card.Message.UpdateSessionPosition(_detailPosition);
        Detail = new MobileNotificationDetailViewModel(card);
    }

    private void ShowAdjacentDetail(int delta)
    {
        if (_detailPosition is not { } position ||
            !RemoteNotificationSessionChain.TryNavigate(position, delta, out var target))
        {
            return;
        }

        var targetId = RemoteNotificationsLegacyStore.StableId(target);
        var card = _all.FirstOrDefault(candidate => string.Equals(candidate.Id, targetId, StringComparison.Ordinal));
        if (card is not null)
        {
            ShowDetail(card);
        }
    }

    public bool IsClearConfirmVisible
    {
        get => _isClearConfirmVisible;
        private set
        {
            if (SetProperty(ref _isClearConfirmVisible, value))
            {
                OnPropertyChanged(nameof(IsSheetVisible));
            }
        }
    }

    /// <summary>True while any bottom sheet is open; the back key closes the sheet before leaving the page.</summary>
    public bool IsSheetVisible => IsDetailVisible || IsClearConfirmVisible;

    public void CloseSheets()
    {
        if (IsDetailVisible)
        {
            Detail = null;
            _detailPosition = null;
        }

        IsClearConfirmVisible = false;
    }

    private IReadOnlyList<RemoteNotificationRecord> OrderedRecords() =>
        _all.Select(card => card.Message.Source).Reverse().ToArray();

    // ---------------------------------------------------------------- background state

    public bool BackgroundActive
    {
        get => _backgroundActive;
        private set
        {
            if (SetProperty(ref _backgroundActive, value))
            {
                OnPropertyChanged(nameof(BackgroundToggleText));
                OnPropertyChanged(nameof(BackgroundHint));
                OnPropertyChanged(nameof(BackgroundActionText));
                OnPropertyChanged(nameof(BackgroundStateText));
                SyncBackgroundSwitch();
            }
        }
    }

    public bool BackgroundAvailable
    {
        get => _backgroundAvailable;
        private set
        {
            if (SetProperty(ref _backgroundAvailable, value))
            {
                OnPropertyChanged(nameof(BackgroundHint));
                OnPropertyChanged(nameof(CanToggleBackground));
                NotifyCommandStates();
            }
        }
    }

    public bool BackgroundRequested
    {
        get => _backgroundRequested;
        private set
        {
            if (SetProperty(ref _backgroundRequested, value))
            {
                OnPropertyChanged(nameof(BackgroundHint));
                OnPropertyChanged(nameof(BackgroundStateText));
            }
        }
    }

    public bool CanToggleBackground => BackgroundAvailable;

    /// <summary>
    /// Two-way target of the settings switch. It carries the user's requested position only until the
    /// module answers, then <see cref="SyncBackgroundSwitch"/> snaps it to the state that really
    /// exists - so a refused notification permission leaves the switch off instead of pretending it
    /// turned on. The value is kept separately from <see cref="BackgroundActive"/> because a two-way
    /// binding ignores a source notification that repeats the value it already produced.
    /// </summary>
    public bool BackgroundSwitchOn
    {
        get => _backgroundSwitchOn;
        set
        {
            if (value == _backgroundSwitchOn)
            {
                return;
            }

            _backgroundSwitchOn = value;
            OnPropertyChanged();
            if (!CanToggleBackground || IsBusy)
            {
                SyncBackgroundSwitch();
                return;
            }

            // Run the module call after the two-way binding has finished writing this value to the
            // source, so the revert below is a real source change the binding will apply.
            Dispatcher.UIThread.Post(() => _ = SetBackgroundAsync(value), DispatcherPriority.Background);
        }
    }

    /// <summary>Snaps the switch to the module's real state when the two differ.</summary>
    private void SyncBackgroundSwitch()
    {
        if (_backgroundSwitchOn == _backgroundActive)
        {
            return;
        }

        _backgroundSwitchOn = _backgroundActive;
        OnPropertyChanged(nameof(BackgroundSwitchOn));
    }

    public string BackgroundToggleText => BackgroundActive ? "后台接收已开启" : "后台接收已关闭";
    public string BackgroundActionText => BackgroundActive ? "停止后台接收" : "开启后台接收";
    public string BackgroundStateText => BackgroundActive
        ? "后台接收已开启"
        : BackgroundRequested
            ? "上次开启过，需要重新开启"
            : "后台接收未开启";

    public string BackgroundHint => !BackgroundAvailable
        ? "当前主机没有 background.activity 能力，退到后台后不会继续接收。"
        : BackgroundActive
            ? "正在后台接收：系统通知栏会显示一个可停止的常驻任务。"
            : BackgroundRequested
                ? "上次已开启后台接收，应用启动后需要手动恢复，打开开关即可。"
                : "开启后应用退到后台仍会继续接收；系统会先请求通知权限，拒绝则不会开启。";

    // ---------------------------------------------------------------- signing key

    public bool KeyConfigured
    {
        get => _keyConfigured;
        private set
        {
            if (SetProperty(ref _keyConfigured, value))
            {
                OnPropertyChanged(nameof(KeyStatusText));
                OnPropertyChanged(nameof(KeyNeedsAttention));
                OnPropertyChanged(nameof(ShowsKeySetupWarning));
                OnPropertyChanged(nameof(ErrorHint));
                OnPropertyChanged(nameof(HasErrorHint));
                OnPropertyChanged(nameof(ErrorActionText));
            }
        }
    }

    public string KeyStatusText => KeyConfigured
        ? "签名密钥已保存在系统凭据库。"
        : "尚未导入签名密钥，无法进行签名同步。";

    /// <summary>Whether the key needs attention; the theme's warning text class carries the colour.</summary>
    public bool KeyNeedsAttention => !KeyConfigured;

    /// <summary>The inbox shows an actionable configuration callout while the key is missing.</summary>
    public bool ShowsKeySetupWarning => !KeyConfigured;
    public string KeySetupText => "还没有签名密钥，无法与电脑端进行可信同步。";
    public string KeySetupActionText => "导入签名密钥";

    public string KeyInput
    {
        get => _keyInput;
        set => SetProperty(ref _keyInput, value ?? "");
    }

    // ---------------------------------------------------------------- settings drafts

    public string ProtocolDraft
    {
        get => _protocolDraft;
        set => SetProperty(ref _protocolDraft, value ?? "");
    }

    public string HostDraft
    {
        get => _hostDraft;
        set => SetProperty(ref _hostDraft, value ?? "");
    }

    public string PortDraft
    {
        get => _portDraft;
        set => SetProperty(ref _portDraft, value ?? "");
    }

    public string ChannelDraft
    {
        get => _channelDraft;
        set => SetProperty(ref _channelDraft, value ?? "");
    }

    public string PollIntervalDraft
    {
        get => _pollIntervalDraft;
        set => SetProperty(ref _pollIntervalDraft, value ?? "");
    }

    public string SettingsFeedback
    {
        get => _settingsFeedback;
        private set
        {
            if (SetProperty(ref _settingsFeedback, value))
            {
                OnPropertyChanged(nameof(HasSettingsFeedback));
            }
        }
    }

    public bool HasSettingsFeedback => SettingsFeedback.Length > 0;

    /// <summary>Feedback severity; the view maps it to the theme's success/warning text classes.</summary>
    public string SettingsFeedbackState => _settingsFeedbackState;

    // ---------------------------------------------------------------- lifecycle

    public void Activate()
    {
        _events ??= _context.SubscribeEvents?.Invoke(OnModuleEvent);
        _ = RefreshStatusAsync();
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
    }

    public void RefreshRelativeTimes()
    {
        foreach (var card in _all)
        {
            card.RefreshRelativeTime();
        }

        Detail?.Refresh();
    }

    // ---------------------------------------------------------------- module interaction

    /// <summary>
    /// One module answer: the parsed state, or the real error code/message it failed with.
    /// Success requires a state object: a failed result that carries no <c>Error</c> object (or a
    /// payload the host could not parse) leaves <see cref="State"/> null and is therefore reported as
    /// a failure instead of being mistaken for a successful empty answer.
    /// </summary>
    private sealed record CallOutcome(JsonObject? State, string ErrorCode, string ErrorMessage)
    {
        public bool Success => State is not null;
    }

    private async Task<CallOutcome> CallAsync(string commandId, JsonObject? args, CancellationToken cancellationToken)
    {
        var result = await _context.ExecuteCommandAsync(commandId, args, cancellationToken).ConfigureAwait(true);
        if (!result.Success)
        {
            // The host may report a failure without an Error object; fall back to the output text and,
            // when even that is empty, to a plain statement so the page never shows an empty error.
            var message = !string.IsNullOrWhiteSpace(result.Error?.Message)
                ? result.Error!.Message
                : !string.IsNullOrWhiteSpace(result.Output)
                    ? result.Output
                    : $"命令 {commandId} 未成功，且宿主未提供错误详情。";
            return new CallOutcome(null, result.Error?.Code ?? "", message);
        }

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return new CallOutcome(new JsonObject(), "", "");
        }

        try
        {
            return JsonNode.Parse(result.Output) is JsonObject state
                ? new CallOutcome(state, "", "")
                : new CallOutcome(null, "", $"命令 {commandId} 返回了无法解析的结果。");
        }
        catch (System.Text.Json.JsonException)
        {
            return new CallOutcome(null, "", $"命令 {commandId} 返回了无法解析的结果。");
        }
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            var outcome = await CallAsync("remote-notifications-android.status", null, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, null);
                return;
            }

            ApplyState(outcome.State!);
            // A status refresh deliberately keeps the last operation error: a refused permission must
            // stay actionable until an operation actually succeeds, and this refresh runs right after
            // such a failure.
            ReloadSnapshot(markNewUnread: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
        }
    }

    public async Task SyncAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var outcome = await CallAsync("remote-notifications-android.sync-now", null, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, () => SyncAsync());
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                return;
            }

            var state = outcome.State!;
            ApplyState(state);
            ClearError();
            ReloadSnapshot(markNewUnread: true);
            var accepted = ReadLong(state, "accepted");
            var fetched = ReadLong(state, "fetched");
            SyncResult = accepted > 0 ? $"新增 {accepted} 条（收到 {fetched} 条）" : "没有新通知";
            SetSettingsFeedback(accepted > 0 ? $"同步完成，新增 {accepted} 条通知。" : "同步完成，没有新通知。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), () => SyncAsync());
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Turns the real background state on or off through the module. Enabling is where Android asks
    /// for the notification permission (the module's foreground-activity lease does that), so a
    /// denial surfaces here as a permission failure with a retry instead of a fake "已开启".
    /// </summary>
    private async Task SetBackgroundAsync(bool enable)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var command = enable
                ? "remote-notifications-android.polling.start"
                : "remote-notifications-android.polling.stop";
            var outcome = await CallAsync(command, null, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, () => SetBackgroundAsync(enable));
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                // Re-read the module and push the real state back into the switch, so a refused
                // permission leaves it off instead of showing the position the user tapped.
                await RefreshStatusAsync().ConfigureAwait(true);
                SyncBackgroundSwitch();
                return;
            }

            ApplyState(outcome.State!);
            ClearError();
            SyncBackgroundSwitch();
            SetSettingsFeedback(enable ? "后台接收已开启。" : "后台接收已停止。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), () => SetBackgroundAsync(enable));
            SetSettingsFeedback(exception.Message, "error");
            SyncBackgroundSwitch();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(PortDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            SetSettingsFeedback("端口必须是数字。", "error");
            return;
        }

        if (!int.TryParse(PollIntervalDraft, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            SetSettingsFeedback("同步间隔必须是数字。", "error");
            return;
        }

        IsBusy = true;
        try
        {
            var args = new JsonObject
            {
                ["protocol"] = ProtocolDraft,
                ["host"] = HostDraft,
                ["port"] = port,
                ["channel"] = ChannelDraft,
                ["pollIntervalSeconds"] = interval
            };
            var outcome = await CallAsync("remote-notifications-android.configure", args, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, null);
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                return;
            }

            ApplyState(outcome.State!);
            ClearError();
            SetSettingsFeedback("服务器设置已保存。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
            SetSettingsFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportKeyAsync()
    {
        var material = KeyInput;
        if (string.IsNullOrWhiteSpace(material))
        {
            SetSettingsFeedback("请先粘贴 OpenSSH Ed25519 私钥内容。", "error");
            return;
        }

        IsBusy = true;
        try
        {
            var args = new JsonObject { ["privateKey"] = material };
            var outcome = await CallAsync("remote-notifications-android.signing-key.import", args, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, null);
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                return;
            }

            KeyInput = "";
            KeyConfigured = ReadBool(outcome.State!, "keyConfigured");
            ClearError();
            SetSettingsFeedback(
                KeyConfigured
                    ? "签名密钥已保存到系统凭据库，不会回显，也不会写入普通文件。"
                    : "模块没有确认密钥已保存，请重试。",
                KeyConfigured ? "success" : "error");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
            SetSettingsFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearKeyAsync()
    {
        IsBusy = true;
        try
        {
            var outcome = await CallAsync("remote-notifications-android.signing-key.clear", null, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, null);
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                return;
            }

            KeyConfigured = ReadBool(outcome.State!, "keyConfigured");
            BackgroundActive = false;
            ClearError();
            SetSettingsFeedback("签名密钥已清除，后台接收已停止。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
            SetSettingsFeedback(exception.Message, "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearInboxAsync()
    {
        IsBusy = true;
        try
        {
            var outcome = await CallAsync("remote-notifications-android.inbox.clear", null, _lifetime.Token).ConfigureAwait(true);
            if (!outcome.Success)
            {
                Fail(outcome, null);
                SetSettingsFeedback(outcome.ErrorMessage, "error");
                return;
            }

            ClearLocalInbox();
            ApplyState(outcome.State!);
            ClearError();
            IsClearConfirmVisible = false;
            SetSettingsFeedback("通知历史已清空。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- projection

    private void OnModuleEvent(MptSurfaceEvent surfaceEvent)
    {
        if (surfaceEvent.Type is not ("message.received" or "inbox.cleared" or "polling.started" or
            "polling.stopped" or "server.connected" or "server.disconnected" or "signing-key.updated" or
            "signing-key.cleared" or "module.running"))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            switch (surfaceEvent.Type)
            {
                case "message.received":
                    ReloadSnapshot(markNewUnread: true);
                    break;
                case "inbox.cleared":
                    ClearLocalInbox();
                    break;
                default:
                    _ = RefreshStatusAsync();
                    break;
            }
        });
    }

    private void ClearLocalInbox()
    {
        _all.Clear();
        Messages.Clear();
        MessageGroups.Clear();
        _unreadLabels.Clear();
        Detail = null;
        _detailPosition = null;
        RefreshCounters();
    }

    private void ReloadSnapshot(bool markNewUnread)
    {
        RemoteNotificationsSnapshot snapshot;
        try
        {
            snapshot = _store.Load();
        }
        catch (Exception exception)
        {
            Fail(new CallOutcome(null, "", exception.Message), null);
            return;
        }

        var known = _all.Select(card => card.Message.Id).ToHashSet(StringComparer.Ordinal);
        var health = "";
        var added = 0;
        foreach (var record in snapshot.MessagesOldestFirst)
        {
            if (RemoteNotificationsLegacyStore.IsSystemHealthRecord(record))
            {
                health = record.Message;
                continue;
            }

            var id = RemoteNotificationsLegacyStore.StableId(record);
            if (!known.Add(id))
            {
                continue;
            }

            var card = new MobileNotificationCardViewModel(
                new RemoteNotificationMessageViewModel(record),
                isUnread: markNewUnread);
            _all.Insert(0, card);
            added++;
            if (markNewUnread)
            {
                _unreadLabels.Add(card.Label);
            }
        }

        while (_all.Count > RemoteNotificationsLegacyStore.MaximumMessages)
        {
            _all.RemoveAt(_all.Count - 1);
        }

        HealthText = health;
        _knownLabels = snapshot.KnownLabels;
        if (added > 0 || Messages.Count == 0 || Labels.Count == 0)
        {
            RebuildLabels(_knownLabels);
        }

        RefreshVisible();
    }

    private void RebuildLabels(IReadOnlyList<string> knownLabels)
    {
        Labels.Clear();
        Labels.Add(new MobileNotificationLabelViewModel(
            "全部",
            AllLabelsFilter,
            !IsClaudeTaskVisible && _filterLabel is null,
            false,
            SelectLabelAsync));
        foreach (var label in knownLabels)
        {
            if (string.Equals(label, ClaudeTaskLabel, StringComparison.Ordinal) ||
                label.StartsWith("CHRS 健康", StringComparison.Ordinal))
            {
                continue;
            }

            Labels.Add(new MobileNotificationLabelViewModel(
                label,
                label,
                !IsClaudeTaskVisible && string.Equals(_filterLabel, label, StringComparison.Ordinal),
                _unreadLabels.Contains(label),
                SelectLabelAsync));
        }

        Labels.Add(new MobileNotificationLabelViewModel(
            ClaudeTaskLabel,
            ClaudeTaskLabel,
            IsClaudeTaskVisible,
            _unreadLabels.Contains(ClaudeTaskLabel),
            SelectLabelAsync));
    }

    private Task SelectLabelAsync(string? value)
    {
        if (string.Equals(value, ClaudeTaskLabel, StringComparison.Ordinal))
        {
            IsClaudeTaskVisible = true;
            _filterLabel = null;
        }
        else
        {
            IsClaudeTaskVisible = false;
            _filterLabel = string.IsNullOrWhiteSpace(value) || string.Equals(value, AllLabelsFilter, StringComparison.Ordinal)
                ? null
                : value;
        }

        foreach (var chip in Labels)
        {
            chip.IsSelected = string.Equals(chip.FilterValue, value, StringComparison.Ordinal);
        }

        RefreshVisible();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Page-local Back for the host's back key: the topmost layer consumes it first - the open bottom
    /// sheet, then the settings page, then the search field. False hands the key back to the Shell so
    /// it can leave the tool page.
    /// </summary>
    public bool TryHandleBack()
    {
        if (IsSheetVisible)
        {
            CloseSheets();
            return true;
        }

        if (IsSettingsVisible)
        {
            IsSettingsVisible = false;
            return true;
        }

        if (IsSearchVisible)
        {
            IsSearchVisible = false;
            SearchQuery = "";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Expands the message a tray-notification tap pointed at, switching back to the inbox (or the
    /// Claude Task page) when the message is hidden by the current filter.
    /// </summary>
    public bool TryActivate(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        var card = _all.FirstOrDefault(candidate =>
            string.Equals(candidate.Message.Id, messageId, StringComparison.Ordinal) ||
            string.Equals(candidate.Message.FallbackId, messageId, StringComparison.Ordinal));
        if (card is null)
        {
            return false;
        }

        IsSettingsVisible = false;
        IsClaudeTaskVisible = string.Equals(card.Label, ClaudeTaskLabel, StringComparison.Ordinal);
        _filterLabel = null;
        SearchQuery = "";
        RebuildLabels(_knownLabels);
        RefreshVisible();
        SetRead(card, isRead: true);
        ShowDetail(card);
        return true;
    }

    private void RefreshVisible()
    {
        var query = SearchQuery.Trim();
        IEnumerable<MobileNotificationCardViewModel> filtered = _all;
        if (IsClaudeTaskVisible)
        {
            filtered = filtered.Where(card => string.Equals(card.Label, ClaudeTaskLabel, StringComparison.Ordinal));
        }
        else if (!string.IsNullOrEmpty(query) && SearchAllLabels)
        {
            // Global search intentionally spans every label and the Claude Task page.
            filtered = filtered.Where(card => !RemoteNotificationsLegacyStore.IsSystemHealthRecord(card.Message.Source));
        }
        else
        {
            filtered = filtered
                .Where(card => !string.Equals(card.Label, ClaudeTaskLabel, StringComparison.Ordinal))
                .Where(card => _filterLabel is null || string.Equals(card.Label, _filterLabel, StringComparison.Ordinal));
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(card => card.MatchesSearch(query));
        }

        Messages.Clear();
        foreach (var card in filtered)
        {
            Messages.Add(card);
        }

        RebuildGroups();
        RefreshCounters();
    }

    /// <summary>Builds the real day buckets (今天 / 昨天 / 具体日期) the list renders as sections.</summary>
    private void RebuildGroups()
    {
        MessageGroups.Clear();
        var today = DateTime.Today;
        var first = true;
        foreach (var group in Messages
                     .GroupBy(card => card.LocalDay)
                     .OrderByDescending(group => group.Key))
        {
            MessageGroups.Add(new MobileNotificationDayGroupViewModel(
                MobileNotificationDetailViewModel.FormatDayTitle(group.Key, today),
                group,
                first));
            first = false;
        }
    }

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowsEmptyState));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(EmptyActionText));
        OnPropertyChanged(nameof(UnreadCount));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(HeadingSubtitle));
    }

    /// <summary>Marks the whole inbox read (the prototype's 全部已读).</summary>
    public void MarkAllAsRead()
    {
        _unreadLabels.Clear();
        foreach (var card in _all)
        {
            card.IsUnread = false;
        }

        foreach (var chip in Labels)
        {
            chip.IsUnread = false;
        }

        Detail?.Refresh();
        RefreshCounters();
    }

    private void SetRead(MobileNotificationCardViewModel card, bool isRead)
    {
        card.IsUnread = !isRead;
        if (isRead && !_all.Any(candidate =>
                candidate.IsUnread && string.Equals(candidate.Label, card.Label, StringComparison.Ordinal)))
        {
            _unreadLabels.Remove(card.Label);
        }

        foreach (var chip in Labels)
        {
            chip.IsUnread = chip.FilterValue is { } value && _unreadLabels.Contains(value);
        }

        Detail?.Refresh();
        RefreshCounters();
    }

    private async Task EmptyActionAsync()
    {
        if (SearchQuery.Trim().Length > 0 || _filterLabel is not null || IsClaudeTaskVisible)
        {
            SearchQuery = "";
            _filterLabel = null;
            IsClaudeTaskVisible = false;
            RebuildLabels(_knownLabels);
            RefreshVisible();
            return;
        }

        await SyncAsync().ConfigureAwait(true);
    }

    private async Task OpenSettingsAsync(bool requestKeySection = false)
    {
        IsSettingsVisible = true;
        LoadDrafts();
        IsSearchVisible = false;
        CloseSheets();
        if (requestKeySection)
        {
            KeySectionRequested?.Invoke();
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async Task ErrorActionAsync()
    {
        if (_lastErrorCode == PermissionRequiredCode)
        {
            if (!KeyConfigured)
            {
                await OpenSettingsAsync(requestKeySection: true).ConfigureAwait(true);
                return;
            }

            var retry = _retry;
            if (retry is not null)
            {
                await retry().ConfigureAwait(true);
                return;
            }
        }

        await OpenSettingsAsync().ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- state helpers

    private void ApplyState(JsonObject state)
    {
        ConnectionState = ReadString(state, "connectionState") is { Length: > 0 } connection ? connection : "idle";
        LastPoll = ReadString(state, "lastPoll");

        // The module state only carries a message, never an error code, and a healthy refresh must not
        // wipe the last operation error: a refused permission stays visible with its action button
        // until an operation actually succeeds (success paths call ClearError).
        var stateError = ReadString(state, "lastError");
        if (stateError.Length > 0)
        {
            _lastError = stateError;
        }
        ServerText = ReadString(state, "endpoint");
        KeyConfigured = ReadBool(state, "keyConfigured");
        BackgroundAvailable = ReadBool(state, "backgroundAvailable");
        BackgroundActive = ReadBool(state, "backgroundActive");
        BackgroundRequested = ReadBool(state, "backgroundPollingRequested");
        var interval = ReadLong(state, "pollIntervalSeconds");
        if (interval > 0 && !IsSettingsVisible)
        {
            PollIntervalDraft = interval.ToString(CultureInfo.InvariantCulture);
        }

        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ErrorHint));
        OnPropertyChanged(nameof(HasErrorHint));
        OnPropertyChanged(nameof(ErrorActionText));
        OnPropertyChanged(nameof(ErrorDetails));
        OnPropertyChanged(nameof(ConnectionState));
    }

    private void Fail(CallOutcome outcome, Func<Task>? retry)
    {
        _lastError = outcome.ErrorMessage.Length > 0 ? outcome.ErrorMessage : "操作未成功。";
        _lastErrorCode = outcome.ErrorCode;
        _retry = retry;
        ConnectionState = outcome.ErrorCode == PermissionRequiredCode ? "auth" : "error";
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ErrorHint));
        OnPropertyChanged(nameof(HasErrorHint));
        OnPropertyChanged(nameof(ErrorActionText));
        OnPropertyChanged(nameof(HasErrorAction));
        OnPropertyChanged(nameof(ErrorDetails));
    }

    private void ClearError()
    {
        if (_lastError.Length == 0 && _lastErrorCode.Length == 0)
        {
            return;
        }

        _lastError = "";
        _lastErrorCode = "";
        _retry = null;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(ErrorHint));
        OnPropertyChanged(nameof(HasErrorHint));
        OnPropertyChanged(nameof(ErrorActionText));
        OnPropertyChanged(nameof(HasErrorAction));
        OnPropertyChanged(nameof(ErrorDetails));
    }

    private void LoadSettingsDrafts()
    {
        var settings = _settingsStore.Load();
        ProtocolDraft = settings.Protocol;
        HostDraft = settings.Host;
        PortDraft = settings.Port.ToString(CultureInfo.InvariantCulture);
        ChannelDraft = settings.Channel;
        PollIntervalDraft = settings.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        ServerText = settings.Endpoint;
    }

    /// <summary>Reloads the persisted drafts and folds in the live module state.</summary>
    public void LoadDrafts() => LoadSettingsDrafts();

    private void SetSettingsFeedback(string message, string state)
    {
        _settingsFeedbackState = state;
        SettingsFeedback = message;
        OnPropertyChanged(nameof(SettingsFeedbackState));
    }

    private static string ReadString(JsonObject values, string key) =>
        values.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static bool ReadBool(JsonObject values, string key) =>
        values.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static long ReadLong(JsonObject values, string key)
    {
        if (values.TryGetPropertyValue(key, out var node) && node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }

            if (value.TryGetValue<int>(out var small))
            {
                return small;
            }
        }

        return 0;
    }
}
