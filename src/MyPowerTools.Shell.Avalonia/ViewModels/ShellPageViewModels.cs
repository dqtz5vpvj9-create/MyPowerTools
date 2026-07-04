using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
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

public sealed class ModuleDetailViewModel : ShellPageViewModel
{
    public ModuleDetailViewModel(
        string moduleId,
        string packageId,
        string displayName,
        string state,
        string summary,
        IReadOnlyList<MetricViewModel> metrics,
        IReadOnlyList<ModulePermissionViewModel> permissions,
        IReadOnlyList<ModuleRequirementViewModel> requirements,
        IReadOnlyList<ModuleDiagnosticItemViewModel> diagnostics,
        IReadOnlyList<CommandItemViewModel> commands,
        ICommand toggleEnabledCommand)
        : base(displayName, packageId, state)
    {
        ModuleId = moduleId;
        PackageId = packageId;
        StateLabel = state;
        Summary = summary;
        Metrics = metrics;
        Permissions = permissions;
        Requirements = requirements;
        Diagnostics = diagnostics;
        Commands = commands;
        ToggleEnabledCommand = toggleEnabledCommand;
    }

    public string ModuleId { get; }
    public string PackageId { get; }
    public string StateLabel { get; }
    public string Summary { get; }
    public IReadOnlyList<MetricViewModel> Metrics { get; }
    public IReadOnlyList<ModulePermissionViewModel> Permissions { get; }
    public IReadOnlyList<ModuleRequirementViewModel> Requirements { get; }
    public IReadOnlyList<ModuleDiagnosticItemViewModel> Diagnostics { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public ICommand ToggleEnabledCommand { get; }
    public string ToggleEnabledLabel => string.Equals(StateLabel, "disabled", StringComparison.OrdinalIgnoreCase) ? "Enable" : "Disable";
    public bool HasNoPermissions => Permissions.Count == 0;
    public bool HasNoRequirements => Requirements.Count == 0;
    public bool HasNoDiagnostics => Diagnostics.Count == 0;
    public bool HasNoCommands => Commands.Count == 0;
}

public sealed class SettingsCenterViewModel : ShellPageViewModel
{
    private string _rawJson;
    private string _statusText;

    public SettingsCenterViewModel(
        string selectedModuleId,
        string selectedModuleName,
        ulong revision,
        string rawJson,
        string statusText,
        IReadOnlyList<ModulePickerItemViewModel> modules,
        IReadOnlyList<SettingsFieldViewModel> fields,
        Func<SettingsCenterViewModel, Task>? saveSettings = null)
        : base("Settings", selectedModuleName, selectedModuleId.Length == 0 ? "empty" : "ready")
    {
        SelectedModuleId = selectedModuleId;
        Revision = revision;
        _rawJson = rawJson;
        _statusText = statusText;
        Modules = modules;
        Fields = fields;
        SaveCommand = new AsyncRelayCommand(() => saveSettings?.Invoke(this) ?? Task.CompletedTask, () => SelectedModuleId.Length > 0);
    }

    public string SelectedModuleId { get; }
    public ulong Revision { get; }
    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; }
    public IReadOnlyList<SettingsFieldViewModel> Fields { get; }
    public bool HasNoModules => Modules.Count == 0;
    public bool HasFields => Fields.Count > 0;
    public bool UsesRawJson => SelectedModuleId.Length > 0 && Fields.Count == 0;
    public ICommand SaveCommand { get; }

    public string RawJson
    {
        get => _rawJson;
        set => SetProperty(ref _rawJson, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
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

public sealed record ModulePermissionViewModel(string Id, string Level, string Capability, string Reason);
public sealed record ModuleRequirementViewModel(string Capability, string StateLabel, string Reason);
public sealed record ModuleDiagnosticItemViewModel(string Label, string State, string Detail);

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
public sealed record BrokerAuditSidebarEntryViewModel(string Title, string Detail);

public sealed class SettingsFieldViewModel : ObservableViewModel
{
    private string _value;
    private bool _booleanValue;
    private string _selectedOption;

    public SettingsFieldViewModel(
        string key,
        string label,
        string editorType,
        string description,
        string value,
        bool booleanValue,
        IReadOnlyList<string> options,
        string selectedOption)
    {
        Key = key;
        Label = label;
        EditorType = editorType;
        Description = description;
        _value = value;
        _booleanValue = booleanValue;
        Options = options;
        _selectedOption = selectedOption;
    }

    public string Key { get; }
    public string Label { get; }
    public string EditorType { get; }
    public string Description { get; }
    public IReadOnlyList<string> Options { get; }
    public bool IsBooleanEditor => EditorType == "boolean";
    public bool IsEnumEditor => EditorType == "enum";
    public bool IsMultilineEditor => EditorType is "object" or "array";
    public bool IsSingleLineTextEditor => !IsBooleanEditor && !IsEnumEditor && !IsMultilineEditor;

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public bool BooleanValue
    {
        get => _booleanValue;
        set => SetProperty(ref _booleanValue, value);
    }

    public string SelectedOption
    {
        get => _selectedOption;
        set => SetProperty(ref _selectedOption, value);
    }
}

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

    public static ModuleDetailViewModel FromModuleDetail(
        HostProto.ModuleDetail detail,
        HostProto.ListCommandsResponse commands,
        Func<string, bool, Task>? setModuleEnabled = null,
        Func<string, Task>? executeCommand = null)
    {
        var enabled = !string.Equals(detail.State, "disabled", StringComparison.OrdinalIgnoreCase);
        var metrics = new[]
        {
            new MetricViewModel("Package", detail.PackageId),
            new MetricViewModel("Module", detail.ModuleId),
            new MetricViewModel("Diagnostics", detail.Diagnostics.Count.ToString()),
            new MetricViewModel("Permissions", detail.Permissions.Count.ToString())
        };

        var permissions = detail.Permissions
            .OrderBy(permission => permission.Level, StringComparer.OrdinalIgnoreCase)
            .ThenBy(permission => permission.Id, StringComparer.OrdinalIgnoreCase)
            .Select(permission => new ModulePermissionViewModel(
                permission.Id,
                permission.Level,
                string.IsNullOrWhiteSpace(permission.Capability) ? "No capability" : permission.Capability,
                permission.Reason))
            .ToArray();

        var requirements = detail.Requirements
            .OrderByDescending(requirement => requirement.Required)
            .ThenBy(requirement => requirement.Capability, StringComparer.OrdinalIgnoreCase)
            .Select(requirement => new ModuleRequirementViewModel(
                requirement.Capability,
                requirement.Required ? "required" : "optional",
                requirement.Reason))
            .ToArray();

        var diagnostics = detail.Diagnostics
            .Select(diagnostic => new ModuleDiagnosticItemViewModel(
                diagnostic.Label,
                diagnostic.State,
                diagnostic.Detail))
            .ToArray();

        var commandItems = commands.Commands
            .Where(command => string.Equals(command.ModuleId, detail.ModuleId, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .Select(command => new CommandItemViewModel(
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
                new AsyncRelayCommand(() => executeCommand?.Invoke(command.CommandId) ?? Task.CompletedTask)))
            .ToArray();

        return new ModuleDetailViewModel(
            detail.ModuleId,
            detail.PackageId,
            detail.DisplayName,
            detail.State,
            detail.Summary,
            metrics,
            permissions,
            requirements,
            diagnostics,
            commandItems,
            new AsyncRelayCommand(() => setModuleEnabled?.Invoke(detail.ModuleId, !enabled) ?? Task.CompletedTask));
    }

    public static SettingsCenterViewModel FromSettings(
        HostProto.ListModulesResponse modules,
        HostProto.ModuleSummary? selected,
        string schemaJson,
        JsonObject values,
        string rawJson,
        ulong revision,
        DateTimeOffset updatedAt,
        Func<string, Task>? selectModule = null,
        Func<SettingsCenterViewModel, Task>? saveSettings = null)
    {
        var selectedModuleId = selected?.ModuleId ?? "";
        var picker = modules.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module => new ModulePickerItemViewModel(
                module.ModuleId,
                module.DisplayName,
                string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase),
                string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase) ? "Selected" : "",
                new AsyncRelayCommand(() => selectModule?.Invoke(module.ModuleId) ?? Task.CompletedTask)))
            .ToArray();

        var fields = selected is null ? [] : BuildSettingsFields(schemaJson, values);
        var statusText = selected is null
            ? "No modules."
            : $"Revision {revision} - {updatedAt:yyyy-MM-dd HH:mm:ss} - Schema fields {fields.Count}";

        return new SettingsCenterViewModel(
            selectedModuleId,
            selected?.DisplayName ?? "No modules.",
            revision,
            rawJson,
            statusText,
            picker,
            fields,
            saveSettings);
    }

    public static JsonObject BuildSettingsPatch(SettingsCenterViewModel viewModel)
    {
        if (viewModel.UsesRawJson)
        {
            return ParseRawSettings(viewModel.RawJson);
        }

        var patch = new JsonObject();
        foreach (var field in viewModel.Fields)
        {
            patch[field.Key] = field.EditorType switch
            {
                "boolean" => JsonValue.Create(field.BooleanValue),
                "integer" => JsonValue.Create(ParseLong(field.Value, field.Key)),
                "number" => JsonValue.Create(ParseDouble(field.Value, field.Key)),
                "object" => ParseCompositeSetting(field.Value, field.Key, "{}"),
                "array" => ParseCompositeSetting(field.Value, field.Key, "[]"),
                "enum" => JsonValue.Create(field.SelectedOption),
                _ => JsonValue.Create(field.Value)
            };
        }

        return patch;
    }

    public static PermissionPromptViewModel FromPermissionPrompt(
        HostProto.CommandExecutionResponse result,
        Func<Task>? showAudit = null)
    {
        var details = result.ErrorDetails;
        var actionId = ReadDetailString(details, "actionId", result.ErrorCode);
        var scope = ReadDetailString(details, "scope", "");
        var reason = ReadDetailString(details, "reason", result.ErrorMessage);
        var applyCount = CountNestedList(details, "expectedChange", "apply");
        var removeCount = CountNestedList(details, "expectedChange", "remove");
        var rollbackCount = CountList(details, "rollback");

        var rows = new[]
        {
            new MetricViewModel("Action", string.IsNullOrWhiteSpace(actionId) ? "-" : actionId),
            new MetricViewModel("Scope", string.IsNullOrWhiteSpace(scope) ? "-" : scope),
            new MetricViewModel("Reason", string.IsNullOrWhiteSpace(reason) ? "-" : reason),
            new MetricViewModel("Expected change", $"{applyCount} apply, {removeCount} remove"),
            new MetricViewModel("Rollback", $"{rollbackCount} step(s)")
        };

        return new PermissionPromptViewModel(rows, new AsyncRelayCommand(() => showAudit?.Invoke() ?? Task.CompletedTask));
    }

    public static BrokerAuditViewModel FromBrokerAudit(HostProto.ListBrokerAuditResponse audit)
    {
        var entries = audit.Entries.Select(entry => new BrokerAuditSidebarEntryViewModel(
            $"{entry.Result} - {entry.ActionId}",
            $"{entry.ModuleId} - {entry.Scope}")).ToArray();

        return new BrokerAuditViewModel(entries);
    }

    public static BrokerAuditViewModel FromBrokerAuditError(string message)
    {
        return new BrokerAuditViewModel([], $"Audit unavailable: {message}");
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

    private static string ReadDetailString(Struct details, string key, string fallback)
    {
        if (details.Fields.TryGetValue(key, out var value))
        {
            return DetailValueToText(value);
        }

        return fallback;
    }

    private static int CountNestedList(Struct details, string objectKey, string listKey)
    {
        if (details.Fields.TryGetValue(objectKey, out var outer) &&
            outer.KindCase == Value.KindOneofCase.StructValue &&
            outer.StructValue.Fields.TryGetValue(listKey, out var inner) &&
            inner.KindCase == Value.KindOneofCase.ListValue)
        {
            return inner.ListValue.Values.Count;
        }

        return 0;
    }

    private static int CountList(Struct details, string key)
    {
        if (details.Fields.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.ListValue)
        {
            return value.ListValue.Values.Count;
        }

        return 0;
    }

    private static string DetailValueToText(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString("0.##", CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.ListValue => $"{value.ListValue.Values.Count} item(s)",
            Value.KindOneofCase.StructValue => $"{value.StructValue.Fields.Count} field(s)",
            _ => ""
        };
    }

    private static IReadOnlyList<SettingsFieldViewModel> BuildSettingsFields(string schemaJson, JsonObject values)
    {
        var schema = TryParseSettingsSchema(schemaJson);
        if (schema is null ||
            !schema.TryGetPropertyValue("properties", out var propertiesNode) ||
            propertiesNode is not JsonObject properties)
        {
            return [];
        }

        var fields = new List<SettingsFieldViewModel>();
        foreach (var propertyPair in properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (propertyPair.Value is not JsonObject property)
            {
                continue;
            }

            var key = propertyPair.Key;
            var label = GetSchemaString(property, "title", key);
            var description = GetSchemaString(property, "description", "");
            var type = GetSchemaString(property, "type", "string").ToLowerInvariant();
            values.TryGetPropertyValue(key, out var currentValue);
            property.TryGetPropertyValue("default", out var defaultValue);
            var effectiveValue = currentValue ?? defaultValue;
            var editorType = type;
            IReadOnlyList<string> options = [];
            var selectedOption = "";

            if (property.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumValues)
            {
                editorType = "enum";
                options = enumValues
                    .Select(NodeToEditorText)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                var selected = NodeToEditorText(effectiveValue);
                selectedOption = options.Contains(selected, StringComparer.OrdinalIgnoreCase)
                    ? selected
                    : options.FirstOrDefault() ?? "";
            }

            var textValue = NodeToEditorText(effectiveValue);
            fields.Add(new SettingsFieldViewModel(
                key,
                label,
                editorType,
                description,
                textValue,
                NodeToBool(effectiveValue),
                options,
                selectedOption));
        }

        return fields;
    }

    private static JsonObject ParseRawSettings(string rawJson)
    {
        var text = string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson;
        return JsonNode.Parse(text) as JsonObject
            ?? throw new FormatException("Raw settings must be a JSON object.");
    }

    private static JsonObject? TryParseSettingsSchema(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(schemaJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetSchemaString(JsonObject schema, string key, string fallback)
    {
        return schema.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;
    }

    private static bool NodeToBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static string NodeToEditorText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = node is JsonObject or JsonArray });
    }

    private static long ParseLong(string value, string key)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be an integer.");
    }

    private static double ParseDouble(string value, string key)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be a number.");
    }

    private static JsonNode ParseCompositeSetting(string value, string key, string emptyValue)
    {
        var text = string.IsNullOrWhiteSpace(value) ? emptyValue : value;
        return JsonNode.Parse(text)
            ?? throw new FormatException($"{key} must be valid JSON.");
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
