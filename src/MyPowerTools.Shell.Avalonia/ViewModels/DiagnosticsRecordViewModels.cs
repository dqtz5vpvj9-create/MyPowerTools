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
    string StdoutText,
    string StderrText,
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
