using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalLagCleaner.MyPowerTools;

public enum LagSeverity
{
    Info,
    Warning,
    Critical
}

public enum DiagnosticDomain
{
    CpuScheduling,
    Memory,
    Storage,
    Graphics,
    KernelDrivers,
    Responsiveness,
    BackgroundProcesses,
    SystemReliability
}

public enum FindingConfidence
{
    Low,
    Medium,
    High
}

public enum MeasurementStatus
{
    Available,
    Partial,
    Unavailable
}

public enum RemediationRisk
{
    ReadOnly,
    Low,
    Moderate,
    High,
    RestartRequired
}

public enum CleanupAction
{
    McpResidue,
    WeFlow,
    DeliveryOptimization,
    NvidiaContainer,
    RemoteDesktop,
    WindowsSearch
}

public enum McpCleanupEvidenceKind
{
    None,
    OrphanedParent,
    SupersededByNewerSameParent
}

public sealed record LagCleanerOptions
{
    public int SampleSeconds { get; init; } = 8;
    public int SampleIntervalMilliseconds { get; init; } = 1_000;
    public int StaleMcpMinutes { get; init; } = 30;
    public int PreserveNewestMcpGroups { get; init; } = 2;
    public double IdleMcpCorePercentThreshold { get; init; } = 2;
    public double WeFlowCorePercentThreshold { get; init; } = 25;
    public double CpuSustainedWarningPercent { get; init; } = 70;
    public double CpuSustainedCriticalPercent { get; init; } = 90;
    public double DpcInterruptWarningPercent { get; init; } = 8;
    public double DiskLatencyWarningMilliseconds { get; init; } = 35;
    public double DiskLatencyCriticalMilliseconds { get; init; } = 100;
    public double HardPagingWarningPagesPerSecond { get; init; } = 100;
    public double SystemDriveFreeWarningPercent { get; init; } = 10;
    public ulong PagedPoolWarningBytes { get; init; } = 2UL * 1024 * 1024 * 1024;
    public ulong NonPagedPoolWarningBytes { get; init; } = 2UL * 1024 * 1024 * 1024;
    public ulong KernelPoolCriticalBytes { get; init; } = 8UL * 1024 * 1024 * 1024;
    public uint SystemHandleWarningCount { get; init; } = 500_000;
    public uint SystemHandleCriticalCount { get; init; } = 1_000_000;
    public uint ProcessWarningCount { get; init; } = 400;
    public uint ProcessCriticalCount { get; init; } = 600;

    public LagCleanerOptions Normalize()
    {
        var cpuWarning = Math.Clamp(CpuSustainedWarningPercent, 1, 100);
        var diskWarning = Math.Clamp(DiskLatencyWarningMilliseconds, 1, 5_000);
        var pagedPoolWarning = Math.Clamp(
            PagedPoolWarningBytes,
            128UL * 1024 * 1024,
            64UL * 1024 * 1024 * 1024);
        var nonPagedPoolWarning = Math.Clamp(
            NonPagedPoolWarningBytes,
            128UL * 1024 * 1024,
            64UL * 1024 * 1024 * 1024);
        var minimumCriticalPool = Math.Max(pagedPoolWarning, nonPagedPoolWarning);
        var handleWarning = Math.Clamp(SystemHandleWarningCount, 10_000U, 10_000_000U);
        var processWarning = Math.Clamp(ProcessWarningCount, 50U, 10_000U);
        return this with
        {
            SampleSeconds = Math.Clamp(SampleSeconds, 3, 60),
            SampleIntervalMilliseconds = Math.Clamp(SampleIntervalMilliseconds, 1_000, 5_000),
            StaleMcpMinutes = Math.Clamp(StaleMcpMinutes, 5, 24 * 60),
            PreserveNewestMcpGroups = Math.Clamp(PreserveNewestMcpGroups, 1, 10),
            IdleMcpCorePercentThreshold = Math.Clamp(IdleMcpCorePercentThreshold, 0, 100),
            WeFlowCorePercentThreshold = Math.Clamp(WeFlowCorePercentThreshold, 1, 800),
            CpuSustainedWarningPercent = cpuWarning,
            CpuSustainedCriticalPercent = Math.Clamp(
                CpuSustainedCriticalPercent,
                cpuWarning,
                100),
            DpcInterruptWarningPercent = Math.Clamp(DpcInterruptWarningPercent, 0.1, 100),
            DiskLatencyWarningMilliseconds = diskWarning,
            DiskLatencyCriticalMilliseconds = Math.Clamp(
                DiskLatencyCriticalMilliseconds,
                diskWarning,
                10_000),
            HardPagingWarningPagesPerSecond = Math.Clamp(HardPagingWarningPagesPerSecond, 1, 1_000_000),
            SystemDriveFreeWarningPercent = Math.Clamp(SystemDriveFreeWarningPercent, 1, 50),
            PagedPoolWarningBytes = pagedPoolWarning,
            NonPagedPoolWarningBytes = nonPagedPoolWarning,
            KernelPoolCriticalBytes = Math.Clamp(
                KernelPoolCriticalBytes,
                minimumCriticalPool,
                128UL * 1024 * 1024 * 1024),
            SystemHandleWarningCount = handleWarning,
            SystemHandleCriticalCount = Math.Clamp(
                SystemHandleCriticalCount,
                handleWarning,
                20_000_000U),
            ProcessWarningCount = processWarning,
            ProcessCriticalCount = Math.Clamp(
                ProcessCriticalCount,
                processWarning,
                20_000U)
        };
    }
}

public sealed record ProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string Name,
    DateTimeOffset? StartTimeUtc,
    double AgeMinutes,
    double CpuPercentMachine,
    double CpuPercentOneCore,
    ulong PrivateBytes,
    ulong WorkingSetBytes,
    int HandleCount,
    int ThreadCount,
    string KnownRole,
    bool IsMcpRelated)
{
    public double ReadBytesPerSecond { get; init; }
    public double WriteBytesPerSecond { get; init; }
    public double OtherBytesPerSecond { get; init; }
    public long PrivateBytesDelta { get; init; }
    public int HandleCountDelta { get; init; }
    public int ThreadCountDelta { get; init; }
    public bool MetricsComplete { get; init; } = true;
}

public sealed record ProcessBreakdownSnapshot(
    string Name,
    string KnownRole,
    int ProcessCount,
    int ThreadCount,
    ulong PrivateBytes,
    ulong WorkingSetBytes,
    int HandleCount,
    double CpuPercentMachine,
    IReadOnlyList<int> SampleProcessIds);

public sealed record McpProcessMember(
    int ProcessId,
    int ParentProcessId,
    string Name,
    DateTimeOffset? StartTimeUtc,
    bool IsDirectMatch);

public sealed record McpSessionIdentity(
    int RootProcessId,
    int ParentProcessId,
    string RootProcessName,
    DateTimeOffset? RootStartTimeUtc)
{
    public int MarkerProcessId { get; init; }
    public int MarkerParentProcessId { get; init; }
    public string MarkerProcessName { get; init; } = "";
    public DateTimeOffset? MarkerStartTimeUtc { get; init; }
}

public sealed record McpCleanupGroupEvidence(
    int RootProcessId,
    int ParentProcessId,
    McpCleanupEvidenceKind Kind,
    FindingConfidence Confidence,
    IReadOnlyList<McpSessionIdentity> SupersedingSessions)
{
    public string ParentProcessName { get; init; } = "";
    public long ParentStartTimeUtcTicks { get; init; }
    public int MinimumAgeMinutes { get; init; } = 30;
    public double MaximumCpuPercentOneCore { get; init; } = 2;
}

public sealed record McpProcessGroup(
    int RootProcessId,
    string RootProcessName,
    DateTimeOffset? RootStartTimeUtc,
    IReadOnlyList<int> ProcessIds,
    DateTimeOffset? NewestStartTimeUtc,
    double YoungestAgeMinutes,
    double CpuPercentOneCore,
    ulong PrivateBytes,
    int HandleCount,
    bool IsCleanupCandidate,
    string Reason)
{
    public int ParentProcessId { get; init; }
    public string ParentProcessName { get; init; } = "";
    public DateTimeOffset? ParentStartTimeUtc { get; init; }
    public IReadOnlyList<McpProcessMember> Members { get; init; } = [];
    public McpCleanupEvidenceKind CleanupEvidence { get; init; }
    public FindingConfidence EvidenceConfidence { get; init; } = FindingConfidence.Low;
    public IReadOnlyList<McpSessionIdentity> SupersedingSessions { get; init; } = [];
    public int MinimumCleanupAgeMinutes { get; init; } = 30;
}

public sealed record LagFinding(
    LagSeverity Severity,
    string Code,
    string Title,
    string Evidence,
    string Recommendation,
    bool CanClean,
    string CleanupAction)
{
    public DiagnosticDomain Domain { get; init; } = DiagnosticDomain.SystemReliability;
    public FindingConfidence Confidence { get; init; } = FindingConfidence.Medium;
    public int Score { get; init; } = 10;
    public RemediationRisk RemediationRisk { get; init; } = RemediationRisk.ReadOnly;
    public string CausalChain { get; init; } = "";
}

public sealed record SystemSignalSample(
    DateTimeOffset CapturedAtUtc,
    double TotalCpuPercent,
    double ProcessorQueueLength,
    double ContextSwitchesPerSecond,
    double DpcPercent,
    double InterruptPercent,
    double PagesInputPerSecond,
    double PageReadsPerSecond,
    double DiskBusyPercent,
    double DiskLatencyMilliseconds,
    double DiskQueueLength,
    double DiskBytesPerSecond);

public sealed record SystemSignalSummary(
    double AverageCpuPercent,
    double PeakCpuPercent,
    double P95CpuPercent,
    double AverageProcessorQueueLength,
    double PeakProcessorQueueLength,
    double AverageDpcInterruptPercent,
    double PeakDpcInterruptPercent,
    double AveragePagesInputPerSecond,
    double PeakPagesInputPerSecond,
    double AverageDiskBusyPercent,
    double PeakDiskBusyPercent,
    double AverageDiskLatencyMilliseconds,
    double PeakDiskLatencyMilliseconds,
    double PeakDiskQueueLength,
    double AverageDiskBytesPerSecond);

public sealed record DriveSnapshot(
    string Name,
    string DriveType,
    ulong TotalBytes,
    ulong FreeBytes,
    double FreePercent,
    bool IsSystemDrive);

public sealed record GpuSnapshot(
    MeasurementStatus Status,
    string Adapter,
    double UtilizationPercent,
    ulong DedicatedMemoryUsedBytes,
    ulong DedicatedMemoryTotalBytes,
    double TemperatureCelsius,
    string Message);

public sealed record StabilityEventSummary(
    int HardwareErrorCount,
    int StorageErrorCount,
    int DisplayResetCount,
    int ResourceExhaustionCount,
    int ApplicationHangCount,
    IReadOnlyList<string> Providers,
    string Window);

public sealed record SystemContextSnapshot(
    double UserIdleSeconds,
    bool IsUserIdle,
    string ActivePowerPlan,
    string PowerSource,
    int StartupEntryCount,
    bool PendingReboot,
    string OsDescription);

public sealed record ProbeCoverage(
    string Probe,
    MeasurementStatus Status,
    string Message);

public sealed record HandleTypeSnapshot(
    ushort ObjectTypeIndex,
    string TypeName,
    ulong SystemHandleCount,
    ulong AllProcessHandleCount,
    double SystemSharePercent);

public sealed record FileHandleAccessSnapshot(
    uint GrantedAccessMask,
    string Rights,
    ulong HandleCount,
    double SharePercent);

public sealed record FileHandlePathGroupSnapshot(
    string PathGroup,
    string FileKind,
    int SampleCount,
    double SampleSharePercent,
    IReadOnlyList<string> Examples);

public sealed record FileSystemFilterSnapshot(
    string ServiceName,
    string DisplayName,
    string LoadOrderGroup,
    string Altitudes,
    string DriverPath,
    string Company,
    string Version,
    bool Running,
    bool IsMicrosoft,
    string Likelihood,
    string Evidence);

public sealed record FileSystemFilterInstanceSnapshot(
    string FilterName,
    string VolumeName,
    string Altitude,
    string InstanceName,
    string Frame,
    string VolumeStatus);

public sealed record SystemFileHandleAttribution(
    ulong TotalFileHandles,
    int RequestedSamples,
    int AttemptedSamples,
    int DuplicatedSamples,
    int ResolvedPathSamples,
    bool RequiresAdministrator,
    string Summary)
{
    public bool RequiresKernelDriver { get; init; }
    public int NativeErrorCode { get; init; }
}

public sealed record DiagnosticTrend(
    bool Available,
    double BaselineAgeMinutes,
    long SystemHandleDelta,
    long KernelPoolDeltaBytes,
    long CommitDeltaBytes,
    int ProcessCountDelta,
    string Summary);

public sealed record DomainHealthSummary(
    DiagnosticDomain Domain,
    LagSeverity Severity,
    int FindingCount,
    int Score,
    string Summary);

public sealed record LagDiagnosticSnapshot(
    DateTimeOffset CapturedAtUtc,
    double SampleSeconds,
    int LogicalProcessorCount,
    double TotalCpuPercent,
    ulong PhysicalTotalBytes,
    ulong PhysicalUsedBytes,
    double PhysicalUsedPercent,
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    double CommitUsedPercent,
    ulong MemoryCompressionBytes,
    ulong KernelPagedBytes,
    ulong KernelNonPagedBytes,
    ulong KernelTotalBytes,
    uint SystemHandleCount,
    uint TotalHandleCount,
    uint ProcessCount,
    uint ThreadCount,
    double UptimeDays,
    IReadOnlyList<ProcessSnapshot> TopCpuProcesses,
    IReadOnlyList<ProcessSnapshot> TopMemoryProcesses,
    IReadOnlyList<ProcessSnapshot> TopHandleProcesses,
    IReadOnlyList<ProcessSnapshot> TopThreadProcesses,
    IReadOnlyList<ProcessSnapshot> WeFlowProcesses,
    IReadOnlyList<McpProcessGroup> McpGroups,
    IReadOnlyList<LagFinding> Findings,
    IReadOnlyList<string> Recommendations)
{
    public IReadOnlyList<SystemSignalSample> SignalSamples { get; init; } = [];
    public SystemSignalSummary? Signals { get; init; }
    public IReadOnlyList<ProcessBreakdownSnapshot> ProcessBreakdown { get; init; } = [];
    public IReadOnlyList<ProcessSnapshot> TopIoProcesses { get; init; } = [];
    public IReadOnlyList<DriveSnapshot> Drives { get; init; } = [];
    public GpuSnapshot? Gpu { get; init; }
    public StabilityEventSummary? StabilityEvents { get; init; }
    public SystemContextSnapshot? SystemContext { get; init; }
    public DiagnosticTrend? Trend { get; init; }
    public IReadOnlyList<ProbeCoverage> Coverage { get; init; } = [];
    public IReadOnlyList<PoolTagSnapshot> PoolTags { get; init; } = [];
    public IReadOnlyList<HandleTypeSnapshot> SystemHandleTypes { get; init; } = [];
    public IReadOnlyList<FileHandleAccessSnapshot> SystemFileHandleAccess { get; init; } = [];
    public SystemFileHandleAttribution? SystemFileAttribution { get; init; }
    public IReadOnlyList<FileHandlePathGroupSnapshot> SystemFilePathGroups { get; init; } = [];
    public IReadOnlyList<FileSystemFilterSnapshot> FileSystemFilters { get; init; } = [];
    public IReadOnlyList<FileSystemFilterInstanceSnapshot> FileSystemFilterInstances { get; init; } = [];
    public IReadOnlyList<WindowResponsivenessSnapshot> WindowResponsiveness { get; init; } = [];

    [JsonIgnore]
    public LagSeverity OverallSeverity =>
        Findings.Any(item => item.Severity == LagSeverity.Critical)
            ? LagSeverity.Critical
            : Findings.Any(item => item.Severity == LagSeverity.Warning)
                ? LagSeverity.Warning
                : LagSeverity.Info;

    [JsonIgnore]
    public int McpCleanupCandidateCount =>
        McpGroups.Where(group => group.IsCleanupCandidate).Sum(group => group.ProcessIds.Count);

    [JsonIgnore]
    public int HealthScore => Math.Clamp(
        100 - Enum.GetValues<DiagnosticDomain>()
            .Sum(domain => CalculateDomainPenalty(domain, Findings)),
        0,
        100);

    [JsonIgnore]
    public IReadOnlyList<DomainHealthSummary> DomainHealth =>
        Enum.GetValues<DiagnosticDomain>()
            .Select(domain =>
            {
                var domainFindings = Findings.Where(item => item.Domain == domain).ToArray();
                var severity = domainFindings.Any(item => item.Severity == LagSeverity.Critical)
                    ? LagSeverity.Critical
                    : domainFindings.Any(item => item.Severity == LagSeverity.Warning)
                        ? LagSeverity.Warning
                        : LagSeverity.Info;
                var score = CalculateDomainPenalty(domain, Findings);
                var summary = domainFindings.Length == 0
                    ? "未发现持续异常"
                    : string.Join("；", domainFindings.Take(2).Select(item => item.Title));
                return new DomainHealthSummary(domain, severity, domainFindings.Length, score, summary);
            })
            .ToArray();

    private static int CalculateDomainPenalty(
        DiagnosticDomain domain,
        IReadOnlyList<LagFinding> findings)
    {
        var domainFindings = findings.Where(item => item.Domain == domain).ToArray();
        if (domainFindings.Length == 0)
        {
            return 0;
        }

        var correlated = findings
            .Where(IsLeakCascadeFinding)
            .OrderByDescending(item => Math.Max(0, item.Score))
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var primaryCorrelated = correlated.FirstOrDefault();
        var raw = domainFindings.Sum(item =>
        {
            var score = Math.Max(0, item.Score);
            if (!IsLeakCascadeFinding(item) || ReferenceEquals(item, primaryCorrelated))
            {
                return score;
            }

            return (int)Math.Ceiling(score * 0.25);
        });
        return Math.Min(raw, DomainPenaltyCap(domain));
    }

    private static bool IsLeakCascadeFinding(LagFinding finding)
    {
        return finding.Code.StartsWith("kernel-pool", StringComparison.Ordinal) ||
               finding.Code.StartsWith("system-handles", StringComparison.Ordinal) ||
               finding.Code.StartsWith("process-count", StringComparison.Ordinal) ||
               string.Equals(finding.Code, "mcp-residue", StringComparison.Ordinal);
    }

    private static int DomainPenaltyCap(DiagnosticDomain domain)
    {
        return domain switch
        {
            DiagnosticDomain.CpuScheduling => 20,
            DiagnosticDomain.Memory => 20,
            DiagnosticDomain.Storage => 15,
            DiagnosticDomain.Graphics => 10,
            DiagnosticDomain.KernelDrivers => 25,
            DiagnosticDomain.Responsiveness => 15,
            DiagnosticDomain.BackgroundProcesses => 20,
            _ => 15
        };
    }
}

public sealed record CleanupTarget(
    int ProcessId,
    long StartTimeUtcTicks,
    string ProcessName,
    bool KillProcessTree)
{
    public int ParentProcessId { get; init; }
    public int McpGroupRootProcessId { get; init; }
    public bool IsMcpDirectMatch { get; init; }
}

public sealed record CleanupPlan(
    string PlanId,
    string ConfirmationToken,
    CleanupAction Action,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<CleanupTarget> Targets,
    string Scope,
    string Impact,
    bool RequiresAdministrator,
    bool MayDisconnectSession)
{
    public RemediationRisk Risk { get; init; } = RemediationRisk.High;
    public bool IsReversible { get; init; }
    public IReadOnlyList<string> Preconditions { get; init; } = [];
    public string VerificationPlan { get; init; } = "";
    public string RecoveryPlan { get; init; } = "";
    public IReadOnlyList<McpCleanupGroupEvidence> McpEvidence { get; init; } = [];
}

public sealed record CleanupItemResult(
    string Target,
    bool Succeeded,
    string Message);

public sealed record CleanupExecutionResult(
    string PlanId,
    CleanupAction Action,
    bool Succeeded,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<CleanupItemResult> Items)
{
    public bool VerificationPassed { get; init; }
    public string VerificationSummary { get; init; } = "";
    public string RecoverySummary { get; init; } = "";
}

public static class LagCleanerJson
{
    public static JsonSerializerOptions Indented { get; } = Create(true);
    public static JsonSerializerOptions Compact { get; } = Create(false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        return options;
    }
}
