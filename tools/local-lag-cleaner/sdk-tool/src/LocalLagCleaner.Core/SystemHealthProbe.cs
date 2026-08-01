using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace LocalLagCleaner.MyPowerTools;

internal enum SystemHealthCoverageState
{
    Complete,
    Partial,
    Unavailable,
    Failed
}

internal sealed record SystemHealthCoverage(
    string Component,
    SystemHealthCoverageState State,
    string Detail);

internal sealed record SystemHealthMetricSummary(
    string MetricId,
    string Unit,
    int ValidSamples,
    double Average,
    double P95,
    double Maximum,
    double Latest);

internal sealed record SystemHealthPdhSample(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string, double> Metrics);

internal sealed record PdhSystemHealthSnapshot(
    int RequestedSamples,
    TimeSpan SampleInterval,
    IReadOnlyList<SystemHealthMetricSummary> Metrics)
{
    public IReadOnlyList<SystemHealthPdhSample> Samples { get; init; } = [];
}

internal sealed record DriveSpaceHealthSnapshot(
    string RootPath,
    DriveType DriveType,
    string Format,
    long TotalBytes,
    long AvailableBytes,
    double AvailablePercent);

internal sealed record UserIdleHealthSnapshot(
    TimeSpan StartIdleTime,
    TimeSpan EndIdleTime,
    DateTimeOffset EstimatedLastInputAt,
    bool InputObservedDuringSampling,
    uint StartLastInputTick,
    uint EndLastInputTick)
{
    public TimeSpan IdleTime => EndIdleTime;
}

internal sealed record SystemHealthPerformanceWindow(
    PdhSystemHealthSnapshot Performance,
    UserIdleHealthSnapshot? UserIdle,
    IReadOnlyList<SystemHealthCoverage> Coverage);

internal sealed record PowerPlanHealthSnapshot(
    string SchemeGuid,
    string DisplayName,
    string RawSummary);

internal sealed record PendingRebootHealthSnapshot(
    bool IsPending,
    IReadOnlyList<string> Reasons);

internal sealed record StartupHealthSnapshot(
    int TotalItems,
    IReadOnlyDictionary<string, int> SourceCounts);

internal sealed record NvidiaGpuHealthSnapshot(
    int Index,
    string Name,
    string Uuid,
    string DriverVersion,
    double? GpuUtilizationPercent,
    double? MemoryUtilizationPercent,
    long? MemoryTotalMiB,
    long? MemoryUsedMiB,
    double? TemperatureCelsius);

internal sealed record ReliabilityEventGroup(
    string LogName,
    string Provider,
    int EventId,
    int Level,
    int Count,
    int RecentCount,
    DateTimeOffset? LatestAtUtc);

internal sealed record ReliabilityHealthSnapshot(
    TimeSpan Window,
    int EventsRead,
    bool MayBeTruncated,
    IReadOnlyList<ReliabilityEventGroup> Groups);

internal sealed record SystemHealthProbeSnapshot(
    DateTimeOffset CapturedAtUtc,
    PdhSystemHealthSnapshot Performance,
    IReadOnlyList<DriveSpaceHealthSnapshot> Drives,
    UserIdleHealthSnapshot? UserIdle,
    PowerPlanHealthSnapshot? PowerPlan,
    PendingRebootHealthSnapshot? PendingReboot,
    StartupHealthSnapshot? Startup,
    IReadOnlyList<NvidiaGpuHealthSnapshot> NvidiaGpus,
    ReliabilityHealthSnapshot Reliability,
    IReadOnlyList<SystemHealthCoverage> Coverage)
{
    public IReadOnlyList<PoolTagSnapshot> PoolTags { get; init; } = [];
    public IReadOnlyList<HandleTypeSnapshot> SystemHandleTypes { get; init; } = [];
    public IReadOnlyList<FileHandleAccessSnapshot> SystemFileHandleAccess { get; init; } = [];
    public SystemFileHandleAttribution? SystemFileAttribution { get; init; }
    public IReadOnlyList<FileHandlePathGroupSnapshot> SystemFilePathGroups { get; init; } = [];
    public IReadOnlyList<FileSystemFilterSnapshot> FileSystemFilters { get; init; } = [];
    public IReadOnlyList<WindowResponsivenessSnapshot> WindowResponsiveness { get; init; } = [];
}

/// <summary>
/// Best-effort Windows health probe. Every sub-probe reports its own coverage so
/// unavailable counters, permissions, optional utilities, and transient failures
/// do not abort the wider diagnostic scan.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SystemHealthProbe
{
    private const int DefaultPdhSamples = 5;
    private const int MaximumPdhSamples = 60;
    private const int ExternalOutputLimitChars = 128 * 1024;
    private const int EventOutputLimitChars = 1024 * 1024;
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhValidData = 0x00000000;
    private const uint PdhNewData = 0x00000001;

    private static readonly Regex XmlDeclarationPattern = new(
        @"<\?xml[^>]*\?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly PdhCounterDefinition[] PdhDefinitions =
    [
        new(
            "cpu.total",
            "percent",
            1,
            [
                @"\Processor Information(_Total)\% Processor Time",
                @"\Processor(_Total)\% Processor Time"
            ]),
        new(
            "cpu.processor-queue",
            "threads",
            1,
            [@"\System\Processor Queue Length"]),
        new(
            "cpu.context-switches",
            "operations/sec",
            1,
            [@"\System\Context Switches/sec"]),
        new(
            "cpu.dpc-time",
            "percent",
            1,
            [
                @"\Processor Information(_Total)\% DPC Time",
                @"\Processor(_Total)\% DPC Time"
            ]),
        new(
            "cpu.interrupt-time",
            "percent",
            1,
            [
                @"\Processor Information(_Total)\% Interrupt Time",
                @"\Processor(_Total)\% Interrupt Time"
            ]),
        new(
            "cpu.dpc-rate",
            "operations/sec",
            1,
            [
                @"\Processor Information(_Total)\DPC Rate",
                @"\Processor(_Total)\DPC Rate"
            ]),
        new(
            "cpu.interrupt-rate",
            "operations/sec",
            1,
            [
                @"\Processor Information(_Total)\Interrupts/sec",
                @"\Processor(_Total)\Interrupts/sec"
            ]),
        new(
            "memory.pages-input",
            "pages/sec",
            1,
            [@"\Memory\Pages Input/sec"]),
        new(
            "memory.page-reads",
            "operations/sec",
            1,
            [@"\Memory\Page Reads/sec"]),
        new(
            "disk.busy",
            "percent",
            1,
            [@"\PhysicalDisk(_Total)\% Disk Time"]),
        new(
            "disk.transfer-latency",
            "milliseconds",
            1_000,
            [@"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer"]),
        new(
            "disk.queue",
            "requests",
            1,
            [@"\PhysicalDisk(_Total)\Current Disk Queue Length"]),
        new(
            "disk.throughput",
            "bytes/sec",
            1,
            [@"\PhysicalDisk(_Total)\Disk Bytes/sec"])
    ];

    public async Task<SystemHealthProbeSnapshot> CollectAsync(
        int pdhSamples = DefaultPdhSamples,
        TimeSpan? pdhSampleInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = NormalizeSampleInterval(pdhSampleInterval);
        pdhSamples = Math.Clamp(pdhSamples, 1, MaximumPdhSamples);

        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedPlatformSnapshot(pdhSamples, interval);
        }

        var performanceWindow = await CollectPerformanceWindowAsync(
            pdhSamples,
            interval,
            cancellationToken).ConfigureAwait(false);
        return await CollectSupplementalAsync(
            performanceWindow,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SystemHealthPerformanceWindow> CollectPerformanceWindowAsync(
        int pdhSamples,
        TimeSpan? pdhSampleInterval = null,
        CancellationToken cancellationToken = default)
    {
        pdhSamples = Math.Clamp(pdhSamples, 1, MaximumPdhSamples);
        var interval = NormalizeSampleInterval(pdhSampleInterval);
        if (!OperatingSystem.IsWindows())
        {
            return new SystemHealthPerformanceWindow(
                new PdhSystemHealthSnapshot(pdhSamples, interval, []),
                null,
                [
                    UnavailableCoverage("pdh", "The probe supports Windows only."),
                    UnavailableCoverage("user-idle", "The probe supports Windows only.")
                ]);
        }

        var idleAtStart = CollectUserIdleObservation();
        var performance = await CollectPdhAsync(
            pdhSamples,
            interval,
            cancellationToken).ConfigureAwait(false);
        var idleAtEnd = CollectUserIdleObservation();
        var idle = CombineUserIdleObservations(idleAtStart, idleAtEnd);

        return new SystemHealthPerformanceWindow(
            performance.Value,
            idle.Value,
            [performance.Coverage, idle.Coverage]);
    }

    public async Task<SystemHealthProbeSnapshot> CollectSupplementalAsync(
        SystemHealthPerformanceWindow performanceWindow,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return UnsupportedPlatformSnapshot(
                performanceWindow.Performance.RequestedSamples,
                performanceWindow.Performance.SampleInterval);
        }

        var gpuTask = CollectNvidiaGpusAsync(cancellationToken);
        var reliabilityTask = CollectReliabilityEventsAsync(cancellationToken);
        var drives = CollectDrives();
        var power = CollectPowerPlan();
        var pendingReboot = CollectPendingReboot();
        var startup = CollectStartupItems();
        var poolTags = CollectKernelPoolTags();
        var windows = CollectWindowResponsiveness();
        await Task.WhenAll(gpuTask, reliabilityTask).ConfigureAwait(false);

        var gpu = await gpuTask.ConfigureAwait(false);
        var reliability = await reliabilityTask.ConfigureAwait(false);

        return new SystemHealthProbeSnapshot(
            DateTimeOffset.UtcNow,
            performanceWindow.Performance,
            drives.Value,
            performanceWindow.UserIdle,
            power.Value,
            pendingReboot.Value,
            startup.Value,
            gpu.Value,
            reliability.Value,
            [
                .. performanceWindow.Coverage,
                drives.Coverage,
                power.Coverage,
                pendingReboot.Coverage,
                startup.Coverage,
                gpu.Coverage,
                reliability.Coverage,
                poolTags.Coverage,
                windows.Coverage
            ])
        {
            PoolTags = poolTags.Value,
            WindowResponsiveness = windows.Value
        };
    }

    private static TimeSpan NormalizeSampleInterval(TimeSpan? requested)
    {
        var interval = requested ?? TimeSpan.FromSeconds(1);
        if (interval < TimeSpan.FromMilliseconds(100))
        {
            return TimeSpan.FromMilliseconds(100);
        }

        return interval > TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : interval;
    }

    private static async Task<ProbeResult<PdhSystemHealthSnapshot>> CollectPdhAsync(
        int requestedSamples,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken)
    {
        IntPtr query = IntPtr.Zero;
        try
        {
            var openStatus = PdhOpenQueryW(null, UIntPtr.Zero, out query);
            if (openStatus != 0 || query == IntPtr.Zero)
            {
                return new ProbeResult<PdhSystemHealthSnapshot>(
                    new PdhSystemHealthSnapshot(requestedSamples, sampleInterval, []),
                    new SystemHealthCoverage(
                        "pdh",
                        SystemHealthCoverageState.Unavailable,
                        $"PdhOpenQueryW failed with 0x{openStatus:x8}."));
            }

            var activeCounters = new List<ActivePdhCounter>();
            var unavailableMetricIds = new List<string>();
            foreach (var definition in PdhDefinitions)
            {
                IntPtr counter = IntPtr.Zero;
                uint addStatus = 0;
                string selectedPath = "";
                foreach (var path in definition.Paths)
                {
                    addStatus = PdhAddEnglishCounterW(
                        query,
                        path,
                        UIntPtr.Zero,
                        out counter);
                    if (addStatus == 0 && counter != IntPtr.Zero)
                    {
                        selectedPath = path;
                        break;
                    }
                }

                if (counter == IntPtr.Zero)
                {
                    unavailableMetricIds.Add($"{definition.MetricId} (0x{addStatus:x8})");
                    continue;
                }

                activeCounters.Add(new ActivePdhCounter(
                    definition,
                    selectedPath,
                    counter,
                    []));
            }

            if (activeCounters.Count == 0)
            {
                return new ProbeResult<PdhSystemHealthSnapshot>(
                    new PdhSystemHealthSnapshot(requestedSamples, sampleInterval, []),
                    new SystemHealthCoverage(
                        "pdh",
                        SystemHealthCoverageState.Unavailable,
                        $"No requested counters were available: {string.Join(", ", unavailableMetricIds)}."));
            }

            var baselineStatus = PdhCollectQueryData(query);
            if (baselineStatus != 0)
            {
                return new ProbeResult<PdhSystemHealthSnapshot>(
                    new PdhSystemHealthSnapshot(requestedSamples, sampleInterval, []),
                    new SystemHealthCoverage(
                        "pdh",
                        SystemHealthCoverageState.Failed,
                        $"Initial PdhCollectQueryData failed with 0x{baselineStatus:x8}."));
            }

            var completedIntervals = 0;
            var alignedSamples = new List<SystemHealthPdhSample>(requestedSamples);
            for (var sampleIndex = 0; sampleIndex < requestedSamples; sampleIndex++)
            {
                await Task.Delay(sampleInterval, cancellationToken).ConfigureAwait(false);
                var collectStatus = PdhCollectQueryData(query);
                if (collectStatus != 0)
                {
                    continue;
                }

                completedIntervals++;
                var alignedValues = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var counter in activeCounters)
                {
                    var valueStatus = PdhGetFormattedCounterValue(
                        counter.Handle,
                        PdhFormatDouble,
                        out _,
                        out var value);
                    if (valueStatus != 0 ||
                        value.CStatus is not (PdhValidData or PdhNewData) ||
                        double.IsNaN(value.DoubleValue) ||
                        double.IsInfinity(value.DoubleValue))
                    {
                        continue;
                    }

                    var scaledValue = value.DoubleValue * counter.Definition.Scale;
                    counter.Values.Add(scaledValue);
                    alignedValues[counter.Definition.MetricId] = scaledValue;
                }

                alignedSamples.Add(new SystemHealthPdhSample(
                    DateTimeOffset.UtcNow,
                    alignedValues));
            }

            var metrics = activeCounters
                .Where(counter => counter.Values.Count > 0)
                .Select(counter => Summarize(counter.Definition, counter.Values))
                .ToArray();
            var missingSamples = activeCounters
                .Where(counter => counter.Values.Count == 0)
                .Select(counter => counter.Definition.MetricId)
                .ToArray();
            var complete = unavailableMetricIds.Count == 0 &&
                           missingSamples.Length == 0 &&
                           completedIntervals == requestedSamples;
            var details = new List<string>
            {
                $"{metrics.Length}/{PdhDefinitions.Length} metrics produced values",
                $"{completedIntervals}/{requestedSamples} collection intervals completed"
            };
            if (unavailableMetricIds.Count > 0)
            {
                details.Add($"unavailable: {string.Join(", ", unavailableMetricIds)}");
            }
            if (missingSamples.Length > 0)
            {
                details.Add($"no valid samples: {string.Join(", ", missingSamples)}");
            }

            return new ProbeResult<PdhSystemHealthSnapshot>(
                new PdhSystemHealthSnapshot(requestedSamples, sampleInterval, metrics)
                {
                    Samples = alignedSamples
                },
                new SystemHealthCoverage(
                    "pdh",
                    complete ? SystemHealthCoverageState.Complete : SystemHealthCoverageState.Partial,
                    string.Join("; ", details) + "."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProbeResult<PdhSystemHealthSnapshot>(
                new PdhSystemHealthSnapshot(requestedSamples, sampleInterval, []),
                FailedCoverage("pdh", exception));
        }
        finally
        {
            if (query != IntPtr.Zero)
            {
                _ = PdhCloseQuery(query);
            }
        }
    }

    private static ProbeResult<IReadOnlyList<DriveSpaceHealthSnapshot>> CollectDrives()
    {
        var snapshots = new List<DriveSpaceHealthSnapshot>();
        var failures = new List<string>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    var total = drive.TotalSize;
                    var available = drive.AvailableFreeSpace;
                    snapshots.Add(new DriveSpaceHealthSnapshot(
                        drive.RootDirectory.FullName,
                        drive.DriveType,
                        drive.DriveFormat,
                        total,
                        available,
                        total <= 0 ? 0 : Math.Round(available * 100d / total, 2)));
                }
                catch (Exception exception) when (
                    exception is IOException or
                    UnauthorizedAccessException)
                {
                    failures.Add($"{drive.Name}: {exception.GetType().Name}");
                }
            }

            var state = failures.Count == 0
                ? snapshots.Count > 0
                    ? SystemHealthCoverageState.Complete
                    : SystemHealthCoverageState.Unavailable
                : SystemHealthCoverageState.Partial;
            var detail = snapshots.Count == 0
                ? "No ready fixed drives were readable."
                : $"{snapshots.Count} fixed drive(s) read.";
            if (failures.Count > 0)
            {
                detail += $" Failures: {string.Join(", ", failures)}.";
            }

            return new ProbeResult<IReadOnlyList<DriveSpaceHealthSnapshot>>(
                snapshots,
                new SystemHealthCoverage("drive-space", state, detail));
        }
        catch (Exception exception)
        {
            return new ProbeResult<IReadOnlyList<DriveSpaceHealthSnapshot>>(
                snapshots,
                FailedCoverage("drive-space", exception));
        }
    }

    private static ProbeResult<IReadOnlyList<PoolTagSnapshot>> CollectKernelPoolTags()
    {
        try
        {
            var tags = KernelPoolTagProbe.ReadTop(32);
            return new ProbeResult<IReadOnlyList<PoolTagSnapshot>>(
                tags,
                new SystemHealthCoverage(
                    "kernel-pool-tags",
                    SystemHealthCoverageState.Complete,
                    $"{tags.Count} kernel pool tag row(s) read."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<IReadOnlyList<PoolTagSnapshot>>(
                [],
                FailedCoverage("kernel-pool-tags", exception));
        }
    }

    private static ProbeResult<IReadOnlyList<WindowResponsivenessSnapshot>>
        CollectWindowResponsiveness()
    {
        try
        {
            var windows = WindowResponsivenessProbe.Read();
            return new ProbeResult<IReadOnlyList<WindowResponsivenessSnapshot>>(
                windows,
                new SystemHealthCoverage(
                    "window-responsiveness",
                    SystemHealthCoverageState.Complete,
                    $"{windows.Sum(item => item.VisibleWindowCount)} visible, uncloaked, user-facing top-level window(s) checked; {windows.Sum(item => item.HungWindowCount)} met the Windows hung-window heuristic."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<IReadOnlyList<WindowResponsivenessSnapshot>>(
                [],
                FailedCoverage("window-responsiveness", exception));
        }
    }

    private static ProbeResult<UserIdleObservation?> CollectUserIdleObservation()
    {
        try
        {
            var input = new LastInputInfo
            {
                Size = (uint)Marshal.SizeOf<LastInputInfo>()
            };
            if (!GetLastInputInfo(ref input))
            {
                return new ProbeResult<UserIdleObservation?>(
                    null,
                    new SystemHealthCoverage(
                        "user-idle",
                        SystemHealthCoverageState.Unavailable,
                        $"GetLastInputInfo failed with Win32 error {Marshal.GetLastWin32Error()}."));
            }

            var currentTick = unchecked((uint)Environment.TickCount64);
            var idleMilliseconds = unchecked(currentTick - input.LastInputTick);
            var idle = TimeSpan.FromMilliseconds(idleMilliseconds);
            return new ProbeResult<UserIdleObservation?>(
                new UserIdleObservation(
                    idle,
                    DateTimeOffset.Now - idle,
                    input.LastInputTick),
                new SystemHealthCoverage(
                    "user-idle",
                    SystemHealthCoverageState.Complete,
                    "Interactive-session idle duration read."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<UserIdleObservation?>(
                null,
                FailedCoverage("user-idle", exception));
        }
    }

    private static ProbeResult<UserIdleHealthSnapshot?> CombineUserIdleObservations(
        ProbeResult<UserIdleObservation?> start,
        ProbeResult<UserIdleObservation?> end)
    {
        if (start.Value is null || end.Value is null)
        {
            var detail = $"Sampling-window idle boundary unavailable. Start: {start.Coverage.Detail} End: {end.Coverage.Detail}";
            return new ProbeResult<UserIdleHealthSnapshot?>(
                null,
                new SystemHealthCoverage(
                    "user-idle",
                    SystemHealthCoverageState.Unavailable,
                    detail));
        }

        var inputObserved = start.Value.LastInputTick != end.Value.LastInputTick;
        return new ProbeResult<UserIdleHealthSnapshot?>(
            new UserIdleHealthSnapshot(
                start.Value.IdleTime,
                end.Value.IdleTime,
                end.Value.EstimatedLastInputAt,
                inputObserved,
                start.Value.LastInputTick,
                end.Value.LastInputTick),
            new SystemHealthCoverage(
                "user-idle",
                SystemHealthCoverageState.Complete,
                inputObserved
                    ? $"Input was observed during the performance sampling window (start idle {start.Value.IdleTime.TotalSeconds:n1}s; end idle {end.Value.IdleTime.TotalSeconds:n1}s)."
                    : $"No input was observed between both sampling-window boundaries (start idle {start.Value.IdleTime.TotalSeconds:n1}s; end idle {end.Value.IdleTime.TotalSeconds:n1}s)."));
    }

    private static ProbeResult<PowerPlanHealthSnapshot?> CollectPowerPlan()
    {
        try
        {
            var activePlan = WindowsNative.ReadActivePowerPlan();
            var guid = activePlan.SchemeGuid.ToString("D");
            var displayName = activePlan.FriendlyName.Trim();
            var state = displayName.Length > 0
                ? SystemHealthCoverageState.Complete
                : SystemHealthCoverageState.Partial;
            var summary = displayName.Length > 0
                ? $"{guid} ({displayName})"
                : guid;
            return new ProbeResult<PowerPlanHealthSnapshot?>(
                new PowerPlanHealthSnapshot(guid, displayName, summary),
                new SystemHealthCoverage(
                    "power-plan",
                    state,
                    displayName.Length > 0
                        ? "Active power scheme GUID and localized friendly name read through PowrProf."
                        : $"Active scheme GUID read; friendly name unavailable (0x{activePlan.FriendlyNameStatus:x8})."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<PowerPlanHealthSnapshot?>(
                null,
                FailedCoverage("power-plan", exception));
        }
    }

    private static ProbeResult<PendingRebootHealthSnapshot?> CollectPendingReboot()
    {
        var reasons = new List<string>();
        var checks = 0;
        var failures = 0;
        try
        {
            CheckRegistryKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                "Component Based Servicing reboot pending",
                reasons,
                ref checks,
                ref failures);
            CheckRegistryKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                "Windows Update reboot required",
                reasons,
                ref checks,
                ref failures);
            CheckRegistryValue(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                @"SYSTEM\CurrentControlSet\Control\Session Manager",
                "PendingFileRenameOperations",
                "Pending file rename operations",
                reasons,
                ref checks,
                ref failures);
            CheckNonZeroRegistryValue(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                @"SOFTWARE\Microsoft\Updates",
                "UpdateExeVolatile",
                "Update executable reports a pending reboot",
                reasons,
                ref checks,
                ref failures);

            var state = failures == 0
                ? SystemHealthCoverageState.Complete
                : checks > 0
                    ? SystemHealthCoverageState.Partial
                    : SystemHealthCoverageState.Unavailable;
            return new ProbeResult<PendingRebootHealthSnapshot?>(
                new PendingRebootHealthSnapshot(reasons.Count > 0, reasons),
                new SystemHealthCoverage(
                    "pending-reboot",
                    state,
                    $"{checks} registry checks completed; {failures} failed."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<PendingRebootHealthSnapshot?>(
                null,
                FailedCoverage("pending-reboot", exception));
        }
    }

    private static ProbeResult<StartupHealthSnapshot?> CollectStartupItems()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var checks = 0;
        var failures = 0;
        try
        {
            var views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : [RegistryView.Registry32];
            foreach (var view in views)
            {
                CountStartupRegistryKey(
                    RegistryHive.LocalMachine,
                    view,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                    $"HKLM Run ({view})",
                    counts,
                    ref checks,
                    ref failures);
                CountStartupRegistryKey(
                    RegistryHive.LocalMachine,
                    view,
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                    $"HKLM RunOnce ({view})",
                    counts,
                    ref checks,
                    ref failures);
            }

            CountStartupRegistryKey(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                "HKCU Run",
                counts,
                ref checks,
                ref failures);
            CountStartupRegistryKey(
                RegistryHive.CurrentUser,
                RegistryView.Default,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                "HKCU RunOnce",
                counts,
                ref checks,
                ref failures);
            CountStartupFolder(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "User Startup folder",
                counts,
                ref checks,
                ref failures);
            CountStartupFolder(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                "Common Startup folder",
                counts,
                ref checks,
                ref failures);

            var state = failures == 0
                ? SystemHealthCoverageState.Complete
                : checks > 0
                    ? SystemHealthCoverageState.Partial
                    : SystemHealthCoverageState.Unavailable;
            return new ProbeResult<StartupHealthSnapshot?>(
                new StartupHealthSnapshot(counts.Values.Sum(), counts),
                new SystemHealthCoverage(
                    "startup-items",
                    state,
                    $"{checks} startup sources read; {failures} failed."));
        }
        catch (Exception exception)
        {
            return new ProbeResult<StartupHealthSnapshot?>(
                null,
                FailedCoverage("startup-items", exception));
        }
    }

    private static async Task<ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>> CollectNvidiaGpusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var nvidiaSmiPath = NvidiaSmiCandidates()
                .FirstOrDefault(IsTrustedCandidateFile);
            if (nvidiaSmiPath is null)
            {
                return new ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>(
                    [],
                    UnavailableCoverage(
                        "nvidia-gpu",
                        "nvidia-smi.exe was not found in trusted System32 or NVIDIA NVSMI locations."));
            }

            var command = await RunExternalAsync(
                nvidiaSmiPath,
                [
                    "--query-gpu=index,name,uuid,driver_version,utilization.gpu,utilization.memory,memory.total,memory.used,temperature.gpu",
                    "--format=csv,noheader,nounits"
                ],
                TimeSpan.FromSeconds(10),
                ExternalOutputLimitChars,
                cancellationToken).ConfigureAwait(false);
            if (command.TimedOut)
            {
                return new ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>(
                    [],
                    UnavailableCoverage("nvidia-gpu", "nvidia-smi.exe timed out."));
            }
            if (command.ExitCode != 0)
            {
                return new ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>(
                    [],
                    new SystemHealthCoverage(
                        "nvidia-gpu",
                        SystemHealthCoverageState.Unavailable,
                        $"nvidia-smi.exe exited with {command.ExitCode}: {OneLine(command.Error)}"));
            }

            var gpus = new List<NvidiaGpuHealthSnapshot>();
            var rejectedRows = 0;
            foreach (var line in command.Output
                         .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = ParseCsvLine(line);
                if (fields.Count < 9 ||
                    !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    rejectedRows++;
                    continue;
                }

                gpus.Add(new NvidiaGpuHealthSnapshot(
                    index,
                    fields[1],
                    fields[2],
                    fields[3],
                    ParseNullableDouble(fields[4]),
                    ParseNullableDouble(fields[5]),
                    ParseNullableLong(fields[6]),
                    ParseNullableLong(fields[7]),
                    ParseNullableDouble(fields[8])));
            }

            var complete = gpus.Count > 0 &&
                           rejectedRows == 0 &&
                           !command.OutputTruncated;
            return new ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>(
                gpus,
                new SystemHealthCoverage(
                    "nvidia-gpu",
                    complete
                        ? SystemHealthCoverageState.Complete
                        : gpus.Count > 0
                            ? SystemHealthCoverageState.Partial
                            : SystemHealthCoverageState.Unavailable,
                    $"{gpus.Count} NVIDIA GPU row(s) parsed; {rejectedRows} rejected."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProbeResult<IReadOnlyList<NvidiaGpuHealthSnapshot>>(
                [],
                FailedCoverage("nvidia-gpu", exception));
        }
    }

    private static async Task<ProbeResult<ReliabilityHealthSnapshot>> CollectReliabilityEventsAsync(
        CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromDays(7);
        var empty = new ReliabilityHealthSnapshot(window, 0, false, []);
        try
        {
            var wevtutilPath = TrustedSystemBinary("wevtutil.exe");
            if (wevtutilPath is null)
            {
                return new ProbeResult<ReliabilityHealthSnapshot>(
                    empty,
                    UnavailableCoverage(
                        "reliability-events",
                        "Trusted System32 wevtutil.exe was not found."));
            }

            const string systemQuery =
                "*[System[TimeCreated[timediff(@SystemTime) <= 604800000] and " +
                "((Level=1 or Level=2 or Level=3) or " +
                "EventID=41 or EventID=2004 or EventID=4101 or EventID=6008) and (" +
                "Provider[@Name='Microsoft-Windows-WHEA-Logger'] or " +
                "Provider[@Name='disk'] or " +
                "Provider[@Name='stornvme'] or " +
                "Provider[@Name='storahci'] or " +
                "Provider[@Name='storport'] or " +
                "Provider[@Name='Ntfs'] or " +
                "Provider[@Name='Microsoft-Windows-Ntfs'] or " +
                "Provider[@Name='volmgr'] or " +
                "Provider[@Name='Display'] or " +
                "Provider[@Name='nvlddmkm'] or " +
                "Provider[@Name='amdkmdag'] or " +
                "Provider[@Name='igfx'] or " +
                "Provider[@Name='igfxCUIService2.0.0.0'] or " +
                "Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] or " +
                "Provider[@Name='Microsoft-Windows-Kernel-Power'] or " +
                "Provider[@Name='EventLog'])]]";
            const string applicationQuery =
                "*[System[TimeCreated[timediff(@SystemTime) <= 604800000] and " +
                "((Level=1 or Level=2 or Level=3) or " +
                "EventID=1000 or EventID=1001 or EventID=1002 or EventID=1026) and (" +
                "Provider[@Name='Application Hang'] or " +
                "Provider[@Name='Application Error'] or " +
                "Provider[@Name='Windows Error Reporting'] or " +
                "Provider[@Name='.NET Runtime'])]]";

            var systemTask = QueryEventLogAsync(
                wevtutilPath,
                "System",
                systemQuery,
                cancellationToken);
            var applicationTask = QueryEventLogAsync(
                wevtutilPath,
                "Application",
                applicationQuery,
                cancellationToken);
            await Task.WhenAll(systemTask, applicationTask).ConfigureAwait(false);
            var results = new[]
            {
                await systemTask.ConfigureAwait(false),
                await applicationTask.ConfigureAwait(false)
            };

            var events = results.SelectMany(result => result.Events).ToArray();
            var recentCutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
            var groups = events
                .GroupBy(item => new { item.LogName, item.Provider, item.EventId, item.Level })
                .Select(group => new ReliabilityEventGroup(
                    group.Key.LogName,
                    group.Key.Provider,
                    group.Key.EventId,
                    group.Key.Level,
                    group.Count(),
                    group.Count(item =>
                        item.CreatedAtUtc is { } createdAt &&
                        createdAt >= recentCutoff),
                    group.Max(item => item.CreatedAtUtc)))
                .OrderByDescending(group => group.LatestAtUtc)
                .ThenByDescending(group => group.Count)
                .ToArray();
            var mayBeTruncated = results.Any(result => result.MayBeTruncated);
            var failures = results.Where(result => result.Error.Length > 0).ToArray();
            var state = failures.Length == 0 && !mayBeTruncated
                ? SystemHealthCoverageState.Complete
                : events.Length > 0 || failures.Length < results.Length
                    ? SystemHealthCoverageState.Partial
                    : SystemHealthCoverageState.Unavailable;
            var detail = $"{events.Length} stability event(s) read from the last seven days.";
            if (failures.Length > 0)
            {
                detail += " " + string.Join(
                    " ",
                    failures.Select(result => $"{result.LogName}: {result.Error}"));
            }
            if (mayBeTruncated)
            {
                detail += " Results may be capped or output-limited.";
            }

            return new ProbeResult<ReliabilityHealthSnapshot>(
                new ReliabilityHealthSnapshot(window, events.Length, mayBeTruncated, groups),
                new SystemHealthCoverage("reliability-events", state, detail));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new ProbeResult<ReliabilityHealthSnapshot>(
                empty,
                FailedCoverage("reliability-events", exception));
        }
    }

    private static async Task<EventQueryResult> QueryEventLogAsync(
        string wevtutilPath,
        string logName,
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            const int eventLimit = 256;
            var command = await RunExternalAsync(
                wevtutilPath,
                [
                    "qe",
                    logName,
                    $"/q:{query}",
                    $"/c:{eventLimit}",
                    "/rd:true",
                    "/f:xml"
                ],
                TimeSpan.FromSeconds(15),
                EventOutputLimitChars,
                cancellationToken).ConfigureAwait(false);
            if (command.TimedOut)
            {
                return new EventQueryResult(logName, [], true, "wevtutil.exe timed out.");
            }
            if (command.ExitCode != 0)
            {
                return new EventQueryResult(
                    logName,
                    [],
                    command.OutputTruncated || command.ErrorTruncated,
                    $"wevtutil.exe exited with {command.ExitCode}: {OneLine(command.Error)}");
            }

            var events = ParseEventXml(logName, command.Output);
            return new EventQueryResult(
                logName,
                events,
                command.OutputTruncated || events.Count >= eventLimit,
                "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new EventQueryResult(
                logName,
                [],
                false,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static IReadOnlyList<ReliabilityEvent> ParseEventXml(
        string logName,
        string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var withoutDeclarations = XmlDeclarationPattern.Replace(output, "");
        var document = XDocument.Parse(
            $"<Events>{withoutDeclarations}</Events>",
            LoadOptions.None);
        XNamespace eventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";
        var events = new List<ReliabilityEvent>();
        foreach (var element in document.Root?.Elements(eventNamespace + "Event") ?? [])
        {
            var system = element.Element(eventNamespace + "System");
            if (system is null)
            {
                continue;
            }

            var provider = system
                .Element(eventNamespace + "Provider")?
                .Attribute("Name")?
                .Value ?? "";
            _ = int.TryParse(
                system.Element(eventNamespace + "EventID")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var eventId);
            _ = int.TryParse(
                system.Element(eventNamespace + "Level")?.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var level);
            DateTimeOffset? createdAt = null;
            var timestamp = system
                .Element(eventNamespace + "TimeCreated")?
                .Attribute("SystemTime")?
                .Value;
            if (DateTimeOffset.TryParse(
                    timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsedTimestamp))
            {
                createdAt = parsedTimestamp;
            }

            events.Add(new ReliabilityEvent(
                logName,
                provider,
                eventId,
                level,
                createdAt));
        }

        return events;
    }

    private static async Task<ExternalCommandResult> RunExternalAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int outputLimitChars,
        CancellationToken cancellationToken)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!Path.IsPathFullyQualified(executablePath) || !IsTrustedCandidateFile(executablePath))
        {
            throw new FileNotFoundException(
                "The trusted absolute executable path is unavailable.",
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {executablePath}.");
        }

        var stdoutTask = ReadLimitedAsync(process.StandardOutput, outputLimitChars);
        var stderrTask = ReadLimitedAsync(process.StandardError, outputLimitChars);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        if (timedOut)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is TimeoutException or
                InvalidOperationException)
            {
                // Disposal closes redirected streams if the process resists termination.
            }
        }

        LimitedText stdout;
        LimitedText stderr;
        try
        {
            stdout = await stdoutTask
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stdout = new LimitedText("", true);
        }
        try
        {
            stderr = await stderrTask
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            stderr = new LimitedText("", true);
        }

        return new ExternalCommandResult(
            timedOut ? -1 : process.ExitCode,
            timedOut,
            stdout.Value,
            stderr.Value,
            stdout.Truncated,
            stderr.Truncated);
    }

    private static async Task<LimitedText> ReadLimitedAsync(
        StreamReader reader,
        int characterLimit)
    {
        var builder = new StringBuilder(Math.Min(characterLimit, 16 * 1024));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = characterLimit - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }
            if (read > Math.Max(0, remaining))
            {
                truncated = true;
            }
        }

        return new LimitedText(builder.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            // The process may have exited while the timeout path was running.
        }
    }

    private static SystemHealthMetricSummary Summarize(
        PdhCounterDefinition definition,
        IReadOnlyList<double> values)
    {
        var ordered = values.Order().ToArray();
        var p95Index = Math.Clamp(
            (int)Math.Ceiling(ordered.Length * 0.95) - 1,
            0,
            ordered.Length - 1);
        return new SystemHealthMetricSummary(
            definition.MetricId,
            definition.Unit,
            values.Count,
            Math.Round(values.Average(), 3),
            Math.Round(ordered[p95Index], 3),
            Math.Round(ordered[^1], 3),
            Math.Round(values[^1], 3));
    }

    private static void CheckRegistryKey(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string reason,
        ICollection<string> reasons,
        ref int checks,
        ref int failures)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            checks++;
            if (key is not null)
            {
                reasons.Add(reason);
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            failures++;
        }
    }

    private static void CheckRegistryValue(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName,
        string reason,
        ICollection<string> reasons,
        ref int checks,
        ref int failures)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            checks++;
            if (value is string[] strings && strings.Length > 0 ||
                value is string text && text.Length > 0 ||
                value is not null and not string and not string[])
            {
                reasons.Add(reason);
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            failures++;
        }
    }

    private static void CheckNonZeroRegistryValue(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string valueName,
        string reason,
        ICollection<string> reasons,
        ref int checks,
        ref int failures)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            var value = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            checks++;
            if (value is not null &&
                long.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var number) &&
                number != 0)
            {
                reasons.Add(reason);
            }
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            failures++;
        }
    }

    private static void CountStartupRegistryKey(
        RegistryHive hive,
        RegistryView view,
        string subKey,
        string source,
        IDictionary<string, int> counts,
        ref int checks,
        ref int failures)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            counts[source] = key?.GetValueNames().Length ?? 0;
            checks++;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            failures++;
        }
    }

    private static void CountStartupFolder(
        string folder,
        string source,
        IDictionary<string, int> counts,
        ref int checks,
        ref int failures)
    {
        try
        {
            counts[source] = string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
                ? 0
                : Directory.EnumerateFileSystemEntries(
                        folder,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Count();
            checks++;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException)
        {
            failures++;
        }
    }

    private static string? TrustedSystemBinary(string fileName)
    {
        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            !Path.IsPathFullyQualified(systemDirectory))
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(systemDirectory, fileName));
        var normalizedSystemDirectory =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(systemDirectory)) +
            Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(normalizedSystemDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return IsTrustedCandidateFile(candidate) ? candidate : null;
    }

    private static IEnumerable<string> NvidiaSmiCandidates()
    {
        var candidates = new List<string>();
        var systemDirectory = Environment.SystemDirectory;
        if (!string.IsNullOrWhiteSpace(systemDirectory) &&
            Path.IsPathFullyQualified(systemDirectory))
        {
            candidates.Add(Path.Combine(systemDirectory, "nvidia-smi.exe"));
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramW6432") ?? ""
                 })
        {
            if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root))
            {
                candidates.Add(Path.Combine(
                    root,
                    "NVIDIA Corporation",
                    "NVSMI",
                    "nvidia-smi.exe"));
            }
        }

        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsTrustedCandidateFile(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                return false;
            }

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
                continue;
            }

            if (character == ',' && !quoted)
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static long? ParseNullableLong(string value)
    {
        return long.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static string OneLine(string value)
    {
        return string.Join(
                " ",
                value.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Trim();
    }

    private static SystemHealthCoverage FailedCoverage(
        string component,
        Exception exception)
    {
        return new SystemHealthCoverage(
            component,
            SystemHealthCoverageState.Failed,
            $"{exception.GetType().Name}: {exception.Message}");
    }

    private static SystemHealthCoverage UnavailableCoverage(
        string component,
        string detail)
    {
        return new SystemHealthCoverage(
            component,
            SystemHealthCoverageState.Unavailable,
            detail);
    }

    private static SystemHealthProbeSnapshot UnsupportedPlatformSnapshot(
        int pdhSamples,
        TimeSpan interval)
    {
        var components = new[]
        {
            "pdh",
            "drive-space",
            "user-idle",
            "power-plan",
            "pending-reboot",
            "startup-items",
            "nvidia-gpu",
            "reliability-events",
            "kernel-pool-tags",
            "window-responsiveness"
        };
        return new SystemHealthProbeSnapshot(
            DateTimeOffset.UtcNow,
            new PdhSystemHealthSnapshot(pdhSamples, interval, []),
            [],
            null,
            null,
            null,
            null,
            [],
            new ReliabilityHealthSnapshot(TimeSpan.FromDays(7), 0, false, []),
            components
                .Select(component => UnavailableCoverage(
                    component,
                    "The probe supports Windows only."))
                .ToArray());
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        UIntPtr userData,
        out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        IntPtr query,
        string fullCounterPath,
        UIntPtr userData,
        out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        IntPtr counter,
        uint format,
        out uint type,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormattedCounterValue
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint LastInputTick;
    }

    private sealed record PdhCounterDefinition(
        string MetricId,
        string Unit,
        double Scale,
        IReadOnlyList<string> Paths);

    private sealed record ActivePdhCounter(
        PdhCounterDefinition Definition,
        string SelectedPath,
        IntPtr Handle,
        List<double> Values);

    private sealed record UserIdleObservation(
        TimeSpan IdleTime,
        DateTimeOffset EstimatedLastInputAt,
        uint LastInputTick);

    private sealed record ProbeResult<T>(
        T Value,
        SystemHealthCoverage Coverage);

    private sealed record LimitedText(
        string Value,
        bool Truncated);

    private sealed record ExternalCommandResult(
        int ExitCode,
        bool TimedOut,
        string Output,
        string Error,
        bool OutputTruncated,
        bool ErrorTruncated);

    private sealed record ReliabilityEvent(
        string LogName,
        string Provider,
        int EventId,
        int Level,
        DateTimeOffset? CreatedAtUtc);

    private sealed record EventQueryResult(
        string LogName,
        IReadOnlyList<ReliabilityEvent> Events,
        bool MayBeTruncated,
        string Error);
}
