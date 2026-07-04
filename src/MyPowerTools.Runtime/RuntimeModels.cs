using MyPowerTools.Packaging;

namespace MyPowerTools.Runtime;

public sealed record RuntimeModuleRecord(
    MptPackageDefinition Package,
    MptModuleDefinition Module,
    SelectedEntrypoint? Entrypoint,
    ModuleStatusSnapshot Status);

public sealed record SelectedEntrypoint(
    string Kind,
    int Priority,
    string? RuntimeId,
    string? Command,
    IReadOnlyList<string> Args,
    string? Assembly,
    string? Type,
    string? Service,
    string? EndpointTransport,
    string? EndpointAddress,
    string? HealthPath);

public sealed record DashboardCard(
    string ModuleId,
    string PackageId,
    string Title,
    string State,
    string Summary,
    IReadOnlyList<DashboardMetric> Metrics,
    IReadOnlyList<DashboardAction> Actions);

public sealed record DashboardMetric(string Label, string Value);

public sealed record DashboardAction(string CommandId, string Title, string Style);

public sealed record HostAlert(string Id, string Level, string Title, string Body);

public sealed record DashboardSnapshot(IReadOnlyList<DashboardCard> Cards, IReadOnlyList<HostAlert> Alerts, ulong EventSeq);

public sealed record PackageSummarySnapshot(
    string PackageId,
    string DisplayName,
    string Version,
    string Publisher,
    string Directory,
    string Hashes,
    string TrustState,
    string TrustPolicy,
    string SignaturePath,
    int TrustIssueCount,
    int ModuleCount,
    int SharedRuntimeCount,
    IReadOnlyList<string> ModuleIds);

public sealed record ModuleDetailSnapshot(
    string ModuleId,
    string PackageId,
    string DisplayName,
    string State,
    string Summary,
    IReadOnlyList<HealthCheckSnapshot> Diagnostics,
    IReadOnlyList<ModulePermissionSnapshot> Permissions,
    IReadOnlyList<ModuleRequirementSnapshot> Requirements);

public sealed record ModulePermissionSnapshot(
    string Id,
    string Level,
    string Capability,
    string Reason);

public sealed record ModuleRequirementSnapshot(
    string Capability,
    bool Required,
    string Reason);

public sealed record RuntimeDiagnosticsSnapshot(
    string RunnerVersion,
    string HostControlProtocolVersion,
    string ModuleProtocolVersion,
    string PlatformRid,
    string DotNetVersion,
    string OsDescription,
    string ProcessArchitecture,
    DateTimeOffset StartedAt,
    DateTimeOffset CollectedAt,
    ulong CurrentEventSeq,
    RuntimePathDiagnostics Paths,
    RuntimeCountDiagnostics Counts,
    IReadOnlyList<RuntimeTransportDiagnostics> Transports,
    IReadOnlyList<RuntimeProcessDiagnostics> Processes,
    IReadOnlyList<RuntimeProcessPolicyHistoryEntry> ProcessPolicyHistory,
    IReadOnlyList<RuntimeModuleDiagnostics> Modules,
    IReadOnlyList<CommandHistoryRecord> RecentCommands);

public sealed record RuntimePathDiagnostics(
    string Root,
    string Settings,
    string Logs,
    string State,
    string Packages,
    string PackageRoot);

public sealed record RuntimeCountDiagnostics(
    int PackageCount,
    int ModuleCount,
    int EnabledModuleCount,
    int DisabledModuleCount,
    int RunningModuleCount,
    int DegradedModuleCount,
    int ErrorModuleCount,
    int CommandCount,
    int DynamicCommandCount,
    int NotificationCount,
    int CommandHistoryCount);

public sealed record RuntimeTransportDiagnostics(
    string Kind,
    bool RuntimeRegistered,
    int ModuleCount);

public sealed record RuntimeProcessDiagnostics(
    string TransportKind,
    string PoolKey,
    string State,
    int ProcessId,
    string Endpoint,
    int StartCount,
    int RestartLimit,
    string RestartPolicy,
    string PolicyReason,
    DateTimeOffset? LastStartedAt,
    IReadOnlyList<string> ModuleIds,
    DateTimeOffset? PolicyExpiresAt = null);

public sealed record RuntimeProcessRestartResult(
    bool Success,
    string TransportKind,
    string PoolKey,
    string State,
    string Message,
    IReadOnlyList<string> ModuleIds);

public sealed record RuntimeProcessPolicyResult(
    bool Success,
    string TransportKind,
    string PoolKey,
    string State,
    string RestartPolicy,
    string Message,
    IReadOnlyList<string> ModuleIds,
    DateTimeOffset? ExpiresAt = null);

public sealed record CommandCancellationResult(
    bool Accepted,
    string InvocationId,
    string State,
    string Message);

public sealed record RuntimeProcessPolicySnapshot(
    ulong Revision,
    IReadOnlyList<RuntimeProcessPolicyRecord> Policies,
    IReadOnlyList<RuntimeProcessPolicyHistoryEntry> History,
    DateTimeOffset UpdatedAt);

public sealed record RuntimeProcessPolicyRecord(
    string TransportKind,
    string PoolKey,
    string RestartPolicy,
    string Reason,
    string Source,
    IReadOnlyList<string> ModuleIds,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record RuntimeProcessPolicyHistoryEntry(
    ulong Revision,
    DateTimeOffset Time,
    string TransportKind,
    string PoolKey,
    string RestartPolicy,
    string Reason,
    string Source,
    IReadOnlyList<string> ModuleIds,
    DateTimeOffset? ExpiresAt = null);

public sealed record RuntimeModuleDiagnostics(
    string ModuleId,
    string PackageId,
    string DisplayName,
    string State,
    string Summary,
    bool Enabled,
    string TransportKind,
    DateTimeOffset UpdatedAt,
    int DiagnosticCount,
    int ObservationCount,
    int ConsecutiveFailureCount,
    string SupervisorState,
    string SupervisorAction,
    DateTimeOffset LastObservedAt);

public sealed record RuntimeModuleSupervisionSnapshot(
    string ModuleId,
    string LastState,
    string LastSummary,
    int ObservationCount,
    int ConsecutiveFailureCount,
    string SupervisorState,
    string NextAction,
    DateTimeOffset LastObservedAt);
