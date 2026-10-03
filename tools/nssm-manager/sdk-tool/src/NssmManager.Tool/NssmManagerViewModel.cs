using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.AvaloniaSdk;
using NssmManager.Contracts;
using NssmManager.Runtime;

namespace NssmManager.Tool;

public sealed partial class NssmManagerViewModel : MptObservableViewModel
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private NssmServiceSnapshot? _selectedService;
    private NssmServiceConfiguration? _loaded;
    private bool _busy;
    private bool _isNew;
    private string _status = "正在读取 Windows 服务…";
    private string _name = "";
    private string _displayName = "";
    private string _description = "";
    private string _application = "";
    private string _parameters = "";
    private string _directory = "";
    private string _account = "LocalSystem";
    private string _password = "";
    private string _startupType = "Automatic";
    private bool _interactive;
    private string _dependencies = "";
    private string _dependencyGroups = "";
    private string _serviceEnvironment = "";
    private string _environmentReplace = "";
    private string _environment = "";
    private string _priority = "NORMAL_PRIORITY_CLASS";
    private string _affinity = "All";
    private string _stdin = "";
    private string _stdout = "";
    private string _stderr = "";
    private uint _stdinShare = 2;
    private uint _stdoutShare = 3;
    private uint _stderrShare = 3;
    private uint _stdinDisposition = 3;
    private uint _stdoutDisposition = 4;
    private uint _stderrDisposition = 4;
    private uint _stdinFlags = 128;
    private uint _stdoutFlags = 128;
    private uint _stderrFlags = 128;
    private bool _stdoutCopyAndTruncate;
    private bool _stderrCopyAndTruncate;
    private bool _rotateFiles;
    private bool _rotateOnline;
    private ulong _rotateBytes;
    private uint _rotateSeconds;
    private uint _rotateDelay;
    private bool _timestampLog;
    private uint _restartDelay;
    private uint _throttle = 1500;
    private bool _killTree = true;
    private uint _stopMethodSkip;
    private uint _stopConsole = 1500;
    private uint _stopWindow = 1500;
    private uint _stopThreads = 1500;
    private bool _noConsole;
    private string _exitAction = "Restart";
    private string _exitRules = "";
    private string _hooks = "";
    private bool _redirectHookOutput;
    private string _compatibility = "选择服务后显示宿主兼容信息。";
    private string _impact = "尚未生成变更预览。";
    private bool _impactConfirmed;
    private string? _pendingApproval;
    private string? _pendingApprovalOperation;
    private bool _privilegedOperation;
    private string _activity = "就绪";
    private string _serviceSearchText = "";

    public NssmManagerViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        Services = [];
        RefreshCommand = Command(RefreshAsync, "refresh");
        NewCommand = Command(NewAsync, "new");
        PreviewCommand = Command(PreviewAsync, "preview");
        SaveCommand = Command(SaveAsync, "save");
        DeleteCommand = Command(DeleteAsync, "delete");
        ConfirmPendingCommand = new MptAsyncRelayCommand(ConfirmPendingAsync, () => CanConfirmPending, "nssm-manager.confirm");
        StartCommand = Command(() => ControlAsync("start"), "start");
        StopCommand = Command(() => ControlAsync("stop"), "stop");
        RestartCommand = Command(() => ControlAsync("restart"), "restart");
        PauseCommand = Command(() => ControlAsync("pause"), "pause");
        ContinueCommand = Command(() => ControlAsync("continue"), "continue");
        RotateCommand = Command(() => ControlAsync("rotate"), "rotate");
        MigrateCommand = Command(MigrateAsync, "migrate");
        RollbackCommand = Command(RollbackAsync, "rollback");
    }

    public Task RefreshAsync() => RefreshAsync(null);

    public async Task<bool> ActivateAsync(string mode, string? serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (mode.ToLowerInvariant())
        {
            case "install":
                await NewAsync().ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(serviceName)) Name = serviceName;
                Status = "填写应用路径和服务配置后保存即可安装。";
                return true;
            case "edit":
                await RefreshAsync(serviceName).ConfigureAwait(true);
                return string.IsNullOrWhiteSpace(serviceName) ||
                    _loaded?.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase) == true;
            case "remove":
                await RefreshAsync(serviceName).ConfigureAwait(true);
                if (_loaded is null ||
                    (!string.IsNullOrWhiteSpace(serviceName) && !_loaded.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase))) return false;
                PrepareApproval("delete", null);
                Status = "请检查删除影响范围，勾选确认后才会执行删除。";
                return true;
            default:
                return false;
        }
    }

    private async Task RefreshAsync(string? preferredServiceName)
    {
        if (Busy) return;
        NssmServiceSnapshot? serviceToLoad = null;
        Busy = true;
        try
        {
            var selectedName = preferredServiceName ?? SelectedService?.Name;
            var node = await ExecuteAsync("nssm-manager.list").ConfigureAwait(true);
            var items = node?.Deserialize<NssmServiceSnapshot[]>(JsonOptions) ?? [];
            Services.Clear();
            foreach (var item in items) Services.Add(item);
            OnPropertyChanged(nameof(FilteredServices));
            OnPropertyChanged(nameof(ServiceCountText));
            serviceToLoad = Services.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase)) ?? Services.FirstOrDefault();
            if (!ReferenceEquals(_selectedService, serviceToLoad))
            {
                _selectedService = serviceToLoad;
                OnPropertyChanged(nameof(SelectedService));
            }
            if (serviceToLoad is null)
            {
                _loaded = null;
                Password = "";
                ClearImpact();
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(IsExisting));
            }
            NotifySelectedServiceState();
            Status = $"已发现 {Services.Count} 个 NSSM 兼容服务。";
        }
        catch (Exception exception) { Status = "读取失败：" + exception.Message; Log("error", exception.Message); }
        finally { Busy = false; }
        if (serviceToLoad is not null) await LoadAsync(serviceToLoad.Name).ConfigureAwait(true);
    }

    private Task NewAsync()
    {
        // Bypass SelectedService setter: it intentionally ignores null while a loaded editor is active
        // (search filter clearing the ListBox must not wipe mid-edit selection).
        if (_selectedService is not null)
        {
            _selectedService = null;
            OnPropertyChanged(nameof(SelectedService));
        }
        _isNew = true;
        _loaded = new NssmServiceConfiguration();
        Apply(_loaded);
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(CanEdit));
        NotifySelectedServiceState();
        Status = "填写服务名和应用路径后保存即可安装。";
        ClearImpact();
        NotifyCommands();
        return Task.CompletedTask;
    }

    private async Task LoadAsync(string name)
    {
        if (Busy) return;
        Busy = true;
        try
        {
            var node = await ExecuteAsync("nssm-manager.get", new JsonObject { ["serviceName"] = name }).ConfigureAwait(true);
            _loaded = node?.Deserialize<NssmServiceConfiguration>(JsonOptions) ?? throw new InvalidDataException("Runtime returned no configuration.");
            _isNew = false;
            OnPropertyChanged(nameof(IsExisting));
            Apply(_loaded);
            var selected = Services.First(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Compatibility = selected.IsManagedByCSharp ? "当前服务由 C# nssm-manager.exe 托管。" : $"当前 ImagePath：{selected.ImagePath}\n可先检查配置，然后显式迁移并保留回滚快照。";
            NotifySelectedServiceState();
            Status = $"已读取 {name}。";
            ClearImpact();
        }
        catch (Exception exception)
        {
            _loaded = null;
            Password = "";
            ClearImpact();
            OnPropertyChanged(nameof(CanEdit));
            NotifySelectedServiceState();
            Status = "读取配置失败：" + exception.Message;
        }
        finally { Busy = false; }
    }

    private async Task<bool> SaveAsync()
    {
        if (_loaded is null) return false;
        string? savedServiceName = null;
        string? successStatus = null;
        var installing = _isNew;
        var operationLabel = installing ? "安装服务" : "应用配置";
        try
        {
            var configuration = configure(installing ? null : _loaded);
            if (RequiresRiskConfirmation(installing ? null : _loaded, configuration, !string.IsNullOrEmpty(Password)) &&
                !Approve("save", configuration))
            {
                Status = "检测到高风险变更：已展示受影响服务名与改动范围，勾选确认后才会提交。";
                return false;
            }
            Busy = true;
            Activity = "正在校验配置";
            await ExecuteAsync("nssm-manager.validate", new JsonObject { ["configuration"] = JsonSerializer.SerializeToNode(configuration, JsonOptions) }).ConfigureAwait(true);
            var applyArguments = new JsonObject { ["configuration"] = JsonSerializer.SerializeToNode(configuration, JsonOptions) };
            BeginPrivilegedOperation(operationLabel);
            await Task.Yield();
            if (string.IsNullOrEmpty(Password))
                await ExecuteAsync(installing ? "nssm-manager.install" : "nssm-manager.apply", applyArguments).ConfigureAwait(true);
            else
            {
                var password = Password.ToCharArray();
                Password = "";
                try
                {
                    if (installing) applyArguments["executablePath"] = NssmElevatedClient.ResolveManagedExecutable();
                    else applyArguments["expectedImagePath"] = ResolveExpectedImagePath(_selectedService, _loaded, Services);
                    await NssmElevatedClient.ExecuteAsync(installing ? "nssm-manager.install" : "nssm-manager.apply", applyArguments, password, _lifetime.Token).ConfigureAwait(true);
                }
                finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }
            }
            _loaded = configuration;
            _isNew = false;
            successStatus = installing ? "服务已安装。" : "配置已应用。";
            Status = successStatus;
            savedServiceName = configuration.Name;
        }
        catch (Exception exception) { Status = PrivilegedFailure(operationLabel, exception); Log("error", exception.Message); }
        finally { EndOperation(); }
        if (savedServiceName is not null)
        {
            ClearImpact();
            await RefreshAsync(savedServiceName).ConfigureAwait(true);
            Status = successStatus!;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Prefer the list selection's ImagePath; if the ListBox cleared selection because the row was
    /// filtered out while the editor still has a loaded service, resolve from the services list by name.
    /// </summary>
    internal static string ResolveExpectedImagePath(
        NssmServiceSnapshot? selectedService,
        NssmServiceConfiguration? loaded,
        IEnumerable<NssmServiceSnapshot> services)
    {
        if (!string.IsNullOrEmpty(selectedService?.ImagePath))
            return selectedService.ImagePath;

        if (loaded is not null)
        {
            var match = services.FirstOrDefault(item =>
                item.Name.Equals(loaded.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match?.ImagePath))
                return match.ImagePath;
        }

        throw new InvalidOperationException("当前服务 ImagePath 不可用。");
    }

    private async Task DeleteAsync()
    {
        if (_loaded is null || _isNew) return;
        if (!Approve("delete", null))
        {
            Status = "已展示删除影响范围；勾选确认前不会向 Windows 服务提交任何更改，删除不可在界面撤销。";
            return;
        }
        var serviceName = _loaded.Name;
        string finalStatus;
        BeginPrivilegedOperation("删除服务");
        await Task.Yield();
        try
        {
            await ExecuteAsync("nssm-manager.remove", new JsonObject { ["serviceName"] = serviceName }).ConfigureAwait(true);
            _loaded = null;
            ClearImpact();
            finalStatus = $"服务 {serviceName} 已删除。";
        }
        catch (Exception exception) { finalStatus = PrivilegedFailure("删除服务", exception); }
        finally { EndOperation(); }
        await RefreshAsync().ConfigureAwait(true);
        Status = finalStatus;
    }

    private async Task ControlAsync(string action)
    {
        if (_loaded is null || _isNew) return;
        var serviceName = _loaded.Name;
        var actionLabel = ControlActionLabel(action);
        string finalStatus;
        BeginPrivilegedOperation(actionLabel + "服务");
        await Task.Yield();
        try
        {
            await ExecuteAsync("nssm-manager.control", new JsonObject { ["serviceName"] = serviceName, ["action"] = action }).ConfigureAwait(true);
            finalStatus = $"服务 {serviceName} 已{actionLabel}。";
        }
        catch (Exception exception) { finalStatus = PrivilegedFailure(actionLabel + "服务", exception); }
        finally { EndOperation(); }
        await RefreshAsync(serviceName).ConfigureAwait(true);
        Status = finalStatus;
    }

    private async Task MigrateAsync()
    {
        if (_loaded is null) return;
        if (!Approve("migrate", null))
        {
            Status = "已展示迁移影响范围；勾选确认前不会切换服务宿主。";
            return;
        }
        var serviceName = _loaded.Name;
        string finalStatus;
        BeginPrivilegedOperation("迁移服务宿主");
        await Task.Yield();
        try
        {
            await ExecuteAsync("nssm-manager.migrate", new JsonObject { ["serviceName"] = serviceName }).ConfigureAwait(true);
            ClearImpact();
            finalStatus = "迁移完成，服务已由 C# nssm-manager.exe 托管。";
        }
        catch (Exception exception) { finalStatus = PrivilegedFailure("迁移服务宿主", exception, "迁移失败时已请求自动回滚。"); }
        finally { EndOperation(); }
        await RefreshAsync(serviceName).ConfigureAwait(true);
        Status = finalStatus;
    }

    private async Task RollbackAsync()
    {
        if (_loaded is null) return;
        if (!Approve("rollback", null))
        {
            Status = "已展示回滚影响范围；勾选确认前不会恢复迁移快照。";
            return;
        }
        var serviceName = _loaded.Name;
        string finalStatus;
        BeginPrivilegedOperation("恢复迁移快照");
        await Task.Yield();
        try
        {
            await ExecuteAsync("nssm-manager.rollback", new JsonObject { ["serviceName"] = serviceName }).ConfigureAwait(true);
            ClearImpact();
            finalStatus = "已恢复迁移前宿主、配置与服务状态。";
        }
        catch (Exception exception) { finalStatus = PrivilegedFailure("恢复迁移快照", exception); }
        finally { EndOperation(); }
        await RefreshAsync(serviceName).ConfigureAwait(true);
        Status = finalStatus;
    }

    private Task PreviewAsync()
    {
        if (_loaded is not null)
        {
            var draft = Build();
            if (RequiresRiskConfirmation(_isNew ? null : _loaded, draft, !string.IsNullOrEmpty(Password)))
            {
                // Re-arming here is what ties the preview to the submit path: refreshing the
                // preview always clears a previous confirmation and blocks the next submit
                // until the user confirms the refreshed impact again.
                PrepareApproval("save", draft);
                Status = "影响范围已重新生成；勾选确认后才会提交。";
            }
            else
            {
                ResetConfirmation();
                Impact = ConfigurationDiff(_loaded, draft);
                Status = "变更预览已更新；当前变更不需要额外确认。";
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Impact fields that make a configuration change high risk: the service account,
    /// the ImagePath inputs and the service start/type. Cosmetic edits stay one-click.
    /// </summary>
    internal static readonly string[] RiskConfirmationFields =
        ["Application", "AppParameters", "AppDirectory", "ServiceAccount", "Interactive", "StartupType"];

    /// <summary>
    /// True when the draft changes the account, the ImagePath inputs, the start/type or
    /// carries a service password. These changes must be confirmed before they are submitted.
    /// </summary>
    internal static bool RequiresRiskConfirmation(NssmServiceConfiguration? before, NssmServiceConfiguration after, bool rotatesPassword)
    {
        if (rotatesPassword) return true;
        var baseline = JsonSerializer.SerializeToNode(before ?? new NssmServiceConfiguration(), JsonOptions)!.AsObject();
        var candidate = JsonSerializer.SerializeToNode(after, JsonOptions)!.AsObject();
        return RiskConfirmationFields.Any(field => !string.Equals(
            baseline[field]?.ToJsonString() ?? "null",
            candidate[field]?.ToJsonString() ?? "null",
            StringComparison.Ordinal));
    }

    /// <summary>
    /// Two-step commit gate. The first call only prepares the impact preview and returns
    /// false; the caller must not touch the runtime in that case. A second call is allowed
    /// only after the user confirmed that exact operation and draft.
    /// </summary>
    private bool Approve(string operation, NssmServiceConfiguration? draft)
    {
        if (_pendingApproval == ApprovalKey(operation, draft) && ImpactConfirmed) return true;
        PrepareApproval(operation, draft);
        return false;
    }

    private void PrepareApproval(string operation, NssmServiceConfiguration? draft)
    {
        ImpactConfirmed = false;
        _pendingApproval = ApprovalKey(operation, draft);
        _pendingApprovalOperation = operation;
        PrepareImpact(operation, draft);
        NotifyConfirmationState();
    }

    private string ApprovalKey(string operation, NssmServiceConfiguration? draft)
    {
        var payload = operation + "\n" + (draft is null ? _loaded?.Name : JsonSerializer.Serialize(draft, JsonOptions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? operation)));
    }

    private async Task ConfirmPendingAsync()
    {
        if (!CanConfirmPending) return;
        switch (_pendingApprovalOperation)
        {
            case "delete": await DeleteAsync().ConfigureAwait(true); break;
            case "migrate": await MigrateAsync().ConfigureAwait(true); break;
            case "rollback": await RollbackAsync().ConfigureAwait(true); break;
            case "save": await SaveAsync().ConfigureAwait(true); break;
        }
    }

    private void PrepareImpact(string operation, NssmServiceConfiguration? draft)
    {
        var serviceName = _loaded?.Name ?? "";
        Impact = operation switch
        {
            "delete" => $"删除服务：{serviceName}\n受影响服务：{serviceName}\n将删除：SCM 服务定义与 HKLM\\SYSTEM\\CurrentControlSet\\Services\\{serviceName}\\Parameters 子树（含全部 NSSM 配置）。\n可回滚：否。界面不提供撤销入口，删除后只能手工重建服务与配置。",
            "migrate" => $"迁移服务：{serviceName}\n受影响服务：{serviceName}\n当前 ImagePath：{_selectedService?.ImagePath}\n目标宿主：nssm-manager.exe\n将改动：SCM ImagePath 与宿主；执行前保存 SCM 配置、完整 Parameters 树和服务状态快照。\n可回滚：是。失败时自动回滚，也可随时点击「恢复迁移快照」。",
            "rollback" => $"回滚服务：{serviceName}\n受影响服务：{serviceName}\n将恢复：迁移快照中的 SCM 配置、ImagePath、完整 Parameters 树及迁移前服务状态。\n可回滚：回滚本身把宿主恢复为迁移前状态；再次迁移需要重新确认。",
            _ => ConfigurationDiff(_loaded!, draft ?? Build()) + PasswordImpactNote()
        };
    }

    private string PasswordImpactNote() => string.IsNullOrEmpty(Password)
        ? ""
        : Environment.NewLine + "服务账户密码将被更新（密码不进入预览、状态、日志、命令行或临时文件，仅通过受保护管道传递）。";

    private static string ConfigurationDiff(NssmServiceConfiguration before, NssmServiceConfiguration after)
    {
        var oldValues = JsonSerializer.SerializeToNode(before, JsonOptions)!.AsObject();
        var newValues = JsonSerializer.SerializeToNode(after, JsonOptions)!.AsObject();
        var changes = new List<string>();
        foreach (var item in newValues)
        {
            var oldValue = oldValues[item.Key]?.ToJsonString() ?? "null";
            var newValue = item.Value?.ToJsonString() ?? "null";
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal)) changes.Add($"{item.Key}: {oldValue} → {newValue}");
        }
        return changes.Count == 0 ? "草稿与当前配置一致。" : string.Join(Environment.NewLine, changes);
    }

    private void ClearImpact()
    {
        Impact = "尚未生成变更预览。";
        ResetConfirmation();
    }

    private void ResetConfirmation()
    {
        _pendingApproval = null;
        _pendingApprovalOperation = null;
        ImpactConfirmed = false;
        NotifyConfirmationState();
    }

    private void NotifyConfirmationState()
    {
        OnPropertyChanged(nameof(HasPendingConfirmation));
        OnPropertyChanged(nameof(CanConfirmPending));
        OnPropertyChanged(nameof(ConfirmationTitle));
        OnPropertyChanged(nameof(ConfirmationHint));
        OnPropertyChanged(nameof(ConfirmationActionText));
        ConfirmPendingCommand.NotifyCanExecuteChanged();
    }

    private void BeginPrivilegedOperation(string action)
    {
        Busy = true;
        PrivilegedOperation = true;
        Activity = "正在等待 Windows 管理员授权";
        Status = $"{action}：正在打开 Windows UAC…";
    }

    private void EndOperation()
    {
        PrivilegedOperation = false;
        Activity = "就绪";
        Busy = false;
    }

    private static string PrivilegedFailure(string action, Exception exception, string? detail = null)
    {
        var cancelled = exception is OperationCanceledException ||
                        exception is NssmRuntimeCommandException { Code: "permission.cancelled" } ||
                        exception.Message.Contains("UAC", StringComparison.OrdinalIgnoreCase) &&
                        (exception.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) || exception.Message.Contains("取消", StringComparison.Ordinal));
        if (cancelled) return "已取消 Windows 管理员授权，服务未发生更改。";
        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : " " + detail;
        return $"{action}失败：{exception.Message}{suffix}";
    }

    private static string ControlActionLabel(string action) => action switch
    {
        "start" => "启动",
        "stop" => "停止",
        "restart" => "重启",
        "pause" => "暂停",
        "continue" => "继续",
        "rotate" => "轮转日志",
        _ => action
    };

    private void NotifySelectedServiceState()
    {
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(EditorSubtitle));
        OnPropertyChanged(nameof(ServiceStateText));
        OnPropertyChanged(nameof(ServiceRuntimeText));
        OnPropertyChanged(nameof(HostText));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanContinue));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanRotate));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanMigrate));
        OnPropertyChanged(nameof(CanRollback));
    }

    private void Apply(NssmServiceConfiguration value)
    {
        Password = "";
        Name = value.Name; DisplayName = value.DisplayName; Description = value.Description; Application = value.Application; Parameters = value.AppParameters; Directory = value.AppDirectory; Account = value.ServiceAccount; StartupType = value.StartupType.ToString(); Interactive = value.Interactive;
        DependenciesText = string.Join(Environment.NewLine, value.DependOnService); DependencyGroupsText = string.Join(Environment.NewLine, value.DependOnGroup); ServiceEnvironmentText = string.Join(Environment.NewLine, value.ServiceEnvironment); EnvironmentReplaceText = string.Join(Environment.NewLine, value.Environment); EnvironmentText = string.Join(Environment.NewLine, value.EnvironmentExtra); Priority = value.Priority; Affinity = value.Affinity; Stdin = value.AppStdin; Stdout = value.AppStdout; Stderr = value.AppStderr;
        StdinShare = value.AppStdinShareMode; StdoutShare = value.AppStdoutShareMode; StderrShare = value.AppStderrShareMode; StdinDisposition = value.AppStdinCreationDisposition; StdoutDisposition = value.AppStdoutCreationDisposition; StderrDisposition = value.AppStderrCreationDisposition; StdinFlags = value.AppStdinFlagsAndAttributes; StdoutFlags = value.AppStdoutFlagsAndAttributes; StderrFlags = value.AppStderrFlagsAndAttributes; StdoutCopyAndTruncate = value.AppStdoutCopyAndTruncate; StderrCopyAndTruncate = value.AppStderrCopyAndTruncate;
        RotateFiles = value.RotateFiles; RotateOnline = value.RotateOnline; RotateBytes = value.RotateBytes; RotateSeconds = value.RotateSeconds; RotateDelay = value.RotateDelayMilliseconds; TimestampLog = value.TimestampLog; RestartDelay = value.RestartDelayMilliseconds; Throttle = value.ThrottleDelayMilliseconds; KillTree = value.KillProcessTree; StopMethodSkip = value.StopMethodSkip; StopConsole = value.StopMethodConsoleMilliseconds; StopWindow = value.StopMethodWindowMilliseconds; StopThreads = value.StopMethodThreadsMilliseconds; NoConsole = value.NoConsole; ExitAction = value.DefaultExitAction.ToString();
        ExitRulesText = string.Join(Environment.NewLine, value.ExitRules.Select(rule => $"{rule.ExitCode}={rule.Action}")); HooksText = string.Join(Environment.NewLine, value.Hooks.Select(hook => $"{hook.Event}/{hook.Action}={hook.Command}")); RedirectHookOutput = value.RedirectHookOutput;
    }

    private NssmServiceConfiguration Build() => _loaded! with
    {
        Name = Name, DisplayName = DisplayName, Description = Description, Application = Application, AppParameters = Parameters, AppDirectory = Directory, ServiceAccount = Account,
        StartupType = Enum.Parse<NssmStartupType>(StartupType), Interactive = Interactive, DependOnService = Lines(DependenciesText), DependOnGroup = Lines(DependencyGroupsText), ServiceEnvironment = Lines(ServiceEnvironmentText), Environment = Lines(EnvironmentReplaceText), EnvironmentExtra = Lines(EnvironmentText), Priority = Priority, Affinity = Affinity, AppStdin = Stdin, AppStdout = Stdout, AppStderr = Stderr,
        AppStdinShareMode = StdinShare, AppStdoutShareMode = StdoutShare, AppStderrShareMode = StderrShare, AppStdinCreationDisposition = StdinDisposition, AppStdoutCreationDisposition = StdoutDisposition, AppStderrCreationDisposition = StderrDisposition, AppStdinFlagsAndAttributes = StdinFlags, AppStdoutFlagsAndAttributes = StdoutFlags, AppStderrFlagsAndAttributes = StderrFlags, AppStdoutCopyAndTruncate = StdoutCopyAndTruncate, AppStderrCopyAndTruncate = StderrCopyAndTruncate,
        RotateFiles = RotateFiles, RotateOnline = RotateOnline, RotateBytes = RotateBytes, RotateSeconds = RotateSeconds, RotateDelayMilliseconds = RotateDelay, TimestampLog = TimestampLog, RestartDelayMilliseconds = RestartDelay, ThrottleDelayMilliseconds = Throttle, KillProcessTree = KillTree, StopMethodSkip = StopMethodSkip, StopMethodConsoleMilliseconds = StopConsole, StopMethodWindowMilliseconds = StopWindow, StopMethodThreadsMilliseconds = StopThreads, NoConsole = NoConsole,
        DefaultExitAction = Enum.Parse<NssmExitAction>(ExitAction), ExitRules = ParseExitRules(), Hooks = ParseHooks(), RedirectHookOutput = RedirectHookOutput
    };

    private NssmExitRule[] ParseExitRules() => Lines(ExitRulesText).Select(line => { var parts = line.Split('=', 2); return parts.Length == 2 && uint.TryParse(parts[0], out var code) && Enum.TryParse<NssmExitAction>(parts[1], true, out var action) ? new NssmExitRule(code, action) : throw new ArgumentException($"无效退出规则：{line}"); }).ToArray();
    private NssmHook[] ParseHooks() => Lines(HooksText).Select(line => { var parts = line.Split('=', 2); var path = parts[0].Split('/', 2); return parts.Length == 2 && path.Length == 2 ? new NssmHook(path[0], path[1], parts[1]) : throw new ArgumentException($"无效 Hook：{line}"); }).ToArray();
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private MptAsyncRelayCommand Command(Func<Task> execute, string id) => new(execute, () => !Busy, "nssm-manager." + id);
    private async Task<JsonNode?> ExecuteAsync(string commandId, JsonObject? arguments = null)
    {
        var result = await _context.ExecuteCommandAsync(commandId, arguments, _lifetime.Token).ConfigureAwait(true);
        if (!result.Success) throw new InvalidOperationException(result.Error?.Message ?? result.Output);
        var response = JsonNode.Parse(result.Output)?.AsObject() ?? throw new InvalidDataException("Runtime response is invalid.");
        var envelope = response["result"]?.AsObject() ?? throw new InvalidDataException("Runtime result is missing.");
        if (envelope["state"]?.GetValue<string>() != "ready")
        {
            var error = envelope["error"]?.AsObject();
            throw new NssmRuntimeCommandException(error?["code"]?.GetValue<string>() ?? "runtime.failed", error?["message"]?.GetValue<string>() ?? "Runtime failed.");
        }
        return envelope["payload"];
    }
    private void NotifyCommands() { RefreshCommand.NotifyCanExecuteChanged(); NewCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged(); PauseCommand.NotifyCanExecuteChanged(); ContinueCommand.NotifyCanExecuteChanged(); RotateCommand.NotifyCanExecuteChanged(); MigrateCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged(); ConfirmPendingCommand.NotifyCanExecuteChanged(); }
    private void Log(string level, string message) => _context.Log(new MptSurfaceLogEntry(level, message, DateTimeOffset.UtcNow));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class NssmRuntimeCommandException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
