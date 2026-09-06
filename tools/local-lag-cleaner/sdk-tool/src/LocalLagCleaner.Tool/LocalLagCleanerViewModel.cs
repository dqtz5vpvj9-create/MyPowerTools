using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using LocalLagCleaner.MyPowerTools;
using MyPowerTools.AvaloniaSdk;

namespace LocalLagCleaner.Tool;

public sealed class LocalLagCleanerViewModel : MptObservableViewModel, IDisposable
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operationCts;
    private LagDiagnosticSnapshot? _snapshot;
    private CleanupPlan? _plan;
    private bool _isBusy;
    private bool _hasPlan;
    private string _statusText = "等待首次多阶段扫描";
    private string _severityLabel = "等待";
    private IBrush _severityBrush = Brushes.SlateGray;
    private string _healthScore = "—";
    private string _cpu = "—";
    private string _physicalMemory = "—";
    private string _scheduler = "—";
    private string _paging = "—";
    private string _storage = "—";
    private string _gpu = "—";
    private string _kernelPool = "—";
    private string _systemHandles = "—";
    private string _fileAttributionStatus = "等待扫描";
    private string _processes = "—";
    private string _processBreakdownSummary = "等待扫描";
    private string _mcpCandidates = "—";
    private string _uptime = "—";
    private string _idleState = "—";
    private string _powerPlan = "—";
    private string _planSummary = "";
    private string _actionMessage = "所有扫描在隔离 Runtime 中执行；系统变更需要计划令牌。";
    private string _reportPath = "";

    public LocalLagCleanerViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        Findings = [];
        Domains = [];
        ProcessBreakdown = [];
        ProcessesDetail = [];
        SystemHandleTypes = [];
        FileAccessPatterns = [];
        FilePathGroups = [];
        FileSystemFilters = [];
        FileSystemFilterInstances = [];
        Coverage = [];
        ExecutionItems = [];

        QuickScanCommand = Command(() => ScanAsync(deep: false), "scan-quick");
        DeepScanCommand = Command(() => ScanAsync(deep: true), "scan-deep");
        ElevatedFileScanCommand = Command(
            ScanElevatedFileHandlesAsync,
            "scan-file-handles-elevated");
        PlanMcpCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.mcp"),
            "plan-mcp");
        PlanWeFlowCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.weflow"),
            "plan-weflow");
        PlanDeliveryOptimizationCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.delivery-optimization"),
            "plan-delivery-optimization");
        PlanNvidiaCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.nvidia-container"),
            "plan-nvidia");
        PlanRemoteDesktopCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.remote-desktop"),
            "plan-remote-desktop");
        PlanWindowsSearchCommand = Command(
            () => CreatePlanAsync("local-lag-cleaner.plan.windows-search"),
            "plan-windows-search");
        ApplyPlanCommand = Command(ApplyPlanAsync, "apply");
        CancelScanCommand = new MptAsyncRelayCommand(CancelScanAsync, () => IsBusy, "local-lag-cleaner.cancel-scan");
    }

    public ObservableCollection<LagFindingRow> Findings { get; }
    public ObservableCollection<DomainHealthRow> Domains { get; }
    public ObservableCollection<ProcessBreakdownRow> ProcessBreakdown { get; }
    public ObservableCollection<ProcessDetailRow> ProcessesDetail { get; }
    public ObservableCollection<SystemHandleTypeRow> SystemHandleTypes { get; }
    public ObservableCollection<FileAccessPatternRow> FileAccessPatterns { get; }
    public ObservableCollection<FilePathGroupRow> FilePathGroups { get; }
    public ObservableCollection<FileSystemFilterRow> FileSystemFilters { get; }
    public ObservableCollection<FileSystemFilterInstanceRow> FileSystemFilterInstances { get; }
    public ObservableCollection<CoverageRow> Coverage { get; }
    public ObservableCollection<ExecutionResultRow> ExecutionItems { get; }

    public MptAsyncRelayCommand QuickScanCommand { get; }
    public MptAsyncRelayCommand DeepScanCommand { get; }
    public MptAsyncRelayCommand ElevatedFileScanCommand { get; }
    public MptAsyncRelayCommand PlanMcpCommand { get; }
    public MptAsyncRelayCommand PlanWeFlowCommand { get; }
    public MptAsyncRelayCommand PlanDeliveryOptimizationCommand { get; }
    public MptAsyncRelayCommand PlanNvidiaCommand { get; }
    public MptAsyncRelayCommand PlanRemoteDesktopCommand { get; }
    public MptAsyncRelayCommand PlanWindowsSearchCommand { get; }
    public MptAsyncRelayCommand ApplyPlanCommand { get; }
    public MptAsyncRelayCommand CancelScanCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandState();
            }
        }
    }

    public bool CanInteract => !IsBusy;
    public bool CanApply =>
        !IsBusy &&
        HasPlan;

    public bool HasPlan
    {
        get => _hasPlan;
        private set
        {
            if (SetProperty(ref _hasPlan, value))
            {
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(PlanRequiresServiceRestart));
            }
        }
    }

    public bool PlanRequiresServiceRestart => _plan?.RequiresAdministrator == true;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SeverityLabel
    {
        get => _severityLabel;
        private set => SetProperty(ref _severityLabel, value);
    }

    public IBrush SeverityBrush
    {
        get => _severityBrush;
        private set => SetProperty(ref _severityBrush, value);
    }

    public string HealthScore
    {
        get => _healthScore;
        private set => SetProperty(ref _healthScore, value);
    }

    public string Cpu
    {
        get => _cpu;
        private set => SetProperty(ref _cpu, value);
    }

    public string PhysicalMemory
    {
        get => _physicalMemory;
        private set => SetProperty(ref _physicalMemory, value);
    }

    public string Scheduler
    {
        get => _scheduler;
        private set => SetProperty(ref _scheduler, value);
    }

    public string Paging
    {
        get => _paging;
        private set => SetProperty(ref _paging, value);
    }

    public string Storage
    {
        get => _storage;
        private set => SetProperty(ref _storage, value);
    }

    public string Gpu
    {
        get => _gpu;
        private set => SetProperty(ref _gpu, value);
    }

    public string KernelPool
    {
        get => _kernelPool;
        private set => SetProperty(ref _kernelPool, value);
    }

    public string SystemHandles
    {
        get => _systemHandles;
        private set => SetProperty(ref _systemHandles, value);
    }

    public string FileAttributionStatus
    {
        get => _fileAttributionStatus;
        private set => SetProperty(ref _fileAttributionStatus, value);
    }

    public string Processes
    {
        get => _processes;
        private set => SetProperty(ref _processes, value);
    }

    public string ProcessBreakdownSummary
    {
        get => _processBreakdownSummary;
        private set => SetProperty(ref _processBreakdownSummary, value);
    }

    public string McpCandidates
    {
        get => _mcpCandidates;
        private set => SetProperty(ref _mcpCandidates, value);
    }

    public string Uptime
    {
        get => _uptime;
        private set => SetProperty(ref _uptime, value);
    }

    public string IdleState
    {
        get => _idleState;
        private set => SetProperty(ref _idleState, value);
    }

    public string PowerPlan
    {
        get => _powerPlan;
        private set => SetProperty(ref _powerPlan, value);
    }

    public string PlanSummary
    {
        get => _planSummary;
        private set => SetProperty(ref _planSummary, value);
    }

    public string ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public string ReportPath
    {
        get => _reportPath;
        private set => SetProperty(ref _reportPath, value);
    }

    public Task InitializeAsync() => ScanAsync(deep: false);

    private MptAsyncRelayCommand Command(Func<Task> execute, string name)
    {
        return new MptAsyncRelayCommand(
            execute,
            () => CanInteract,
            $"local-lag-cleaner.{name}");
    }

    private async Task ScanAsync(bool deep)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        CancelScanCommand.NotifyCanExecuteChanged();
        StatusText = deep
            ? "正在执行 15 秒深度扫描：调度、分页、存储、GPU、进程 I/O 与稳定性事件…"
            : "正在执行 5 秒快速扫描：资源、调度、分页、磁盘与进程树…";
        _operationCts?.Dispose();
        _operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCts.CancelAfter(TimeSpan.FromSeconds(deep ? 60 : 30));
        try
        {
            var commandId = deep
                ? "local-lag-cleaner.scan.deep"
                : "local-lag-cleaner.scan.quick";
            var payload = await ExecutePayloadAsync(commandId, cancellationToken: _operationCts.Token);
            var snapshotNode = payload?["snapshot"] ??
                               throw new InvalidDataException("Runtime 未返回诊断快照。");
            var snapshot = snapshotNode.Deserialize<LagDiagnosticSnapshot>(
                               LagCleanerJson.Compact) ??
                           throw new InvalidDataException("Runtime 诊断快照无法解析。");
            _snapshot = snapshot;
            ReportPath = payload?["markdownReportPath"]?.GetValue<string>() ?? "";
            ApplySnapshot(snapshot, deep);
            Log(
                "info",
                $"{(deep ? "Deep" : "Quick")} scan completed; findings={snapshot.Findings.Count}; score={snapshot.HealthScore}.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusText = "扫描已取消。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消。";
            ActionMessage = "用户取消了扫描或扫描超时，现有诊断结果保持不变。";
        }
        catch (Exception exception)
        {
            StatusText = $"扫描失败：{exception.Message}";
            SeverityLabel = "失败";
            SeverityBrush = Brushes.Crimson;
            ActionMessage = "隔离 Runtime 已返回失败，MPT Shell 仍保持运行；检查探针覆盖与工具日志。";
            Log("error", exception.Message);
        }
        finally
        {
            IsBusy = false;
            CancelScanCommand.NotifyCanExecuteChanged();
        }
    }

    private Task CancelScanAsync()
    {
        _operationCts?.Cancel();
        return Task.CompletedTask;
    }

    private async Task CreatePlanAsync(string commandId)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var payload = await ExecutePayloadAsync(commandId);
            var plan = payload?.Deserialize<CleanupPlan>(LagCleanerJson.Compact) ??
                       throw new InvalidDataException("Runtime 未返回处置计划。");
            _plan = plan;
            PlanSummary =
                $"{ActionName(plan.Action)}｜风险 {RiskName(plan.Risk)}｜{plan.Scope}。" +
                $"{plan.Impact} 验证：{plan.VerificationPlan} 恢复：{plan.RecoveryPlan} " +
                $"计划于 {plan.ExpiresAtUtc.ToLocalTime():HH:mm:ss} 过期。";
            HasPlan = true;
            if (plan.RequiresAdministrator)
            {
                ActionMessage =
                    plan.MayDisconnectSession
                        ? "点击执行将请求管理员权限，并立即断开当前远程桌面会话；服务恢复后可重新连接。"
                        : "点击执行将请求管理员权限重启计划中的服务，并等待 SCM 回到 Running。";
            }
            else
            {
                ActionMessage = plan.Action == CleanupAction.McpResidue
                    ? $"核对精确目标与证据：{McpEvidenceSummary(plan.McpEvidence)}。计划凭据已自动绑定。"
                    : "核对精确目标、影响、验证和恢复步骤。计划凭据已自动绑定。";
            }
            Log(
                "warning",
                $"Cleanup plan {plan.PlanId} created for {plan.Action}; targets={plan.Targets.Count}.");
        }
        catch (Exception exception)
        {
            ActionMessage = $"计划生成失败：{exception.Message}";
            Log("warning", ActionMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ScanElevatedFileHandlesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "等待 Windows UAC；管理员 Broker 将只读抽样最多 512 个 PID 4 File 句柄…";
        ActionMessage = "管理员归因会启用 SeDebugPrivilege，执行 DuplicateHandle 与路径读取，不关闭任何句柄。";
        try
        {
            var payload = await ExecutePayloadAsync(
                "local-lag-cleaner.scan.file-handles-elevated");
            var snapshotNode = payload?["snapshot"] ??
                               throw new InvalidDataException(
                                   "Runtime 未返回管理员诊断快照。");
            var snapshot = snapshotNode.Deserialize<LagDiagnosticSnapshot>(
                               LagCleanerJson.Compact) ??
                           throw new InvalidDataException(
                               "管理员诊断快照无法解析。");
            _snapshot = snapshot;
            ReportPath = payload?["markdownReportPath"]?.GetValue<string>() ?? "";
            ApplySnapshot(snapshot, deep: false);
            StatusText =
                $"管理员 File 归因完成：复制 {snapshot.SystemFileAttribution?.DuplicatedSamples ?? 0:n0}，" +
                $"解析路径 {snapshot.SystemFileAttribution?.ResolvedPathSamples ?? 0:n0}，" +
                $"路径组 {snapshot.SystemFilePathGroups.Count:n0}，" +
                $"minifilter 卷实例 {snapshot.FileSystemFilterInstances.Count:n0}。";
            ActionMessage = snapshot.SystemFileAttribution?.RequiresKernelDriver == true
                ? "管理员与 SeDebugPrivilege 已确认；Windows 仍封锁 PID 4 远程句柄复制，现存句柄单体路径需要内核驱动。卷实例证据已写入报告。"
                : payload?["debugPrivilegeEnabled"]?.GetValue<bool>() == true
                ? "管理员 Broker 已启用 SeDebugPrivilege；完整路径证据已写入报告。"
                : "管理员 Broker 已完成采样；当前令牌没有取得 SeDebugPrivilege，查看覆盖率确认结果。";
            Log(
                "info",
                $"Elevated File attribution completed; resolved={snapshot.SystemFileAttribution?.ResolvedPathSamples ?? 0}; groups={snapshot.SystemFilePathGroups.Count}.");
        }
        catch (OperationCanceledException)
        {
            StatusText = "管理员 File 归因已取消。";
            ActionMessage = "Windows UAC 未批准或诊断被取消，现有扫描结果保持不变。";
        }
        catch (Exception exception)
        {
            StatusText = $"管理员 File 归因失败：{exception.Message}";
            ActionMessage = "管理员 Broker 未写入结果；现有诊断快照保持不变。";
            Log("error", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyPlanAsync()
    {
        if (!CanApply)
        {
            return;
        }

        IsBusy = true;
        ExecutionItems.Clear();
        try
        {
            var plan = _plan ??
                       throw new InvalidOperationException("当前没有可执行计划。");
            var payload = await ExecutePayloadAsync(
                "local-lag-cleaner.cleanup.apply",
                new JsonObject
                {
                    ["planId"] = plan.PlanId,
                    ["expectedAction"] = JsonSerializer.SerializeToNode(
                        plan.Action,
                        LagCleanerJson.Compact),
                    ["confirmationToken"] = plan.ConfirmationToken,
                    ["allowDisconnect"] = plan.MayDisconnectSession,
                    ["allowServiceRestart"] = plan.RequiresAdministrator
                });
            var result = payload?.Deserialize<CleanupExecutionResult>(LagCleanerJson.Compact) ??
                         throw new InvalidDataException("Runtime 未返回处置结果。");
            foreach (var item in result.Items)
            {
                ExecutionItems.Add(new ExecutionResultRow(
                    item.Succeeded ? "成功" : "失败",
                    item.Target,
                    item.Message,
                    item.Succeeded ? Brushes.SeaGreen : Brushes.Crimson));
            }
            var succeeded = result.Items.Count(item => item.Succeeded);
            var failed = result.Items.Count - succeeded;
            ActionMessage = result.Succeeded
                ? $"处置完成：{succeeded} 个目标已处理；{result.VerificationSummary}"
                : $"处置结束：成功 {succeeded} 个，失败 {failed} 个；{result.RecoverySummary}";
            HasPlan = false;
            _plan = null;
            PlanSummary = "";
            Log(result.Succeeded ? "info" : "warning", ActionMessage);
        }
        catch (Exception exception)
        {
            ActionMessage = $"执行被拒绝：{exception.Message}";
            Log("warning", ActionMessage);
        }
        finally
        {
            IsBusy = false;
        }

        if (!HasPlan)
        {
            await ScanAsync(deep: false);
        }
    }

    private async Task<JsonObject?> ExecutePayloadAsync(
        string commandId,
        JsonObject? arguments = null,
        CancellationToken? cancellationToken = null)
    {
        var command = await _context.ExecuteCommandAsync(
            commandId,
            arguments,
            cancellationToken ?? _lifetime.Token);
        if (!command.Success)
        {
            throw new InvalidOperationException(
                command.Error?.Message ??
                (string.IsNullOrWhiteSpace(command.Output)
                    ? "MPT Runtime 命令执行失败。"
                    : command.Output));
        }

        var response = JsonNode.Parse(command.Output)?.AsObject() ??
                       throw new InvalidDataException("Runtime 输出不是 JSON-RPC 对象。");
        var result = response["result"]?.AsObject() ??
                     throw new InvalidDataException("Runtime 输出缺少 result。");
        var state = result["state"]?.GetValue<string>() ?? "failed";
        if (!string.Equals(state, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var message = result["error"]?["message"]?.GetValue<string>() ??
                          "Runtime 拒绝了该命令。";
            throw new InvalidOperationException(message);
        }

        return result["payload"] as JsonObject;
    }

    private void ApplySnapshot(LagDiagnosticSnapshot snapshot, bool deep)
    {
        SeverityLabel = snapshot.OverallSeverity switch
        {
            LagSeverity.Critical => "严重",
            LagSeverity.Warning => "需关注",
            _ => "正常"
        };
        SeverityBrush = SeverityBrushFor(snapshot.OverallSeverity);
        StatusText =
            $"完成 {(deep ? "深度" : "快速")}扫描（{snapshot.SampleSeconds:n1} 秒）；" +
            $"健康分 {snapshot.HealthScore}，发现 {snapshot.Findings.Count} 项。";
        HealthScore = $"{snapshot.HealthScore} / 100";
        Cpu = snapshot.Signals is null
            ? $"{snapshot.TotalCpuPercent:n1}%"
            : $"{snapshot.Signals.AverageCpuPercent:n1}% 平均";
        PhysicalMemory = $"{snapshot.PhysicalUsedPercent:n1}%";
        Scheduler = snapshot.Signals is null
            ? "探针不可用"
            : $"队列 {snapshot.Signals.PeakProcessorQueueLength:n1}｜DPC/IRQ {snapshot.Signals.PeakDpcInterruptPercent:n1}%";
        Paging = snapshot.Signals is null
            ? "探针不可用"
            : $"{snapshot.Signals.PeakPagesInputPerSecond:n1} 页/秒峰值";
        Storage = snapshot.Signals is null
            ? "探针不可用"
            : $"{snapshot.Signals.PeakDiskLatencyMilliseconds:n1} ms｜队列 {snapshot.Signals.PeakDiskQueueLength:n1}";
        Gpu = snapshot.Gpu is { Status: MeasurementStatus.Available } gpu
            ? $"{gpu.UtilizationPercent:n0}%｜{gpu.TemperatureCelsius:n0} °C"
            : "探针不可用";
        KernelPool = LagDiagnosticsEngine.FormatBytes(snapshot.KernelTotalBytes);
        SystemHandles = snapshot.SystemHandleCount.ToString("n0");
        FileAttributionStatus = snapshot.SystemFileAttribution?.Summary ??
                                "File 句柄未达到归因触发线";
        Processes = $"{snapshot.ProcessCount:n0} / {snapshot.ThreadCount:n0}";
        var classifiedProcessCount = snapshot.ProcessBreakdown.Sum(item => item.ProcessCount);
        ProcessBreakdownSummary = snapshot.ProcessBreakdown.Count == 0
            ? "未取得可归类进程样本；请重试深度扫描。"
            : $"总计 {snapshot.ProcessCount:n0} 个进程；已聚合 {classifiedProcessCount:n0} 个，按数量显示前 {Math.Min(20, snapshot.ProcessBreakdown.Count)} 组。";
        McpCandidates = snapshot.McpCleanupCandidateCount.ToString("n0");
        Uptime = $"{snapshot.UptimeDays:n1} 天";
        IdleState = snapshot.SystemContext is null
            ? "未知"
            : snapshot.SystemContext.IsUserIdle
                ? $"静置 {snapshot.SystemContext.UserIdleSeconds:n0} 秒"
                : $"活动 {snapshot.SystemContext.UserIdleSeconds:n0} 秒";
        PowerPlan = snapshot.SystemContext is null
            ? "未知"
            : $"{snapshot.SystemContext.PowerSource}｜{snapshot.SystemContext.ActivePowerPlan}";

        Domains.Clear();
        foreach (var domain in snapshot.DomainHealth)
        {
            Domains.Add(new DomainHealthRow(
                DomainName(domain.Domain),
                SeverityName(domain.Severity),
                domain.Summary,
                domain.Score.ToString(),
                SeverityBrushFor(domain.Severity)));
        }

        Findings.Clear();
        foreach (var finding in snapshot.Findings)
        {
            Findings.Add(new LagFindingRow(
                SeverityName(finding.Severity),
                DomainName(finding.Domain),
                ConfidenceName(finding.Confidence),
                finding.Title,
                finding.Evidence,
                finding.CausalChain,
                finding.Recommendation,
                RiskName(finding.RemediationRisk),
                SeverityBrushFor(finding.Severity)));
        }

        ProcessBreakdown.Clear();
        foreach (var group in snapshot.ProcessBreakdown.Take(20))
        {
            ProcessBreakdown.Add(new ProcessBreakdownRow(
                group.Name,
                string.IsNullOrWhiteSpace(group.KnownRole) ? "未标注" : group.KnownRole,
                group.ProcessCount.ToString("n0"),
                group.ThreadCount.ToString("n0"),
                LagDiagnosticsEngine.FormatBytes(group.PrivateBytes),
                LagDiagnosticsEngine.FormatBytes(group.WorkingSetBytes),
                $"{group.CpuPercentMachine:n1}%",
                group.HandleCount.ToString("n0"),
                string.Join(", ", group.SampleProcessIds)));
        }

        ProcessesDetail.Clear();
        foreach (var process in snapshot.TopCpuProcesses
                     .Concat(snapshot.TopIoProcesses)
                     .Concat(snapshot.TopMemoryProcesses)
                     .DistinctBy(item => item.ProcessId)
                     .Take(20))
        {
            ProcessesDetail.Add(new ProcessDetailRow(
                process.Name,
                process.ProcessId.ToString(),
                process.KnownRole,
                $"{process.CpuPercentMachine:n1}%",
                LagDiagnosticsEngine.FormatBytes(process.PrivateBytes),
                LagDiagnosticsEngine.FormatBytes((ulong)Math.Max(
                    0,
                    process.ReadBytesPerSecond +
                    process.WriteBytesPerSecond +
                    process.OtherBytesPerSecond)) + "/s",
                process.HandleCount.ToString("n0"),
                process.ThreadCount.ToString("n0")));
        }

        SystemHandleTypes.Clear();
        foreach (var type in snapshot.SystemHandleTypes.Take(20))
        {
            SystemHandleTypes.Add(new SystemHandleTypeRow(
                type.TypeName,
                type.ObjectTypeIndex.ToString(),
                type.SystemHandleCount.ToString("n0"),
                $"{type.SystemSharePercent:n2}%",
                type.AllProcessHandleCount.ToString("n0")));
        }

        FileAccessPatterns.Clear();
        foreach (var access in snapshot.SystemFileHandleAccess.Take(16))
        {
            FileAccessPatterns.Add(new FileAccessPatternRow(
                $"0x{access.GrantedAccessMask:x8}",
                access.Rights,
                access.HandleCount.ToString("n0"),
                $"{access.SharePercent:n2}%"));
        }

        FilePathGroups.Clear();
        foreach (var path in snapshot.SystemFilePathGroups.Take(16))
        {
            FilePathGroups.Add(new FilePathGroupRow(
                path.PathGroup,
                path.FileKind,
                path.SampleCount.ToString("n0"),
                $"{path.SampleSharePercent:n2}%",
                string.Join("；", path.Examples.Take(2))));
        }

        FileSystemFilters.Clear();
        foreach (var filter in snapshot.FileSystemFilters.Take(24))
        {
            FileSystemFilters.Add(new FileSystemFilterRow(
                filter.ServiceName,
                filter.Running ? "运行" : "未运行",
                filter.Likelihood,
                filter.LoadOrderGroup,
                filter.Altitudes,
                filter.Company,
                filter.DriverPath,
                filter.Evidence));
        }

        FileSystemFilterInstances.Clear();
        foreach (var instance in snapshot.FileSystemFilterInstances.Take(128))
        {
            FileSystemFilterInstances.Add(new FileSystemFilterInstanceRow(
                instance.FilterName,
                instance.VolumeName,
                instance.Altitude,
                instance.InstanceName,
                instance.Frame,
                instance.VolumeStatus));
        }

        Coverage.Clear();
        foreach (var probe in snapshot.Coverage)
        {
            Coverage.Add(new CoverageRow(
                probe.Probe,
                probe.Status switch
                {
                    MeasurementStatus.Available => "完整",
                    MeasurementStatus.Partial => "部分",
                    _ => "不可用"
                },
                probe.Message,
                probe.Status == MeasurementStatus.Available
                    ? Brushes.SeaGreen
                    : probe.Status == MeasurementStatus.Partial
                        ? Brushes.DarkOrange
                        : Brushes.SlateGray));
        }
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanApply));
        QuickScanCommand.NotifyCanExecuteChanged();
        DeepScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        ElevatedFileScanCommand.NotifyCanExecuteChanged();
        PlanMcpCommand.NotifyCanExecuteChanged();
        PlanWeFlowCommand.NotifyCanExecuteChanged();
        PlanDeliveryOptimizationCommand.NotifyCanExecuteChanged();
        PlanNvidiaCommand.NotifyCanExecuteChanged();
        PlanRemoteDesktopCommand.NotifyCanExecuteChanged();
        PlanWindowsSearchCommand.NotifyCanExecuteChanged();
        ApplyPlanCommand.NotifyCanExecuteChanged();
    }

    private void Log(string level, string message)
    {
        _context.Log(new MptSurfaceLogEntry(level, message, DateTimeOffset.UtcNow));
    }

    private static IBrush SeverityBrushFor(LagSeverity severity) => severity switch
    {
        LagSeverity.Critical => Brushes.Crimson,
        LagSeverity.Warning => Brushes.DarkOrange,
        _ => Brushes.SeaGreen
    };

    private static string SeverityName(LagSeverity severity) => severity switch
    {
        LagSeverity.Critical => "严重",
        LagSeverity.Warning => "警告",
        _ => "正常"
    };

    private static string DomainName(DiagnosticDomain domain) => domain switch
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

    private static string ConfidenceName(FindingConfidence confidence) => confidence switch
    {
        FindingConfidence.High => "高",
        FindingConfidence.Medium => "中",
        _ => "低"
    };

    private static string RiskName(RemediationRisk risk) => risk switch
    {
        RemediationRisk.Low => "低",
        RemediationRisk.Moderate => "中",
        RemediationRisk.High => "高",
        RemediationRisk.RestartRequired => "需重启",
        _ => "只读"
    };

    private static string ActionName(CleanupAction action) => action switch
    {
        CleanupAction.McpResidue => "清理旧 MCP 会话",
        CleanupAction.WeFlow => "退出高 CPU WeFlow",
        CleanupAction.DeliveryOptimization => "重启 Delivery Optimization",
        CleanupAction.NvidiaContainer => "重启 NVIDIA Display Container",
        CleanupAction.RemoteDesktop => "重启 Remote Desktop Services",
        CleanupAction.WindowsSearch => "重启 Windows Search",
        _ => action.ToString()
    };

    private static string McpEvidenceSummary(
        IReadOnlyList<McpCleanupGroupEvidence> evidence)
    {
        var orphaned = evidence.Count(
            item => item.Kind == McpCleanupEvidenceKind.OrphanedParent);
        var superseded = evidence.Count(
            item => item.Kind == McpCleanupEvidenceKind.SupersededByNewerSameParent);
        return $"高可信孤儿组 {orphaned} 个，中可信同父替代组 {superseded} 个";
    }

    public void Dispose()
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record DomainHealthRow(
    string Domain,
    string Severity,
    string Summary,
    string Score,
    IBrush SeverityBrush);

public sealed record LagFindingRow(
    string Severity,
    string Domain,
    string Confidence,
    string Title,
    string Evidence,
    string CausalChain,
    string Recommendation,
    string Risk,
    IBrush SeverityBrush);

public sealed record ProcessDetailRow(
    string Name,
    string ProcessId,
    string Role,
    string Cpu,
    string PrivateMemory,
    string IoPerSecond,
    string Handles,
    string Threads);

public sealed record ProcessBreakdownRow(
    string Name,
    string Role,
    string ProcessCount,
    string Threads,
    string PrivateMemory,
    string WorkingSet,
    string Cpu,
    string Handles,
    string SampleProcessIds);

public sealed record SystemHandleTypeRow(
    string TypeName,
    string TypeIndex,
    string SystemCount,
    string SystemShare,
    string AllProcessCount);

public sealed record FileAccessPatternRow(
    string AccessMask,
    string Rights,
    string HandleCount,
    string Share);

public sealed record FilePathGroupRow(
    string PathGroup,
    string FileKind,
    string SampleCount,
    string Share,
    string Examples);

public sealed record FileSystemFilterRow(
    string ServiceName,
    string State,
    string Likelihood,
    string LoadOrderGroup,
    string Altitudes,
    string Company,
    string DriverPath,
    string Evidence);

public sealed record FileSystemFilterInstanceRow(
    string FilterName,
    string VolumeName,
    string Altitude,
    string InstanceName,
    string Frame,
    string VolumeStatus);

public sealed record CoverageRow(
    string Probe,
    string State,
    string Message,
    IBrush StateBrush);

public sealed record ExecutionResultRow(
    string State,
    string Target,
    string Message,
    IBrush StateBrush);
