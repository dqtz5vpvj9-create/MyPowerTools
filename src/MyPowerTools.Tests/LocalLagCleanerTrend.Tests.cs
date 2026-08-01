using LocalLagCleaner.MyPowerTools;

namespace MyPowerTools.Tests;

public sealed class LocalLagCleanerTrendTests
{
    private static readonly DateTimeOffset BootTime =
        DateTimeOffset.Parse("2026-07-28T00:00:00Z");

    [Fact]
    public void ApplyHistory_reports_monotonic_kernel_growth_across_four_samples()
    {
        var samples = Enumerable.Range(0, 4)
            .Select(index => Snapshot(
                index,
                100_000U + (uint)(index * 2_000),
                2UL * GiB + (ulong)index * 32 * MiB,
                isUserIdle: true))
            .ToArray();

        var analyzed = LagTrendAnalyzer.ApplyHistory(samples[^1], samples[..^1]);

        Assert.True(analyzed.Trend?.Available);
        var finding = Assert.Single(
            analyzed.Findings,
            item => item.Code == "kernel-growth-trend");
        Assert.Equal(LagSeverity.Warning, finding.Severity);
        Assert.Contains("4 个样本", analyzed.Trend!.Summary, StringComparison.Ordinal);
        Assert.Contains("3/3", analyzed.Trend.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyHistory_ignores_a_single_peak()
    {
        var handles = new[] { 100_000U, 100_000U, 900_000U, 100_000U };
        var kernels = new[] { 2UL * GiB, 2UL * GiB, 8UL * GiB, 2UL * GiB };
        var samples = Enumerable.Range(0, 4)
            .Select(index => Snapshot(
                index,
                handles[index],
                kernels[index],
                isUserIdle: true))
            .ToArray();

        var analyzed = LagTrendAnalyzer.ApplyHistory(samples[^1], samples[..^1]);

        Assert.True(analyzed.Trend?.Available);
        Assert.DoesNotContain(
            analyzed.Findings,
            item => item.Code == "kernel-growth-trend");
    }

    [Fact]
    public void ApplyHistory_filters_snapshots_from_another_boot()
    {
        var current = Snapshot(3, 200_000, 4UL * GiB, isUserIdle: true);
        var history = Enumerable.Range(0, 3)
            .Select(index => Snapshot(
                index,
                100_000U + (uint)(index * 50_000),
                2UL * GiB + (ulong)index * GiB,
                isUserIdle: true,
                bootTime: BootTime.AddHours(-1)))
            .ToArray();

        var analyzed = LagTrendAnalyzer.ApplyHistory(current, history);

        Assert.False(analyzed.Trend?.Available);
        Assert.DoesNotContain(
            analyzed.Findings,
            item => item.Code == "kernel-growth-trend");
    }

    [Fact]
    public void ApplyHistory_filters_snapshots_with_a_different_idle_state()
    {
        var current = Snapshot(3, 200_000, 4UL * GiB, isUserIdle: true);
        var history = Enumerable.Range(0, 3)
            .Select(index => Snapshot(
                index,
                100_000U + (uint)(index * 50_000),
                2UL * GiB + (ulong)index * GiB,
                isUserIdle: false))
            .ToArray();

        var analyzed = LagTrendAnalyzer.ApplyHistory(current, history);

        Assert.False(analyzed.Trend?.Available);
        Assert.DoesNotContain(
            analyzed.Findings,
            item => item.Code == "kernel-growth-trend");
    }

    private static LagDiagnosticSnapshot Snapshot(
        int fiveMinuteOffset,
        uint systemHandleCount,
        ulong kernelTotalBytes,
        bool isUserIdle,
        DateTimeOffset? bootTime = null)
    {
        var capturedAt = BootTime.AddHours(12).AddMinutes(fiveMinuteOffset * 5);
        var inferredBoot = bootTime ?? BootTime;
        return EmptySnapshot() with
        {
            CapturedAtUtc = capturedAt,
            SampleSeconds = 8,
            UptimeDays = (capturedAt - inferredBoot).TotalDays,
            SystemHandleCount = systemHandleCount,
            KernelPagedBytes = kernelTotalBytes / 2,
            KernelNonPagedBytes = kernelTotalBytes - kernelTotalBytes / 2,
            KernelTotalBytes = kernelTotalBytes,
            CommitTotalBytes = 20UL * GiB + (ulong)fiveMinuteOffset * 32 * MiB,
            ProcessCount = 200U + (uint)fiveMinuteOffset,
            SystemContext = new SystemContextSnapshot(
                isUserIdle ? 300 : 5,
                isUserIdle,
                "平衡",
                "交流电",
                5,
                false,
                "Windows")
        };
    }

    private static LagDiagnosticSnapshot EmptySnapshot()
    {
        return new LagDiagnosticSnapshot(
            BootTime,
            8,
            16,
            1,
            64UL * GiB,
            16UL * GiB,
            25,
            20UL * GiB,
            128UL * GiB,
            15.625,
            0,
            1UL * GiB,
            1UL * GiB,
            2UL * GiB,
            100_000,
            200_000,
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

    private const ulong MiB = 1024UL * 1024;
    private const ulong GiB = 1024UL * MiB;
}
