namespace LocalLagCleaner.MyPowerTools;

public enum RemediationEntry
{
    DeepScan, ResourceMonitor, TaskManager, StorageSettings, StartupSettings,
    WindowsUpdate, DeviceManager, ReliabilityMonitor, ElevatedFileScan,
    McpCleanup, WeFlowCleanup, NvidiaRestart,
    AutomaticMemory, AutomaticKernel, AutomaticBackground, AutomaticStorage, AutomaticAll
}

public sealed record FindingRemediation(
    string FindingCode,
    string Title,
    string Status,
    string Target,
    string Steps,
    string Verification,
    string Recovery,
    string ActionLabel,
    RemediationEntry Entry);

/// <summary>Every finding has a next step and an explicit completion criterion.</summary>
public static class RemediationCatalog
{
    public static FindingRemediation Create(LagDiagnosticSnapshot snapshot, LagFinding finding)
    {
        var detail = CreateTechnicalPlan(snapshot, finding);
        var entry = finding.Domain switch
        {
            DiagnosticDomain.Memory => RemediationEntry.AutomaticMemory,
            DiagnosticDomain.KernelDrivers => RemediationEntry.AutomaticKernel,
            DiagnosticDomain.Storage => RemediationEntry.AutomaticStorage,
            _ => RemediationEntry.AutomaticBackground
        };
        if (finding.Code == "hard-paging" || finding.Code.StartsWith("memory-", StringComparison.Ordinal)) entry = RemediationEntry.AutomaticMemory;
        if (finding.Code.StartsWith("kernel-", StringComparison.Ordinal)) entry = RemediationEntry.AutomaticKernel;
        if (finding.Code == "system-drive-low-space") entry = RemediationEntry.AutomaticStorage;
        if (detail.Entry is RemediationEntry.McpCleanup or RemediationEntry.WeFlowCleanup or RemediationEntry.NvidiaRestart)
            return detail;
        if (detail.Entry == RemediationEntry.ElevatedFileScan)
            return detail with { ActionLabel = "自动追查系统文件占用（需管理员）", Status = "可自动补充归因" };
        if (finding.Code is "pending-reboot" or "startup-density" or "reliability-events" or "disk-latency" or "process-io-pressure" ||
            detail.Entry == RemediationEntry.DeepScan ||
            (detail.Entry == RemediationEntry.TaskManager && finding.Code != "process-count-warning" &&
             finding.Domain != DiagnosticDomain.BackgroundProcesses))
            return detail with { Entry = RemediationEntry.DeepScan, ActionLabel = "自动复查并保存结果", Status = "可自动复查，尚未支持此项自动修复" };
        return detail with
        {
            Entry = entry,
            Status = "由工具自动检查",
            ActionLabel = entry switch
            {
                RemediationEntry.AutomaticMemory => "自动查清内存读盘来源",
                RemediationEntry.AutomaticKernel => "自动追查系统内存增长",
                RemediationEntry.AutomaticStorage => "检查并清理过期临时文件",
                _ => "检查并减轻后台抢占"
            }
        };
    }

    private static FindingRemediation CreateTechnicalPlan(LagDiagnosticSnapshot snapshot, LagFinding finding)
    {
        FindingRemediation Plan(string status, string target, string steps, string verification,
            string recovery, string action, RemediationEntry entry) =>
            new(finding.Code, finding.Title, status, target, steps, verification, recovery, action, entry);

        var code = finding.Code;
        if (finding.CanClean && finding.CleanupAction == "mcp-residue")
            return Plan("可执行专清", finding.Evidence,
                "清理已识别的旧 MCP 会话；执行前重新核对进程身份、年龄和替代会话。当前活动会话继续保留。",
                "执行结果确认目标退出；随后复测进程数、私有提交及交互响应。",
                "需要恢复工具连接时，在所属应用中重新连接 MCP。", "清理旧 MCP 会话", RemediationEntry.McpCleanup);
        if (finding.CanClean && finding.CleanupAction == "weflow")
            return Plan("可执行专清 · 将退出应用", finding.Evidence,
                "先保存 WeFlow 中的工作；确认静置时仍有异常后，退出采样中确认的高 CPU 实例。",
                "退出后在相同负载下复测 CPU 与磁盘延迟；重新启动后观察问题是否复现。",
                "重新打开 WeFlow；未保存的内容存在丢失风险。", "退出高 CPU WeFlow", RemediationEntry.WeFlowCleanup);
        if (code == "nvidia-container-leak" && finding.CanClean)
            return Plan("可执行专清 · 服务重启", finding.Evidence,
                "重启 NVIDIA Container，等待服务恢复；如资源再次增长，再核查驱动版本。",
                "确认服务恢复运行，句柄/线程数回落且随后保持稳定。",
                "服务恢复失败时在服务管理器中启动；继续失败则保存报告后安排重启。",
                "重启 NVIDIA Container", RemediationEntry.NvidiaRestart);

        if (code == "hard-paging" || code.StartsWith("memory-", StringComparison.Ordinal))
            return Plan(finding.Severity == LagSeverity.Info ? "观察与归因" : "需要定位进程", MemoryTargets(snapshot),
                "1. 在资源监视器“内存”页按硬错误/秒排序，在“磁盘”页核对相同进程读取的文件。\n" +
                "2. 区分页文件与程序、DLL、映射文件读入；内存前列仅用于调查，尚未归因为硬缺页来源。\n" +
                "3. 确认某应用引发压力后保存工作、正常退出，或减少该应用的并发任务。保持分页文件由系统管理；保留工作集和待机缓存。",
                "相同负载下复测：可用内存、连续硬缺页样本、磁盘延迟和实际卡顿同时检查。孤立峰值无需降到零。",
                "重新打开已退出的应用并恢复任务；保留前后报告用于比较。",
                "打开资源监视器", RemediationEntry.ResourceMonitor);

        if (code.StartsWith("kernel-", StringComparison.Ordinal) || code == "dpc-interrupt-pressure")
            return Plan("需要定位驱动", PoolTargets(snapshot),
                "1. 保留本次报告，间隔一段时间在相同负载下复测，对比池标签、句柄和 DPC。\n" +
                "2. 池标签用于缩小范围；用 PoolMon/WPR 进一步映射分配来源，再核对设备或过滤驱动版本。\n" +
                "3. 对已定位的驱动逐项更新或回退；资源压力影响使用时保存工作并安排重启，再记录开机基线。",
                "同一次开机的多次样本中主要标签停止持续增长，DPC 与交互恢复；单次池总量回落仅证明资源释放。",
                "记录原驱动版本与报告；更新引入问题时通过设备管理器回退。",
                "打开设备管理器", RemediationEntry.DeviceManager);

        if (code.StartsWith("system-file-", StringComparison.Ordinal) ||
            code.StartsWith("system-handle", StringComparison.Ordinal) || code == "file-system-filter-inventory")
            return Plan("需要句柄来源归因", finding.Evidence,
                "1. 查看句柄类型、文件路径抽样和过滤驱动列表。\n" +
                "2. 路径权限不足时运行管理员 File 归因；抽样无法解析时保留报告用于后续内核追踪。\n" +
                "3. 对已确认的关联软件逐项暂停业务并复测；驱动卸载、句柄强制关闭需专门验证。",
                "比较同一次开机的句柄增长率与对应路径/标签；确认关闭关联业务后增长停止。",
                "恢复暂停的软件；保存前后报告。驱动变更前记录当前版本。",
                "管理员 File 归因", RemediationEntry.ElevatedFileScan);

        if (code == "system-drive-low-space")
            return Plan("按文件类别处理", string.Join("；", snapshot.Drives.Where(d => d.IsSystemDrive)
                    .Select(d => $"{d.Name} 可用 {LagDiagnosticsEngine.FormatBytes(d.FreeBytes)}（{d.FreePercent:n1}%）")),
                "1. 打开存储设置，查看临时文件、应用和大文件。\n2. 核对勾选内容后清理；下载目录与个人文件先确认用途。\n3. 将大型构建缓存或已归档资料迁移到其他盘。",
                "复测系统盘可用容量，结合更新、分页文件与当前任务所需空间评估；百分比越线只是筛查结果。",
                "重要资料先备份；永久删除的临时文件通常无法恢复。",
                "打开存储设置", RemediationEntry.StorageSettings);

        if (code == "startup-density")
            return Plan("需要选择启动项", finding.Evidence,
                "打开启动应用，核对发布者和用途，逐项关闭无需随登录启动的应用。保留正在使用的同步、输入法和设备组件。",
                "下次登录后复测进程数与静置 CPU，确认需要的功能仍可用。",
                "在同一设置页面重新启用该启动项。", "管理启动应用", RemediationEntry.StartupSettings);
        if (code == "pending-reboot")
            return Plan("需要保存工作并安排重启", finding.Evidence,
                "检查 Windows 更新与待完成事务，保存工作后从 Windows 选择合适时间重启。",
                "重启后复测，确认待重启标志消失、更新成功且功能正常。",
                "重启会关闭当前会话；提前保存文档并记录运行中的任务。",
                "查看 Windows 更新", RemediationEntry.WindowsUpdate);
        if (code == "reliability-events")
            return Plan("按事件定位组件", finding.Evidence,
                "打开可靠性历史，按卡顿时间核对应用挂起、硬件和存储错误。对重复出现的组件修复或更新；存储错误出现时优先备份资料。",
                "重复原操作后确认同类事件未再出现，并结合磁盘延迟与窗口响应复测。",
                "保留故障时间、事件详情与原组件版本。", "查看可靠性历史", RemediationEntry.ReliabilityMonitor);
        if (code == "disk-latency" || code == "process-io-pressure")
            return Plan("需要定位读写任务", string.Join("；", snapshot.TopIoProcesses.Take(3)
                    .Select(p => $"{p.Name} (PID {p.ProcessId}) 读写 {LagDiagnosticsEngine.FormatBytes((ulong)Math.Max(0, p.ReadBytesPerSecond + p.WriteBytesPerSecond))}/s")),
                "在资源监视器“磁盘”页核对进程、文件和响应时间。逐项暂停确认的下载、同步、索引或构建任务后复测。持续高延迟伴随存储错误时检查设备健康。",
                "比较相同负载下的磁盘平均延迟、队列与交互；进程 I/O 包含缓存访问，吞吐量单独偏高不足以确认故障。",
                "恢复被暂停的任务；存储设备维护前备份资料。", "查看磁盘活动", RemediationEntry.ResourceMonitor);
        if (code is "sample-active-session" or "diagnostic-coverage-incomplete" or "baseline-healthy")
            return Plan(code == "baseline-healthy" ? "保留基线" : "补充测量", finding.Evidence,
                finding.Recommendation,
                "静置至少一分钟后深度复测；检查探针覆盖。仍缺少的项目保留为未知，并保留报告中的错误信息。",
                "此入口仅重新采集诊断数据。", "执行深度复测", RemediationEntry.DeepScan);

        return Plan("需要人工核对目标", finding.Evidence,
            finding.Recommendation + "\n在任务管理器中核对 PID、资源与所属应用，保存工作后逐项正常退出确认的应用。",
            "在相同负载下深度复测对应指标，并确认交互与所需功能恢复。进程总数只用于筛查。",
            "重新打开已退出的应用；系统宿主需继续核对具体服务归属。",
            "打开任务管理器", RemediationEntry.TaskManager);
    }

    private static string MemoryTargets(LagDiagnosticSnapshot snapshot) =>
        $"可用内存 {LagDiagnosticsEngine.FormatBytes(snapshot.PhysicalTotalBytes - Math.Min(snapshot.PhysicalUsedBytes, snapshot.PhysicalTotalBytes))}，" +
        $"提交使用 {snapshot.CommitUsedPercent:n1}%。调查候选：" +
        string.Join("；", snapshot.TopMemoryProcesses.Take(3).Select(p =>
            $"{p.Name} (PID {p.ProcessId}) 私有提交 {LagDiagnosticsEngine.FormatBytes(p.PrivateBytes)}"));

    private static string PoolTargets(LagDiagnosticSnapshot snapshot) =>
        snapshot.PoolTags.Count == 0 ? "池标签尚未取得，先补充采样。" :
        string.Join("；", snapshot.PoolTags.Take(5).Select(p => $"{p.Tag} {LagDiagnosticsEngine.FormatBytes(p.TotalBytes)}")) +
        "。标签总量尚未证明具体驱动泄漏。";
}
