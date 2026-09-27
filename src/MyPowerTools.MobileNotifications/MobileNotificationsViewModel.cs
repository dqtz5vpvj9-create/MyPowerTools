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
/// </summary>
public sealed class MobileNotificationsViewModel : MptObservableViewModel, IDisposable
{
    private const string ClaudeTaskLabel = "Claude Task";
    private const string AllLabelsFilter = RemoteNotificationsLegacyStore.FilterAll;

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly RemoteNotificationsLegacyStore _store;
    private readonly RemoteNotificationSettingsStore _settingsStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<MobileNotificationCardViewModel> _all = [];
    private readonly HashSet<string> _unreadLabels = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

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
    private string _connectionState = "starting";
    private string _lastPoll = "";
    private string _lastError = "";
    private string _syncResult = "";
    private string _serverText = "";
    private bool _keyConfigured;
    private bool _backgroundActive;
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
        Labels = [];
        SyncCommand = new MptAsyncRelayCommand(() => SyncAsync(), () => !IsBusy, "remote-notifications.sync");
        ToggleSettingsCommand = new MptAsyncRelayCommand(() =>
        {
            IsSettingsVisible = !IsSettingsVisible;
            if (IsSettingsVisible)
            {
                LoadDrafts();
                IsSearchVisible = false;
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
        ToggleBackgroundCommand = new MptAsyncRelayCommand(ToggleBackgroundAsync, () => !IsBusy);
        SaveSettingsCommand = new MptAsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        ImportKeyCommand = new MptAsyncRelayCommand(ImportKeyAsync, () => !IsBusy);
        ClearKeyCommand = new MptAsyncRelayCommand(ClearKeyAsync, () => !IsBusy);
        ClearInboxCommand = new MptAsyncRelayCommand(ClearInboxAsync, () => !IsBusy);
        MarkReadCommand = new MptAsyncRelayCommand(() =>
        {
            MarkVisibleAsRead();
            return Task.CompletedTask;
        });
        ToggleErrorDetailsCommand = new MptAsyncRelayCommand(() =>
        {
            IsErrorDetailsVisible = !IsErrorDetailsVisible;
            return Task.CompletedTask;
        });
        OpenSettingsFromErrorCommand = new MptAsyncRelayCommand(() =>
        {
            IsSettingsVisible = true;
            LoadDrafts();
            return Task.CompletedTask;
        });

        LoadSettingsDrafts();
        ReloadSnapshot(markNewUnread: false);
    }

    // ---------------------------------------------------------------- collections

    /// <summary>Cards after label, Claude Task and search filtering, newest first.</summary>
    public ObservableCollection<MobileNotificationCardViewModel> Messages { get; }

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
                ((MptAsyncRelayCommand)SyncCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ToggleBackgroundCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)SaveSettingsCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ImportKeyCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ClearKeyCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ClearInboxCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public string SyncButtonText => IsBusy ? "同步中…" : "立即同步";

    public string ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusBackground));
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

    public string StatusColor => ConnectionState switch
    {
        "running" => "#1D4ED8",
        "ok" => "#15803D",
        "idle" => "#4B5563",
        "auth" or "error" => "#B91C1C",
        _ => "#4B5563"
    };

    public string StatusBackground => ConnectionState switch
    {
        "running" => "#DBEAFE",
        "ok" => "#DCFCE7",
        "idle" => "#F3F4F6",
        "auth" or "error" => "#FEE2E2",
        _ => "#F3F4F6"
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
        private set => SetProperty(ref _serverText, value);
    }

    public string CountText => Messages.Count == 0 ? "暂无通知" : $"{Messages.Count} 条通知";

    public bool ShowsEmptyState => Messages.Count == 0;
    public string EmptyText => SearchQuery.Trim().Length > 0
        ? $"没有匹配“{SearchQuery.Trim()}”的通知"
        : IsClaudeTaskVisible
            ? "还没有 Claude Task 通知"
            : _filterLabel is not null
                ? $"没有“{_filterLabel}”标签的通知"
                : "还没有收到通知，点“立即同步”检查一次。";

    public bool HasError => _lastError.Length > 0 || ConnectionState is "error" or "auth";
    public string ErrorText => _lastError.Length > 0 ? _lastError : "最近一次同步未成功。";
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

    public string PageTitle => IsClaudeTaskVisible ? "Claude Task" : "通知";

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set
        {
            if (SetProperty(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsInboxVisible));
            }
        }
    }

    public bool IsInboxVisible => !IsSettingsVisible;

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
            }
        }
    }

    public bool CanToggleBackground => BackgroundAvailable;
    public string BackgroundToggleText => BackgroundActive ? "后台接收已开启" : "后台接收已关闭";
    public string BackgroundActionText => BackgroundActive ? "停止后台接收" : "开启后台接收";
    public string BackgroundHint => !BackgroundAvailable
        ? "当前主机没有 background.activity 能力，退到后台后不会继续接收。"
        : BackgroundActive
            ? "正在后台接收：系统通知栏会显示一个可停止的常驻任务。"
            : BackgroundRequested
                ? "上次已开启后台接收。应用启动后需要手动恢复，点右侧按钮即可。"
                : "开启后应用退到后台仍会继续接收；关闭后立即停止轮询并释放后台任务。";

    // ---------------------------------------------------------------- signing key

    public bool KeyConfigured
    {
        get => _keyConfigured;
        private set
        {
            if (SetProperty(ref _keyConfigured, value))
            {
                OnPropertyChanged(nameof(KeyStatusText));
                OnPropertyChanged(nameof(KeyStatusColor));
            }
        }
    }

    public string KeyStatusText => KeyConfigured
        ? "签名密钥已保存在系统凭据库。"
        : "尚未导入签名密钥，无法进行签名同步。";
    public string KeyStatusColor => KeyConfigured ? "#15803D" : "#B45309";

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
    public string SettingsFeedbackColor => _settingsFeedbackState switch
    {
        "success" => "#15803D",
        "error" => "#B91C1C",
        _ => "#4B5563"
    };

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
        _operationGate.Dispose();
    }

    public void RefreshRelativeTimes()
    {
        foreach (var card in _all)
        {
            card.RefreshRelativeTime();
        }
    }

    // ---------------------------------------------------------------- module interaction

    private async Task<JsonObject> CallAsync(string commandId, JsonObject? args, CancellationToken cancellationToken)
    {
        var result = await _context.ExecuteCommandAsync(commandId, args, cancellationToken).ConfigureAwait(true);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Error?.Message) ? result.Output : result.Error!.Message);
        }

        if (string.IsNullOrWhiteSpace(result.Output))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(result.Output) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            ApplyState(await CallAsync("remote-notifications-android.status", null, _lifetime.Token).ConfigureAwait(true));
            ReloadSnapshot(markNewUnread: false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
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
            var state = await CallAsync("remote-notifications-android.sync-now", null, _lifetime.Token).ConfigureAwait(true);
            ApplyState(state);
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
            Fail(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleBackgroundAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var command = BackgroundActive
                ? "remote-notifications-android.polling.stop"
                : "remote-notifications-android.polling.start";
            var state = await CallAsync(command, null, _lifetime.Token).ConfigureAwait(true);
            ApplyState(state);
            SetSettingsFeedback(
                BackgroundActive ? "后台接收已开启。" : "后台接收已停止。",
                "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
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
            var state = await CallAsync("remote-notifications-android.configure", args, _lifetime.Token).ConfigureAwait(true);
            ApplyState(state);
            SetSettingsFeedback("服务器设置已保存。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
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
            var state = await CallAsync("remote-notifications-android.signing-key.import", args, _lifetime.Token).ConfigureAwait(true);
            KeyInput = "";
            KeyConfigured = ReadBool(state, "keyConfigured");
            SetSettingsFeedback("签名密钥已保存到系统凭据库，不会回显，也不会写入普通文件。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
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
            var state = await CallAsync("remote-notifications-android.signing-key.clear", null, _lifetime.Token).ConfigureAwait(true);
            KeyConfigured = ReadBool(state, "keyConfigured");
            BackgroundActive = false;
            SetSettingsFeedback("签名密钥已清除，后台接收已停止。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
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
            var state = await CallAsync("remote-notifications-android.inbox.clear", null, _lifetime.Token).ConfigureAwait(true);
            _all.Clear();
            Messages.Clear();
            _unreadLabels.Clear();
            RefreshCounters();
            ApplyState(state);
            SetSettingsFeedback("通知历史已清空。", "success");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------------------------------------------------------------- projection

    private void OnModuleEvent(MptSurfaceEvent surfaceEvent)
    {
        if (surfaceEvent.Type is not ("message.received" or "polling.started" or "polling.stopped" or "server.connected" or "server.disconnected"))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (surfaceEvent.Type == "message.received")
            {
                ReloadSnapshot(markNewUnread: true);
            }
            else
            {
                _ = RefreshStatusAsync();
            }
        });
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
            Fail(exception.Message);
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
        if (added > 0 || Messages.Count == 0)
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
        RebuildLabels(_knownLabels);
        RefreshVisible();
        card.IsExpanded = true;
        card.IsUnread = false;
        _unreadLabels.Remove(card.Label);
        RefreshCounters();
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
            filtered = filtered.Where(card => card.Message.MatchesSearch(query));
        }

        Messages.Clear();
        foreach (var card in filtered)
        {
            Messages.Add(card);
        }

        RefreshCounters();
    }

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowsEmptyState));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void MarkVisibleAsRead()
    {
        var labels = Messages.Select(card => card.Label).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var label in labels)
        {
            _unreadLabels.Remove(label);
        }

        foreach (var chip in Labels)
        {
            chip.IsUnread = chip.FilterValue is { } value && _unreadLabels.Contains(value);
        }

        foreach (var card in _all)
        {
            if (labels.Contains(card.Label, StringComparer.Ordinal))
            {
                card.IsUnread = false;
            }
        }
    }

    // ---------------------------------------------------------------- state helpers

    private void ApplyState(JsonObject state)
    {
        ConnectionState = ReadString(state, "connectionState") is { Length: > 0 } connection ? connection : "idle";
        LastPoll = ReadString(state, "lastPoll");
        _lastError = ReadString(state, "lastError");
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
        OnPropertyChanged(nameof(ErrorDetails));
        OnPropertyChanged(nameof(ConnectionState));
    }

    private void Fail(string message)
    {
        _lastError = message;
        ConnectionState = "error";
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(ErrorText));
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
        OnPropertyChanged(nameof(SettingsFeedbackColor));
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
