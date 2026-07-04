using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public abstract class ShellPageViewModel : ObservableViewModel
{
    private string _title;
    private string _subtitle;
    private string _state;

    protected ShellPageViewModel(string title, string subtitle = "", string state = "ready")
    {
        _title = title;
        _subtitle = subtitle;
        _state = state;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

public sealed class DashboardViewModel : ShellPageViewModel
{
    public DashboardViewModel(string subtitle, IReadOnlyList<DashboardCardViewModel> cards, IReadOnlyList<ShellAlertViewModel> alerts)
        : base("Dashboard", subtitle)
    {
        Cards = cards;
        Alerts = alerts;
    }

    public IReadOnlyList<DashboardCardViewModel> Cards { get; }
    public IReadOnlyList<ShellAlertViewModel> Alerts { get; }
}

public sealed class CommandPaletteViewModel : ShellPageViewModel
{
    public CommandPaletteViewModel(string query, IReadOnlyList<CommandItemViewModel> commands)
        : base("Command Palette", $"{commands.Count} commands", commands.Count == 0 ? "empty" : "ready")
    {
        Query = query;
        Commands = commands;
    }

    public string Query { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public bool IsEmpty => Commands.Count == 0;
}

public sealed class ModulesViewModel : ShellPageViewModel
{
    public ModulesViewModel(IReadOnlyList<ModuleSummaryItemViewModel> modules)
        : base("Modules", $"{modules.Count} modules", modules.Count == 0 ? "empty" : "ready")
    {
        Modules = modules;
    }

    public IReadOnlyList<ModuleSummaryItemViewModel> Modules { get; }
    public bool IsEmpty => Modules.Count == 0;
}

public sealed class SettingsCenterViewModel : ShellPageViewModel
{
    public SettingsCenterViewModel(string selectedModuleId, string selectedModuleName, ulong revision, IReadOnlyList<SettingsFieldViewModel> fields)
        : base("Settings", selectedModuleName, fields.Count == 0 ? "empty" : "ready")
    {
        SelectedModuleId = selectedModuleId;
        Revision = revision;
        Fields = fields;
    }

    public string SelectedModuleId { get; }
    public ulong Revision { get; }
    public IReadOnlyList<SettingsFieldViewModel> Fields { get; }
}

public sealed class LogsViewModel : ShellPageViewModel
{
    public LogsViewModel(string selectedModuleName, IReadOnlyList<ModulePickerItemViewModel> modules, IReadOnlyList<LogLineViewModel> lines)
        : base("Logs", selectedModuleName, modules.Count == 0 || lines.Count == 0 ? "empty" : "ready")
    {
        Modules = modules;
        Lines = lines;
    }

    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; }
    public IReadOnlyList<LogLineViewModel> Lines { get; }
    public bool HasNoModules => Modules.Count == 0;
    public bool HasNoLogs => Modules.Count > 0 && Lines.Count == 0;
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

    public PackageManagerViewModel(
        IReadOnlyList<PackageSummaryViewModel> packages,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null)
        : base("Packages", $"{packages.Count} packages")
    {
        Packages = packages;
        InstallCommand = new AsyncRelayCommand(() => installPackage?.Invoke(InstallSourceDirectory) ?? Task.CompletedTask);
        RollbackCommand = new AsyncRelayCommand(() => rollbackPackage?.Invoke(RollbackPackageId) ?? Task.CompletedTask);
    }

    public IReadOnlyList<PackageSummaryViewModel> Packages { get; }
    public bool IsEmpty => Packages.Count == 0;
    public ICommand InstallCommand { get; }
    public ICommand RollbackCommand { get; }

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

public sealed record DashboardCardViewModel(
    string ModuleId,
    string PackageId,
    string Title,
    string State,
    string Summary,
    IReadOnlyList<MetricViewModel> Metrics,
    IReadOnlyList<ShellActionViewModel> Actions,
    ICommand DetailsCommand);

public sealed record CommandItemViewModel(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation,
    string ModuleLabel,
    string RiskLabel,
    string ParameterSummary,
    bool HasParameters,
    ICommand ExecuteCommand);

public sealed record ModuleSummaryItemViewModel(
    string ModuleId,
    string PackageId,
    string DisplayName,
    string State,
    string Summary,
    bool Enabled,
    string Identity,
    string PermissionSummary,
    string ToggleLabel,
    bool HasElevatedPermissions,
    ICommand DetailsCommand,
    ICommand SettingsCommand,
    ICommand LogsCommand,
    ICommand ToggleEnabledCommand);

public sealed record PackageSummaryViewModel(
    string PackageId,
    string DisplayName,
    string Version,
    string Publisher,
    string Directory,
    string Hashes,
    string TrustPolicy,
    string SignaturePath,
    string TrustState,
    uint ModuleCount,
    uint SharedRuntimeCount,
    uint TrustIssueCount,
    string ModuleIdsText,
    IReadOnlyList<MetricViewModel> Metrics,
    IReadOnlyList<PackageModuleLinkViewModel> ModuleLinks,
    ICommand RepairCommand,
    ICommand UninstallCommand,
    ICommand RollbackCommand);

public sealed record PackageModuleLinkViewModel(string ModuleId, ICommand OpenCommand);

public sealed record RuntimeProcessViewModel(
    string TransportKind,
    string PoolKey,
    string State,
    uint ProcessId,
    string ProcessText,
    string Endpoint,
    string Starts,
    string RestartPolicy,
    string PolicyText,
    string PolicyExpiresAt,
    string ModulesText,
    string StartedAt,
    bool IsPaused,
    bool CanPauseForMaintenance,
    string PolicyToggleLabel,
    ICommand RestartCommand,
    ICommand ToggleRestartPolicyCommand,
    ICommand PauseForMaintenanceCommand);

public sealed record RuntimeTransportViewModel(string Kind, string State, string ModuleCount);
public sealed record RuntimeProcessPolicyHistoryItemViewModel(string Title, string Detail, string Reason);
public sealed record RuntimeModuleDiagnosticViewModel(string DisplayName, string State, IReadOnlyList<MetricViewModel> Details);
public sealed record RuntimeCommandHistoryItemViewModel(string Title, string Detail, string Summary);
public sealed record BrokerAuditEntryViewModel(string Title, string Detail, string Reason, string Rollback);

public sealed record SettingsFieldViewModel(string Key, string Label, string EditorType, string Description, string Value);
public sealed record ModulePickerItemViewModel(string ModuleId, string DisplayName, bool IsSelected, string SelectionText, ICommand SelectCommand);
public sealed record LogLineViewModel(string Time, string Level, string Message);
public sealed record NotificationItemViewModel(string Id, string Time, string ModuleId, string Level, string Title, string Body, bool IsRead);
public sealed record ShellAlertViewModel(string Id, string Level, string Title, string Body);
public sealed record ShellActionViewModel(string CommandId, string Title, string Style, ICommand ExecuteCommand);
public sealed record MetricViewModel(string Label, string Value);

public static class ShellPageViewModelFactory
{
    public static DashboardViewModel FromDashboard(
        HostProto.DashboardSnapshot snapshot,
        Func<string, Task>? showDetails = null,
        Func<string, Task>? executeAction = null)
    {
        var cards = snapshot.Cards.Select(card => new DashboardCardViewModel(
            card.ModuleId,
            card.PackageId,
            card.Title,
            card.State,
            card.Summary,
            card.Metrics.Select(metric => new MetricViewModel(metric.Label, metric.Value)).ToArray(),
            card.Actions.Select(action => new ShellActionViewModel(
                action.CommandId,
                action.Title,
                action.Style,
                new AsyncRelayCommand(() => executeAction?.Invoke(action.CommandId) ?? Task.CompletedTask))).ToArray(),
            new AsyncRelayCommand(() => showDetails?.Invoke(card.ModuleId) ?? Task.CompletedTask))).ToArray();

        var alerts = snapshot.Alerts.Select(alert => new ShellAlertViewModel(
            alert.Id,
            alert.Level,
            alert.Title,
            alert.Body)).ToArray();

        return new DashboardViewModel($"{cards.Length} modules indexed, event seq {snapshot.EventSeq}", cards, alerts);
    }

    public static CommandPaletteViewModel FromCommands(
        string query,
        HostProto.ListCommandsResponse response,
        Func<string, Task>? executeCommand = null)
    {
        var commands = response.Commands.Select(command => new CommandItemViewModel(
            command.CommandId,
            command.ModuleId,
            command.Title,
            command.Subtitle,
            command.DangerLevel,
            command.RequiresElevation,
            string.IsNullOrWhiteSpace(command.ModuleId) ? "Module: unknown" : $"Module: {command.ModuleId}",
            command.RequiresElevation ? $"{command.DangerLevel} - elevation" : command.DangerLevel,
            "",
            false,
            new AsyncRelayCommand(() => executeCommand?.Invoke(command.CommandId) ?? Task.CompletedTask))).ToArray();

        return new CommandPaletteViewModel(query, commands);
    }

    public static ModulesViewModel FromModules(
        HostProto.ListModulesResponse response,
        Func<string, Task>? showDetails = null,
        Func<string, Task>? showSettings = null,
        Func<string, Task>? showLogs = null,
        Func<string, bool, Task>? setModuleEnabled = null)
    {
        var modules = response.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var permissionSummary = module.Permissions.Count == 0
                    ? "Permissions: none"
                    : $"Permissions: {module.Permissions.Count} declared";
                var hasElevatedPermissions = module.Permissions.Any(permission =>
                    permission.Level is "broker" or "elevated" or "service");

                return new ModuleSummaryItemViewModel(
                    module.ModuleId,
                    module.PackageId,
                    module.DisplayName,
                    module.State,
                    module.Summary,
                    module.Enabled,
                    $"{module.PackageId} · {module.ModuleId}",
                    $"{permissionSummary} · Requirements: {module.Requirements.Count}",
                    module.Enabled ? "Disable" : "Enable",
                    hasElevatedPermissions,
                    new AsyncRelayCommand(() => showDetails?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => showSettings?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => showLogs?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => setModuleEnabled?.Invoke(module.ModuleId, !module.Enabled) ?? Task.CompletedTask));
            }).ToArray();

        return new ModulesViewModel(modules);
    }

    public static PackageManagerViewModel FromPackages(
        HostProto.ListPackagesResponse response,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<string, Task>? repairPackage = null,
        Func<string, Task>? uninstallPackage = null,
        Func<string, Task>? showModuleDetails = null)
    {
        var packages = response.Packages
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(package =>
            {
                var hashes = string.IsNullOrWhiteSpace(package.Hashes) ? "-" : package.Hashes;
                var signaturePath = string.IsNullOrWhiteSpace(package.SignaturePath) ? "-" : package.SignaturePath;
                var trustPolicy = string.IsNullOrWhiteSpace(package.TrustPolicy) ? "-" : package.TrustPolicy;
                var metrics = new[]
                {
                    new MetricViewModel("Version", package.Version),
                    new MetricViewModel("Modules", package.ModuleCount.ToString()),
                    new MetricViewModel("Runtimes", package.SharedRuntimeCount.ToString()),
                    new MetricViewModel("Trust", trustPolicy),
                    new MetricViewModel("Issues", package.TrustIssueCount.ToString()),
                    new MetricViewModel("Hashes", hashes),
                    new MetricViewModel("Signature", signaturePath)
                };
                var moduleLinks = package.ModuleIds
                    .Take(3)
                    .Select(moduleId => new PackageModuleLinkViewModel(
                        moduleId,
                        new AsyncRelayCommand(() => showModuleDetails?.Invoke(moduleId) ?? Task.CompletedTask)))
                    .ToArray();

                return new PackageSummaryViewModel(
                    package.PackageId,
                    package.DisplayName,
                    package.Version,
                    package.Publisher,
                    package.Directory,
                    hashes,
                    trustPolicy,
                    signaturePath,
                    package.TrustState,
                    package.ModuleCount,
                    package.SharedRuntimeCount,
                    package.TrustIssueCount,
                    package.ModuleIds.Count == 0 ? "No modules." : string.Join(", ", package.ModuleIds),
                    metrics,
                    moduleLinks,
                    new AsyncRelayCommand(() => repairPackage?.Invoke(package.PackageId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => uninstallPackage?.Invoke(package.PackageId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => rollbackPackage?.Invoke(package.PackageId) ?? Task.CompletedTask));
            }).ToArray();

        return new PackageManagerViewModel(packages, installPackage, rollbackPackage);
    }

    public static LogsViewModel FromLogs(
        HostProto.ListModulesResponse modules,
        HostProto.ModuleSummary? selectedModule,
        IReadOnlyList<HostProto.LogEntry> entries,
        Func<string, Task>? selectModule = null)
    {
        var selectedModuleId = selectedModule?.ModuleId ?? "";
        var moduleItems = modules.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var isSelected = string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase);
                return new ModulePickerItemViewModel(
                    module.ModuleId,
                    module.DisplayName,
                    isSelected,
                    isSelected ? "Selected" : "",
                    new AsyncRelayCommand(() => selectModule?.Invoke(module.ModuleId) ?? Task.CompletedTask));
            }).ToArray();

        var lines = entries.Select(entry => new LogLineViewModel(
            entry.Time.ToDateTimeOffset().ToString("HH:mm:ss"),
            entry.Level,
            entry.Message)).ToArray();

        return new LogsViewModel(selectedModule?.DisplayName ?? "", moduleItems, lines);
    }

    public static NotificationsViewModel FromNotifications(HostProto.ListNotificationsResponse response)
    {
        var notifications = response.Notifications.Select(notification => new NotificationItemViewModel(
            notification.Id,
            notification.Time.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            notification.ModuleId,
            notification.Level,
            notification.Title,
            notification.Body,
            notification.IsRead)).ToArray();

        return new NotificationsViewModel(notifications);
    }

    public static DiagnosticsViewModel FromDiagnostics(
        HostProto.RuntimeDiagnostics diagnostics,
        HostProto.ListBrokerAuditResponse? audit = null,
        Func<string, string, Task>? restartProcess = null,
        Func<string, string, bool, DateTimeOffset?, string?, Task>? setRestartPolicy = null)
    {
        var metrics = new[]
        {
            new MetricViewModel("Runner", diagnostics.RunnerVersion),
            new MetricViewModel("Host IPC", diagnostics.HostControlProtocolVersion),
            new MetricViewModel("Module IPC", diagnostics.ModuleProtocolVersion),
            new MetricViewModel("Platform", diagnostics.PlatformRid),
            new MetricViewModel("Packages", diagnostics.Counts.PackageCount.ToString()),
            new MetricViewModel("Modules", diagnostics.Counts.ModuleCount.ToString()),
            new MetricViewModel("Enabled", diagnostics.Counts.EnabledModuleCount.ToString()),
            new MetricViewModel("Commands", diagnostics.Counts.CommandCount.ToString()),
            new MetricViewModel("Running", diagnostics.Counts.RunningModuleCount.ToString()),
            new MetricViewModel("Degraded", diagnostics.Counts.DegradedModuleCount.ToString()),
            new MetricViewModel("Errors", diagnostics.Counts.ErrorModuleCount.ToString()),
            new MetricViewModel("Events", diagnostics.CurrentEventSeq.ToString())
        };

        var paths = new[]
        {
            new MetricViewModel("Root", diagnostics.Paths.Root),
            new MetricViewModel("Settings", diagnostics.Paths.Settings),
            new MetricViewModel("Logs", diagnostics.Paths.Logs),
            new MetricViewModel("State", diagnostics.Paths.State),
            new MetricViewModel("Packages", diagnostics.Paths.Packages),
            new MetricViewModel("Package Root", diagnostics.Paths.PackageRoot)
        };

        var transports = diagnostics.Transports.Select(transport => new RuntimeTransportViewModel(
            transport.Kind,
            transport.RuntimeRegistered ? "registered" : "manifest",
            transport.ModuleCount.ToString())).ToArray();

        var processes = diagnostics.Processes.Select(process => new RuntimeProcessViewModel(
            process.TransportKind,
            process.PoolKey,
            process.State,
            process.ProcessId,
            process.ProcessId == 0 ? "external" : process.ProcessId.ToString(),
            process.Endpoint,
            $"{process.StartCount}/{process.RestartLimit}",
            process.RestartPolicy,
            string.IsNullOrWhiteSpace(process.PolicyReason) ? process.RestartPolicy : $"{process.RestartPolicy} - {process.PolicyReason}",
            process.PolicyExpiresAt is null ? "" : process.PolicyExpiresAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            process.ModuleIds.Count == 0 ? "none" : string.Join(", ", process.ModuleIds),
            process.LastStartedAt is null ? "" : process.LastStartedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase),
            !string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase),
            string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase) ? "Resume" : "Pause",
            new AsyncRelayCommand(() => restartProcess?.Invoke(process.TransportKind, process.PoolKey) ?? Task.CompletedTask),
            new AsyncRelayCommand(() =>
            {
                var paused = string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase);
                return setRestartPolicy?.Invoke(process.TransportKind, process.PoolKey, !paused, null, null) ?? Task.CompletedTask;
            }),
            new AsyncRelayCommand(() => setRestartPolicy?.Invoke(
                process.TransportKind,
                process.PoolKey,
                true,
                DateTimeOffset.UtcNow.AddHours(1),
                "Shell Diagnostics maintenance window") ?? Task.CompletedTask))).ToArray();

        var processPolicyHistory = diagnostics.ProcessPolicyHistory.Select(entry => new RuntimeProcessPolicyHistoryItemViewModel(
            $"{entry.RestartPolicy} - {entry.PoolKey}",
            $"{entry.Source} - {entry.TransportKind} - rev {entry.Revision} - {entry.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            entry.ExpiresAt is null
                ? (string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)
                : $"{(string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)} - expires {entry.ExpiresAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}")).ToArray();

        var modules = diagnostics.Modules.Select(module => new RuntimeModuleDiagnosticViewModel(
            module.DisplayName,
            module.State,
            new[]
            {
                new MetricViewModel("Module", module.ModuleId),
                new MetricViewModel("Package", module.PackageId),
                new MetricViewModel("Transport", module.TransportKind),
                new MetricViewModel("Summary", module.Summary),
                new MetricViewModel("Diagnostics", module.DiagnosticCount.ToString()),
                new MetricViewModel("Supervisor", $"{module.SupervisorState} - failures {module.ConsecutiveFailureCount} - observations {module.ObservationCount}"),
                new MetricViewModel("Action", module.SupervisorAction),
                new MetricViewModel("Updated", module.UpdatedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")),
                new MetricViewModel("Observed", module.LastObservedAt is null ? "" : module.LastObservedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"))
            })).ToArray();

        var recentCommands = diagnostics.RecentCommands.Select(command => new RuntimeCommandHistoryItemViewModel(
            $"{command.State} - {command.CommandId}",
            $"{command.ModuleId} - {command.StartedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            command.Summary)).ToArray();

        var brokerAudit = (audit?.Entries ?? []).Select(entry => new BrokerAuditEntryViewModel(
            $"{entry.Result} - {entry.ActionId}",
            $"{entry.ModuleId} - {entry.PermissionLevel} - {entry.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            string.IsNullOrWhiteSpace(entry.Reason) ? entry.Scope : $"{entry.Scope} - {entry.Reason}",
            string.IsNullOrWhiteSpace(entry.Rollback) ? "No rollback." : entry.Rollback)).ToArray();

        return new DiagnosticsViewModel(
            $"Collected {diagnostics.CollectedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            metrics,
            paths,
            transports,
            processes,
            processPolicyHistory,
            modules,
            recentCommands,
            brokerAudit);
    }
}
