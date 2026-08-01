using System.Diagnostics;
using System.Runtime.Versioning;

namespace LocalLagCleaner.MyPowerTools;

public sealed class LagDiagnosticsEngine
{
    public async Task<LagDiagnosticSnapshot> ScanAsync(
        LagCleanerOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("本机卡顿专清目前支持 Windows。");
        }

        options = options.Normalize();
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        var processTable = WindowsNative.ReadProcessTable();
        var probe = new SystemHealthProbe();
        var timer = Stopwatch.StartNew();
        var cpuStart = WindowsNative.ReadCpuTimes();
        var processStart = CaptureProcessCounters(processTable);
        var interval = TimeSpan.FromMilliseconds(options.SampleIntervalMilliseconds);
        var healthSamples = Math.Max(
            1,
            (int)Math.Ceiling(TimeSpan.FromSeconds(options.SampleSeconds).TotalMilliseconds /
                              interval.TotalMilliseconds));
        var performanceWindow = await probe.CollectPerformanceWindowAsync(
            healthSamples,
            interval,
            cancellationToken).ConfigureAwait(false);
        var cpuEnd = WindowsNative.ReadCpuTimes();
        timer.Stop();

        var capturedAt = DateTimeOffset.UtcNow;
        var performance = WindowsNative.ReadPerformance();
        var processEndTable = WindowsNative.ReadProcessTable();
        var processEndRoles = WindowsNative.ReadKnownServiceProcessRoles();
        var processEnd = CaptureProcesses(processEndTable, processEndRoles);
        var elapsedSeconds = Math.Max(0.1, timer.Elapsed.TotalSeconds);
        var health = await probe.CollectSupplementalAsync(
            performanceWindow,
            cancellationToken).ConfigureAwait(false);
        var analyzed = processEnd
            .Where(item => item.ProcessId != Environment.ProcessId)
            .Select(item => AnalyzeProcess(
                item,
                processStart.GetValueOrDefault(item.ProcessId),
                elapsedSeconds,
                logicalProcessors,
                capturedAt))
            .ToArray();
        var analysisInputs = analyzed.Select(item => new ProcessAnalysisInput(
            item.Public.ProcessId,
            item.Public.ParentProcessId,
            item.Public.Name,
            item.Public.StartTimeUtc,
            item.Public.CpuPercentOneCore,
            item.Public.PrivateBytes,
            item.Public.HandleCount,
            item.ExecutablePath,
            item.CommandLine)).ToArray();
        var mcpGroups = McpResidueAnalyzer.Analyze(analysisInputs, options, capturedAt);
        var mcpIds = mcpGroups.SelectMany(group => group.ProcessIds).ToHashSet();
        var publicProcesses = analyzed
            .Select(item => item.Public with { IsMcpRelated = mcpIds.Contains(item.Public.ProcessId) })
            .ToArray();

        var physicalUsed = performance.PhysicalTotalBytes - performance.PhysicalAvailableBytes;
        var physicalPercent = Percent(physicalUsed, performance.PhysicalTotalBytes);
        var commitPercent = Percent(performance.CommitTotalBytes, performance.CommitLimitBytes);
        var systemHandles = (uint)Math.Max(
            0,
            publicProcesses.FirstOrDefault(item => item.ProcessId == 4)?.HandleCount ?? 0);
        health = CollectSystemHandleTypes(
            health,
            systemHandles,
            options,
            cancellationToken);
        var memoryCompression = publicProcesses
            .Where(item => string.Equals(item.Name, "Memory Compression", StringComparison.OrdinalIgnoreCase))
            .Aggregate(0UL, (total, item) => total + item.WorkingSetBytes);
        var weFlow = publicProcesses
            .Where(item => string.Equals(item.Name, "WeFlow", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CpuPercentOneCore)
            .ToArray();
        var signals = BuildSignalSummary(health);
        var drives = BuildDriveSnapshots(health);
        var gpu = BuildGpuSnapshot(health);
        var stability = BuildStabilitySummary(health);
        var systemContext = BuildSystemContext(health);
        var coverage = BuildCoverage(health);
        var findings = BuildFindings(
            options,
            performance,
            publicProcesses,
            weFlow,
            mcpGroups,
            physicalPercent,
            commitPercent,
            memoryCompression,
            systemHandles,
            systemContext?.IsUserIdle).ToList();
        findings.RemoveAll(item => item.Code == "baseline-healthy");
        findings.AddRange(BuildSystemHealthFindings(
            options,
            signals,
            drives,
            gpu,
            stability,
            systemContext,
            publicProcesses,
            logicalProcessors,
            coverage));
        findings.AddRange(BuildSupplementalProbeFindings(health, performance));
        var enrichedFindings = findings
            .Select(EnrichFinding)
            .OrderByDescending(item => item.Severity)
            .ThenByDescending(item => item.Score)
            .ToArray();
        if (enrichedFindings.Length == 0 && CriticalCoverageComplete(health, signals, systemContext))
        {
            enrichedFindings =
            [
                EnrichFinding(new LagFinding(
                    LagSeverity.Info,
                    "baseline-healthy",
                    "当前多阶段采样未发现高风险项",
                    "CPU 调度、内存、分页、磁盘、GPU、进程、句柄、内核池与稳定性事件均未越过默认阈值。",
                    "保留本次报告作为静置基线，后续扫描可用于增长趋势对比。",
                    false,
                    ""))
            ];
        }
        else if (!CriticalCoverageComplete(health, signals, systemContext) &&
                 !enrichedFindings.Any(item => item.Code == "diagnostic-coverage-incomplete"))
        {
            enrichedFindings =
            [
                .. enrichedFindings,
                EnrichFinding(BuildCoverageIncompleteFinding(health, signals, systemContext))
            ];
        }
        var recommendations = BuildRecommendations(enrichedFindings, performance, systemHandles);
        var totalCpu = signals?.AverageCpuPercent ?? CalculateTotalCpu(cpuStart, cpuEnd);
        var signalSamples = signals is null
            ? Array.Empty<SystemSignalSample>()
            : health.Performance.Samples
                .Where(HasCompletePublishedSignalSample)
                .Select(sample => new SystemSignalSample(
                    sample.CapturedAtUtc,
                    sample.Metrics["cpu.total"],
                    sample.Metrics["cpu.processor-queue"],
                    sample.Metrics["cpu.context-switches"],
                    sample.Metrics["cpu.dpc-time"],
                    sample.Metrics["cpu.interrupt-time"],
                    sample.Metrics["memory.pages-input"],
                    sample.Metrics["memory.page-reads"],
                    sample.Metrics["disk.busy"],
                    sample.Metrics["disk.transfer-latency"],
                    sample.Metrics["disk.queue"],
                    sample.Metrics["disk.throughput"]))
                .ToArray();

        return new LagDiagnosticSnapshot(
            capturedAt,
            elapsedSeconds,
            logicalProcessors,
            totalCpu,
            performance.PhysicalTotalBytes,
            physicalUsed,
            physicalPercent,
            performance.CommitTotalBytes,
            performance.CommitLimitBytes,
            commitPercent,
            memoryCompression,
            performance.KernelPagedBytes,
            performance.KernelNonPagedBytes,
            performance.KernelTotalBytes,
            systemHandles,
            performance.HandleCount,
            performance.ProcessCount,
            performance.ThreadCount,
            WindowsNative.ReadUptimeDays(),
            publicProcesses.OrderByDescending(item => item.CpuPercentMachine).Take(12).ToArray(),
            publicProcesses.OrderByDescending(item => item.PrivateBytes).Take(12).ToArray(),
            publicProcesses.OrderByDescending(item => item.HandleCount).Take(12).ToArray(),
            publicProcesses.OrderByDescending(item => item.ThreadCount).Take(12).ToArray(),
            weFlow,
            mcpGroups,
            enrichedFindings,
            recommendations)
        {
            SignalSamples = signalSamples,
            Signals = signals,
            TopIoProcesses = publicProcesses
                .OrderByDescending(item =>
                    item.ReadBytesPerSecond +
                    item.WriteBytesPerSecond)
                .Take(12)
                .ToArray(),
            Drives = drives,
            Gpu = gpu,
            StabilityEvents = stability,
            SystemContext = systemContext,
            Coverage = coverage,
            PoolTags = health.PoolTags,
            SystemHandleTypes = health.SystemHandleTypes,
            SystemFileHandleAccess = health.SystemFileHandleAccess,
            SystemFileAttribution = health.SystemFileAttribution,
            SystemFilePathGroups = health.SystemFilePathGroups,
            FileSystemFilters = health.FileSystemFilters,
            WindowResponsiveness = health.WindowResponsiveness
        };
    }

    [SupportedOSPlatform("windows")]
    private static SystemHealthProbeSnapshot CollectSystemHandleTypes(
        SystemHealthProbeSnapshot health,
        uint systemHandleCount,
        LagCleanerOptions options,
        CancellationToken cancellationToken)
    {
        var triggerCount = Math.Min(
            options.SystemHandleWarningCount,
            100_000U);
        if (systemHandleCount < triggerCount)
        {
            return health with
            {
                Coverage =
                [
                    .. health.Coverage,
                    new SystemHealthCoverage(
                        "system-handle-types",
                        SystemHealthCoverageState.Complete,
                        $"PID 4 has {systemHandleCount:n0} handles, below the {triggerCount:n0} high-cost enumeration trigger.")
                ]
            };
        }

        try
        {
            var result = SystemHandleTypeProbe.Read(cancellationToken);
            var difference = Math.Abs(
                (long)result.EnumeratedSystemHandles -
                systemHandleCount);
            var tolerance = Math.Max(
                5_000d,
                systemHandleCount * 0.10);
            var complete =
                result.UnmappedSystemHandles == 0 &&
                difference <= tolerance;
            var mapping = result.UnmappedSystemHandles == 0
                ? "all PID 4 handles mapped to named object types"
                : $"{result.UnmappedSystemHandles:n0} PID 4 handles retained a numeric type index";
            var collected = health with
            {
                SystemHandleTypes = result.Rows,
                SystemFileHandleAccess = result.FileAccessPatterns,
                Coverage =
                [
                    .. health.Coverage,
                    new SystemHealthCoverage(
                        "system-handle-types",
                        complete
                            ? SystemHealthCoverageState.Complete
                            : SystemHealthCoverageState.Partial,
                        $"Enumerated {result.EnumeratedSystemHandles:n0} PID 4 handles and {result.EnumeratedAllHandles:n0} handles system-wide; {result.Rows.Count} PID 4 object types, {result.MappedTypeCount} type names, {mapping}; native buffer {FormatBytes((ulong)result.BufferBytes)}.")
                ]
            };

            var fileHandleCount = result.FileAccessPatterns
                .Aggregate(0UL, (sum, item) => sum + item.HandleCount);
            try
            {
                var paths = SystemFileHandlePathProbe.Read(
                    fileHandleCount,
                    result.FileHandleSamples,
                    cancellationToken);
                var pathState = paths.Attribution.RequiresAdministrator ||
                                paths.Attribution.ResolvedPathSamples <
                                Math.Max(
                                    1,
                                    paths.Attribution.RequestedSamples / 2)
                    ? SystemHealthCoverageState.Partial
                    : SystemHealthCoverageState.Complete;
                collected = collected with
                {
                    SystemFileAttribution = paths.Attribution,
                    SystemFilePathGroups = paths.PathGroups,
                    Coverage =
                    [
                        .. collected.Coverage,
                        new SystemHealthCoverage(
                            "system-file-handle-paths",
                            pathState,
                            paths.Attribution.Summary)
                    ]
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                collected = collected with
                {
                    Coverage =
                    [
                        .. collected.Coverage,
                        new SystemHealthCoverage(
                            "system-file-handle-paths",
                            SystemHealthCoverageState.Failed,
                            $"{exception.GetType().Name}: {exception.Message}")
                    ]
                };
            }

            try
            {
                var filters = FileSystemFilterProbe.Read(health.PoolTags);
                collected = collected with
                {
                    FileSystemFilters = filters,
                    Coverage =
                    [
                        .. collected.Coverage,
                        new SystemHealthCoverage(
                            "file-system-filters",
                            filters.Count > 0
                                ? SystemHealthCoverageState.Complete
                                : SystemHealthCoverageState.Unavailable,
                            $"{filters.Count} registered file-system filter driver(s) inventoried; {filters.Count(item => item.Running)} report a running driver service. Registry, binary metadata, service state, altitude, and Pool Tag correlation were collected; live volume attachments require an administrator token.")
                    ]
                };
            }
            catch (Exception exception)
            {
                collected = collected with
                {
                    Coverage =
                    [
                        .. collected.Coverage,
                        new SystemHealthCoverage(
                            "file-system-filters",
                            SystemHealthCoverageState.Failed,
                            $"{exception.GetType().Name}: {exception.Message}")
                    ]
                };
            }

            return collected;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return health with
            {
                Coverage =
                [
                    .. health.Coverage,
                    new SystemHealthCoverage(
                        "system-handle-types",
                        SystemHealthCoverageState.Failed,
                        $"{exception.GetType().Name}: {exception.Message}")
                ]
            };
        }
    }

    private static IReadOnlyDictionary<int, ProcessCounterSample> CaptureProcessCounters(
        IReadOnlyDictionary<int, NativeProcessEntry> processTable)
    {
        var result = new Dictionary<int, ProcessCounterSample>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var native = processTable.GetValueOrDefault(processId);
                    var io = WindowsNative.TryReadProcessIo(processId);
                    result[processId] = new ProcessCounterSample(
                        TryReadStartTime(process),
                        TryReadLong(() => process.TotalProcessorTime.Ticks),
                        ToUInt64(TryReadLong(() => process.PrivateMemorySize64)),
                        TryRead(() => process.HandleCount),
                        native?.ThreadCount ?? 0,
                        io);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    // The process exited during enumeration.
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<RawProcessSnapshot> CaptureProcesses(
        IReadOnlyDictionary<int, NativeProcessEntry> processTable,
        IReadOnlyDictionary<int, string> serviceRoles)
    {
        var result = new List<RawProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var processId = process.Id;
                    var name = process.ProcessName;
                    var handleCount = TryRead(() => process.HandleCount);
                    var nativeEntry = processTable.GetValueOrDefault(processId);
                    var inspectIdentity = NeedsIdentity(name);
                    result.Add(new RawProcessSnapshot(
                        processId,
                        nativeEntry?.ParentProcessId ?? 0,
                        name,
                        TryReadStartTime(process),
                        TryReadLong(() => process.TotalProcessorTime.Ticks),
                        ToUInt64(TryReadLong(() => process.PrivateMemorySize64)),
                        ToUInt64(TryReadLong(() => process.WorkingSet64)),
                        handleCount,
                        nativeEntry?.ThreadCount ?? 0,
                        WindowsNative.TryReadProcessIo(processId),
                        serviceRoles.GetValueOrDefault(processId) ?? "",
                        inspectIdentity ? WindowsNative.TryReadImagePath(processId) : "",
                        inspectIdentity ? WindowsNative.TryReadCommandLine(processId) : ""));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    // The process exited or denied access between enumeration and sampling.
                }
            }
        }

        return result;
    }

    private static AnalyzedProcess AnalyzeProcess(
        RawProcessSnapshot current,
        ProcessCounterSample? previous,
        double elapsedSeconds,
        int logicalProcessors,
        DateTimeOffset capturedAtUtc)
    {
        var sameProcess = previous is not null &&
                          SameProcessIdentity(previous.StartTimeUtc, current.StartTimeUtc);
        var previousProcessorTicks = sameProcess ? previous!.TotalProcessorTicks : 0;
        var deltaTicks = previousProcessorTicks == 0 || current.TotalProcessorTicks < previousProcessorTicks
            ? 0
            : current.TotalProcessorTicks - previousProcessorTicks;
        var corePercent = deltaTicks / (double)TimeSpan.TicksPerSecond / elapsedSeconds * 100;
        var machinePercent = corePercent / logicalProcessors;
        var ageMinutes = current.StartTimeUtc.HasValue
            ? Math.Max(0, (capturedAtUtc - current.StartTimeUtc.Value).TotalMinutes)
            : 0;
        var ioAvailable = sameProcess && previous!.Io.Available && current.Io.Available;
        var processSnapshot = new ProcessSnapshot(
                current.ProcessId,
                current.ParentProcessId,
                current.Name,
                current.StartTimeUtc,
                ageMinutes,
                Math.Round(machinePercent, 2),
                Math.Round(corePercent, 2),
                current.PrivateBytes,
                current.WorkingSetBytes,
                current.HandleCount,
                current.ThreadCount,
                current.KnownRole.Length > 0
                    ? current.KnownRole
                    : KnownRole(current.Name),
                false)
            {
                ReadBytesPerSecond = ioAvailable
                    ? PerSecond(current.Io.ReadBytes, previous!.Io.ReadBytes, elapsedSeconds)
                    : 0,
                WriteBytesPerSecond = ioAvailable
                    ? PerSecond(current.Io.WriteBytes, previous!.Io.WriteBytes, elapsedSeconds)
                    : 0,
                OtherBytesPerSecond = ioAvailable
                    ? PerSecond(current.Io.OtherBytes, previous!.Io.OtherBytes, elapsedSeconds)
                    : 0,
                PrivateBytesDelta = !sameProcess
                    ? 0
                    : SaturatingLongDelta(current.PrivateBytes, previous!.PrivateBytes),
                HandleCountDelta = sameProcess ? current.HandleCount - previous!.HandleCount : 0,
                ThreadCountDelta = sameProcess ? current.ThreadCount - previous!.ThreadCount : 0,
                MetricsComplete = sameProcess && ioAvailable
            };
        return new AnalyzedProcess(
            processSnapshot,
            current.ExecutablePath,
            current.CommandLine);
    }

    private static bool SameProcessIdentity(
        DateTimeOffset? previousStartTimeUtc,
        DateTimeOffset? currentStartTimeUtc)
    {
        if (!previousStartTimeUtc.HasValue || !currentStartTimeUtc.HasValue)
        {
            return false;
        }

        return Math.Abs(
            (previousStartTimeUtc.Value - currentStartTimeUtc.Value).TotalMilliseconds) < 1;
    }

    private static SystemHealthMetricSummary? Metric(
        SystemHealthProbeSnapshot health,
        string metricId)
    {
        return health.Performance.Metrics.FirstOrDefault(
            item => string.Equals(item.MetricId, metricId, StringComparison.Ordinal));
    }

    private static bool HasCompletePublishedSignalSample(SystemHealthPdhSample sample)
    {
        string[] requiredMetrics =
        [
            "cpu.total",
            "cpu.processor-queue",
            "cpu.context-switches",
            "cpu.dpc-time",
            "cpu.interrupt-time",
            "memory.pages-input",
            "memory.page-reads",
            "disk.busy",
            "disk.transfer-latency",
            "disk.queue",
            "disk.throughput"
        ];
        return requiredMetrics.All(sample.Metrics.ContainsKey);
    }

    private static SystemSignalSummary? BuildSignalSummary(
        SystemHealthProbeSnapshot health)
    {
        var cpu = Metric(health, "cpu.total");
        var queue = Metric(health, "cpu.processor-queue");
        var dpc = Metric(health, "cpu.dpc-time");
        var interrupts = Metric(health, "cpu.interrupt-time");
        var pagesInput = Metric(health, "memory.pages-input");
        var diskBusy = Metric(health, "disk.busy");
        var diskLatency = Metric(health, "disk.transfer-latency");
        var diskQueue = Metric(health, "disk.queue");
        var diskBytes = Metric(health, "disk.throughput");
        if (cpu is null ||
            queue is null ||
            dpc is null ||
            interrupts is null ||
            pagesInput is null ||
            diskBusy is null ||
            diskLatency is null ||
            diskQueue is null ||
            diskBytes is null)
        {
            return null;
        }

        var dpcInterruptValues = health.Performance.Samples
            .Where(sample =>
                sample.Metrics.ContainsKey("cpu.dpc-time") &&
                sample.Metrics.ContainsKey("cpu.interrupt-time"))
            .Select(sample =>
                sample.Metrics["cpu.dpc-time"] +
                sample.Metrics["cpu.interrupt-time"])
            .ToArray();
        if (dpcInterruptValues.Length == 0)
        {
            return null;
        }

        return new SystemSignalSummary(
            Math.Round(cpu.Average, 2),
            Math.Round(cpu.Maximum, 2),
            Math.Round(cpu.P95, 2),
            queue.Average,
            queue.Maximum,
            Math.Round(dpcInterruptValues.Average(), 3),
            Math.Round(dpcInterruptValues.Max(), 3),
            pagesInput.Average,
            pagesInput.Maximum,
            diskBusy.Average,
            diskBusy.Maximum,
            diskLatency.Average,
            diskLatency.Maximum,
            diskQueue.Maximum,
            diskBytes.Average);
    }

    private static IReadOnlyList<DriveSnapshot> BuildDriveSnapshots(
        SystemHealthProbeSnapshot health)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        return health.Drives
            .Select(item => new DriveSnapshot(
                item.RootPath,
                item.DriveType.ToString(),
                item.TotalBytes <= 0 ? 0UL : (ulong)item.TotalBytes,
                item.AvailableBytes <= 0 ? 0UL : (ulong)item.AvailableBytes,
                item.AvailablePercent,
                string.Equals(
                    Path.GetPathRoot(item.RootPath),
                    systemRoot,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    private static GpuSnapshot BuildGpuSnapshot(SystemHealthProbeSnapshot health)
    {
        if (health.NvidiaGpus.Count == 0)
        {
            var gpuCoverage = health.Coverage.FirstOrDefault(
                item => item.Component == "nvidia-gpu");
            return new GpuSnapshot(
                MapStatus(gpuCoverage?.State),
                "",
                0,
                0,
                0,
                0,
                gpuCoverage?.Detail ?? "未发现可用的 NVIDIA GPU 探针。");
        }

        var usedMiB = health.NvidiaGpus.Sum(item => item.MemoryUsedMiB ?? 0);
        var totalMiB = health.NvidiaGpus.Sum(item => item.MemoryTotalMiB ?? 0);
        return new GpuSnapshot(
            MeasurementStatus.Available,
            string.Join("；", health.NvidiaGpus.Select(item => item.Name).Distinct()),
            health.NvidiaGpus.Max(item => item.GpuUtilizationPercent ?? 0),
            usedMiB <= 0 ? 0UL : (ulong)usedMiB * 1024 * 1024,
            totalMiB <= 0 ? 0UL : (ulong)totalMiB * 1024 * 1024,
            health.NvidiaGpus.Max(item => item.TemperatureCelsius ?? 0),
            $"驱动 {string.Join(", ", health.NvidiaGpus.Select(item => item.DriverVersion).Distinct())}");
    }

    private static StabilityEventSummary BuildStabilitySummary(
        SystemHealthProbeSnapshot health)
    {
        static bool ProviderContains(ReliabilityEventGroup item, params string[] values) =>
            values.Any(value => item.Provider.Contains(value, StringComparison.OrdinalIgnoreCase));

        static bool IsSeriousWhea(ReliabilityEventGroup item) =>
            ProviderContains(item, "WHEA") &&
            item.Level is 1 or 2 &&
            item.EventId is 1 or 18 or 20 or 46;

        static bool IsStorageEvent(ReliabilityEventGroup item) =>
            ProviderContains(item, "disk", "stor", "nvme", "ntfs", "volmgr") &&
            (item.Level is 1 or 2 ||
             item.EventId is 7 or 9 or 11 or 15 or 51 or 55 or 57 or 129 or 140 or 153 or 157);

        static bool IsDisplayEvent(ReliabilityEventGroup item) =>
            ProviderContains(item, "display", "nvlddmkm", "amdkmdag", "igfx") &&
            (item.EventId == 4101 || item.Level is 1 or 2);

        var groups = health.Reliability.Groups
            .Where(item => item.RecentCount > 0)
            .ToArray();
        var relevantGroups = groups
            .Where(item =>
                IsSeriousWhea(item) ||
                IsStorageEvent(item) ||
                IsDisplayEvent(item) ||
                item.EventId == 2004 ||
                ProviderContains(item, "Resource-Exhaustion") ||
                item.EventId == 1002 ||
                ProviderContains(item, "Application Hang"))
            .ToArray();
        return new StabilityEventSummary(
            groups.Where(IsSeriousWhea).Sum(item => item.RecentCount),
            groups.Where(IsStorageEvent).Sum(item => item.RecentCount),
            groups.Where(IsDisplayEvent).Sum(item => item.RecentCount),
            groups.Where(item =>
                    item.EventId == 2004 ||
                    ProviderContains(item, "Resource-Exhaustion"))
                .Sum(item => item.RecentCount),
            groups.Where(item =>
                    item.EventId == 1002 ||
                    ProviderContains(item, "Application Hang"))
                .Sum(item => item.RecentCount),
            relevantGroups.OrderByDescending(item => item.RecentCount)
                .Select(item =>
                    $"{item.Provider}#{item.EventId}/L{item.Level} ×{item.RecentCount}")
                .Take(8)
                .ToArray(),
            $"最近 24 小时关键事件（检索 {health.Reliability.Window.TotalDays:n0} 天）");
    }

    private static SystemContextSnapshot? BuildSystemContext(
        SystemHealthProbeSnapshot health)
    {
        if (health.UserIdle is null ||
            health.Startup is null ||
            health.PendingReboot is null)
        {
            return null;
        }

        var idleSeconds = Math.Min(
            health.UserIdle.StartIdleTime.TotalSeconds,
            health.UserIdle.EndIdleTime.TotalSeconds);
        var powerPlan = health.PowerPlan is null
            ? "未知"
            : string.IsNullOrWhiteSpace(health.PowerPlan.DisplayName)
                ? health.PowerPlan.SchemeGuid
                : health.PowerPlan.DisplayName;
        return new SystemContextSnapshot(
            idleSeconds,
            !health.UserIdle.InputObservedDuringSampling && idleSeconds >= 60,
            powerPlan,
            WindowsNative.ReadPowerSource(),
            health.Startup.TotalItems,
            health.PendingReboot.IsPending,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }

    private static IReadOnlyList<ProbeCoverage> BuildCoverage(
        SystemHealthProbeSnapshot health)
    {
        return health.Coverage
            .Select(item => new ProbeCoverage(
                item.Component,
                MapStatus(item.State),
                item.Detail))
            .ToArray();
    }

    private static MeasurementStatus MapStatus(SystemHealthCoverageState? state)
    {
        return state switch
        {
            SystemHealthCoverageState.Complete => MeasurementStatus.Available,
            SystemHealthCoverageState.Partial => MeasurementStatus.Partial,
            _ => MeasurementStatus.Unavailable
        };
    }

    private static bool CriticalCoverageComplete(
        SystemHealthProbeSnapshot health,
        SystemSignalSummary? signals,
        SystemContextSnapshot? context)
    {
        string[] requiredComponents =
        [
            "pdh",
            "drive-space",
            "user-idle",
            "pending-reboot",
            "startup-items",
            "reliability-events",
            "window-responsiveness"
        ];
        return signals is not null &&
               context is not null &&
               requiredComponents.All(component =>
                   health.Coverage.FirstOrDefault(item => item.Component == component)?.State ==
                   SystemHealthCoverageState.Complete);
    }

    private static LagFinding BuildCoverageIncompleteFinding(
        SystemHealthProbeSnapshot health,
        SystemSignalSummary? signals,
        SystemContextSnapshot? context)
    {
        var missing = new List<string>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pdh"] = "性能计数器",
            ["drive-space"] = "磁盘空间",
            ["user-idle"] = "用户空闲边界",
            ["pending-reboot"] = "待重启状态",
            ["startup-items"] = "启动项",
            ["reliability-events"] = "稳定性事件",
            ["window-responsiveness"] = "窗口响应性"
        };
        foreach (var pair in labels)
        {
            var state = health.Coverage
                .FirstOrDefault(item => item.Component == pair.Key)?
                .State;
            if (state != SystemHealthCoverageState.Complete)
            {
                missing.Add($"{pair.Value}（{CoverageStateText(state)}）");
            }
        }

        if (signals is null && missing.All(item => !item.StartsWith("性能计数器", StringComparison.Ordinal)))
        {
            missing.Add("关键性能指标（未采集完整）");
        }
        if (context is null &&
            missing.All(item =>
                !item.StartsWith("用户空闲边界", StringComparison.Ordinal) &&
                !item.StartsWith("待重启状态", StringComparison.Ordinal) &&
                !item.StartsWith("启动项", StringComparison.Ordinal)))
        {
            missing.Add("系统上下文（未采集完整）");
        }

        return new LagFinding(
            LagSeverity.Warning,
            "diagnostic-coverage-incomplete",
            "关键测量未采集完整",
            $"以下项目缺少完整覆盖：{string.Join("、", missing)}。当前报告不会给出健康基线结论。",
            "重试扫描；持续缺失时检查性能计数器、事件日志权限与系统服务状态。",
            false,
            "")
        {
            Domain = DiagnosticDomain.SystemReliability,
            Confidence = FindingConfidence.High,
            Score = 0,
            CausalChain = "关键探针缺失 → 阈值无法验证 → 健康结论被暂停"
        };
    }

    private static string CoverageStateText(SystemHealthCoverageState? state) => state switch
    {
        SystemHealthCoverageState.Partial => "部分采集",
        SystemHealthCoverageState.Failed => "采集失败",
        _ => "未采集"
    };

    private static IReadOnlyList<LagFinding> BuildSystemHealthFindings(
        LagCleanerOptions options,
        SystemSignalSummary? signals,
        IReadOnlyList<DriveSnapshot> drives,
        GpuSnapshot gpu,
        StabilityEventSummary stability,
        SystemContextSnapshot? context,
        IReadOnlyList<ProcessSnapshot> processes,
        int logicalProcessors,
        IReadOnlyList<ProbeCoverage> coverage)
    {
        var findings = new List<LagFinding>();
        var idleConfidence = context?.IsUserIdle == true
            ? FindingConfidence.High
            : FindingConfidence.Low;

        if (context is { IsUserIdle: false })
        {
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "sample-active-session",
                "采样期间未满足静置条件",
                $"最近输入距采样约 {context.UserIdleSeconds:n0} 秒，前台交互可能抬高 CPU、磁盘和 GPU 指标。",
                "关闭正在操作的窗口，保持至少一分钟无输入后执行深度扫描。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Responsiveness,
                Confidence = FindingConfidence.High,
                Score = 0,
                CausalChain = "用户输入活动 → 前台任务执行 → 当前采样只能作为活动场景证据"
            });
        }

        if (signals is not null &&
            signals.AverageCpuPercent >= options.CpuSustainedWarningPercent)
        {
            var critical = signals.AverageCpuPercent >= options.CpuSustainedCriticalPercent;
            findings.Add(new LagFinding(
                critical ? LagSeverity.Critical : LagSeverity.Warning,
                "cpu-sustained",
                "整机 CPU 持续繁忙",
                $"采样窗口平均 CPU {signals.AverageCpuPercent:n1}%。",
                "按 CPU 前列进程和处理器队列继续定位；驱动时间偏高时优先排查 DPC/中断。",
                false,
                "")
            {
                Domain = DiagnosticDomain.CpuScheduling,
                Confidence = idleConfidence,
                Score = critical ? 24 : 12,
                CausalChain = "持续 CPU 占用 → 可运行线程争抢 → 交互延迟增加"
            });
        }

        if (signals is not null &&
            signals.PeakProcessorQueueLength >= Math.Max(2, logicalProcessors))
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "cpu-run-queue",
                "处理器等待队列出现峰值",
                $"处理器队列峰值 {signals.PeakProcessorQueueLength:n1}，逻辑处理器 {logicalProcessors} 个。",
                "结合 CPU 前列、DPC/中断和电源计划判断计算负载或驱动抢占。",
                false,
                "")
            {
                Domain = DiagnosticDomain.CpuScheduling,
                Confidence = FindingConfidence.High,
                Score = 14,
                CausalChain = "可运行线程数量超过调度容量 → 等待队列增长 → UI 响应变慢"
            });
        }

        if (signals is not null &&
            signals.PeakDpcInterruptPercent >= options.DpcInterruptWarningPercent)
        {
            findings.Add(new LagFinding(
                signals.PeakDpcInterruptPercent >= options.DpcInterruptWarningPercent * 2
                    ? LagSeverity.Critical
                    : LagSeverity.Warning,
                "dpc-interrupt-pressure",
                "驱动 DPC/中断时间偏高",
                $"同一采样间隔内 DPC 与中断时间平均 {signals.AverageDpcInterruptPercent:n1}%、峰值合计 {signals.PeakDpcInterruptPercent:n1}%。",
                "使用 WPR 的 DPC/ISR profile 定位网卡、存储、USB 或显卡驱动；更新前保留当前报告。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 20,
                RemediationRisk = RemediationRisk.RestartRequired,
                CausalChain = "驱动中断/DPC 占用 CPU → 普通线程被推迟 → 鼠标、音频与窗口出现卡顿"
            });
        }

        if (signals is not null &&
            signals.PeakPagesInputPerSecond >= options.HardPagingWarningPagesPerSecond)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "hard-paging",
                "硬分页活动明显",
                $"Pages Input/sec 平均 {signals.AveragePagesInputPerSecond:n1}，峰值 {signals.PeakPagesInputPerSecond:n1}。",
                "结合提交内存、内存前列和磁盘延迟确认换页主因，避免通过强制清空工作集制造更多缺页。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Memory,
                Confidence = FindingConfidence.High,
                Score = 15,
                CausalChain = "工作集页被换出 → 访问触发磁盘读入 → 前台线程等待存储 I/O"
            });
        }

        var diskQueueThreshold = Math.Max(8, drives.Count * 2);
        if (signals is not null &&
            (signals.PeakDiskLatencyMilliseconds >= options.DiskLatencyWarningMilliseconds ||
            (signals.PeakDiskQueueLength >= diskQueueThreshold &&
             signals.AverageDiskBusyPercent >= 75)))
        {
            var critical = signals.PeakDiskLatencyMilliseconds >= options.DiskLatencyCriticalMilliseconds;
            findings.Add(new LagFinding(
                critical ? LagSeverity.Critical : LagSeverity.Warning,
                "disk-latency",
                "存储延迟或队列偏高",
                $"传输延迟平均 {signals.AverageDiskLatencyMilliseconds:n1} ms、峰值 {signals.PeakDiskLatencyMilliseconds:n1} ms，队列峰值 {signals.PeakDiskQueueLength:n1}。",
                "查看进程读写前列并核对最近存储事件；持续超时需检查磁盘健康、控制器和驱动。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Storage,
                Confidence = FindingConfidence.High,
                Score = critical ? 24 : 14,
                CausalChain = "磁盘请求等待 → 应用同步 I/O 阻塞 → 窗口与文件操作停顿"
            });
        }

        foreach (var drive in drives.Where(item =>
                     item.IsSystemDrive &&
                     item.FreePercent < options.SystemDriveFreeWarningPercent))
        {
            findings.Add(new LagFinding(
                drive.FreePercent < 5 ? LagSeverity.Critical : LagSeverity.Warning,
                "system-drive-low-space",
                "系统盘可用空间不足",
                $"{drive.Name} 剩余 {FormatBytes(drive.FreeBytes)}，占 {drive.FreePercent:n1}%。",
                "释放可确认的大文件与应用缓存，保留系统更新、页面文件和崩溃转储所需空间。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Storage,
                Confidence = FindingConfidence.High,
                Score = drive.FreePercent < 5 ? 18 : 10
            });
        }

        if (gpu.Status == MeasurementStatus.Available &&
            context?.IsUserIdle == true &&
            (gpu.UtilizationPercent >= 80 || gpu.TemperatureCelsius >= 85))
        {
            findings.Add(new LagFinding(
                gpu.TemperatureCelsius >= 92 ? LagSeverity.Critical : LagSeverity.Warning,
                "gpu-idle-pressure",
                "静置 GPU 负载或温度偏高",
                $"{gpu.Adapter} 利用率 {gpu.UtilizationPercent:n1}%，温度 {gpu.TemperatureCelsius:n1} °C，显存 {FormatBytes(gpu.DedicatedMemoryUsedBytes)} / {FormatBytes(gpu.DedicatedMemoryTotalBytes)}。",
                "检查占用 GPU 的窗口、录屏、浏览器与 NVIDIA Container；驱动重置记录存在时更新或回滚显卡驱动。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Graphics,
                Confidence = FindingConfidence.Medium,
                Score = 14
            });
        }

        var ioProcesses = processes
            .Where(item =>
                item.ReadBytesPerSecond +
                item.WriteBytesPerSecond >= 50 * 1024 * 1024)
            .OrderByDescending(item =>
                item.ReadBytesPerSecond +
                item.WriteBytesPerSecond)
            .Take(3)
            .ToArray();
        if (ioProcesses.Length > 0)
        {
            var diskSignalCorrelated = signals is not null &&
                                       (signals.PeakDiskLatencyMilliseconds >=
                                            options.DiskLatencyWarningMilliseconds ||
                                        signals.AverageDiskBusyPercent >= 75);
            findings.Add(new LagFinding(
                diskSignalCorrelated ? LagSeverity.Warning : LagSeverity.Info,
                "process-io-pressure",
                "进程读写活动偏高",
                string.Join(
                    "；",
                    ioProcesses.Select(item =>
                        $"{item.Name}({item.ProcessId}) {FormatBytes((ulong)(
                            item.ReadBytesPerSecond +
                            item.WriteBytesPerSecond))}/s")),
                "该计数包含文件系统缓存路径。核对同步、索引、下载或构建任务，并结合磁盘延迟确认物理存储瓶颈。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Storage,
                Confidence = diskSignalCorrelated
                    ? idleConfidence
                    : FindingConfidence.Medium,
                Score = diskSignalCorrelated ? 10 : 2
            });
        }

        var growingProcesses = processes
            .Where(item =>
                item.HandleCountDelta >= 1_000 ||
                item.ThreadCountDelta >= 100 ||
                item.PrivateBytesDelta >= 512L * 1024 * 1024)
            .OrderByDescending(item => item.HandleCountDelta + item.ThreadCountDelta)
            .Take(5)
            .ToArray();
        if (growingProcesses.Length > 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "process-growth",
                "采样窗口内出现快速资源增长",
                string.Join(
                    "；",
                    growingProcesses.Select(item =>
                        $"{item.Name}({item.ProcessId}) 句柄 {item.HandleCountDelta:+#;-#;0}、线程 {item.ThreadCountDelta:+#;-#;0}、私有提交 {FormatSignedBytes(item.PrivateBytesDelta)}")),
                "执行深度扫描确认增长持续性；重复出现时修复对应进程的退出、句柄或线程清理逻辑。",
                false,
                "")
            {
                Domain = DiagnosticDomain.BackgroundProcesses,
                Confidence = FindingConfidence.High,
                Score = 16,
                CausalChain = "对象持续创建且未释放 → 内核/提交资源增长 → 调度与内存压力逐步升高"
            });
        }

        var reliabilityTotal =
            stability.HardwareErrorCount +
            stability.StorageErrorCount +
            stability.DisplayResetCount +
            stability.ResourceExhaustionCount +
            stability.ApplicationHangCount;
        var reliabilityAvailable = coverage
            .FirstOrDefault(item => item.Probe == "reliability-events")?
            .Status is MeasurementStatus.Available or MeasurementStatus.Partial;
        if (reliabilityAvailable && reliabilityTotal > 0)
        {
            var storageSignalCorrelated = signals is not null &&
                                          (signals.PeakDiskLatencyMilliseconds >=
                                               options.DiskLatencyWarningMilliseconds ||
                                           signals.PeakDiskQueueLength >= diskQueueThreshold);
            var driverSignalCorrelated = signals is not null &&
                                         signals.PeakDpcInterruptPercent >=
                                         options.DpcInterruptWarningPercent;
            var displaySignalCorrelated =
                gpu.Status == MeasurementStatus.Available &&
                (gpu.UtilizationPercent >= 80 || gpu.TemperatureCelsius >= 85);
            var seriousHardware = stability.HardwareErrorCount > 0;
            var critical = seriousHardware ||
                           stability.StorageErrorCount >= 3 && storageSignalCorrelated;
            var currentSignalCorrelated =
                storageSignalCorrelated ||
                driverSignalCorrelated ||
                displaySignalCorrelated;
            findings.Add(new LagFinding(
                critical ? LagSeverity.Critical : LagSeverity.Warning,
                "reliability-events",
                "事件日志存在与卡顿相关的稳定性证据",
                $"硬件 {stability.HardwareErrorCount}、存储 {stability.StorageErrorCount}、显示重置 {stability.DisplayResetCount}、资源耗尽 {stability.ResourceExhaustionCount}、应用挂起 {stability.ApplicationHangCount}；{stability.Window}。",
                "打开报告核对 Provider 与 Event ID；硬件、存储或显示错误应优先处理驱动和设备稳定性。",
                false,
                "")
            {
                Domain = DiagnosticDomain.SystemReliability,
                Confidence = currentSignalCorrelated
                    ? FindingConfidence.High
                    : FindingConfidence.Medium,
                Score = critical ? 24 : currentSignalCorrelated ? 12 : 6,
                CausalChain = currentSignalCorrelated
                    ? "近期关键事件与当前采样信号同时出现 → 设备或驱动稳定性风险提高"
                    : "近期关键事件存在 → 当前短窗口未复现对应性能信号 → 需要延长采样确认"
            });
        }

        if (context is { StartupEntryCount: >= 30 })
        {
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "startup-density",
                "启动项数量较多",
                $"当前用户与系统启动入口合计约 {context.StartupEntryCount} 项。",
                "仅对多次扫描中持续消耗 CPU、I/O 或内存的启动项执行隔离；保留恢复记录。",
                false,
                "")
            {
                Domain = DiagnosticDomain.BackgroundProcesses,
                Confidence = FindingConfidence.Medium,
                Score = 3
            });
        }

        if (context is { PendingReboot: true })
        {
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "pending-reboot",
                "系统存在待完成的重启事务",
                "组件服务、Windows Update 或文件替换标记表明系统等待重启。",
                "保存工作后安排重启，再以相同采样窗口建立新基线。",
                false,
                "")
            {
                Domain = DiagnosticDomain.SystemReliability,
                Confidence = FindingConfidence.High,
                Score = 2,
                RemediationRisk = RemediationRisk.RestartRequired
            });
        }

        return findings;
    }

    private static IReadOnlyList<LagFinding> BuildSupplementalProbeFindings(
        SystemHealthProbeSnapshot health,
        PerformanceSnapshot performance)
    {
        var findings = new List<LagFinding>();
        var hungWindows = health.WindowResponsiveness
            .Where(item => item.HungWindowCount > 0)
            .ToArray();
        if (hungWindows.Length > 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "hung-visible-windows",
                "检测到疑似无响应的用户窗口",
                string.Join(
                    "；",
                    hungWindows.Select(item =>
                        $"{item.ProcessName}({item.ProcessId}) {item.HungWindowCount}/{item.VisibleWindowCount} 个窗口触发 Windows 五秒消息泵超时判据")),
                "先保存其他应用工作并观察；重复扫描仍命中时，再结束对应应用进程并核对存储与驱动事件。",
                false,
                "")
            {
                Domain = DiagnosticDomain.Responsiveness,
                Confidence = FindingConfidence.Medium,
                Score = 12,
                CausalChain = "用户窗口超过系统消息泵响应阈值 → 前台交互可能无法处理 → 用户感知为卡死"
            });
        }

        if (performance.KernelTotalBytes >= 2UL * 1024 * 1024 * 1024 &&
            health.PoolTags.Count > 0)
        {
            var topTags = health.PoolTags
                .OrderByDescending(item => item.TotalBytes)
                .Take(6)
                .ToArray();
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "kernel-pool-tags",
                "已采集内核池主要标签",
                string.Join(
                    "；",
                    topTags.Select(item =>
                        $"{item.Tag} {FormatBytes(item.TotalBytes)}（分页 {FormatBytes(item.PagedBytes)}，非分页 {FormatBytes(item.NonPagedBytes)}）")),
                "将高占用标签与 PoolMon 的驱动映射对照；保留重启前后报告，比较同一标签的增长量。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 0,
                CausalChain = "内核池总量异常 → 标签聚合定位主要分配来源 → 映射驱动后开展复测"
            });
        }

        if (health.SystemHandleTypes.Count > 0)
        {
            var topTypes = health.SystemHandleTypes
                .OrderByDescending(item => item.SystemHandleCount)
                .Take(8)
                .ToArray();
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "system-handle-breakdown",
                "已拆分 System 句柄对象类型",
                string.Join(
                    "；",
                    topTypes.Select(item =>
                        $"{item.TypeName} {item.SystemHandleCount:n0}（{item.SystemSharePercent:n1}%）")),
                "保留重启前后和定时扫描的类型分布；同一类型持续增长时，再按对象类型选择 ETW、服务隔离或驱动排查路径。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 0,
                CausalChain = "PID 4 句柄总量异常 → 按内核对象类型聚合 → 将排查范围缩小到对应子系统"
            });

            var dominant = topTypes[0];
            if (dominant.SystemHandleCount >= 100_000 &&
                dominant.SystemSharePercent >= 35)
            {
                var poolCorrelation = HandleTypePoolCorrelation(
                    dominant.TypeName,
                    health.PoolTags);
                findings.Add(new LagFinding(
                    LagSeverity.Warning,
                    "system-handles-dominant-type",
                    $"System 句柄集中于 {dominant.TypeName}",
                    $"{dominant.TypeName} 占 PID 4 的 {dominant.SystemHandleCount:n0} 个句柄（{dominant.SystemSharePercent:n1}%）；全机同类型共 {dominant.AllProcessHandleCount:n0} 个。{poolCorrelation}",
                    HandleTypeRecommendation(dominant.TypeName),
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.KernelDrivers,
                    Confidence = FindingConfidence.High,
                    Score = 6,
                    CausalChain = $"PID 4 句柄异常 → {dominant.TypeName} 类型形成主导占比 → 优先检查该对象类型关联的系统组件"
                });
            }
        }

        if (health.SystemFileHandleAccess.Count > 0)
        {
            var accessPatterns = health.SystemFileHandleAccess
                .Take(8)
                .ToArray();
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "system-file-handle-access",
                "已拆分 System File 句柄访问模式",
                string.Join(
                    "；",
                    accessPatterns.Select(item =>
                        $"0x{item.GrantedAccessMask:x8} {item.Rights}：{item.HandleCount:n0}（{item.SharePercent:n1}%）")),
                "访问掩码能区分数据读写、元数据查询和同步用途；结合路径采样与过滤驱动候选判断创建链。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 0,
                CausalChain = "File 对象形成主导 → 按 GrantedAccess 精确聚合 → 区分数据 I/O、元数据与同步句柄"
            });

            var dominantAccess = accessPatterns[0];
            if (dominantAccess.HandleCount >= 100_000 &&
                dominantAccess.SharePercent >= 90)
            {
                findings.Add(new LagFinding(
                    LagSeverity.Warning,
                    "system-handles-file-access-uniform",
                    "System File 句柄呈现单一访问模式",
                    $"0x{dominantAccess.GrantedAccessMask:x8} {dominantAccess.Rights} 占 {dominantAccess.HandleCount:n0} 个句柄（{dominantAccess.SharePercent:n2}%）。",
                    "高度一致的访问掩码表明句柄可能来自同一重复打开路径；优先围绕过滤驱动、容器文件链和只读扫描组件做重启前后对照。",
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.KernelDrivers,
                    Confidence = FindingConfidence.Medium,
                    Score = 3,
                    CausalChain = "File 句柄异常 → 绝大多数句柄使用同一 GrantedAccess → 单一创建路径或同类过滤操作成为重点"
                });
            }
        }

        if (health.SystemFileAttribution is { } attribution)
        {
            if (attribution.RequiresAdministrator)
            {
                findings.Add(new LagFinding(
                    LagSeverity.Info,
                    "system-file-path-attribution-permission",
                    "File 路径归因需要管理员诊断令牌",
                    attribution.Summary,
                    "从管理员终端运行同一专清扫描即可执行有界路径采样；工具只抽样最多 512 个 File 句柄，不遍历 160 万个对象名称。",
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.KernelDrivers,
                    Confidence = FindingConfidence.High,
                    Score = 0,
                    CausalChain = "PID 4 File 句柄 → DuplicateHandle 需要 PROCESS_DUP_HANDLE → 当前标准令牌被系统拒绝"
                });
            }
            else if (health.SystemFilePathGroups.Count > 0)
            {
                findings.Add(new LagFinding(
                    LagSeverity.Info,
                    "system-file-path-attribution",
                    "已抽样解析 System File 路径来源",
                    string.Join(
                        "；",
                        health.SystemFilePathGroups
                            .Take(8)
                            .Select(item =>
                                $"{item.PathGroup} [{item.FileKind}] {item.SampleCount}（{item.SampleSharePercent:n1}%）")),
                    "将占比最高的卷和目录与过滤器实例、服务状态及文件 I/O 事件对照。",
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.KernelDrivers,
                    Confidence = FindingConfidence.Medium,
                    Score = 0,
                    CausalChain = "均匀抽样 PID 4 File 句柄 → 复制到诊断进程 → 按打开路径和设备类型聚合"
                });
            }
        }

        if (health.FileSystemFilters.Count > 0)
        {
            var runningCandidates = health.FileSystemFilters
                .Where(item => item.Running)
                .Take(8)
                .ToArray();
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "file-system-filter-inventory",
                "已采集文件系统过滤驱动来源候选",
                string.Join(
                    "；",
                    runningCandidates.Select(item =>
                        $"{item.ServiceName} [{item.LoadOrderGroup}] {item.Likelihood}：{item.Evidence}")),
                "该清单表示注册和运行状态，无法单独证明某个过滤器创建了目标 File 句柄；结合重启前后增长、卷实例和路径样本确认。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = 0,
                CausalChain = "File 句柄异常 → 枚举注册过滤器、驱动二进制和运行状态 → 建立可验证的来源候选集"
            });

            var strongCandidate = health.FileSystemFilters.FirstOrDefault(
                item => item.Running &&
                        string.Equals(
                            item.Likelihood,
                            "强相关候选",
                            StringComparison.Ordinal));
            if (strongCandidate is not null)
            {
                findings.Add(new LagFinding(
                    LagSeverity.Warning,
                    "system-handles-file-source-candidate",
                    $"文件过滤来源强相关候选：{strongCandidate.ServiceName}",
                    $"{strongCandidate.DisplayName}；驱动 {strongCandidate.DriverPath}；{strongCandidate.Evidence}",
                    $"先重启建立基线；若 File 句柄和关联 Pool Tag 再次增长，围绕 {strongCandidate.ServiceName} 的容器、隔离或卷实例做启停对照，避免直接卸载内核驱动。",
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.KernelDrivers,
                    Confidence = FindingConfidence.Medium,
                    Score = 4,
                    RemediationRisk = RemediationRisk.RestartRequired,
                    CausalChain = "File 句柄占 99% → 相关 Pool Tag 同步异常 → 运行中的过滤驱动形成强相关候选"
                });
            }
        }

        return findings;
    }

    private static string HandleTypePoolCorrelation(
        string typeName,
        IReadOnlyList<PoolTagSnapshot> poolTags)
    {
        if (!string.Equals(typeName, "File", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        var fileSystemTags = new HashSet<string>(
            ["FMfn", "File", "FMfc", "FMwi", "FMsl", "FOCX", "Ntfs", "NtfF", "NtfC"],
            StringComparer.OrdinalIgnoreCase);
        var matches = poolTags
            .Where(item => fileSystemTags.Contains(item.Tag))
            .OrderByDescending(item => item.TotalBytes)
            .Take(6)
            .ToArray();
        return matches.Length == 0
            ? ""
            : " 同次文件系统相关池标签：" +
              string.Join(
                  "、",
                  matches.Select(item =>
                      $"{item.Tag} {FormatBytes(item.TotalBytes)}")) +
              "。";
    }

    private static string HandleTypeRecommendation(string typeName)
    {
        return typeName.ToUpperInvariant() switch
        {
            "EVENT" =>
                "优先检查服务、驱动和远程会话的同步对象增长；记录重启前后 Event 数，并通过按服务隔离复测定位创建源。",
            "FILE" =>
                "优先检查文件系统过滤驱动、杀毒、同步、索引与备份链；结合 Pool Tag、Filter Manager 清单和重启前后增长量定位。",
            "THREAD" =>
                "优先检查线程对象泄漏与高频进程退出；对照进程/线程总量、服务 PID 和对象增长趋势。",
            "PROCESS" =>
                "优先检查进程对象回收、作业对象和长期持有进程句柄的系统组件；对照进程创建/退出事件。",
            "KEY" =>
                "优先检查注册表监控、策略、杀毒与配置服务的句柄增长；分服务复测并保留类型趋势。",
            "SECTION" =>
                "优先检查共享内存、映像映射、容器和图形组件；结合提交内存、GPU 及容器状态复测。",
            "IOCOMPLETION" =>
                "优先检查高并发 I/O 服务、网络栈和完成端口回收；结合 AFD、文件与网络 Pool Tag 变化。",
            "ALPC PORT" =>
                "优先检查服务 IPC、COM/RPC 和容器通信端点增长；按服务重启前后比较 ALPC Port 数。",
            "DESKTOP" or "WINDOWSTATION" =>
                "优先检查远程桌面、交互会话、NVIDIA Container 和 GUI 子系统；对照会话数量及服务重启前后变化。",
            _ =>
                $"持续采样 {typeName} 类型增长率，并结合对应内核对象、服务和驱动事件定位创建源。"
        };
    }

    private static LagFinding EnrichFinding(LagFinding finding)
    {
        var inferredDomain = finding.Code switch
        {
            var code when code.StartsWith("kernel-", StringComparison.Ordinal) ||
                              code.StartsWith("system-handles", StringComparison.Ordinal) ||
                              code.Contains("nvidia-container", StringComparison.Ordinal) =>
                DiagnosticDomain.KernelDrivers,
            var code when code.StartsWith("memory-", StringComparison.Ordinal) =>
                DiagnosticDomain.Memory,
            var code when code.StartsWith("mcp-", StringComparison.Ordinal) ||
                              code.StartsWith("process-", StringComparison.Ordinal) ||
                              code.StartsWith("weflow-", StringComparison.Ordinal) ||
                              code.StartsWith("service-host", StringComparison.Ordinal) =>
                DiagnosticDomain.BackgroundProcesses,
            _ => finding.Domain
        };
        var domain = finding.Domain == DiagnosticDomain.SystemReliability
            ? inferredDomain
            : finding.Domain;
        var score = finding.Score != 10
            ? finding.Score
            : finding.Severity switch
            {
                LagSeverity.Critical => 24,
                LagSeverity.Warning => 12,
                _ => 0
            };
        var risk = finding.RemediationRisk != RemediationRisk.ReadOnly
            ? finding.RemediationRisk
            : finding.CanClean
                ? RemediationRisk.High
                : finding.Code.StartsWith("kernel-", StringComparison.Ordinal) ||
                  finding.Code.StartsWith("system-handles", StringComparison.Ordinal)
                    ? RemediationRisk.RestartRequired
                    : RemediationRisk.ReadOnly;
        return finding with
        {
            Domain = domain,
            Score = score,
            RemediationRisk = risk
        };
    }

    private static string FormatSignedBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }

        var prefix = bytes > 0 ? "+" : "-";
        var magnitude = bytes == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(bytes);
        return prefix + FormatBytes(magnitude);
    }

    private static IReadOnlyList<LagFinding> BuildFindings(
        LagCleanerOptions options,
        PerformanceSnapshot performance,
        IReadOnlyList<ProcessSnapshot> processes,
        IReadOnlyList<ProcessSnapshot> weFlow,
        IReadOnlyList<McpProcessGroup> mcpGroups,
        double physicalPercent,
        double commitPercent,
        ulong memoryCompression,
        uint systemHandles,
        bool? userIdleConfirmed)
    {
        var findings = new List<LagFinding>();
        var kernelPool = performance.KernelPagedBytes + performance.KernelNonPagedBytes;
        if (kernelPool >= options.KernelPoolCriticalBytes)
        {
            findings.Add(new LagFinding(
                LagSeverity.Critical,
                "kernel-pool-critical",
                "内核池占用严重异常",
                $"分页池 {FormatBytes(performance.KernelPagedBytes)}，非分页池 {FormatBytes(performance.KernelNonPagedBytes)}，合计 {FormatBytes(kernelPool)}。",
                "保存工作并重启；重启后连续采样，若数日内再次升至数 GB，使用 PoolMon 对比增长标签并更新关联驱动。",
                false,
                ""));
        }
        else if (performance.KernelPagedBytes >= options.PagedPoolWarningBytes ||
                 performance.KernelNonPagedBytes >= options.NonPagedPoolWarningBytes)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "kernel-pool-warning",
                "内核池持续偏高",
                $"分页池 {FormatBytes(performance.KernelPagedBytes)}，非分页池 {FormatBytes(performance.KernelNonPagedBytes)}。",
                "安排重启并观察增长速度，优先核对文件过滤、显卡、同步盘和安全软件驱动。",
                false,
                ""));
        }

        if (systemHandles >= options.SystemHandleCriticalCount)
        {
            findings.Add(new LagFinding(
                LagSeverity.Critical,
                "system-handles-critical",
                "System 句柄数严重异常",
                $"System 当前持有 {systemHandles:n0} 个句柄。",
                "重启可释放已泄漏句柄；重启后记录每日增长，定位对应驱动或系统组件。",
                false,
                ""));
        }
        else if (systemHandles >= options.SystemHandleWarningCount)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "system-handles-warning",
                "System 句柄数偏高",
                $"System 当前持有 {systemHandles:n0} 个句柄。",
                "观察句柄增长曲线，增长持续时安排重启并排查驱动。",
                false,
                ""));
        }

        var mcpCandidates = mcpGroups.Where(group => group.IsCleanupCandidate).ToArray();
        if (performance.ProcessCount >= options.ProcessCriticalCount)
        {
            findings.Add(new LagFinding(
                LagSeverity.Critical,
                "process-count-critical",
                "后台进程数量严重异常",
                $"当前共有 {performance.ProcessCount:n0} 个进程和 {performance.ThreadCount:n0} 个线程。",
                "清理已确认的残留进程，并修复其退出清理逻辑。",
                mcpCandidates.Length > 0,
                mcpCandidates.Length > 0 ? "mcp-residue" : ""));
        }
        else if (performance.ProcessCount >= options.ProcessWarningCount)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "process-count-warning",
                "后台进程数量偏高",
                $"当前共有 {performance.ProcessCount:n0} 个进程和 {performance.ThreadCount:n0} 个线程。",
                "检查重复拉起的工具链和托盘程序。",
                mcpCandidates.Length > 0,
                mcpCandidates.Length > 0 ? "mcp-residue" : ""));
        }

        if (mcpCandidates.Length > 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "mcp-residue",
                "发现静置的 computer-use MCP 残留",
                $"{mcpCandidates.Length} 组、{mcpCandidates.Sum(group => group.ProcessIds.Count)} 个进程符合清理条件，占用 {FormatBytes(mcpCandidates.Aggregate(0UL, (total, group) => total + group.PrivateBytes))} 私有内存。",
                "先生成清理计划；核对保留组数、PID 和存活时间后提交确认令牌。",
                true,
                "mcp-residue"));
        }

        var busyWeFlow = weFlow.Where(item => item.CpuPercentOneCore >= options.WeFlowCorePercentThreshold).ToArray();
        if (busyWeFlow.Length > 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Warning,
                "weflow-cpu",
                userIdleConfirmed switch
                {
                    true => "WeFlow 静置采样 CPU 偏高",
                    false => "WeFlow 活动场景 CPU 偏高",
                    null => "WeFlow CPU 偏高（采样场景未确认）"
                },
                $"WeFlow 合计占用整机 {busyWeFlow.Sum(item => item.CpuPercentMachine):n1}%，折合单核 {busyWeFlow.Sum(item => item.CpuPercentOneCore):n1}%；采样场景：{userIdleConfirmed switch
                {
                    true => "已确认静置",
                    false => "检测到用户活动",
                    null => "用户空闲边界缺失"
                }}。",
                userIdleConfirmed == true
                    ? "保存 WeFlow 内工作，随后退出或生成专清计划；退出后复测可验证关联。"
                    : "保持一分钟无输入后复测；静置窗口仍持续偏高时再退出、更新或重装 WeFlow。",
                true,
                "weflow"));
        }

        AddProcessLeakFinding(
            findings,
            processes,
            item =>
                item.Name.Contains("NVDisplay.Container", StringComparison.OrdinalIgnoreCase) ||
                item.Name.Contains("nvcontainer", StringComparison.OrdinalIgnoreCase) ||
                item.KnownRole.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase),
            "nvidia-container-leak",
            "NVIDIA Container 句柄或线程异常",
            "更新 NVIDIA 驱动与 NVIDIA App；显著增长时使用提权 CLI 重启 NVIDIA Display Container 服务。",
            "nvidia-container");
        AddProcessLeakFinding(
            findings,
            processes,
            item => string.Equals(item.Name, "svchost", StringComparison.OrdinalIgnoreCase) &&
                    item.HandleCount >= 10_000,
            "service-host-leak",
            "服务宿主句柄数异常",
            "在详细进程列表核对服务宿主；Delivery Optimization 可单独重启，远程桌面服务重启会断开会话。",
            "");

        if (physicalPercent >= 85 || commitPercent >= 85)
        {
            findings.Add(new LagFinding(
                physicalPercent >= 95 || commitPercent >= 95 ? LagSeverity.Critical : LagSeverity.Warning,
                "memory-pressure",
                "系统内存压力偏高",
                $"物理内存使用 {physicalPercent:n1}%，提交使用 {commitPercent:n1}%。",
                "先处理进程和内核泄漏，再评估业务进程的真实内存需求。",
                false,
                ""));
        }

        if (memoryCompression >= 2UL * 1024 * 1024 * 1024)
        {
            findings.Add(new LagFinding(
                memoryCompression >= 4UL * 1024 * 1024 * 1024 ? LagSeverity.Warning : LagSeverity.Info,
                "memory-compression-high",
                "内存压缩占用偏高",
                $"Memory Compression 工作集为 {FormatBytes(memoryCompression)}。",
                "该数值说明系统此前经历过内存压力；先处理内核池与残留进程，再观察重启后的基线。",
                false,
                ""));
        }

        if (findings.Count == 0)
        {
            findings.Add(new LagFinding(
                LagSeverity.Info,
                "baseline-healthy",
                "当前采样未发现高风险项",
                "CPU、内存、进程、句柄与内核池均未越过默认阈值。",
                "保留本次报告作为静置基线。",
                false,
                ""));
        }

        return findings;
    }

    private static void AddProcessLeakFinding(
        ICollection<LagFinding> findings,
        IEnumerable<ProcessSnapshot> processes,
        Func<ProcessSnapshot, bool> selector,
        string code,
        string title,
        string recommendation,
        string cleanupAction)
    {
        var matches = processes.Where(selector)
            .Where(item => item.HandleCount >= 10_000 || item.ThreadCount >= 1_000)
            .ToArray();
        if (matches.Length == 0)
        {
            return;
        }

        findings.Add(new LagFinding(
            matches.Any(item => item.HandleCount >= 50_000 || item.ThreadCount >= 10_000)
                ? LagSeverity.Critical
                : LagSeverity.Warning,
            code,
            title,
            string.Join("; ", matches.Select(item =>
                $"{item.Name}({item.ProcessId}){(item.KnownRole.Length > 0 ? $" [{item.KnownRole}]" : "")} 句柄 {item.HandleCount:n0}、线程 {item.ThreadCount:n0}")),
            recommendation,
            cleanupAction.Length > 0,
            cleanupAction));
    }

    private static IReadOnlyList<string> BuildRecommendations(
        IReadOnlyList<LagFinding> findings,
        PerformanceSnapshot performance,
        uint systemHandles)
    {
        var recommendations = new List<string>();
        if (findings.Any(item => item.Code is "kernel-pool-critical" or "system-handles-critical"))
        {
            recommendations.Add("立即保存工作并重启 Windows，清空内核池与泄漏句柄。");
        }

        if (findings.Any(item => item.Code == "mcp-residue"))
        {
            recommendations.Add("执行 mcp-residue 两阶段清理，并修复 computer-use MCP 的退出回收。");
        }

        if (findings.Any(item => item.Code == "weflow-cpu"))
        {
            recommendations.Add("退出 WeFlow 后重新采样；CPU 恢复即可确认主因。");
        }

        if (performance.KernelTotalBytes >= 2UL * 1024 * 1024 * 1024 || systemHandles >= 500_000)
        {
            recommendations.Add("重启后每天保留一份报告；内核池或句柄持续增长时比较 PoolMon 标签。");
        }

        if (recommendations.Count == 0)
        {
            recommendations.Add("保留报告并在下一次卡顿时重复 3–5 秒采样。");
        }

        return recommendations;
    }

    private static bool NeedsIdentity(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        return name.Contains("python", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("mcp", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("uv", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("uvx", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("node", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("NVDisplay.Container", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("WeFlow", StringComparison.OrdinalIgnoreCase);
    }

    private static string KnownRole(string processName)
    {
        if (processName.Contains("NVDisplay.Container", StringComparison.OrdinalIgnoreCase))
        {
            return "NVIDIA Display Container";
        }

        return "";
    }

    private static DateTimeOffset? TryReadStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    private static int TryRead(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return 0;
        }
    }

    private static long TryReadLong(Func<long> getter)
    {
        try
        {
            return getter();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return 0;
        }
    }

    private static ulong ToUInt64(long value) => value <= 0 ? 0 : (ulong)value;

    private static double PerSecond(ulong current, ulong previous, double elapsedSeconds)
    {
        if (current < previous || elapsedSeconds <= 0)
        {
            return 0;
        }

        return Math.Round((current - previous) / elapsedSeconds, 2);
    }

    private static long SaturatingLongDelta(ulong current, ulong previous)
    {
        if (current >= previous)
        {
            return current - previous > long.MaxValue
                ? long.MaxValue
                : (long)(current - previous);
        }

        return previous - current > long.MaxValue
            ? long.MinValue
            : -(long)(previous - current);
    }

    private static double Percent(ulong value, ulong total)
    {
        return total == 0 ? 0 : Math.Round(value * 100d / total, 2);
    }

    private static double CalculateTotalCpu(CpuTimes start, CpuTimes end)
    {
        var idle = end.Idle >= start.Idle ? end.Idle - start.Idle : 0;
        var kernel = end.Kernel >= start.Kernel ? end.Kernel - start.Kernel : 0;
        var user = end.User >= start.User ? end.User - start.User : 0;
        var total = kernel + user;
        return total == 0 ? 0 : Math.Round((total - Math.Min(total, idle)) * 100d / total, 2);
    }

    public static string FormatBytes(ulong bytes)
    {
        var value = (double)bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:n1} {units[unit]}";
    }

    private sealed record RawProcessSnapshot(
        int ProcessId,
        int ParentProcessId,
        string Name,
        DateTimeOffset? StartTimeUtc,
        long TotalProcessorTicks,
        ulong PrivateBytes,
        ulong WorkingSetBytes,
        int HandleCount,
        int ThreadCount,
        ProcessIoSnapshot Io,
        string KnownRole,
        string ExecutablePath,
        string CommandLine);

    private sealed record ProcessCounterSample(
        DateTimeOffset? StartTimeUtc,
        long TotalProcessorTicks,
        ulong PrivateBytes,
        int HandleCount,
        int ThreadCount,
        ProcessIoSnapshot Io);

    private sealed record AnalyzedProcess(
        ProcessSnapshot Public,
        string ExecutablePath,
        string CommandLine);
}
