using System.Collections.ObjectModel;
using MyPowerTools.AvaloniaSdk;
using NssmManager.Contracts;

namespace NssmManager.Tool;

public sealed partial class NssmManagerViewModel
{
    public ObservableCollection<NssmServiceSnapshot> Services { get; }
    public IReadOnlyList<string> StartupTypes { get; } = Enum.GetNames<NssmStartupType>();
    public IReadOnlyList<string> ExitActions { get; } = Enum.GetNames<NssmExitAction>();
    public MptAsyncRelayCommand RefreshCommand { get; }
    public MptAsyncRelayCommand NewCommand { get; }
    public MptAsyncRelayCommand PreviewCommand { get; }
    public MptAsyncRelayCommand SaveCommand { get; }
    public MptAsyncRelayCommand DeleteCommand { get; }
    public MptAsyncRelayCommand StartCommand { get; }
    public MptAsyncRelayCommand StopCommand { get; }
    public MptAsyncRelayCommand RestartCommand { get; }
    public MptAsyncRelayCommand PauseCommand { get; }
    public MptAsyncRelayCommand ContinueCommand { get; }
    public MptAsyncRelayCommand RotateCommand { get; }
    public MptAsyncRelayCommand MigrateCommand { get; }
    public MptAsyncRelayCommand RollbackCommand { get; }
    public MptAsyncRelayCommand ConfirmPendingCommand { get; }

    public NssmServiceSnapshot? SelectedService
    {
        get => _selectedService;
        set
        {
            // FilteredServices is a new array; when the current row is filtered out the ListBox
            // two-way-binds SelectedItem to null. Keep the loaded editor's selection so mid-edit
            // apply (including password + expectedImagePath) still has ImagePath and UI state.
            if (value is null && _selectedService is not null && _loaded is not null && !_isNew)
                return;
            if (!SetProperty(ref _selectedService, value)) return;
            NotifySelectedServiceState();
            if (value is not null) _ = LoadAsync(value.Name);
        }
    }

    public bool Busy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(CanEdit)); NotifySelectedServiceState(); NotifyCommands(); NotifyConfirmationState(); } } }
    public bool CanEdit => !Busy && _loaded is not null;
    public bool IsExisting => _loaded is not null && !_isNew;
    public bool IsNew => _loaded is not null && _isNew;
    public bool HasSelection => _loaded is not null;
    public bool PrivilegedOperation { get => _privilegedOperation; private set => SetProperty(ref _privilegedOperation, value); }
    public string Activity { get => _activity; private set => SetProperty(ref _activity, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ServiceSearchText
    {
        get => _serviceSearchText;
        set
        {
            if (!SetProperty(ref _serviceSearchText, value)) return;
            OnPropertyChanged(nameof(FilteredServices));
            OnPropertyChanged(nameof(ServiceCountText));
        }
    }
    public IReadOnlyList<NssmServiceSnapshot> FilteredServices => string.IsNullOrWhiteSpace(ServiceSearchText)
        ? Services
        : Services.Where(service => service.Name.Contains(ServiceSearchText, StringComparison.OrdinalIgnoreCase) ||
                                    service.DisplayName.Contains(ServiceSearchText, StringComparison.OrdinalIgnoreCase) ||
                                    service.Application.Contains(ServiceSearchText, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string ServiceCountText => string.IsNullOrWhiteSpace(ServiceSearchText)
        ? $"{Services.Count} 个兼容服务"
        : $"显示 {FilteredServices.Count} / {Services.Count}";
    public string EditorTitle => _isNew ? "新建 Windows 服务" : _selectedService?.DisplayName ?? "选择一个服务";
    public string EditorSubtitle => _isNew ? "填写应用和服务信息，点击安装后立即请求管理员权限。" : _selectedService is null ? "从左侧选择服务，或创建新服务。" : $"{_selectedService.Name} · {_selectedService.StartupType}";
    public string ServiceStateText => _isNew ? "尚未安装" : _selectedService?.State.ToString() ?? "未选择";
    public string ServiceRuntimeText => _selectedService is null ? "" : _selectedService.ProcessId == 0 ? "当前没有子进程" : $"PID {_selectedService.ProcessId}";
    public string HostText => _isNew ? "将由 C# nssm-manager.exe 托管" : _selectedService?.IsManagedByCSharp == true ? "C# nssm-manager.exe" : "原版 NSSM 兼容宿主";
    public string SaveButtonText => _isNew ? "安装并申请管理员权限" : "应用并申请管理员权限";
    public bool CanStart => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Stopped;
    public bool CanStop => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Running or NssmServiceState.Paused or NssmServiceState.StartPending or NssmServiceState.PausePending or NssmServiceState.ContinuePending;
    public bool CanPause => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Running;
    public bool CanContinue => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Paused;
    public bool CanRestart => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Running or NssmServiceState.Paused;
    public bool CanRotate => !Busy && IsExisting && _selectedService?.State is NssmServiceState.Running;
    public bool CanDelete => !Busy && IsExisting;
    public bool CanMigrate => !Busy && IsExisting && _selectedService?.IsManagedByCSharp == false;
    public bool CanRollback => !Busy && IsExisting && _selectedService?.IsManagedByCSharp == true;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Application { get => _application; set => SetProperty(ref _application, value); }
    public string Parameters { get => _parameters; set => SetProperty(ref _parameters, value); }
    public string Directory { get => _directory; set => SetProperty(ref _directory, value); }
    public string Account { get => _account; set { if (SetProperty(ref _account, value)) { var localSystem = value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase); var virtualAccount = value.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase); set_logon_enabled(localSystem, !localSystem && !virtualAccount); } } }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string StartupType { get => _startupType; set => SetProperty(ref _startupType, value); }
    public bool Interactive { get => _interactive; set => SetProperty(ref _interactive, value); }
    public string DependenciesText { get => _dependencies; set => SetProperty(ref _dependencies, value); }
    public string DependencyGroupsText { get => _dependencyGroups; set => SetProperty(ref _dependencyGroups, value); }
    public string ServiceEnvironmentText { get => _serviceEnvironment; set => SetProperty(ref _serviceEnvironment, value); }
    public string EnvironmentReplaceText { get => _environmentReplace; set => SetProperty(ref _environmentReplace, value); }
    public string EnvironmentText { get => _environment; set => SetProperty(ref _environment, value); }
    public string Priority { get => _priority; set => SetProperty(ref _priority, value); }
    public string Affinity { get => _affinity; set { if (SetProperty(ref _affinity, value)) AffinityEnabled = !value.Equals("All", StringComparison.OrdinalIgnoreCase); } }
    public string Stdin { get => _stdin; set => SetProperty(ref _stdin, value); }
    public string Stdout { get => _stdout; set => SetProperty(ref _stdout, value); }
    public string Stderr { get => _stderr; set => SetProperty(ref _stderr, value); }
    public uint StdinShare { get => _stdinShare; set => SetProperty(ref _stdinShare, value); }
    public uint StdoutShare { get => _stdoutShare; set => SetProperty(ref _stdoutShare, value); }
    public uint StderrShare { get => _stderrShare; set => SetProperty(ref _stderrShare, value); }
    public uint StdinDisposition { get => _stdinDisposition; set => SetProperty(ref _stdinDisposition, value); }
    public uint StdoutDisposition { get => _stdoutDisposition; set => SetProperty(ref _stdoutDisposition, value); }
    public uint StderrDisposition { get => _stderrDisposition; set => SetProperty(ref _stderrDisposition, value); }
    public uint StdinFlags { get => _stdinFlags; set => SetProperty(ref _stdinFlags, value); }
    public uint StdoutFlags { get => _stdoutFlags; set => SetProperty(ref _stdoutFlags, value); }
    public uint StderrFlags { get => _stderrFlags; set => SetProperty(ref _stderrFlags, value); }
    public bool StdoutCopyAndTruncate { get => _stdoutCopyAndTruncate; set => SetProperty(ref _stdoutCopyAndTruncate, value); }
    public bool StderrCopyAndTruncate { get => _stderrCopyAndTruncate; set => SetProperty(ref _stderrCopyAndTruncate, value); }
    public bool RotateFiles { get => _rotateFiles; set { if (SetProperty(ref _rotateFiles, value)) RotationOptionsEnabled = value; } }
    public bool RotateOnline { get => _rotateOnline; set => SetProperty(ref _rotateOnline, value); }
    public ulong RotateBytes { get => _rotateBytes; set => SetProperty(ref _rotateBytes, value); }
    public uint RotateSeconds { get => _rotateSeconds; set => SetProperty(ref _rotateSeconds, value); }
    public uint RotateDelay { get => _rotateDelay; set => SetProperty(ref _rotateDelay, value); }
    public bool TimestampLog { get => _timestampLog; set => SetProperty(ref _timestampLog, value); }
    public uint RestartDelay { get => _restartDelay; set => SetProperty(ref _restartDelay, value); }
    public uint Throttle { get => _throttle; set => SetProperty(ref _throttle, value); }
    public bool KillTree { get => _killTree; set => SetProperty(ref _killTree, value); }
    public uint StopMethodSkip { get => _stopMethodSkip; set { if (SetProperty(ref _stopMethodSkip, value)) { ConsoleTimeoutEnabled = (value & 1) == 0; WindowTimeoutEnabled = (value & 2) == 0; ThreadsTimeoutEnabled = (value & 4) == 0; } } }
    public uint StopConsole { get => _stopConsole; set => SetProperty(ref _stopConsole, value); }
    public uint StopWindow { get => _stopWindow; set => SetProperty(ref _stopWindow, value); }
    public uint StopThreads { get => _stopThreads; set => SetProperty(ref _stopThreads, value); }
    public bool NoConsole { get => _noConsole; set => SetProperty(ref _noConsole, value); }
    public string ExitAction { get => _exitAction; set => SetProperty(ref _exitAction, value); }
    public string ExitRulesText { get => _exitRules; set => SetProperty(ref _exitRules, value); }
    public string HooksText { get => _hooks; set { if (SetProperty(ref _hooks, value)) LoadSelectedHook(); } }
    public bool RedirectHookOutput { get => _redirectHookOutput; set => SetProperty(ref _redirectHookOutput, value); }
    public string Compatibility { get => _compatibility; private set => SetProperty(ref _compatibility, value); }
    public string Impact { get => _impact; private set => SetProperty(ref _impact, value); }

    /// <summary>
    /// Explicit user confirmation for the pending high-risk operation. It is reset whenever
    /// the impact preview is regenerated, so it always refers to the preview on screen.
    /// </summary>
    public bool ImpactConfirmed
    {
        get => _impactConfirmed;
        set
        {
            if (!SetProperty(ref _impactConfirmed, value)) return;
            NotifyConfirmationState();
        }
    }

    public bool HasPendingConfirmation => _pendingApproval is not null;
    public bool CanConfirmPending => HasPendingConfirmation && ImpactConfirmed && !Busy;
    public string ConfirmationTitle => _pendingApprovalOperation switch
    {
        "delete" => "确认删除服务",
        "migrate" => "确认迁移服务宿主",
        "rollback" => "确认恢复迁移快照",
        "save" => "确认应用高风险变更",
        _ => "确认更改"
    };
    public string ConfirmationHint => HasPendingConfirmation
        ? "请先阅读下方影响范围。勾选确认前按钮不可用，也不会向 Windows 服务提交任何更改。"
        : "当前没有待确认的更改。";
    public string ConfirmationActionText => _pendingApprovalOperation switch
    {
        "delete" => "确认并删除服务",
        "migrate" => "确认并迁移宿主",
        "rollback" => "确认并恢复快照",
        "save" => "确认并提交配置",
        _ => "确认并提交"
    };
}
