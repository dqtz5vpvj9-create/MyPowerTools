using LocalLagCleaner.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerMeasurementTests
{
    [Fact]
    public void Report_marks_missing_critical_measurements_as_uncollected()
    {
        var snapshot = EmptySnapshot() with
        {
            Coverage =
            [
                new ProbeCoverage(
                    "pdh",
                    MeasurementStatus.Unavailable,
                    "Performance counters unavailable."),
                new ProbeCoverage(
                    "user-idle",
                    MeasurementStatus.Unavailable,
                    "Idle boundaries unavailable.")
            ]
        };

        var markdown = LagReportWriter.ToMarkdown(snapshot);

        Assert.Contains("系统上下文：未采集完整", markdown, StringComparison.Ordinal);
        Assert.Contains("关键性能计数器未采集完整", markdown, StringComparison.Ordinal);
        Assert.Contains("不可用", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_prints_cpu_distribution_and_read_write_semantics()
    {
        var snapshot = EmptySnapshot() with
        {
            Signals = new SystemSignalSummary(
                12,
                44,
                31,
                0.5,
                2,
                0.4,
                1.2,
                0,
                0,
                5,
                8,
                0.3,
                0.8,
                1,
                1024),
            SystemContext = new SystemContextSnapshot(
                120,
                true,
                "高性能",
                "交流电",
                5,
                false,
                "Windows")
        };

        var markdown = LagReportWriter.ToMarkdown(snapshot);

        Assert.Contains(
            "CPU 统计：平均 12.0%，P95 31.0%，峰值 44.0%",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("进程读写前列", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_prints_system_handle_type_breakdown()
    {
        var snapshot = EmptySnapshot() with
        {
            SystemHandleTypes =
            [
                new HandleTypeSnapshot(
                    16,
                    "Event",
                    900_000,
                    1_100_000,
                    75),
                new HandleTypeSnapshot(
                    37,
                    "File",
                    200_000,
                    500_000,
                    16.67)
            ],
            SystemFileHandleAccess =
            [
                new FileHandleAccessSnapshot(
                    0x00100080,
                    "ReadAttributes+Synchronize",
                    800_000,
                    88.89)
            ],
            SystemFileAttribution = new SystemFileHandleAttribution(
                900_000,
                512,
                512,
                512,
                480,
                false,
                "Uniform path sample completed."),
            SystemFilePathGroups =
            [
                new FileHandlePathGroupSnapshot(
                    @"\Device\HarddiskVolume3\Windows",
                    "Disk",
                    400,
                    83.33,
                    [@"\Device\HarddiskVolume3\Windows\System32\file.dll"])
            ],
            FileSystemFilters =
            [
                new FileSystemFilterSnapshot(
                    "wcifs",
                    "Windows Container Isolation FS Filter Driver",
                    "FSFilter Virtualization",
                    "",
                    @"C:\Windows\System32\drivers\wcifs.sys",
                    "Microsoft Corporation",
                    "10.0",
                    true,
                    true,
                    "强相关候选",
                    "WC* Pool Tag 1.5 GB")
            ]
        };

        var markdown = LagReportWriter.ToMarkdown(snapshot);

        Assert.Contains("System 句柄类型拆分", markdown, StringComparison.Ordinal);
        Assert.Contains("| Event | 16 | 900,000 | 75.00% | 1,100,000 |", markdown, StringComparison.Ordinal);
        Assert.Contains("具体创建栈仍需增长对比或 ETW/驱动级跟踪", markdown, StringComparison.Ordinal);
        Assert.Contains("System File 访问模式", markdown, StringComparison.Ordinal);
        Assert.Contains("ReadAttributes+Synchronize", markdown, StringComparison.Ordinal);
        Assert.Contains("System File 路径归因", markdown, StringComparison.Ordinal);
        Assert.Contains(@"\Device\HarddiskVolume3\Windows", markdown, StringComparison.Ordinal);
        Assert.Contains("文件系统过滤驱动来源候选", markdown, StringComparison.Ordinal);
        Assert.Contains("wcifs", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_prints_process_breakdown_and_explicit_remediation()
    {
        var snapshot = EmptySnapshot() with
        {
            ProcessCount = 609,
            ThreadCount = 11_153,
            ProcessBreakdown =
            [
                new ProcessBreakdownSnapshot(
                    "svchost",
                    "Delivery Optimization",
                    12,
                    240,
                    512UL * 1024 * 1024,
                    768UL * 1024 * 1024,
                    4_800,
                    2.5,
                    [120, 121])
            ],
            Findings =
            [
                new LagFinding(
                    LagSeverity.Critical,
                    "process-count-critical",
                    "后台进程数量严重异常",
                    "数量前列：svchost ×12。",
                    "按进程拆分核对来源。",
                    false,
                    "")
                {
                    Domain = DiagnosticDomain.BackgroundProcesses,
                    CausalChain = "进程总数超过阈值 → 进程族聚合 → 按服务归属核查"
                }
            ]
        };

        var markdown = LagReportWriter.ToMarkdown(snapshot);

        Assert.Contains("后台进程拆分", markdown, StringComparison.Ordinal);
        Assert.Contains("| svchost | Delivery Optimization | 12 | 240 |", markdown, StringComparison.Ordinal);
        Assert.Contains("处理方法：按进程拆分核对来源。", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_scan_retains_every_complete_aligned_signal_sample()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = new LagCleanerOptions
        {
            SampleSeconds = 3,
            SampleIntervalMilliseconds = 1_000,
            ProcessWarningCount = 50,
            ProcessCriticalCount = 10_000
        };
        var snapshot = await new LagDiagnosticsEngine().ScanAsync(options);
        Assert.NotEmpty(snapshot.ProcessBreakdown);
        Assert.All(
            snapshot.ProcessBreakdown,
            item =>
            {
                Assert.True(item.ProcessCount > 0);
                Assert.False(string.IsNullOrWhiteSpace(item.Name));
                Assert.NotEmpty(item.SampleProcessIds);
            });
        if (snapshot.ProcessCount >= options.Normalize().ProcessWarningCount)
        {
            var processFinding = Assert.Single(
                snapshot.Findings,
                item => item.Code is "process-count-critical" or "process-count-warning");
            Assert.Contains("数量最多的进程组", processFinding.Evidence, StringComparison.Ordinal);
            Assert.Contains("按进程族", processFinding.CausalChain, StringComparison.Ordinal);
            Assert.Contains("进程拆分", processFinding.Recommendation, StringComparison.Ordinal);
        }
        var pdh = Assert.Single(snapshot.Coverage, item => item.Probe == "pdh");
        if (pdh.Status == MeasurementStatus.Unavailable)
        {
            Assert.Null(snapshot.Signals);
            Assert.Empty(snapshot.SignalSamples);
            return;
        }

        Assert.NotNull(snapshot.Signals);
        Assert.True(
            snapshot.SignalSamples.Count >= 3,
            $"Expected at least three complete aligned samples, got {snapshot.SignalSamples.Count}.");
        Assert.Equal(
            snapshot.SignalSamples.OrderBy(item => item.CapturedAtUtc),
            snapshot.SignalSamples);

        var handleCoverage = Assert.Single(
            snapshot.Coverage,
            item => item.Probe == "system-handle-types");
        if (snapshot.SystemHandleCount >= 100_000)
        {
            Assert.NotEqual(MeasurementStatus.Unavailable, handleCoverage.Status);
            Assert.NotEmpty(snapshot.SystemHandleTypes);
            Assert.All(
                snapshot.SystemHandleTypes,
                item =>
                {
                    Assert.False(string.IsNullOrWhiteSpace(item.TypeName));
                    Assert.True(item.SystemHandleCount > 0);
                });
            Assert.InRange(
                snapshot.SystemHandleTypes.Sum(item => item.SystemSharePercent),
                99,
                101);
            var fileType = Assert.Single(
                snapshot.SystemHandleTypes,
                item => item.TypeName == "File");
            Assert.NotEmpty(snapshot.SystemFileHandleAccess);
            Assert.Equal(
                fileType.SystemHandleCount,
                snapshot.SystemFileHandleAccess.Aggregate(
                    0UL,
                    (sum, item) => sum + item.HandleCount));
            Assert.NotNull(snapshot.SystemFileAttribution);
            Assert.NotEmpty(snapshot.FileSystemFilters);
            Assert.Contains(
                snapshot.FileSystemFilters,
                item => item.ServiceName.Equals(
                    "wcifs",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Single(
                snapshot.Coverage,
                item => item.Probe == "system-file-handle-paths");
            Assert.Single(
                snapshot.Coverage,
                item => item.Probe == "file-system-filters");
        }
        else
        {
            Assert.Empty(snapshot.SystemHandleTypes);
        }
    }

    private static LagDiagnosticSnapshot EmptySnapshot()
    {
        return new LagDiagnosticSnapshot(
            DateTimeOffset.Parse("2026-07-28T00:00:00Z"),
            5,
            8,
            1,
            64UL * 1024 * 1024 * 1024,
            16UL * 1024 * 1024 * 1024,
            25,
            20UL * 1024 * 1024 * 1024,
            128UL * 1024 * 1024 * 1024,
            15.625,
            0,
            128UL * 1024 * 1024,
            256UL * 1024 * 1024,
            384UL * 1024 * 1024,
            10_000,
            100_000,
            200,
            2_000,
            1,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            ["保留报告。"]);
    }
}
