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

    public NssmManagerViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        Services = [];
        RefreshCommand = Command(RefreshAsync, "refresh");
        NewCommand = Command(NewAsync, "new");
        PreviewCommand = Command(PreviewAsync, "preview");
        SaveCommand = Command(async () => { if (_isNew) _ = await install().ConfigureAwait(true); else if (_loaded is not null) _ = await edit(_loaded).ConfigureAwait(true); }, "save");
        DeleteCommand = Command(async () => _ = await remove().ConfigureAwait(true), "delete");
        StartCommand = Command(() => ControlAsync("start"), "start");
        StopCommand = Command(() => ControlAsync("stop"), "stop");
        RestartCommand = Command(() => ControlAsync("restart"), "restart");
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
                Status = "请检查删除影响范围，勾选确认后执行删除。";
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
            serviceToLoad = Services.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase)) ?? Services.FirstOrDefault();
            if (!ReferenceEquals(_selectedService, serviceToLoad))
            {
                _selectedService = serviceToLoad;
                OnPropertyChanged(nameof(SelectedService));
            }
            if (serviceToLoad is null)
            {
                _loaded = null;
                OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(IsExisting));
            }
            Status = $"已发现 {Services.Count} 个 NSSM 兼容服务。";
        }
        catch (Exception exception) { Status = "读取失败：" + exception.Message; Log("error", exception.Message); }
        finally { Busy = false; }
        if (serviceToLoad is not null) await LoadAsync(serviceToLoad.Name).ConfigureAwait(true);
    }

    private Task NewAsync()
    {
        SelectedService = null;
        _isNew = true;
        _loaded = new NssmServiceConfiguration();
        Apply(_loaded);
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(CanEdit));
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
            Status = $"已读取 {name}。";
            ClearImpact();
        }
        catch (Exception exception) { Status = "读取配置失败：" + exception.Message; }
        finally { Busy = false; }
    }

    private async Task SaveAsync()
    {
        if (_loaded is null) return;
        string? savedServiceName = null;
        try
        {
            var configuration = configure(_isNew ? null : _loaded);
            if (!Approve("save", configuration)) return;
            Busy = true;
            await ExecuteAsync("nssm-manager.validate", new JsonObject { ["configuration"] = JsonSerializer.SerializeToNode(configuration, JsonOptions) }).ConfigureAwait(true);
            var applyArguments = new JsonObject { ["configuration"] = JsonSerializer.SerializeToNode(configuration, JsonOptions) };
            if (string.IsNullOrEmpty(Password))
                await ExecuteAsync(_isNew ? "nssm-manager.install" : "nssm-manager.apply", applyArguments).ConfigureAwait(true);
            else
            {
                var password = Password.ToCharArray();
                Password = "";
                try
                {
                    if (_isNew) applyArguments["executablePath"] = NssmElevatedClient.ResolveManagedExecutable();
                    else applyArguments["expectedImagePath"] = _selectedService?.ImagePath ?? throw new InvalidOperationException("当前服务 ImagePath 不可用。");
                    await NssmElevatedClient.ExecuteAsync(_isNew ? "nssm-manager.install" : "nssm-manager.apply", applyArguments, password, _lifetime.Token).ConfigureAwait(true);
                }
                finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }
            }
            _loaded = configuration;
            _isNew = false;
            Status = "配置已保存。";
            savedServiceName = configuration.Name;
        }
        catch (Exception exception) { Status = "保存失败：" + exception.Message; Log("error", exception.Message); }
        finally { Busy = false; }
        if (savedServiceName is not null) { ClearImpact(); await RefreshAsync(savedServiceName).ConfigureAwait(true); }
    }

    private async Task DeleteAsync()
    {
        if (_loaded is null || _isNew) return;
        if (!Approve("delete", null)) return;
        Busy = true;
        try { await ExecuteAsync("nssm-manager.remove", new JsonObject { ["serviceName"] = _loaded.Name }).ConfigureAwait(true); _loaded = null; Status = "服务已删除。"; }
        catch (Exception exception) { Status = "删除失败：" + exception.Message; }
        finally { Busy = false; await RefreshAsync().ConfigureAwait(true); }
    }

    private async Task ControlAsync(string action)
    {
        if (_loaded is null) return;
        Busy = true;
        try { await ExecuteAsync("nssm-manager.control", new JsonObject { ["serviceName"] = _loaded.Name, ["action"] = action }).ConfigureAwait(true); Status = $"{action} 已完成。"; }
        catch (Exception exception) { Status = $"{action} 失败：{exception.Message}"; }
        finally { Busy = false; await RefreshAsync().ConfigureAwait(true); }
    }

    private async Task MigrateAsync()
    {
        if (_loaded is null) return;
        if (!Approve("migrate", null)) return;
        Busy = true;
        try { await ExecuteAsync("nssm-manager.migrate", new JsonObject { ["serviceName"] = _loaded.Name }).ConfigureAwait(true); Status = "迁移完成，服务已由 C# 宿主管理。"; }
        catch (Exception exception) { Status = "迁移失败并已尝试回滚：" + exception.Message; }
        finally { Busy = false; await RefreshAsync().ConfigureAwait(true); }
    }

    private async Task RollbackAsync()
    {
        if (_loaded is null) return;
        if (!Approve("rollback", null)) return;
        Busy = true;
        try { await ExecuteAsync("nssm-manager.rollback", new JsonObject { ["serviceName"] = _loaded.Name }).ConfigureAwait(true); Status = "已恢复迁移前宿主、配置与服务状态。"; }
        catch (Exception exception) { Status = "回滚失败：" + exception.Message; }
        finally { Busy = false; await RefreshAsync().ConfigureAwait(true); }
    }

    private Task PreviewAsync()
    {
        if (_loaded is not null) PrepareApproval("save", Build());
        return Task.CompletedTask;
    }

    private bool Approve(string operation, NssmServiceConfiguration? draft)
    {
        var key = ApprovalKey(operation, draft);
        if (_pendingApproval == key && ImpactConfirmed) return true;
        PrepareApproval(operation, draft);
        Status = "请检查影响范围，勾选确认后再次执行。";
        return false;
    }

    private void PrepareApproval(string operation, NssmServiceConfiguration? draft)
    {
        _pendingApproval = ApprovalKey(operation, draft);
        ImpactConfirmed = false;
        Impact = operation switch
        {
            "delete" => $"删除服务：{_loaded!.Name}\nSCM 服务定义及 NSSM Parameters 将被删除。",
            "migrate" => $"迁移服务：{_loaded!.Name}\nImagePath：{_selectedService?.ImagePath}\n目标宿主：nssm-manager.exe\n执行前保存 SCM、ImagePath 和 Parameters 快照。",
            "rollback" => $"回滚服务：{_loaded!.Name}\n恢复迁移快照中的 SCM 配置、ImagePath、完整 Parameters 树及迁移前服务状态。",
            _ => ConfigurationDiff(_loaded!, draft!)
        };
    }

    private string ApprovalKey(string operation, NssmServiceConfiguration? draft)
    {
        var payload = operation + "\n" + (draft is null ? _loaded?.Name : JsonSerializer.Serialize(draft, JsonOptions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload ?? operation)));
    }

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
        _pendingApproval = null;
        ImpactConfirmed = false;
        Impact = "尚未生成变更预览。";
    }

    private void Apply(NssmServiceConfiguration value)
    {
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
        if (envelope["state"]?.GetValue<string>() != "ready") throw new InvalidOperationException(envelope["error"]?["message"]?.GetValue<string>() ?? "Runtime failed.");
        return envelope["payload"];
    }
    private void NotifyCommands() { RefreshCommand.NotifyCanExecuteChanged(); NewCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); SaveCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); StartCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); RestartCommand.NotifyCanExecuteChanged(); RotateCommand.NotifyCanExecuteChanged(); MigrateCommand.NotifyCanExecuteChanged(); RollbackCommand.NotifyCanExecuteChanged(); }
    private void Log(string level, string message) => _context.Log(new MptSurfaceLogEntry(level, message, DateTimeOffset.UtcNow));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
