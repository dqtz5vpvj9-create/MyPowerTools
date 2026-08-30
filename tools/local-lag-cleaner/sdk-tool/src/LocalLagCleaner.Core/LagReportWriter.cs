using System.Text;
using System.Text.Json;

namespace LocalLagCleaner.MyPowerTools;

public static class LagReportWriter
{
    public static async Task<LagReportPaths> WriteAsync(
        LagDiagnosticSnapshot snapshot,
        string reportDirectory,
        CancellationToken cancellationToken = default)
    {
        reportDirectory = Path.GetFullPath(reportDirectory);
        Directory.CreateDirectory(reportDirectory);
        var jsonPath = Path.Combine(reportDirectory, "latest.json");
        var markdownPath = Path.Combine(reportDirectory, "latest.md");
        var historyDirectory = Path.Combine(reportDirectory, "history");
        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(
            historyDirectory,
            $"{snapshot.CapturedAtUtc:yyyyMMdd-HHmmss-fff}.json");
        var json = JsonSerializer.Serialize(snapshot, LagCleanerJson.Indented);
        await File.WriteAllTextAsync(
            jsonPath,
            json,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            historyPath,
            json,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            markdownPath,
            ToMarkdown(snapshot),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        TrimHistory(historyDirectory, 96);
        return new LagReportPaths(jsonPath, markdownPath)
        {
            HistoryJsonPath = historyPath
        };
    }

    public static string ToMarkdown(LagDiagnosticSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 本机卡顿诊断报告");
        builder.AppendLine();
        builder.AppendLine($"- 采样时间：{snapshot.CapturedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"- 采样时长：{snapshot.SampleSeconds:n1} 秒");
        builder.AppendLine($"- 风险等级：{SeverityText(snapshot.OverallSeverity)}");
        builder.AppendLine($"- CPU：{snapshot.TotalCpuPercent:n1}%");
        builder.AppendLine($"- 物理内存：{LagDiagnosticsEngine.FormatBytes(snapshot.PhysicalUsedBytes)} / {LagDiagnosticsEngine.FormatBytes(snapshot.PhysicalTotalBytes)} ({snapshot.PhysicalUsedPercent:n1}%)");
        builder.AppendLine($"- 提交内存：{LagDiagnosticsEngine.FormatBytes(snapshot.CommitTotalBytes)} / {LagDiagnosticsEngine.FormatBytes(snapshot.CommitLimitBytes)} ({snapshot.CommitUsedPercent:n1}%)");
        builder.AppendLine($"- 内存压缩：{LagDiagnosticsEngine.FormatBytes(snapshot.MemoryCompressionBytes)}");
        builder.AppendLine($"- 内核池：分页 {LagDiagnosticsEngine.FormatBytes(snapshot.KernelPagedBytes)}，非分页 {LagDiagnosticsEngine.FormatBytes(snapshot.KernelNonPagedBytes)}，合计 {LagDiagnosticsEngine.FormatBytes(snapshot.KernelTotalBytes)}");
        builder.AppendLine($"- 句柄：System {snapshot.SystemHandleCount:n0}，全机 {snapshot.TotalHandleCount:n0}");
        builder.AppendLine($"- 进程 / 线程：{snapshot.ProcessCount:n0} / {snapshot.ThreadCount:n0}");
        builder.AppendLine($"- 连续运行：{snapshot.UptimeDays:n1} 天");
        builder.AppendLine($"- 综合健康分：{snapshot.HealthScore} / 100");
        if (snapshot.SystemContext is { } context)
        {
            builder.AppendLine($"- 用户空闲：{context.UserIdleSeconds:n0} 秒（{(context.IsUserIdle ? "满足静置条件" : "活动场景")}）");
            builder.AppendLine($"- 电源：{context.PowerSource}，计划 {context.ActivePowerPlan}");
            builder.AppendLine($"- 启动入口：{context.StartupEntryCount}，待重启：{(context.PendingReboot ? "是" : "否")}");
        }
        else
        {
            builder.AppendLine("- 系统上下文：未采集完整（用户空闲、启动项或待重启状态缺失）");
        }
        if (snapshot.Signals is { } signals)
        {
            builder.AppendLine($"- CPU 统计：平均 {signals.AverageCpuPercent:n1}%，P95 {signals.P95CpuPercent:n1}%，峰值 {signals.PeakCpuPercent:n1}%（{snapshot.SignalSamples.Count} 个完整对齐采样）");
            builder.AppendLine($"- 调度：处理器队列峰值 {signals.PeakProcessorQueueLength:n1}，DPC/中断峰值 {signals.PeakDpcInterruptPercent:n1}%");
            builder.AppendLine($"- 分页：Pages Input/sec 平均 {signals.AveragePagesInputPerSecond:n1}、峰值 {signals.PeakPagesInputPerSecond:n1}");
            builder.AppendLine($"- 存储：延迟平均 {signals.AverageDiskLatencyMilliseconds:n1} ms、峰值 {signals.PeakDiskLatencyMilliseconds:n1} ms，队列峰值 {signals.PeakDiskQueueLength:n1}");
        }
        else
        {
            builder.AppendLine("- 调度 / 分页 / 存储：关键性能计数器未采集完整");
        }
        if (snapshot.Gpu is { Status: MeasurementStatus.Available } gpu)
        {
            builder.AppendLine($"- GPU：{gpu.Adapter}，利用率 {gpu.UtilizationPercent:n1}%，温度 {gpu.TemperatureCelsius:n1} °C，显存 {LagDiagnosticsEngine.FormatBytes(gpu.DedicatedMemoryUsedBytes)} / {LagDiagnosticsEngine.FormatBytes(gpu.DedicatedMemoryTotalBytes)}");
        }
        if (snapshot.Trend is { } trend)
        {
            builder.AppendLine($"- 趋势：{trend.Summary}");
        }
        builder.AppendLine();
        builder.AppendLine("## 诊断域");
        builder.AppendLine();
        builder.AppendLine("| 领域 | 状态 | 发现 | 扣分 | 摘要 |");
        builder.AppendLine("|---|---|---:|---:|---|");
        foreach (var domain in snapshot.DomainHealth)
        {
            builder.AppendLine($"| {DomainText(domain.Domain)} | {SeverityText(domain.Severity)} | {domain.FindingCount} | {domain.Score} | {Escape(domain.Summary)} |");
        }
        builder.AppendLine();
        builder.AppendLine("## 发现");
        builder.AppendLine();
        foreach (var finding in snapshot.Findings)
        {
            builder.AppendLine($"### [{SeverityText(finding.Severity)}] {finding.Title}");
            builder.AppendLine();
            builder.AppendLine(finding.Evidence);
            builder.AppendLine();
            builder.AppendLine($"领域：{DomainText(finding.Domain)}；可信度：{ConfidenceText(finding.Confidence)}；处置风险：{RiskText(finding.RemediationRisk)}");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(finding.CausalChain))
            {
                builder.AppendLine($"因果链：{finding.CausalChain}");
                builder.AppendLine();
            }
            builder.AppendLine($"处理方法：{finding.Recommendation}");
            builder.AppendLine();
        }

        if (snapshot.ProcessBreakdown.Count > 0)
        {
            builder.AppendLine("## 后台进程拆分");
            builder.AppendLine();
            builder.AppendLine(
                $"按进程名和已知服务角色聚合；总进程 {snapshot.ProcessCount:n0} 个，已归类 {snapshot.ProcessBreakdown.Sum(item => item.ProcessCount):n0} 个。扫描期间进程创建、退出或权限差异会造成少量数量变化。");
            builder.AppendLine();
            builder.AppendLine("| 进程组 | 已知角色 | 数量 | 线程 | 私有提交 | 工作集 | 整机 CPU | 句柄 | 示例 PID |");
            builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
            foreach (var group in snapshot.ProcessBreakdown.Take(24))
            {
                builder.AppendLine(
                    $"| {Escape(group.Name)} | {Escape(group.KnownRole)} | {group.ProcessCount:n0} | {group.ThreadCount:n0} | {LagDiagnosticsEngine.FormatBytes(group.PrivateBytes)} | {LagDiagnosticsEngine.FormatBytes(group.WorkingSetBytes)} | {group.CpuPercentMachine:n1}% | {group.HandleCount:n0} | {string.Join(", ", group.SampleProcessIds)} |");
            }
            builder.AppendLine();
            builder.AppendLine(
                "处理方法：先按数量、线程和服务角色确认来源；有可信 MCP 残留时使用专清计划，其余目标逐项退出或停用后复测，避免批量结束 svchost 等系统宿主。");
            builder.AppendLine();
        }

        AppendProcessTable(builder, "CPU 前列", snapshot.TopCpuProcesses);
        AppendProcessTable(builder, "内存前列", snapshot.TopMemoryProcesses);
        AppendProcessTable(builder, "句柄前列", snapshot.TopHandleProcesses);
        AppendProcessTable(builder, "线程前列", snapshot.TopThreadProcesses);
        AppendProcessTable(builder, "进程读写前列", snapshot.TopIoProcesses);

        if (snapshot.SystemHandleTypes.Count > 0)
        {
            builder.AppendLine("## System 句柄类型拆分");
            builder.AppendLine();
            builder.AppendLine("| 对象类型 | 类型索引 | PID 4 句柄 | PID 4 占比 | 全机同类型句柄 |");
            builder.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var type in snapshot.SystemHandleTypes.Take(24))
            {
                builder.AppendLine(
                    $"| {Escape(type.TypeName)} | {type.ObjectTypeIndex} | {type.SystemHandleCount:n0} | {type.SystemSharePercent:n2}% | {type.AllProcessHandleCount:n0} |");
            }
            builder.AppendLine();
            builder.AppendLine(
                "说明：该表按对象类型聚合 PID 4 句柄，可缩小到同步、文件、线程、IPC、会话等子系统；具体创建栈仍需增长对比或 ETW/驱动级跟踪。");
            builder.AppendLine();
        }

        if (snapshot.SystemFileHandleAccess.Count > 0)
        {
            builder.AppendLine("## System File 访问模式");
            builder.AppendLine();
            builder.AppendLine("| GrantedAccess | 权限拆分 | 句柄 | File 占比 |");
            builder.AppendLine("|---:|---|---:|---:|");
            foreach (var access in snapshot.SystemFileHandleAccess.Take(24))
            {
                builder.AppendLine(
                    $"| `0x{access.GrantedAccessMask:x8}` | {Escape(access.Rights)} | {access.HandleCount:n0} | {access.SharePercent:n2}% |");
            }
            builder.AppendLine();
        }

        if (snapshot.SystemFileAttribution is { } attribution)
        {
            builder.AppendLine("## System File 路径归因");
            builder.AppendLine();
            builder.AppendLine(attribution.Summary);
            builder.AppendLine();
            if (snapshot.SystemFilePathGroups.Count > 0)
            {
                builder.AppendLine("| 路径组 | 设备类型 | 样本 | 已解析样本占比 | 示例 |");
                builder.AppendLine("|---|---|---:|---:|---|");
                foreach (var path in snapshot.SystemFilePathGroups.Take(24))
                {
                    builder.AppendLine(
                        $"| {Escape(path.PathGroup)} | {Escape(path.FileKind)} | {path.SampleCount:n0} | {path.SampleSharePercent:n2}% | {Escape(string.Join("<br>", path.Examples))} |");
                }
                builder.AppendLine();
            }
        }

        if (snapshot.FileSystemFilters.Count > 0)
        {
            builder.AppendLine("## 文件系统过滤驱动来源候选");
            builder.AppendLine();
            builder.AppendLine("| 服务 | 状态 | 来源判断 | 加载组 / Altitude | 公司 / 版本 | 驱动路径 | 证据 |");
            builder.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var filter in snapshot.FileSystemFilters.Take(32))
            {
                builder.AppendLine(
                    $"| {Escape(filter.ServiceName)} | {(filter.Running ? "运行" : "未运行")} | {Escape(filter.Likelihood)} | {Escape(filter.LoadOrderGroup)} / {Escape(filter.Altitudes)} | {Escape(filter.Company)} / {Escape(filter.Version)} | {Escape(filter.DriverPath)} | {Escape(filter.Evidence)} |");
            }
            builder.AppendLine();
            builder.AppendLine(
                "说明：过滤器清单提供注册、运行、加载组、Altitude、二进制厂商和 Pool Tag 相关性。单次清单只能形成候选，卷实例、路径样本和增长对照用于确认来源。");
            builder.AppendLine();
        }

        if (snapshot.FileSystemFilterInstances.Count > 0)
        {
            builder.AppendLine("## 管理员 minifilter 卷实例");
            builder.AppendLine();
            builder.AppendLine("| 过滤器 | 卷 | Altitude | 实例 | Frame | 卷状态 |");
            builder.AppendLine("|---|---|---:|---|---:|---|");
            foreach (var instance in snapshot.FileSystemFilterInstances.Take(128))
            {
                builder.AppendLine(
                    $"| {Escape(instance.FilterName)} | {Escape(instance.VolumeName)} | {Escape(instance.Altitude)} | {Escape(instance.InstanceName)} | {Escape(instance.Frame)} | {Escape(instance.VolumeStatus)} |");
            }
            builder.AppendLine();
            builder.AppendLine(
                "说明：该表来自管理员 Broker 执行的实时 Filter Manager 实例枚举，表示过滤器实际挂载到哪些卷。");
            builder.AppendLine();
        }

        if (snapshot.PoolTags.Count > 0)
        {
            builder.AppendLine("## 内核池标签");
            builder.AppendLine();
            builder.AppendLine("| 标签 | 分页池 | 非分页池 | 合计 | 分页未释放分配 | 非分页未释放分配 |");
            builder.AppendLine("|---|---:|---:|---:|---:|---:|");
            foreach (var tag in snapshot.PoolTags.Take(16))
            {
                builder.AppendLine(
                    $"| {Escape(tag.Tag)} | {LagDiagnosticsEngine.FormatBytes(tag.PagedBytes)} | {LagDiagnosticsEngine.FormatBytes(tag.NonPagedBytes)} | {LagDiagnosticsEngine.FormatBytes(tag.TotalBytes)} | {tag.PagedOutstandingAllocations:n0} | {tag.NonPagedOutstandingAllocations:n0} |");
            }
            builder.AppendLine();
        }

        if (snapshot.WindowResponsiveness.Count > 0)
        {
            builder.AppendLine("## 窗口响应性");
            builder.AppendLine();
            builder.AppendLine("| 进程 | PID | 用户窗口 | 疑似无响应 | 状态 |");
            builder.AppendLine("|---|---:|---:|---:|---|");
            foreach (var process in snapshot.WindowResponsiveness.Take(20))
            {
                builder.AppendLine(
                    $"| {Escape(process.ProcessName)} | {process.ProcessId} | {process.VisibleWindowCount} | {process.HungWindowCount} | {(process.HungWindowCount > 0 ? "需复测" : "未触发超时")} |");
            }
            builder.AppendLine();
        }

        if (snapshot.Drives.Count > 0)
        {
            builder.AppendLine("## 磁盘空间");
            builder.AppendLine();
            builder.AppendLine("| 卷 | 类型 | 可用 | 总量 | 可用比例 | 系统盘 |");
            builder.AppendLine("|---|---|---:|---:|---:|---|");
            foreach (var drive in snapshot.Drives)
            {
                builder.AppendLine($"| {Escape(drive.Name)} | {Escape(drive.DriveType)} | {LagDiagnosticsEngine.FormatBytes(drive.FreeBytes)} | {LagDiagnosticsEngine.FormatBytes(drive.TotalBytes)} | {drive.FreePercent:n1}% | {(drive.IsSystemDrive ? "是" : "否")} |");
            }
            builder.AppendLine();
        }

        builder.AppendLine("## 探针覆盖");
        builder.AppendLine();
        builder.AppendLine("| 探针 | 覆盖状态 | 说明 |");
        builder.AppendLine("|---|---|---|");
        foreach (var probe in snapshot.Coverage)
        {
            builder.AppendLine($"| {Escape(probe.Probe)} | {CoverageText(probe.Status)} | {Escape(probe.Message)} |");
        }
        builder.AppendLine();

        builder.AppendLine("## 处理顺序");
        builder.AppendLine();
        for (var index = 0; index < snapshot.Recommendations.Count; index++)
        {
            builder.AppendLine($"{index + 1}. {snapshot.Recommendations[index]}");
        }

        return builder.ToString();
    }

    private static void AppendProcessTable(
        StringBuilder builder,
        string title,
        IReadOnlyList<ProcessSnapshot> processes)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| 进程 | 角色 | PID | 整机 CPU | 单核 CPU | 私有内存 | 读/秒 | 写/秒 | 句柄 | 线程 |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var process in processes.Take(10))
        {
            builder.AppendLine(
                $"| {Escape(process.Name)} | {Escape(process.KnownRole)} | {process.ProcessId} | {process.CpuPercentMachine:n1}% | {process.CpuPercentOneCore:n1}% | {LagDiagnosticsEngine.FormatBytes(process.PrivateBytes)} | {LagDiagnosticsEngine.FormatBytes((ulong)Math.Max(0, process.ReadBytesPerSecond))} | {LagDiagnosticsEngine.FormatBytes((ulong)Math.Max(0, process.WriteBytesPerSecond))} | {process.HandleCount:n0} | {process.ThreadCount:n0} |");
        }
        builder.AppendLine();
    }

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string SeverityText(LagSeverity severity) => severity switch
    {
        LagSeverity.Critical => "严重",
        LagSeverity.Warning => "警告",
        _ => "正常"
    };

    private static string DomainText(DiagnosticDomain domain) => domain switch
    {
        DiagnosticDomain.CpuScheduling => "CPU 与调度",
        DiagnosticDomain.Memory => "内存与分页",
        DiagnosticDomain.Storage => "存储",
        DiagnosticDomain.Graphics => "GPU 与显示",
        DiagnosticDomain.KernelDrivers => "内核与驱动",
        DiagnosticDomain.Responsiveness => "响应性",
        DiagnosticDomain.BackgroundProcesses => "后台进程",
        _ => "系统可靠性"
    };

    private static string ConfidenceText(FindingConfidence confidence) => confidence switch
    {
        FindingConfidence.High => "高",
        FindingConfidence.Medium => "中",
        _ => "低"
    };

    private static string RiskText(RemediationRisk risk) => risk switch
    {
        RemediationRisk.Low => "低",
        RemediationRisk.Moderate => "中",
        RemediationRisk.High => "高",
        RemediationRisk.RestartRequired => "需重启",
        _ => "只读"
    };

    private static string CoverageText(MeasurementStatus status) => status switch
    {
        MeasurementStatus.Available => "完整",
        MeasurementStatus.Partial => "部分",
        _ => "不可用"
    };

    private static void TrimHistory(string historyDirectory, int keep)
    {
        try
        {
            foreach (var file in Directory
                         .EnumerateFiles(historyDirectory, "*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(keep))
            {
                File.Delete(file);
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            // A report is still valid when bounded history maintenance is denied.
        }
    }
}

public sealed record LagReportPaths(string JsonPath, string MarkdownPath)
{
    public string HistoryJsonPath { get; init; } = "";
}
