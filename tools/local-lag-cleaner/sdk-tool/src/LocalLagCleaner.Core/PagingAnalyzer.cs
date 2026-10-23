namespace LocalLagCleaner.MyPowerTools;

/// <summary>Hard-fault read volume alone cannot identify pagefile traffic or a stall.</summary>
public static class PagingAnalyzer
{
    public static LagFinding? Analyze(
        IReadOnlyList<PagingSample> samples,
        ulong physicalTotalBytes,
        LagCleanerOptions options)
    {
        options = options.Normalize();
        var valid = samples.Where(sample => double.IsFinite(sample.PagesInputPerSecond) &&
                                            sample.PagesInputPerSecond >= 0)
            .OrderBy(sample => sample.CapturedAtUtc).ToArray();
        if (valid.Length == 0 || valid.Max(sample => sample.PagesInputPerSecond) < options.HardPagingWarningPagesPerSecond)
            return null;

        var elevated = valid.Count(sample => sample.PagesInputPerSecond >= options.HardPagingWarningPagesPerSecond);
        var availableThreshold = Math.Max(1024d * 1024 * 1024, physicalTotalBytes * 0.1);
        bool HasMemoryPressure(PagingSample sample) =>
            sample.AvailableBytes is >= 0 && sample.AvailableBytes <= availableThreshold ||
            sample.CommitUsedPercent is >= 90 and <= 100;
        bool HasStoragePressure(PagingSample sample) =>
            sample.DiskLatencyMilliseconds >= options.DiskLatencyWarningMilliseconds;
        var memoryCorrelated = valid.Count(sample =>
            sample.PagesInputPerSecond >= options.HardPagingWarningPagesPerSecond && HasMemoryPressure(sample));
        var storageCorrelated = valid.Count(sample =>
            sample.PagesInputPerSecond >= options.HardPagingWarningPagesPerSecond && HasStoragePressure(sample));
        var run = 0;
        var longestRun = 0;
        DateTimeOffset? previousAt = null;
        foreach (var sample in valid)
        {
            if (previousAt is not null &&
                (sample.CapturedAtUtc - previousAt.Value).TotalMilliseconds > options.SampleIntervalMilliseconds * 1.5)
                run = 0;
            run = sample.PagesInputPerSecond >= options.HardPagingWarningPagesPerSecond &&
                  (HasMemoryPressure(sample) || HasStoragePressure(sample)) ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
            previousAt = sample.CapturedAtUtc;
        }
        var correlated = valid.Count(sample =>
            sample.PagesInputPerSecond >= options.HardPagingWarningPagesPerSecond &&
            (HasMemoryPressure(sample) || HasStoragePressure(sample)));
        var pressure = valid.Length >= 3 && longestRun >= 3 && correlated * 2 >= valid.Length;
        var complete = valid.All(sample => sample.AvailableBytes is >= 0 &&
                                           sample.CommitUsedPercent is >= 0 and <= 100 &&
                                           sample.DiskLatencyMilliseconds is >= 0);
        var average = valid.Average(sample => sample.PagesInputPerSecond);
        var peak = valid.Max(sample => sample.PagesInputPerSecond);
        var reads = valid.Where(sample => sample.PageReadsPerSecond is >= 0).ToArray();
        var readsText = reads.Length == 0 ? "磁盘读操作数未取得" :
            $"Page Reads/sec 平均 {reads.Average(sample => sample.PageReadsPerSecond!.Value):n1}";
        var available = valid.Where(sample => sample.AvailableBytes is >= 0).ToArray();
        var memoryText = available.Length == 0 ? "可用内存未取得" :
            $"最低可用内存 {LagDiagnosticsEngine.FormatBytes((ulong)available.Min(sample => sample.AvailableBytes!.Value))}";
        var latency = valid.Where(sample => sample.DiskLatencyMilliseconds is >= 0).ToArray();
        var diskText = latency.Length == 0 ? "磁盘延迟未取得" :
            $"磁盘平均延迟 {latency.Average(sample => sample.DiskLatencyMilliseconds!.Value):n3} ms";

        return new LagFinding(
            pressure ? LagSeverity.Warning : LagSeverity.Info,
            "hard-paging",
            pressure ? "硬缺页读入与资源压力同时持续出现" : "检测到硬缺页读入，卡顿关联待确认",
            $"Pages Input/sec 平均 {average:n1}、峰值 {peak:n1} 页/秒，约 {average * Environment.SystemPageSize / 1048576:n2} MiB/秒读入；" +
            $"{readsText}。超过筛查线 {elevated}/{valid.Length} 个样本；同时出现内存/提交压力 {memoryCorrelated} 个、存储延迟 {storageCorrelated} 个。{memoryText}，{diskText}。",
            pressure
                ? "打开资源监视器的内存页，按硬错误/秒定位进程，同时在磁盘页核对读入文件；保存工作后逐项退出确认的高占用应用，再在相同负载下复测。保留系统管理的分页文件。"
                : "程序、DLL 和映射文件首次载入都会触发硬缺页。保持相同负载复测；如能复现卡顿，打开资源监视器核对进程硬错误与文件读入。无需为降低此计数清空工作集或待机缓存。",
            false,
            "")
        {
            Domain = DiagnosticDomain.Memory,
            Confidence = pressure ? FindingConfidence.Medium : FindingConfidence.Low,
            Score = pressure ? 15 : 0,
            CausalChain = pressure
                ? "同一时间窗口内硬缺页读入与资源压力相关；进程、文件来源和前台等待仍需归因。"
                : complete
                    ? "已确认发生磁盘读页；当前样本未满足持续资源压力条件。数据无法区分页文件与程序/映射文件读入。"
                    : "关联计数采集不完整，当前仅能确认磁盘读页，无法判定内存不足或前台等待。"
        };
    }
}
