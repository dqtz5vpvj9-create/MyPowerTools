using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class LogsViewModel : ShellPageViewModel
{
    private readonly List<LogLineViewModel> _allLines = [];
    private string _searchText = "";
    private string _levelFilter = "All";
    private bool _wrapLines = true;
    private string _errorMessage = "";
    private string _actionStatus = "";

    public LogsViewModel(
        string selectedModuleName,
        IReadOnlyList<ModulePickerItemViewModel> modules,
        IReadOnlyList<LogLineViewModel> lines,
        Func<Task>? refresh = null,
        string? errorMessage = null)
        : base("Logs", selectedModuleName, modules.Count == 0 ? "empty" : "ready")
    {
        Modules = modules;
        _allLines.AddRange(lines);
        Lines = [];
        ErrorMessage = errorMessage ?? "";
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ToggleWrapCommand = new AsyncRelayCommand(ToggleWrapAsync);
        AllCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("All"));
        InfoCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Info"));
        WarningCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Warning"));
        ErrorCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Error"));
        ApplyFilter();
    }

    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; }
    public ObservableCollection<LogLineViewModel> Lines { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ToggleWrapCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand InfoCommand { get; }
    public ICommand WarningCommand { get; }
    public ICommand ErrorCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                ApplyFilter();
            }
        }
    }

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (SetProperty(ref _levelFilter, value))
            {
                OnPropertyChanged(nameof(AllLabel));
                OnPropertyChanged(nameof(InfoLabel));
                OnPropertyChanged(nameof(WarningLabel));
                OnPropertyChanged(nameof(ErrorLabel));
                ApplyFilter();
            }
        }
    }

    public bool WrapLines
    {
        get => _wrapLines;
        set
        {
            if (SetProperty(ref _wrapLines, value))
            {
                OnPropertyChanged(nameof(WrapText));
            }
        }
    }

    public string WrapText => WrapLines ? "Wrap: On" : "Wrap: Off";
    public string AllLabel => LevelFilter == "All" ? "● All" : "All";
    public string InfoLabel => LevelFilter == "Info" ? "● Info" : "Info";
    public string WarningLabel => LevelFilter == "Warning" ? "● Warning" : "Warning";
    public string ErrorLabel => LevelFilter == "Error" ? "● Error" : "Error";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value ?? ""))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoModules => Modules.Count == 0;
    public bool HasNoLogs => Modules.Count > 0 && Lines.Count == 0;
    public string FilterSummary => $"{Lines.Count}/{_allLines.Count} 行";

    public string ActionStatus
    {
        get => _actionStatus;
        private set
        {
            if (SetProperty(ref _actionStatus, value ?? ""))
            {
                OnPropertyChanged(nameof(HasActionStatus));
            }
        }
    }

    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatus);

    public string CopyText => string.Join(
        Environment.NewLine,
        Lines.Select(line => $"[{line.Time}] [{line.Level}] {line.Message}"));

    private async Task ToggleWrapAsync()
    {
        WrapLines = !WrapLines;
        await Task.CompletedTask;
    }

    private async Task ExportAsync()
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools",
                "logs");
            Directory.CreateDirectory(logsDir);
            var exportPath = Path.Combine(
                logsDir,
                $"logs-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(exportPath, CopyText);
            ActionStatus = $"已导出 {Lines.Count} 行到 {exportPath}";
        }
        catch (Exception ex)
        {
            ActionStatus = $"导出失败：{ex.Message}";
        }
    }

    private async Task SetLevelFilterAsync(string level)
    {
        LevelFilter = level;
        await Task.CompletedTask;
    }

    private void ApplyFilter()
    {
        var query = _searchText?.Trim() ?? "";
        Lines.Clear();
        foreach (var line in _allLines)
        {
            if (_levelFilter != "All" &&
                !string.Equals(line.Level, _levelFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0 &&
                !line.Message.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !line.Level.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Lines.Add(line);
        }

        OnPropertyChanged(nameof(HasNoLogs));
        OnPropertyChanged(nameof(FilterSummary));
        State = HasError
            ? "error"
            : Modules.Count == 0
                ? "empty"
                : Lines.Count == 0
                    ? "empty"
                    : "ready";
    }
}

public sealed class NotificationsViewModel : ShellPageViewModel
{
    public NotificationsViewModel(IReadOnlyList<NotificationItemViewModel> notifications)
        : base("Notifications", $"{notifications.Count} notifications", notifications.Count == 0 ? "empty" : "ready")
    {
        Notifications = notifications;
    }

    public IReadOnlyList<NotificationItemViewModel> Notifications { get; }
    public bool IsEmpty => Notifications.Count == 0;
}

public sealed class PackageManagerViewModel : ShellPageViewModel
{
    private string _installSourceDirectory = "";
    private string _rollbackPackageId = "";
    private string _currentVersion = "-";
    private string _latestVersion = "-";
    private string _updateStatus = "尚未检查更新。";
    private bool _updateAvailable;
    private bool _isUpdateBusy;

    public PackageManagerViewModel(
        IReadOnlyList<PackageSummaryViewModel> packages,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<Task<string?>>? checkUpdate = null,
        Func<Task<string?>>? applyUpdate = null,
        string currentVersion = "-")
        : base("Packages", $"{packages.Count} packages")
    {
        Packages = packages;
        CurrentVersion = currentVersion;
        InstallCommand = new AsyncRelayCommand(() => installPackage?.Invoke(InstallSourceDirectory) ?? Task.CompletedTask);
        RollbackCommand = new AsyncRelayCommand(() => rollbackPackage?.Invoke(RollbackPackageId) ?? Task.CompletedTask);
        CheckUpdateCommand = new AsyncRelayCommand(() => RunOtaCheckAsync(checkUpdate));
        ApplyUpdateCommand = new AsyncRelayCommand(() => RunOtaApplyAsync(applyUpdate));
    }

    public IReadOnlyList<PackageSummaryViewModel> Packages { get; }
    public bool IsEmpty => Packages.Count == 0;
    public ICommand InstallCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }

    public string InstallSourceDirectory
    {
        get => _installSourceDirectory;
        set => SetProperty(ref _installSourceDirectory, value);
    }

    public string RollbackPackageId
    {
        get => _rollbackPackageId;
        set => SetProperty(ref _rollbackPackageId, value);
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        private set
        {
            if (SetProperty(ref _currentVersion, value))
            {
                OnPropertyChanged(nameof(UpdateVersionText));
            }
        }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(UpdateVersionText));
            }
        }
    }

    public string UpdateVersionText => $"当前版本 {CurrentVersion} · 最新版本 {LatestVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set => SetProperty(ref _isUpdateBusy, value);
    }

    private async Task RunOtaCheckAsync(Func<Task<string?>>? checkUpdate)
    {
        if (checkUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            var output = await checkUpdate();
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "检查更新失败：更新器没有返回结果。";
                return;
            }

            var node = JsonNode.Parse(output);
            if (node is null)
            {
                UpdateStatus = $"检查更新失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"检查更新失败：{error}";
                return;
            }

            var current = node["currentVersion"]?.GetValue<string>() ?? CurrentVersion;
            var latest = node["latestVersion"]?.GetValue<string>() ?? "-";
            var available = node["available"]?.GetValue<bool>() ?? false;
            var reason = node["reason"]?.GetValue<string>() ?? "";
            CurrentVersion = current;
            LatestVersion = latest;
            UpdateAvailable = available;
            UpdateStatus = available
                ? $"发现新版本 {latest}。点击“立即升级”开始更新（更新期间界面会关闭并自动重启）。"
                : $"当前已是最新版本（{reason}）。";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private async Task RunOtaApplyAsync(Func<Task<string?>>? applyUpdate)
    {
        if (applyUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateStatus = "正在下载并升级，界面即将关闭并自动重启…";
        try
        {
            var output = await applyUpdate();
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "升级失败：更新器没有返回结果。";
                return;
            }

            var node = JsonNode.Parse(output);
            if (node is null)
            {
                UpdateStatus = $"升级失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"升级失败：{error}";
                return;
            }

            var success = node["success"]?.GetValue<bool>() ?? false;
            var toVersion = node["toVersion"]?.GetValue<string>();
            var healthOk = node["health"]?["ok"]?.GetValue<bool>() ?? false;
            if (success && !string.IsNullOrWhiteSpace(toVersion))
            {
                CurrentVersion = toVersion;
                LatestVersion = toVersion;
                UpdateAvailable = false;
                UpdateStatus = healthOk
                    ? $"已升级到 {toVersion}，健康检查通过。"
                    : $"已升级到 {toVersion}，但健康检查未完全通过，请查看 OTA 日志。";
            }
            else
            {
                UpdateStatus = "升级失败，请查看 %LOCALAPPDATA%\\MyPowerTools\\ota-state\\last-update.json 与日志。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"升级失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private static string CollapseOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length <= 400)
        {
            return trimmed.Replace("\r", " ").Replace("\n", " ");
        }

        return trimmed[..400].Replace("\r", " ").Replace("\n", " ") + "…";
    }
}

public sealed class DiagnosticsViewModel : ShellPageViewModel
{
    public DiagnosticsViewModel(
        string subtitle,
        IReadOnlyList<MetricViewModel> metrics,
        IReadOnlyList<MetricViewModel> paths,
        IReadOnlyList<RuntimeTransportViewModel> transports,
        IReadOnlyList<RuntimeProcessViewModel> processes,
        IReadOnlyList<RuntimeProcessPolicyHistoryItemViewModel> processPolicyHistory,
        IReadOnlyList<RuntimeModuleDiagnosticViewModel> modules,
        IReadOnlyList<RuntimeCommandHistoryItemViewModel> recentCommands,
        IReadOnlyList<BrokerAuditEntryViewModel> brokerAudit)
        : base("Diagnostics", subtitle)
    {
        Metrics = metrics;
        Paths = paths;
        Transports = transports;
        Processes = processes;
        ProcessPolicyHistory = processPolicyHistory;
        Modules = modules;
        RecentCommands = recentCommands;
        BrokerAudit = brokerAudit;
    }

    public IReadOnlyList<MetricViewModel> Metrics { get; }
    public IReadOnlyList<MetricViewModel> Paths { get; }
    public IReadOnlyList<RuntimeTransportViewModel> Transports { get; }
    public IReadOnlyList<RuntimeProcessViewModel> Processes { get; }
    public IReadOnlyList<RuntimeProcessPolicyHistoryItemViewModel> ProcessPolicyHistory { get; }
    public IReadOnlyList<RuntimeModuleDiagnosticViewModel> Modules { get; }
    public IReadOnlyList<RuntimeCommandHistoryItemViewModel> RecentCommands { get; }
    public IReadOnlyList<BrokerAuditEntryViewModel> BrokerAudit { get; }
    public bool HasNoTransports => Transports.Count == 0;
    public bool HasNoProcesses => Processes.Count == 0;
    public bool HasNoProcessPolicyHistory => ProcessPolicyHistory.Count == 0;
    public bool HasNoModules => Modules.Count == 0;
    public bool HasNoRecentCommands => RecentCommands.Count == 0;
    public bool HasNoBrokerAudit => BrokerAudit.Count == 0;
}

public sealed class PermissionPromptViewModel
{
    public PermissionPromptViewModel(IReadOnlyList<MetricViewModel> details, ICommand auditCommand)
    {
        Details = details;
        AuditCommand = auditCommand;
    }

    public string Title => "Permission Required";
    public IReadOnlyList<MetricViewModel> Details { get; }
    public ICommand AuditCommand { get; }
}

public sealed class BrokerAuditViewModel
{
    public BrokerAuditViewModel(IReadOnlyList<BrokerAuditSidebarEntryViewModel> entries, string errorMessage = "")
    {
        Entries = entries;
        ErrorMessage = errorMessage;
    }

    public string Title => "Broker Audit";
    public IReadOnlyList<BrokerAuditSidebarEntryViewModel> Entries { get; }
    public string ErrorMessage { get; }
    public bool IsEmpty => Entries.Count == 0 && ErrorMessage.Length == 0;
    public bool HasError => ErrorMessage.Length > 0;
}

public sealed class UnavailablePageViewModel : ShellPageViewModel
{
    public UnavailablePageViewModel(
        string title,
        string message,
        Func<Task>? retry = null,
        Func<Task>? returnToSafety = null)
        : base(title, "", "error")
    {
        Message = message;
        HasRetry = retry is not null;
        HasReturnAction = returnToSafety is not null;
        RetryCommand = new AsyncRelayCommand(() => retry?.Invoke() ?? Task.CompletedTask, operationName: $"Retry {title}");
        ReturnCommand = new AsyncRelayCommand(() => returnToSafety?.Invoke() ?? Task.CompletedTask, operationName: $"Leave {title}");
    }

    public string Message { get; }
    public bool HasRetry { get; }
    public bool HasReturnAction { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReturnCommand { get; }
}
