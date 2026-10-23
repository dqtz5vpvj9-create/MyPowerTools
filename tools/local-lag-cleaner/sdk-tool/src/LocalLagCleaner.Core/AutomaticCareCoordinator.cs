using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LocalLagCleaner.MyPowerTools;

public sealed record CareItem(string Title, string State, string Message);
public sealed record CareResult(string Summary, IReadOnlyList<CareItem> Items,
    LagDiagnosticSnapshot Before, LagDiagnosticSnapshot After, bool CanUndo);
public sealed record PriorityRestorePoint(int ProcessId, long StartTimeUtcTicks, string Name);

/// <summary>Bounded diagnosis, reversible intervention, then a fresh measurement.</summary>
public sealed class AutomaticCareCoordinator(string stateDirectory)
{
    private static readonly HashSet<string> BackgroundApplications = new(StringComparer.OrdinalIgnoreCase)
        { "WeFlow", "OneDrive", "Dropbox", "msedge", "chrome", "firefox" };
    private readonly string _stateDirectory = Path.GetFullPath(stateDirectory);
    private string JournalPath => Path.Combine(_stateDirectory, "background-priority-restore.json");

    public async Task<CareResult> RunAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (scope is not ("all" or "memory" or "kernel" or "background" or "storage"))
            throw new ArgumentException("未知检查范围。", nameof(scope));
        Directory.CreateDirectory(_stateDirectory);
        using var operationLock = new FileStream(Path.Combine(_stateDirectory, "automatic-care.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var engine = new LagDiagnosticsEngine();
        var options = new LagCleanerOptions { SampleSeconds = 5 };
        var before = await engine.ScanAsync(options, cancellationToken).ConfigureAwait(false);
        var confirmed = await engine.ScanAsync(options, cancellationToken).ConfigureAwait(false);
        var items = new List<CareItem>();
        var changes = 0;
        if (scope is "all" or "storage")
        {
            var temporaryRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            var cleanup = ExpiredTemporaryFileCleaner.Clean(temporaryRoot, DateTimeOffset.UtcNow, cancellationToken);
            changes += cleanup.DeletedFiles > 0 ? 1 : 0;
            items.Add(new("过期临时文件", cleanup.DeletedFiles > 0 ? "已清理" : "本轮未清理",
                $"已删除 {cleanup.DeletedFiles} 个超过 7 天的 .tmp/.temp 文件，释放 {LagDiagnosticsEngine.FormatBytes((ulong)cleanup.ReleasedBytes)}；跳过 {cleanup.SkippedFiles} 个占用或无权限文件。"));
        }
        if (scope is "all" or "background")
        {
            changes += ReduceBackgroundContention(before, confirmed, items);
            if (confirmed.McpCleanupCandidateCount > 0)
            {
                try
                {
                    var cleanup = new CleanupCoordinator(_stateDirectory);
                    var plan = cleanup.CreatePlan(CleanupAction.McpResidue, confirmed);
                    var result = await cleanup.ApplyPendingPlanAsync(plan.PlanId, plan.Action,
                        plan.ConfirmationToken, false, false, cancellationToken).ConfigureAwait(false);
                    changes += result.Items.Count(item => item.Succeeded);
                    items.AddRange(result.Items.Select(item => new CareItem("旧工具连接", item.Succeeded ? "已处理" : "未处理", item.Message)));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    items.Add(new("旧工具连接", "未处理", exception.Message));
                }
            }
        }
        var after = await engine.ScanAsync(options, cancellationToken).ConfigureAwait(false);
        if (scope is "all" or "memory")
        {
            var completePaging = after.PagingSamples.Count >= 3 && after.PagingSamples.All(sample =>
                sample.AvailableBytes is >= 0 && sample.CommitUsedPercent is >= 0 && sample.DiskLatencyMilliseconds is >= 0);
            var pressure = after.Findings.Any(f => f.Code == "hard-paging" && f.Severity != LagSeverity.Info);
            var sources = string.Join("、", after.HardFaultProcesses.Take(3)
                .Select(p => $"{p.Name}（每秒 {p.FaultsPerSecond:n1} 次磁盘读页）"));
            items.Add(new("内存读盘", after.HardFaultAttributionError.Length > 0 || !completePaging ? "采集未完成" : pressure ? "仍需处理" : "本次无需清理",
                after.HardFaultAttributionError.Length > 0 ? after.HardFaultAttributionError :
                $"已自动检查三轮。{(sources.Length > 0 ? "读盘来源：" + sources + "。" : "本轮存活进程未记录明显硬缺页读盘。")}" +
                (!completePaging ? "关联计数未取得完整，当前无法判断卡顿原因。" : pressure ? "持续读盘仍伴随资源压力，当前无法通过后台调度调整消除。" : "本轮未发现硬缺页与资源压力持续同时出现。") +
                "这些计数包含程序和文件载入。"));
        }
        if (scope is "all" or "kernel")
        {
            var firstTags = before.PoolTags.ToDictionary(tag => tag.Tag);
            var growing = after.PoolTags.Where(tag => firstTags.ContainsKey(tag.Tag))
                .Select(tag => (tag.Tag, Growth: (long)tag.TotalBytes - (long)firstTags[tag.Tag].TotalBytes))
                .Where(tag => tag.Growth > 0).OrderByDescending(tag => tag.Growth).Take(3).ToArray();
            var growth = string.Join("、", growing.Select(tag => $"{tag.Tag} +{LagDiagnosticsEngine.FormatBytes((ulong)tag.Growth)}"));
            var persistent = new[] { before, confirmed, after }.All(s => s.Findings.Any(f => f.Code.StartsWith("kernel-pool-", StringComparison.Ordinal) && f.Severity != LagSeverity.Info));
            items.Add(new("系统内存占用", persistent ? "尚未修复" : "本轮未持续异常",
                $"已自动对比三轮系统内存分配：{LagDiagnosticsEngine.FormatBytes(before.KernelTotalBytes)} → {LagDiagnosticsEngine.FormatBytes(after.KernelTotalBytes)}。" +
                (growth.Length > 0 ? $"增长来源已记录：{growth}。" : "主要分配标签未见正增长。") +
                (persistent ? "现有证据还无法确认具体驱动，短时间采样也无法证明长期泄漏；保留结果继续定位。" : "本次保留观察结果。")));
        }
        if (scope is "all" or "storage")
        {
            var systemDrive = after.Drives.FirstOrDefault(d => d.IsSystemDrive);
            if (systemDrive is not null)
                items.Add(new("系统盘空间", systemDrive.FreeBytes >= 30UL * 1024 * 1024 * 1024 ? "空间充足" : "尚未清理",
                    $"剩余 {LagDiagnosticsEngine.FormatBytes(systemDrive.FreeBytes)}。" +
                    (systemDrive.FreeBytes >= 30UL * 1024 * 1024 * 1024
                        ? "已按实际可用容量复核，无需为百分比告警删除文件。"
                        : "过期临时文件清理已完成，剩余空间仍需继续关注。")));
        }
        if (scope == "all")
        {
            foreach (var finding in after.Findings.Where(f => f.Severity != LagSeverity.Info &&
                         f.Code != "hard-paging" && f.Code != "system-drive-low-space" &&
                         !f.Code.StartsWith("kernel-pool-", StringComparison.Ordinal)))
                items.Add(new(finding.Title, "复测仍存在", "本轮自动处理尚未消除此项。检测证据已保存在详细数据中。"));
        }
        items.Add(new("处理前后对比", "已复测",
            $"CPU {before.TotalCpuPercent:n1}% → {after.TotalCpuPercent:n1}%；" +
            $"内存 {before.PhysicalUsedPercent:n1}% → {after.PhysicalUsedPercent:n1}%；" +
            $"磁盘平均延迟 {before.Signals?.AverageDiskLatencyMilliseconds:n2} → {after.Signals?.AverageDiskLatencyMilliseconds:n2} ms。" +
            "指标会随当前任务变化，已执行的操作与未解决的项目分别列出。"));
        var summary = changes > 0 ? $"已完成 {changes} 项处理，并自动复测。" : "自动检查完成；本轮没有执行系统变更。";
        var care = new CareResult(summary, items, before, after, ReadJournal().Count > 0);
        await File.WriteAllTextAsync(Path.Combine(_stateDirectory, "latest-care.json"), JsonSerializer.Serialize(care, LagCleanerJson.Indented), cancellationToken).ConfigureAwait(false);
        await LagReportWriter.WriteAsync(after, Path.Combine(_stateDirectory, "reports"), cancellationToken).ConfigureAwait(false);
        return care;
    }

    public IReadOnlyList<CareItem> Restore()
    {
        Directory.CreateDirectory(_stateDirectory);
        using var operationLock = new FileStream(Path.Combine(_stateDirectory, "automatic-care.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var remaining = new List<PriorityRestorePoint>();
        var results = new List<CareItem>();
        foreach (var point in ReadJournal())
        {
            try
            {
                using var process = Process.GetProcessById(point.ProcessId);
                if (!Matches(process, point) || !IsOwnedByCurrentUser(process))
                {
                    results.Add(new(point.Name, "无需恢复", "原程序已退出或身份已变化。"));
                    continue;
                }
                if (process.PriorityClass == ProcessPriorityClass.BelowNormal)
                {
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    if (process.PriorityClass != ProcessPriorityClass.Normal)
                        throw new InvalidOperationException("系统未接受恢复请求。");
                    results.Add(new(point.Name, "已恢复", "已恢复本工具调整的普通运行优先级。"));
                }
                else
                    results.Add(new(point.Name, "无需恢复", "运行优先级已被其他操作改变，保留当前设置。"));
            }
            catch (ArgumentException) { results.Add(new(point.Name, "无需恢复", "原程序已退出。")); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            { remaining.Add(point); results.Add(new(point.Name, "恢复失败", exception.Message)); }
        }
        WriteJournal(remaining);
        var lastResultPath = Path.Combine(_stateDirectory, "latest-care.json");
        if (File.Exists(lastResultPath))
        {
            var last = JsonSerializer.Deserialize<CareResult>(File.ReadAllText(lastResultPath), LagCleanerJson.Compact);
            if (last is not null && results.Count > 0)
            {
                var names = results.Select(item => item.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var updated = last with
                {
                    Summary = remaining.Count == 0 ? "后台调整恢复检查已完成，检测结果已保留。" : "部分后台调整尚未恢复，可再次撤销。",
                    Items = last.Items.Where(item => !names.Contains(item.Title)).Concat(results).ToArray(),
                    CanUndo = remaining.Count > 0
                };
                File.WriteAllText(lastResultPath, JsonSerializer.Serialize(updated, LagCleanerJson.Indented));
            }
        }
        return results;
    }

    private int ReduceBackgroundContention(LagDiagnosticSnapshot before, LagDiagnosticSnapshot confirmed, List<CareItem> items)
    {
        var journal = ReadJournal().ToList();
        var visibleNames = confirmed.WindowResponsiveness.Where(w => w.VisibleWindowCount > 0).Select(w => w.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = confirmed.TopCpuProcesses.Where(current => BackgroundApplications.Contains(current.Name) &&
            !visibleNames.Contains(current.Name) && current.CpuPercentOneCore >= 25 &&
            before.TopCpuProcesses.Any(previous => previous.ProcessId == current.ProcessId &&
                previous.StartTimeUtc == current.StartTimeUtc && previous.CpuPercentOneCore >= 25)).Take(4);
        var changed = 0;
        using var currentProcess = Process.GetCurrentProcess();
        foreach (var candidate in candidates)
        {
            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                var point = new PriorityRestorePoint(candidate.ProcessId, candidate.StartTimeUtc?.UtcTicks ?? 0, candidate.Name);
                if (!Matches(process, point) || process.MainWindowHandle != IntPtr.Zero ||
                    process.SessionId != currentProcess.SessionId || !IsOwnedByCurrentUser(process) ||
                    process.PriorityClass != ProcessPriorityClass.Normal)
                    continue;
                // Journal before changing the OS, so an interrupted request remains recoverable.
                if (!journal.Contains(point)) journal.Add(point);
                WriteJournal(journal);
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
                if (process.PriorityClass != ProcessPriorityClass.BelowNormal)
                    throw new InvalidOperationException("系统未接受后台调度调整。");
                changed++;
                items.Add(new(candidate.Name, "已调整，可撤销", "让正在使用的窗口优先响应；此后台程序仍继续运行，任务完成速度可能降低。"));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { items.Add(new(candidate.Name, "未调整", exception.Message)); }
        }
        if (changed == 0) items.Add(new("后台程序", "本轮未调整", "未找到连续两轮繁忙、且没有可见窗口的受支持后台应用。正在使用的应用保持原状。"));
        return changed;
    }

    private static bool Matches(Process process, PriorityRestorePoint point) =>
        point.StartTimeUtcTicks > 0 && BackgroundApplications.Contains(point.Name) &&
        process.ProcessName.Equals(point.Name, StringComparison.OrdinalIgnoreCase) &&
        process.StartTime.ToUniversalTime().Ticks == point.StartTimeUtcTicks;

    private static bool IsOwnedByCurrentUser(Process process)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!OpenProcessToken(process.Handle, 0x0008, out var token)) return false;
        using (token)
        using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            return owner.User == current.User;
    }

    private IReadOnlyList<PriorityRestorePoint> ReadJournal() => File.Exists(JournalPath)
        ? JsonSerializer.Deserialize<PriorityRestorePoint[]>(File.ReadAllText(JournalPath), LagCleanerJson.Compact) ?? [] : [];
    private void WriteJournal(IReadOnlyList<PriorityRestorePoint> points)
    {
        var temporary = JournalPath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(points, LagCleanerJson.Indented));
        File.Move(temporary, JournalPath, overwrite: true);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);
}
