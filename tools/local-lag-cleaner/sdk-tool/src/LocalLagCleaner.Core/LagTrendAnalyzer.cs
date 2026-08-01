namespace LocalLagCleaner.MyPowerTools;

public static class LagTrendAnalyzer
{
    private const int MaximumHistorySnapshots = 8;
    private const int MinimumHistorySnapshots = 3;
    private const double RequiredPositiveIntervalRatio = 2d / 3;
    private const double HandleWarningRatePerHour = 10_000;
    private const double HandleCriticalRatePerHour = 50_000;
    private const double KernelWarningRatePerHour = 128d * 1024 * 1024;
    private const double KernelCriticalRatePerHour = 512d * 1024 * 1024;

    public static LagDiagnosticSnapshot Apply(
        LagDiagnosticSnapshot current,
        LagDiagnosticSnapshot? baseline)
    {
        var trend = Compare(current, baseline);
        if (!trend.Available)
        {
            return current with { Trend = trend };
        }

        var hours = Math.Max(trend.BaselineAgeMinutes / 60, 1d / 60);
        var handleRate = trend.SystemHandleDelta / hours;
        var kernelRate = trend.KernelPoolDeltaBytes / hours;
        var findings = current.Findings.ToList();
        if (handleRate >= 10_000 || kernelRate >= 128d * 1024 * 1024)
        {
            var critical = handleRate >= 50_000 || kernelRate >= 512d * 1024 * 1024;
            findings.Add(new LagFinding(
                critical ? LagSeverity.Critical : LagSeverity.Warning,
                "kernel-growth-trend",
                "同一次开机内核资源持续增长",
                trend.Summary,
                "保留本次与基线报告；安排重启后继续定点采样，使用 PoolTag、句柄类型和 WPR 定位增长来源。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = critical ? 25 : 16,
                RemediationRisk = RemediationRisk.RestartRequired,
                CausalChain = "同一次开机持续增长 → 排除单次峰值 → 内核对象或驱动池泄漏概率升高"
            });
        }

        return current with
        {
            Trend = trend,
            Findings = findings
                .OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Score)
                .ToArray()
        };
    }

    public static LagDiagnosticSnapshot ApplyHistory(
        LagDiagnosticSnapshot current,
        IReadOnlyList<LagDiagnosticSnapshot> history)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);

        var analysis = AnalyzeHistory(current, history);
        if (!analysis.Trend.Available)
        {
            return current with { Trend = analysis.Trend };
        }

        var findings = current.Findings
            .Where(item => !string.Equals(
                item.Code,
                "kernel-growth-trend",
                StringComparison.Ordinal))
            .ToList();
        var handleGrowth =
            analysis.HandlePositiveRatio >= RequiredPositiveIntervalRatio &&
            analysis.HandleRatePerHour >= HandleWarningRatePerHour;
        var kernelGrowth =
            analysis.KernelPositiveRatio >= RequiredPositiveIntervalRatio &&
            analysis.KernelRatePerHour >= KernelWarningRatePerHour;
        if (handleGrowth || kernelGrowth)
        {
            var critical =
                (handleGrowth && analysis.HandleRatePerHour >= HandleCriticalRatePerHour) ||
                (kernelGrowth && analysis.KernelRatePerHour >= KernelCriticalRatePerHour);
            findings.Add(new LagFinding(
                critical ? LagSeverity.Critical : LagSeverity.Warning,
                "kernel-growth-trend",
                "同一次开机内核资源持续增长",
                analysis.Trend.Summary,
                "保留本次与历史报告；安排重启后继续定点采样，使用 PoolTag、句柄类型和 WPR 定位增长来源。",
                false,
                "")
            {
                Domain = DiagnosticDomain.KernelDrivers,
                Confidence = FindingConfidence.High,
                Score = critical ? 25 : 16,
                RemediationRisk = RemediationRisk.RestartRequired,
                CausalChain = "同次开机多点稳健斜率与连续正增长 → 排除单次峰值 → 内核对象或驱动池泄漏概率升高"
            });
        }

        return current with
        {
            Trend = analysis.Trend,
            Findings = findings
                .OrderByDescending(item => item.Severity)
                .ThenByDescending(item => item.Score)
                .ToArray()
        };
    }

    public static DiagnosticTrend Compare(
        LagDiagnosticSnapshot current,
        LagDiagnosticSnapshot? baseline)
    {
        if (baseline is null)
        {
            return new DiagnosticTrend(
                false,
                0,
                0,
                0,
                0,
                0,
                "缺少同一次开机的历史基线。");
        }

        var age = current.CapturedAtUtc - baseline.CapturedAtUtc;
        if (age < TimeSpan.FromMinutes(5))
        {
            return new DiagnosticTrend(
                false,
                Math.Max(0, age.TotalMinutes),
                0,
                0,
                0,
                0,
                "历史间隔不足五分钟，暂不推断泄漏趋势。");
        }

        var inferredCurrentBoot = current.CapturedAtUtc - TimeSpan.FromDays(current.UptimeDays);
        var inferredBaselineBoot = baseline.CapturedAtUtc - TimeSpan.FromDays(baseline.UptimeDays);
        if ((inferredCurrentBoot - inferredBaselineBoot).Duration() > TimeSpan.FromMinutes(3))
        {
            return new DiagnosticTrend(
                false,
                age.TotalMinutes,
                0,
                0,
                0,
                0,
                "历史快照来自另一次开机，已作为重启前基线保留。");
        }

        var handleDelta = (long)current.SystemHandleCount - baseline.SystemHandleCount;
        var kernelDelta = SaturatingDelta(current.KernelTotalBytes, baseline.KernelTotalBytes);
        var commitDelta = SaturatingDelta(current.CommitTotalBytes, baseline.CommitTotalBytes);
        var processDelta = (int)current.ProcessCount - (int)baseline.ProcessCount;
        var hours = Math.Max(age.TotalHours, 1d / 60);
        var summary =
            $"{age.TotalHours:n1} 小时内：System 句柄 {handleDelta:+#,0;-#,0;0}，" +
            $"内核池 {FormatSigned(kernelDelta)}，提交 {FormatSigned(commitDelta)}，" +
            $"进程 {processDelta:+#,0;-#,0;0}；折算句柄增长 {handleDelta / hours:n0}/小时。";
        return new DiagnosticTrend(
            true,
            age.TotalMinutes,
            handleDelta,
            kernelDelta,
            commitDelta,
            processDelta,
            summary);
    }

    private static HistoryTrendAnalysis AnalyzeHistory(
        LagDiagnosticSnapshot current,
        IReadOnlyList<LagDiagnosticSnapshot> history)
    {
        if (current.SystemContext is null)
        {
            return UnavailableHistory("当前用户空闲状态未采集，无法建立同状态趋势。");
        }

        var currentBoot = InferBootTime(current);
        if (currentBoot is null)
        {
            return UnavailableHistory("当前开机时间无法可靠推断。");
        }

        var eligible = history
            .Where(item => item.CapturedAtUtc < current.CapturedAtUtc)
            .Where(item => IsSameBoot(item, currentBoot.Value))
            .Where(item => HasSimilarSampleDuration(item, current))
            .Where(item => item.SystemContext?.IsUserIdle == current.SystemContext.IsUserIdle)
            .GroupBy(item => item.CapturedAtUtc)
            .Select(group => group.Last())
            .OrderByDescending(item => item.CapturedAtUtc)
            .Take(MaximumHistorySnapshots)
            .OrderBy(item => item.CapturedAtUtc)
            .ToArray();
        if (eligible.Length < MinimumHistorySnapshots)
        {
            return UnavailableHistory(
                $"有效历史快照仅 {eligible.Length} 个；至少需要 {MinimumHistorySnapshots} 个同次开机、同空闲状态样本。");
        }

        var samples = eligible
            .Append(current)
            .OrderBy(item => item.CapturedAtUtc)
            .ToArray();
        var span = samples[^1].CapturedAtUtc - samples[0].CapturedAtUtc;
        if (span < TimeSpan.FromMinutes(15))
        {
            return UnavailableHistory(
                $"有效样本跨度 {Math.Max(0, span.TotalMinutes):n1} 分钟；至少需要 15 分钟。");
        }

        var handleRate = MedianPairwiseSlope(
            samples,
            item => item.SystemHandleCount);
        var kernelRate = MedianPairwiseSlope(
            samples,
            item => item.KernelTotalBytes);
        var commitRate = MedianPairwiseSlope(
            samples,
            item => item.CommitTotalBytes);
        var processRate = MedianPairwiseSlope(
            samples,
            item => item.ProcessCount);
        var handlePositiveRatio = PositiveIntervalRatio(
            samples,
            item => item.SystemHandleCount);
        var kernelPositiveRatio = PositiveIntervalRatio(
            samples,
            item => item.KernelTotalBytes);
        var oldest = samples[0];
        var handleDelta = (long)current.SystemHandleCount - oldest.SystemHandleCount;
        var kernelDelta = SaturatingDelta(current.KernelTotalBytes, oldest.KernelTotalBytes);
        var commitDelta = SaturatingDelta(current.CommitTotalBytes, oldest.CommitTotalBytes);
        var processDelta = (int)current.ProcessCount - (int)oldest.ProcessCount;
        var intervalCount = samples.Length - 1;
        var summary =
            $"{samples.Length} 个样本覆盖 {span.TotalMinutes:n0} 分钟；稳健增长率：" +
            $"System 句柄 {handleRate:+#,0;-#,0;0}/小时，" +
            $"内核池 {FormatSignedRate(kernelRate)}，" +
            $"提交 {FormatSignedRate(commitRate)}，" +
            $"进程 {processRate:+#,0.0;-#,0.0;0}/小时；" +
            $"句柄正增长区间 {CountPositiveIntervals(samples, item => item.SystemHandleCount)}/{intervalCount}，" +
            $"内核池正增长区间 {CountPositiveIntervals(samples, item => item.KernelTotalBytes)}/{intervalCount}。";
        var trend = new DiagnosticTrend(
            true,
            span.TotalMinutes,
            handleDelta,
            kernelDelta,
            commitDelta,
            processDelta,
            summary);
        return new HistoryTrendAnalysis(
            trend,
            handleRate,
            kernelRate,
            handlePositiveRatio,
            kernelPositiveRatio);
    }

    private static HistoryTrendAnalysis UnavailableHistory(string summary)
    {
        return new HistoryTrendAnalysis(
            new DiagnosticTrend(false, 0, 0, 0, 0, 0, summary),
            0,
            0,
            0,
            0);
    }

    private static DateTimeOffset? InferBootTime(LagDiagnosticSnapshot snapshot)
    {
        if (!double.IsFinite(snapshot.UptimeDays) || snapshot.UptimeDays < 0)
        {
            return null;
        }

        return snapshot.CapturedAtUtc - TimeSpan.FromDays(snapshot.UptimeDays);
    }

    private static bool IsSameBoot(
        LagDiagnosticSnapshot snapshot,
        DateTimeOffset currentBoot)
    {
        var boot = InferBootTime(snapshot);
        return boot is not null &&
               (boot.Value - currentBoot).Duration() <= TimeSpan.FromMinutes(3);
    }

    private static bool HasSimilarSampleDuration(
        LagDiagnosticSnapshot candidate,
        LagDiagnosticSnapshot current)
    {
        if (!double.IsFinite(candidate.SampleSeconds) ||
            !double.IsFinite(current.SampleSeconds) ||
            candidate.SampleSeconds <= 0 ||
            current.SampleSeconds <= 0)
        {
            return false;
        }

        var maximum = Math.Max(candidate.SampleSeconds, current.SampleSeconds);
        return Math.Abs(candidate.SampleSeconds - current.SampleSeconds) / maximum <= 0.5;
    }

    private static double MedianPairwiseSlope(
        IReadOnlyList<LagDiagnosticSnapshot> samples,
        Func<LagDiagnosticSnapshot, double> valueSelector)
    {
        var slopes = new List<double>();
        for (var left = 0; left < samples.Count - 1; left++)
        {
            for (var right = left + 1; right < samples.Count; right++)
            {
                var elapsedHours =
                    (samples[right].CapturedAtUtc - samples[left].CapturedAtUtc).TotalHours;
                if (elapsedHours > 0)
                {
                    slopes.Add(
                        (valueSelector(samples[right]) - valueSelector(samples[left])) /
                        elapsedHours);
                }
            }
        }

        if (slopes.Count == 0)
        {
            return 0;
        }

        slopes.Sort();
        var middle = slopes.Count / 2;
        return slopes.Count % 2 == 0
            ? (slopes[middle - 1] + slopes[middle]) / 2
            : slopes[middle];
    }

    private static double PositiveIntervalRatio(
        IReadOnlyList<LagDiagnosticSnapshot> samples,
        Func<LagDiagnosticSnapshot, double> valueSelector)
    {
        var intervalCount = samples.Count - 1;
        return intervalCount <= 0
            ? 0
            : (double)CountPositiveIntervals(samples, valueSelector) / intervalCount;
    }

    private static int CountPositiveIntervals(
        IReadOnlyList<LagDiagnosticSnapshot> samples,
        Func<LagDiagnosticSnapshot, double> valueSelector)
    {
        var count = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            if (valueSelector(samples[index]) > valueSelector(samples[index - 1]))
            {
                count++;
            }
        }

        return count;
    }

    private static long SaturatingDelta(ulong current, ulong baseline)
    {
        if (current >= baseline)
        {
            var value = current - baseline;
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }

        var negative = baseline - current;
        return negative > long.MaxValue ? long.MinValue : -(long)negative;
    }

    private static string FormatSigned(long value)
    {
        if (value == 0)
        {
            return "0 B";
        }

        var prefix = value > 0 ? "+" : "-";
        var magnitude = value == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(value);
        return prefix + LagDiagnosticsEngine.FormatBytes(magnitude);
    }

    private static string FormatSignedRate(double bytesPerHour)
    {
        if (Math.Abs(bytesPerHour) < 0.5)
        {
            return "0 B/小时";
        }

        var prefix = bytesPerHour > 0 ? "+" : "-";
        var magnitude = Math.Min(
            Math.Abs(bytesPerHour),
            ulong.MaxValue);
        return prefix + LagDiagnosticsEngine.FormatBytes((ulong)Math.Round(magnitude)) + "/小时";
    }

    private sealed record HistoryTrendAnalysis(
        DiagnosticTrend Trend,
        double HandleRatePerHour,
        double KernelRatePerHour,
        double HandlePositiveRatio,
        double KernelPositiveRatio);
}
