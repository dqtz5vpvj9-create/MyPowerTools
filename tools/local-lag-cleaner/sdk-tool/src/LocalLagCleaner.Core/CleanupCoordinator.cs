using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LocalLagCleaner.MyPowerTools;

public sealed class CleanupCoordinator
{
    private const string PendingPlanFileName = "pending-cleanup.json";
    private const string StateLockFileName = "cleanup-state.lock";
    private const int MaxCleanupTargets = 256;
    private const double WeFlowCpuCandidateThreshold = 25;
    private const double McpLiveCpuCandidateThreshold = 2;
    private static readonly TimeSpan McpLiveSampleDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StateLockTimeout = TimeSpan.FromSeconds(15);
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceEnumerateDependents = 0x0008;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const uint ServiceActive = 0x00000001;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly IReadOnlyDictionary<CleanupAction, ServiceCleanupDefinition> ServiceDefinitions =
        new Dictionary<CleanupAction, ServiceCleanupDefinition>
        {
            [CleanupAction.DeliveryOptimization] = new(
                "DoSvc",
                "Delivery Optimization",
                "Windows 更新与应用分发会短暂暂停，随后恢复。",
                false),
            [CleanupAction.NvidiaContainer] = new(
                "NVDisplay.ContainerLocalSystem",
                "NVIDIA Display Container",
                "NVIDIA 控制面板和显示辅助功能会短暂中断。",
                false),
            [CleanupAction.RemoteDesktop] = new(
                "TermService",
                "Remote Desktop Services",
                "当前远程桌面连接会立即断开。",
                true),
            [CleanupAction.WindowsSearch] = new(
                "WSearch",
                "Windows Search",
                "索引和开始菜单搜索会短暂暂停，服务恢复后继续。",
                false)
        };

    private readonly string _stateDirectory;

    public CleanupCoordinator(string stateDirectory)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(_stateDirectory);
    }

    public string StateDirectory => _stateDirectory;

    public CleanupPlan CreatePlan(
        CleanupAction action,
        LagDiagnosticSnapshot snapshot,
        TimeSpan? validity = null)
    {
        using var stateLock = AcquireStateLock();
        var mcpMaterial = action == CleanupAction.McpResidue
            ? BuildMcpTargets(snapshot)
            : McpPlanMaterial.Empty;
        var targets = action switch
        {
            CleanupAction.McpResidue => mcpMaterial.Targets,
            CleanupAction.WeFlow => BuildWeFlowTargets(snapshot),
            _ => []
        };
        if (action is CleanupAction.McpResidue or CleanupAction.WeFlow &&
            targets.Count == 0)
        {
            throw new InvalidOperationException(
                action == CleanupAction.McpResidue
                    ? "当前快速扫描没有同时满足身份、年龄、低 CPU、保留策略和可信会话证据的 MCP 清理目标。"
                    : "当前快照没有达到单核 CPU 门槛的 WeFlow 目标。");
        }
        if (targets.Count > MaxCleanupTargets)
        {
            throw new InvalidOperationException(
                $"清理目标共 {targets.Count} 个，超过单次上限 {MaxCleanupTargets}；请缩小范围后重新扫描。");
        }

        var (scope, impact, administrator, disconnect) = Describe(action, targets);
        var plan = new CleanupPlan(
            Guid.NewGuid().ToString("N"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
            action,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.Add(validity ?? TimeSpan.FromMinutes(10)),
            targets,
            scope,
            impact,
            administrator,
            disconnect)
        {
            Risk = action switch
            {
                CleanupAction.RemoteDesktop => RemediationRisk.High,
                CleanupAction.McpResidue or CleanupAction.WeFlow => RemediationRisk.High,
                _ when ServiceDefinitions.ContainsKey(action) => RemediationRisk.Moderate,
                _ => RemediationRisk.High
            },
            IsReversible = ServiceDefinitions.ContainsKey(action) &&
                           action != CleanupAction.RemoteDesktop,
            Preconditions = action switch
            {
                CleanupAction.McpResidue =>
                    [
                        "目标仍属于计划中明确列出的 MCP 成员",
                        "PID、名称、启动时间、父子链和 MCP 标记全部一致",
                        "执行前短采样单核 CPU 合计不高于 2%",
                        "孤儿父进程仍缺失，或同一可信父进程下的更新会话仍存活"
                    ],
                CleanupAction.WeFlow =>
                    ["目标仍为 WeFlow", "生成计划时单核 CPU 不低于 25%", "已保存应用内工作"],
                CleanupAction.RemoteDesktop =>
                    ["当前具有带外恢复路径", "已接受远程会话立即断开"],
                _ => ["服务当前处于 Running", "具备管理员权限", "已接受服务短暂中断"]
            },
            VerificationPlan = ServiceDefinitions.ContainsKey(action)
                ? "等待服务回到 Running，并记录真实 SCM 状态。"
                : "逐一等待计划 PID 退出，随后以同口径快速扫描复测资源变化。",
            RecoveryPlan = action switch
            {
                CleanupAction.McpResidue => "需要时由调用方重新建立 MCP 会话；已终止进程无法恢复原状态。",
                CleanupAction.WeFlow => "需要时重新启动 WeFlow；未保存的应用状态无法恢复。",
                CleanupAction.RemoteDesktop => "服务恢复后重新建立远程连接。",
                _ => "启动失败时尽力将服务恢复到执行前的 Running 状态。"
            },
            McpEvidence = mcpMaterial.Evidence
        };
        WritePlanCore(plan);
        return plan;
    }

    public CleanupPlan? TryReadPendingPlan()
    {
        using var stateLock = AcquireStateLock();
        try
        {
            var plan = ReadPendingPlanCore();
            if (plan is not null)
            {
                ValidatePlanShape(plan);
            }

            return plan;
        }
        catch (Exception exception) when (
            exception is JsonException or
            InvalidOperationException)
        {
            return null;
        }
    }

    public Task<CleanupExecutionResult> ApplyPendingPlanAsync(
        string confirmationToken,
        bool allowDisconnect,
        bool allowServiceRestart,
        CancellationToken cancellationToken = default)
    {
        return ApplyPendingPlanCoreAsync(
            expectedPlanId: null,
            expectedAction: null,
            confirmationToken,
            allowDisconnect,
            allowServiceRestart,
            cancellationToken);
    }

    public Task<CleanupExecutionResult> ApplyPendingPlanAsync(
        string planId,
        CleanupAction expectedAction,
        string confirmationToken,
        bool allowDisconnect,
        bool allowServiceRestart,
        CancellationToken cancellationToken = default)
    {
        return ApplyPendingPlanCoreAsync(
            planId,
            expectedAction,
            confirmationToken,
            allowDisconnect,
            allowServiceRestart,
            cancellationToken);
    }

    private async Task<CleanupExecutionResult> ApplyPendingPlanCoreAsync(
        string? expectedPlanId,
        CleanupAction? expectedAction,
        string confirmationToken,
        bool allowDisconnect,
        bool allowServiceRestart,
        CancellationToken cancellationToken)
    {
        await using var stateLock = await AcquireStateLockAsync(cancellationToken)
            .ConfigureAwait(false);
        var plan = ReadPendingPlanCore() ??
            throw new InvalidOperationException("没有待执行的清理计划，请先生成计划。");
        ValidateExpectedPlan(plan, expectedPlanId, expectedAction);
        ValidatePlanShape(plan);
        var requiresAdministrator = ServiceDefinitions.ContainsKey(plan.Action);
        var mayDisconnect = ServiceDefinitions.TryGetValue(plan.Action, out var describedService) &&
                            describedService.MayDisconnect;
        ValidatePlan(
            plan,
            confirmationToken,
            allowDisconnect,
            allowServiceRestart,
            requiresAdministrator,
            mayDisconnect);

        var claimedPath = ClaimedPlanPath(plan.PlanId);
        if (File.Exists(claimedPath))
        {
            throw new InvalidOperationException(
                $"计划 {plan.PlanId} 已存在执行领取记录；请检查上一次执行结果。");
        }

        File.Move(PendingPlanPath(), claimedPath);
        IReadOnlyList<CleanupItemResult> results;
        try
        {
            if (ServiceDefinitions.TryGetValue(plan.Action, out var service))
            {
                if (plan.Targets.Count != 0)
                {
                    throw new InvalidOperationException("服务重启计划不得包含进程目标。");
                }

                results = await RestartServiceAsync(service, cancellationToken).ConfigureAwait(false);
            }
            else if (plan.Action is CleanupAction.McpResidue or CleanupAction.WeFlow)
            {
                results = await StopProcessesAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException($"清理计划包含未知动作值 {(int)plan.Action}。");
            }

            var succeeded = results.Count > 0 && results.All(item => item.Succeeded);
            return new CleanupExecutionResult(
                plan.PlanId,
                plan.Action,
                succeeded,
                DateTimeOffset.UtcNow,
                results)
            {
                VerificationPassed = succeeded,
                VerificationSummary = succeeded
                    ? ServiceDefinitions.ContainsKey(plan.Action)
                        ? "SCM 已确认服务恢复运行。"
                        : "所有计划 PID 已退出；资源改善需要调用方另行执行同口径快速复测。"
                    : "至少一个目标未通过执行后验证。",
                RecoverySummary = succeeded
                    ? plan.RecoveryPlan
                    : "本次计划已消费；服务动作已尽力恢复原运行状态。需要重试时请重新快速扫描并生成新计划。"
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            ArgumentException or
            Win32Exception)
        {
            return new CleanupExecutionResult(
                plan.PlanId,
                plan.Action,
                false,
                DateTimeOffset.UtcNow,
                [new CleanupItemResult(plan.Scope, false, exception.Message)])
            {
                VerificationPassed = false,
                VerificationSummary = "执行前身份、关系、证据或活动度复核失败。",
                RecoverySummary =
                    "本次计划已消费，未继续操作目标。需要重试时请重新快速扫描并生成新计划。"
            };
        }
        finally
        {
            if (File.Exists(claimedPath))
            {
                File.Delete(claimedPath);
            }
        }
    }

    private static McpPlanMaterial BuildMcpTargets(LagDiagnosticSnapshot snapshot)
    {
        var result = new List<CleanupTarget>();
        var evidence = new List<McpCleanupGroupEvidence>();
        var seen = new HashSet<int>();
        var processTable = WindowsNative.ReadProcessTable();
        foreach (var group in snapshot.McpGroups.Where(item => item.IsCleanupCandidate))
        {
            ValidateMcpSnapshotGroup(group, processTable);
            var members = group.Members.ToDictionary(item => item.ProcessId);
            foreach (var processId in group.ProcessIds.Order())
            {
                if (!seen.Add(processId))
                {
                    throw new InvalidOperationException(
                        $"MCP 快速扫描在多个候选组中重复列出 PID {processId}。");
                }

                var member = members[processId];
                var target = CaptureLiveTarget(
                    processId,
                    member.StartTimeUtc,
                    member.Name) with
                {
                    ParentProcessId = member.ParentProcessId,
                    McpGroupRootProcessId = group.RootProcessId,
                    IsMcpDirectMatch = member.IsDirectMatch
                };
                if (member.IsDirectMatch &&
                    !IsLiveMcpDirectMatch(target, member.ParentProcessId))
                {
                    throw new InvalidOperationException(
                        $"PID {processId} 已失去快速扫描时的 MCP 标记，请重新扫描。");
                }

                result.Add(target);
                if (result.Count > MaxCleanupTargets)
                {
                    throw new InvalidOperationException(
                        $"MCP 清理目标超过单次上限 {MaxCleanupTargets}；请缩小范围后重新扫描。");
                }
            }

            if (!group.Members.Any(item => item.IsDirectMatch))
            {
                throw new InvalidOperationException(
                    $"MCP 候选组 {group.RootProcessId} 没有直接 MCP 标记，计划已拒绝。");
            }

            evidence.Add(new McpCleanupGroupEvidence(
                group.RootProcessId,
                group.ParentProcessId,
                group.CleanupEvidence,
                group.EvidenceConfidence,
                group.SupersedingSessions)
            {
                ParentProcessName = group.ParentProcessName,
                ParentStartTimeUtcTicks = group.ParentStartTimeUtc?.UtcTicks ?? 0,
                MinimumAgeMinutes = group.MinimumCleanupAgeMinutes,
                MaximumCpuPercentOneCore = McpLiveCpuCandidateThreshold
            });
        }

        VerifyMcpCpuSample(
            result,
            evidence,
            McpLiveSampleDuration,
            cancellationToken: default,
            asynchronous: false).GetAwaiter().GetResult();
        return new McpPlanMaterial(result, evidence);
    }

    private static IReadOnlyList<CleanupTarget> BuildWeFlowTargets(LagDiagnosticSnapshot snapshot)
    {
        return snapshot.WeFlowProcesses
            .Where(item =>
                item.StartTimeUtc.HasValue &&
                item.CpuPercentOneCore >= WeFlowCpuCandidateThreshold)
            .Select(item => CaptureLiveTarget(item.ProcessId, item.StartTimeUtc, "WeFlow"))
            .ToArray();
    }

    private static CleanupTarget CaptureLiveTarget(
        int processId,
        DateTimeOffset? expectedStartTimeUtc,
        string? expectedName)
    {
        if (processId is 0 or 4 || processId == Environment.ProcessId)
        {
            throw new InvalidOperationException($"进程 {processId} 属于受保护目标，无法加入清理计划。");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var processName = process.ProcessName;
            var startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (expectedStartTimeUtc.HasValue &&
                Math.Abs(startTimeUtc.UtcTicks - expectedStartTimeUtc.Value.UtcTicks) > TimeSpan.TicksPerSecond)
            {
                throw new InvalidOperationException($"进程 {processId} 的启动时间已变化，请重新扫描。");
            }

            if (!string.IsNullOrWhiteSpace(expectedName) &&
                !string.Equals(processName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"进程 {processId} 的名称已变化，请重新扫描。");
            }

            return new CleanupTarget(processId, startTimeUtc.UtcTicks, processName, false);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"进程 {processId} 已退出，请重新扫描。", exception);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"无法核对进程 {processId} 的身份，请重新扫描。", exception);
        }
    }

    private static void ValidateMcpSnapshotGroup(
        McpProcessGroup group,
        IReadOnlyDictionary<int, NativeProcessEntry> processTable)
    {
        if (group.CleanupEvidence == McpCleanupEvidenceKind.None)
        {
            throw new InvalidOperationException(
                $"MCP 候选组 {group.RootProcessId} 缺少可信候选证据。");
        }

        if (group.Members is null ||
            group.Members.Count == 0 ||
            group.Members.Select(item => item.ProcessId).Distinct().Count() != group.Members.Count ||
            !group.ProcessIds.ToHashSet().SetEquals(group.Members.Select(item => item.ProcessId)))
        {
            throw new InvalidOperationException(
                $"MCP 候选组 {group.RootProcessId} 的成员快照不完整。");
        }

        var members = group.Members.ToDictionary(item => item.ProcessId);
        ValidateMcpTopology(
            group.RootProcessId,
            group.ParentProcessId,
            members.ToDictionary(
                item => item.Key,
                item => item.Value.ParentProcessId));
        foreach (var member in group.Members)
        {
            if (!member.StartTimeUtc.HasValue)
            {
                throw new InvalidOperationException(
                    $"MCP 成员 {member.ProcessId} 缺少启动时间。");
            }

            if (!processTable.TryGetValue(member.ProcessId, out var live) ||
                live.ParentProcessId != member.ParentProcessId)
            {
                throw new InvalidOperationException(
                    $"MCP 成员 {member.ProcessId} 的父进程关系已变化，请重新扫描。");
            }
        }

        ValidateMcpEvidenceState(
            group.RootProcessId,
            group.ParentProcessId,
            group.CleanupEvidence,
            group.ParentProcessName,
            group.ParentStartTimeUtc?.UtcTicks ?? 0,
            group.SupersedingSessions,
            group.RootStartTimeUtc?.UtcTicks ?? 0,
            processTable);
    }

    private static void ValidateMcpExecutionEvidence(
        CleanupPlan plan,
        IReadOnlyList<VerifiedCleanupTarget> verified,
        IReadOnlyDictionary<int, NativeProcessEntry> processTable)
    {
        var evidenceByRoot = plan.McpEvidence
            .GroupBy(item => item.RootProcessId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var targetGroups = verified
            .GroupBy(item => item.Target.McpGroupRootProcessId)
            .ToArray();
        if (targetGroups.Any(group => group.Key <= 0) ||
            evidenceByRoot.Count != targetGroups.Length ||
            evidenceByRoot.Any(item => item.Value.Length != 1))
        {
            throw new InvalidOperationException("MCP 计划的目标组与候选证据无法一一对应。");
        }

        foreach (var targetGroup in targetGroups)
        {
            if (!evidenceByRoot.TryGetValue(targetGroup.Key, out var matchingEvidence))
            {
                throw new InvalidOperationException(
                    $"MCP 目标组 {targetGroup.Key} 缺少候选证据。");
            }

            var evidence = matchingEvidence[0];
            var targets = targetGroup.Select(item => item.Target).ToArray();
            var parentByProcess = new Dictionary<int, int>();
            foreach (var target in targets)
            {
                if (!processTable.TryGetValue(target.ProcessId, out var live) ||
                    live.ParentProcessId != target.ParentProcessId)
                {
                    throw new InvalidOperationException(
                        $"MCP 成员 {target.ProcessId} 的父进程关系已变化；整份计划已拒绝。");
                }

                parentByProcess[target.ProcessId] = target.ParentProcessId;
                if (target.IsMcpDirectMatch &&
                    !IsLiveMcpDirectMatch(target, target.ParentProcessId))
                {
                    throw new InvalidOperationException(
                        $"MCP 成员 {target.ProcessId} 已失去直接 MCP 标记；整份计划已拒绝。");
                }
            }

            if (!targets.Any(item => item.IsMcpDirectMatch))
            {
                throw new InvalidOperationException(
                    $"MCP 目标组 {targetGroup.Key} 缺少直接 MCP 标记成员。");
            }

            ValidateMcpTopology(
                targetGroup.Key,
                evidence.ParentProcessId,
                parentByProcess);
            var newestStartTicks = targets.Max(item => item.StartTimeUtcTicks);
            var minimumAge = TimeSpan.FromMinutes(
                Math.Clamp(evidence.MinimumAgeMinutes, 5, 24 * 60));
            if (DateTimeOffset.UtcNow.UtcTicks - newestStartTicks < minimumAge.Ticks)
            {
                throw new InvalidOperationException(
                    $"MCP 目标组 {targetGroup.Key} 尚未达到计划年龄门槛。");
            }

            ValidateMcpEvidenceState(
                targetGroup.Key,
                evidence.ParentProcessId,
                evidence.Kind,
                evidence.ParentProcessName,
                evidence.ParentStartTimeUtcTicks,
                evidence.SupersedingSessions,
                targets.Single(item => item.ProcessId == targetGroup.Key).StartTimeUtcTicks,
                processTable);
        }
    }

    private static void ValidateMcpTopology(
        int rootProcessId,
        int rootParentProcessId,
        IReadOnlyDictionary<int, int> parentByProcess)
    {
        if (!parentByProcess.TryGetValue(rootProcessId, out var actualRootParent) ||
            actualRootParent != rootParentProcessId)
        {
            throw new InvalidOperationException(
                $"MCP 根进程 {rootProcessId} 的父进程关系无效。");
        }

        foreach (var processId in parentByProcess.Keys)
        {
            var current = processId;
            var visited = new HashSet<int>();
            while (current != rootProcessId)
            {
                if (!visited.Add(current) ||
                    !parentByProcess.TryGetValue(current, out var parent) ||
                    !parentByProcess.ContainsKey(parent))
                {
                    throw new InvalidOperationException(
                        $"MCP 成员 {processId} 无法沿计划父子链回溯到根进程 {rootProcessId}。");
                }

                current = parent;
            }
        }
    }

    private static void ValidateMcpEvidenceState(
        int rootProcessId,
        int parentProcessId,
        McpCleanupEvidenceKind kind,
        string parentProcessName,
        long parentStartTimeUtcTicks,
        IReadOnlyList<McpSessionIdentity> supersedingSessions,
        long rootStartTimeUtcTicks,
        IReadOnlyDictionary<int, NativeProcessEntry> processTable)
    {
        switch (kind)
        {
            case McpCleanupEvidenceKind.OrphanedParent:
                if (parentProcessId > 0 && processTable.ContainsKey(parentProcessId))
                {
                    throw new InvalidOperationException(
                        $"MCP 根进程 {rootProcessId} 的父进程 {parentProcessId} 已重新出现，孤儿证据失效。");
                }
                return;
            case McpCleanupEvidenceKind.SupersededByNewerSameParent:
                if (!processTable.ContainsKey(parentProcessId) ||
                    string.IsNullOrWhiteSpace(parentProcessName) ||
                    parentStartTimeUtcTicks <= 0 ||
                    !McpResidueAnalyzer.IsTrustedLaunchingProcessName(parentProcessName))
                {
                    throw new InvalidOperationException(
                        $"MCP 根进程 {rootProcessId} 的可信父进程证据失效。");
                }

                VerifyLiveProcessIdentity(
                    parentProcessId,
                    parentProcessName,
                    parentStartTimeUtcTicks,
                    "可信父进程");
                if (supersedingSessions is null || supersedingSessions.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"MCP 根进程 {rootProcessId} 缺少更新会话身份。");
                }

                foreach (var session in supersedingSessions)
                {
                    if (!session.RootStartTimeUtc.HasValue ||
                        session.ParentProcessId != parentProcessId ||
                        session.RootStartTimeUtc.Value.UtcTicks <= rootStartTimeUtcTicks ||
                        !processTable.TryGetValue(session.RootProcessId, out var liveRoot) ||
                        liveRoot.ParentProcessId != parentProcessId)
                    {
                        throw new InvalidOperationException(
                            $"MCP 更新会话 {session.RootProcessId} 已失去同父替代证据。");
                    }

                    VerifyLiveProcessIdentity(
                        session.RootProcessId,
                        session.RootProcessName,
                        session.RootStartTimeUtc.Value.UtcTicks,
                        "更新会话根进程");
                    if (!session.MarkerStartTimeUtc.HasValue ||
                        session.MarkerProcessId <= 0 ||
                        !processTable.TryGetValue(session.MarkerProcessId, out var liveMarker) ||
                        liveMarker.ParentProcessId != session.MarkerParentProcessId)
                    {
                        throw new InvalidOperationException(
                            $"MCP 更新会话 {session.RootProcessId} 的标记进程已变化。");
                    }

                    var marker = new CleanupTarget(
                        session.MarkerProcessId,
                        session.MarkerStartTimeUtc.Value.UtcTicks,
                        session.MarkerProcessName,
                        false);
                    VerifyLiveProcessIdentity(
                        marker.ProcessId,
                        marker.ProcessName,
                        marker.StartTimeUtcTicks,
                        "更新会话标记进程");
                    if (!IsLiveMcpDirectMatch(marker, session.MarkerParentProcessId))
                    {
                        throw new InvalidOperationException(
                            $"MCP 更新会话 {session.RootProcessId} 已失去 MCP 标记。");
                    }
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"MCP 根进程 {rootProcessId} 的证据类型无效。");
        }
    }

    private static bool IsLiveMcpDirectMatch(
        CleanupTarget target,
        int parentProcessId)
    {
        var input = new ProcessAnalysisInput(
            target.ProcessId,
            parentProcessId,
            target.ProcessName,
            new DateTimeOffset(target.StartTimeUtcTicks, TimeSpan.Zero),
            0,
            0,
            0,
            WindowsNative.TryReadImagePath(target.ProcessId),
            WindowsNative.TryReadCommandLine(target.ProcessId));
        return McpResidueAnalyzer.IsDirectMatch(input);
    }

    private static void VerifyLiveProcessIdentity(
        int processId,
        string expectedName,
        long expectedStartTimeUtcTicks,
        string role)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            if (Math.Abs(startTimeUtcTicks - expectedStartTimeUtcTicks) > TimeSpan.TicksPerSecond ||
                !string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{role} {expectedName} ({processId}) 的身份已变化。");
            }
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"{role} {expectedName} ({processId}) 已退出。",
                exception);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"无法核对{role} {expectedName} ({processId}) 的身份。",
                exception);
        }
    }

    private static async Task VerifyMcpCpuSample(
        IReadOnlyList<CleanupTarget> targets,
        IReadOnlyList<McpCleanupGroupEvidence> evidence,
        TimeSpan duration,
        CancellationToken cancellationToken,
        bool asynchronous)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var samples = new List<McpCpuSample>(targets.Count);
        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VerifyLiveProcessIdentity(
                    target.ProcessId,
                    target.ProcessName,
                    target.StartTimeUtcTicks,
                    "MCP 采样目标");
                var process = Process.GetProcessById(target.ProcessId);
                samples.Add(new McpCpuSample(
                    target,
                    process,
                    process.TotalProcessorTime));
            }

            var timer = Stopwatch.StartNew();
            if (asynchronous)
            {
                await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Thread.Sleep(duration);
                cancellationToken.ThrowIfCancellationRequested();
            }
            timer.Stop();

            var evidenceByRoot = evidence.ToDictionary(item => item.RootProcessId);
            foreach (var group in samples.GroupBy(item => item.Target.McpGroupRootProcessId))
            {
                if (!evidenceByRoot.TryGetValue(group.Key, out var groupEvidence))
                {
                    throw new InvalidOperationException(
                        $"MCP CPU 采样组 {group.Key} 缺少证据配置。");
                }

                var processorDelta = TimeSpan.Zero;
                foreach (var sample in group)
                {
                    sample.Process.Refresh();
                    if (sample.Process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"MCP 采样目标 {sample.Target.ProcessId} 已退出，请重新扫描。");
                    }

                    processorDelta += sample.Process.TotalProcessorTime - sample.InitialProcessorTime;
                }

                var cpuPercentOneCore =
                    processorDelta.TotalMilliseconds /
                    Math.Max(1, timer.Elapsed.TotalMilliseconds) *
                    100;
                var threshold = Math.Min(
                    McpLiveCpuCandidateThreshold,
                    Math.Max(0, groupEvidence.MaximumCpuPercentOneCore));
                if (cpuPercentOneCore > threshold)
                {
                    throw new InvalidOperationException(
                        $"MCP 目标组 {group.Key} 的执行前短采样为单核 {cpuPercentOneCore:n1}%，高于 {threshold:n1}% 门槛。");
                }
            }
        }
        finally
        {
            foreach (var sample in samples)
            {
                sample.Process.Dispose();
            }
        }
    }

    private static (string Scope, string Impact, bool Administrator, bool Disconnect) Describe(
        CleanupAction action,
        IReadOnlyList<CleanupTarget> targets)
    {
        return action switch
        {
            CleanupAction.McpResidue => (
                $"{targets.Count} 个明确列出的旧 computer-use MCP 成员进程",
                "会结束旧 MCP 会话；最新会话组会保留。",
                false,
                false),
            CleanupAction.WeFlow => (
                $"{targets.Count} 个持续高 CPU 的 WeFlow 进程",
                "未保存的 WeFlow 工作可能丢失。",
                false,
                false),
            _ when ServiceDefinitions.TryGetValue(action, out var service) => (
                $"Windows 服务 {service.DisplayName} ({service.ServiceName})",
                service.Impact,
                true,
                service.MayDisconnect),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知清理动作。")
        };
    }

    private void WritePlanCore(CleanupPlan plan)
    {
        var path = PendingPlanPath();
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(plan, LagCleanerJson.Indented),
                new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private CleanupPlan? ReadPendingPlanCore()
    {
        var path = PendingPlanPath();
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CleanupPlan>(
            File.ReadAllText(path, Encoding.UTF8),
            LagCleanerJson.Compact);
    }

    private static void ValidateExpectedPlan(
        CleanupPlan plan,
        string? expectedPlanId,
        CleanupAction? expectedAction)
    {
        if (expectedPlanId is not null &&
            !string.Equals(plan.PlanId, expectedPlanId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"待执行计划 ID 为 {plan.PlanId}，与调用方确认的 {expectedPlanId} 不一致。");
        }

        if (expectedAction.HasValue && plan.Action != expectedAction.Value)
        {
            throw new InvalidOperationException(
                $"待执行计划动作为 {plan.Action}，与调用方确认的 {expectedAction.Value} 不一致。");
        }
    }

    private static void ValidatePlanShape(CleanupPlan plan)
    {
        if (plan is null)
        {
            throw new InvalidOperationException("待执行计划为空。");
        }

        if (string.IsNullOrWhiteSpace(plan.PlanId) ||
            plan.PlanId.Length != 32 ||
            !plan.PlanId.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("待执行计划 ID 格式无效。");
        }

        if (string.IsNullOrWhiteSpace(plan.ConfirmationToken) ||
            plan.ConfirmationToken.Length != 8 ||
            !plan.ConfirmationToken.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("待执行计划确认令牌格式无效。");
        }

        if (!Enum.IsDefined(plan.Action))
        {
            throw new InvalidOperationException("待执行计划动作无效。");
        }

        if (plan.Targets is null)
        {
            throw new InvalidOperationException("待执行计划缺少目标集合。");
        }

        if (plan.Targets.Count > MaxCleanupTargets)
        {
            throw new InvalidOperationException(
                $"计划包含 {plan.Targets.Count} 个目标，超过单次上限 {MaxCleanupTargets}。");
        }

        if (plan.ExpiresAtUtc - plan.CreatedAtUtc > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException("待执行计划有效期超过 30 分钟上限。");
        }

        foreach (var target in plan.Targets)
        {
            if (target is null ||
                target.ProcessId <= 0 ||
                target.StartTimeUtcTicks <= 0 ||
                string.IsNullOrWhiteSpace(target.ProcessName) ||
                target.ProcessName.Length > 260)
            {
                throw new InvalidOperationException("待执行计划包含无效进程身份。");
            }
        }

        if (plan.Action == CleanupAction.McpResidue &&
            (plan.McpEvidence is null || plan.McpEvidence.Count == 0))
        {
            throw new InvalidOperationException("MCP 计划缺少候选证据。");
        }
    }

    private static void ValidatePlan(
        CleanupPlan plan,
        string confirmationToken,
        bool allowDisconnect,
        bool allowServiceRestart,
        bool requiresAdministrator,
        bool mayDisconnectSession)
    {
        if (DateTimeOffset.UtcNow > plan.ExpiresAtUtc)
        {
            throw new InvalidOperationException("清理计划已过期，请重新扫描并生成计划。");
        }

        var expected = Encoding.UTF8.GetBytes(plan.ConfirmationToken.ToUpperInvariant());
        var supplied = Encoding.UTF8.GetBytes((confirmationToken ?? "").Trim().ToUpperInvariant());
        if (expected.Length != supplied.Length ||
            !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            throw new UnauthorizedAccessException("确认令牌不匹配。");
        }

        if (plan.Targets.Count > MaxCleanupTargets)
        {
            throw new InvalidOperationException(
                $"计划包含 {plan.Targets.Count} 个目标，超过单次上限 {MaxCleanupTargets}。");
        }

        if (requiresAdministrator && !allowServiceRestart)
        {
            throw new InvalidOperationException("该计划会重启 Windows 服务；请显式确认服务重启。");
        }

        if (mayDisconnectSession && !allowDisconnect)
        {
            throw new InvalidOperationException("该计划会断开远程桌面；请显式传入 --allow-disconnect。");
        }

        if (requiresAdministrator && !IsAdministrator())
        {
            throw new UnauthorizedAccessException("该计划需要管理员权限。");
        }
    }

    private static async Task<IReadOnlyList<CleanupItemResult>> StopProcessesAsync(
        CleanupPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Targets.Count == 0)
        {
            return [new CleanupItemResult(plan.Scope, false, "计划中没有可执行目标。")];
        }

        var verifiedTargets = await VerifyAllProcessTargetsAsync(plan, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var results = new List<CleanupItemResult>();
            var gracefullyExited = new HashSet<int>();
            if (plan.Action == CleanupAction.WeFlow)
            {
                var gracePeriod = Stopwatch.StartNew();
                foreach (var verified in verifiedTargets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!HasExited(verified.Process))
                    {
                        _ = TryCloseMainWindow(verified.Process);
                    }
                }

                foreach (var verified in verifiedTargets)
                {
                    var remaining = TimeSpan.FromSeconds(8) - gracePeriod.Elapsed;
                    if (remaining > TimeSpan.Zero &&
                        await WaitForExitAsync(verified.Process, remaining, cancellationToken).ConfigureAwait(false))
                    {
                        gracefullyExited.Add(verified.Target.ProcessId);
                    }
                }
            }

            foreach (var verified in verifiedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = verified.Target;
                var process = verified.Process;
                var label = $"{target.ProcessName} ({target.ProcessId})";
                if (gracefullyExited.Contains(target.ProcessId) || HasExited(process))
                {
                    results.Add(new CleanupItemResult(label, true, "进程已正常退出。"));
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: false);
                    var exited = await WaitForExitAsync(
                        process,
                        TimeSpan.FromSeconds(5),
                        cancellationToken).ConfigureAwait(false);
                    results.Add(new CleanupItemResult(
                        label,
                        exited,
                        exited ? "已结束计划中的单个进程。" : "终止请求已发送，进程在五秒内仍未退出。"));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    Win32Exception)
                {
                    results.Add(new CleanupItemResult(label, false, exception.Message));
                }
            }

            return results;
        }
        finally
        {
            foreach (var verified in verifiedTargets)
            {
                verified.Process.Dispose();
            }
        }
    }

    private static async Task<IReadOnlyList<VerifiedCleanupTarget>> VerifyAllProcessTargetsAsync(
        CleanupPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Action is not CleanupAction.McpResidue and not CleanupAction.WeFlow)
        {
            throw new InvalidOperationException("当前计划动作不允许结束进程。");
        }

        if (plan.Targets.Count > MaxCleanupTargets)
        {
            throw new InvalidOperationException(
                $"计划包含 {plan.Targets.Count} 个目标，超过单次上限 {MaxCleanupTargets}。");
        }

        var seen = new HashSet<int>();
        var verified = new List<VerifiedCleanupTarget>(plan.Targets.Count);
        try
        {
            foreach (var target in plan.Targets)
            {
                if (!seen.Add(target.ProcessId))
                {
                    throw new InvalidOperationException($"计划包含重复 PID {target.ProcessId}。");
                }

                if (target.ProcessId is 0 or 4 || target.ProcessId == Environment.ProcessId)
                {
                    throw new InvalidOperationException($"计划包含受保护 PID {target.ProcessId}。");
                }

                if (target.KillProcessTree)
                {
                    throw new InvalidOperationException(
                        $"PID {target.ProcessId} 请求结束动态进程树，计划已拒绝。");
                }

                Process process;
                try
                {
                    process = Process.GetProcessById(target.ProcessId);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidOperationException(
                        $"目标 {target.ProcessName} ({target.ProcessId}) 已退出；整份计划已拒绝。",
                        exception);
                }

                try
                {
                    var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks;
                    var processName = process.ProcessName;
                    if (Math.Abs(startTime - target.StartTimeUtcTicks) > TimeSpan.TicksPerSecond ||
                        !string.Equals(processName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"目标 {target.ProcessName} ({target.ProcessId}) 的身份已变化；整份计划已拒绝。");
                    }

                    if (plan.Action == CleanupAction.WeFlow &&
                        !string.Equals(processName, "WeFlow", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"PID {target.ProcessId} 已失去 WeFlow 身份；整份计划已拒绝。");
                    }

                    verified.Add(new VerifiedCleanupTarget(target, process));
                }
                catch
                {
                    process.Dispose();
                    throw;
                }
            }

            if (plan.Action == CleanupAction.McpResidue)
            {
                var processTable = WindowsNative.ReadProcessTable();
                ValidateMcpExecutionEvidence(plan, verified, processTable);
                await VerifyMcpCpuSample(
                        plan.Targets,
                        plan.McpEvidence,
                        McpLiveSampleDuration,
                        cancellationToken,
                        asynchronous: true)
                    .ConfigureAwait(false);
            }

            return verified;
        }
        catch
        {
            foreach (var item in verified)
            {
                item.Process.Dispose();
            }

            throw;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryCloseMainWindow(Process process)
    {
        try
        {
            return process.CloseMainWindow();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            Win32Exception)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (HasExited(process))
        {
            return true;
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return HasExited(process);
        }
    }

    private static async Task<IReadOnlyList<CleanupItemResult>> RestartServiceAsync(
        ServiceCleanupDefinition service,
        CancellationToken cancellationToken)
    {
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    $"无法连接服务控制管理器：{new Win32Exception(Marshal.GetLastWin32Error()).Message}")
            ];
        }

        using var serviceHandle = OpenService(
            manager,
            service.ServiceName,
            ServiceQueryStatus |
            ServiceEnumerateDependents |
            ServiceStart |
            ServiceStop);
        if (serviceHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    FormatWin32Failure("无法打开服务", error))
            ];
        }

        ServiceStatusProcess initial;
        try
        {
            initial = QueryServiceStatus(serviceHandle);
        }
        catch (Win32Exception exception)
        {
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    exception.Message)
            ];
        }
        if (initial.CurrentState == ServiceStopped)
        {
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    "服务原本处于停止状态；已拒绝改变其状态。")
            ];
        }

        if (initial.CurrentState != ServiceRunning)
        {
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    $"服务当前状态为 {ServiceStateName(initial.CurrentState)}；仅允许重启稳定运行中的服务。")
            ];
        }

        var dependentServices = service.ServiceName == "TermService"
            ? EnumerateActiveDependentServices(serviceHandle)
            : [];
        var servicesToRestore = new List<ServiceDependency>(dependentServices);
        try
        {
            foreach (var dependent in dependentServices)
            {
                await StopNamedServiceAsync(
                        manager,
                        dependent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await StopServiceAsync(
                    serviceHandle,
                    service.DisplayName,
                    cancellationToken)
                .ConfigureAwait(false);
            await StartServiceAsync(
                    serviceHandle,
                    service.DisplayName,
                    cancellationToken)
                .ConfigureAwait(false);
            await StartNamedServicesAsync(
                    manager,
                    dependentServices.AsEnumerable().Reverse(),
                    cancellationToken)
                .ConfigureAwait(false);
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    true,
                    dependentServices.Count == 0
                        ? "服务已重新启动并确认处于运行状态。"
                        : $"服务已重新启动并确认处于运行状态；同时恢复 {dependentServices.Count} 个依赖服务。")
            ];
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            TimeoutException or
            OperationCanceledException or
            InvalidOperationException)
        {
            var recovered = await TryEnsureServiceRunningAsync(serviceHandle).ConfigureAwait(false);
            var recoveredDependents = await TryEnsureNamedServicesAsync(
                    manager,
                    servicesToRestore.AsEnumerable().Reverse())
                .ConfigureAwait(false);
            var recoveryMessage = recovered && recoveredDependents
                ? "已恢复目标服务和依赖服务到运行状态。"
                : "恢复运行失败，请立即在服务管理器中检查目标服务及其依赖服务。";
            return
            [
                new CleanupItemResult(
                    service.DisplayName,
                    false,
                    $"服务重启失败：{exception.Message} {recoveryMessage}")
            ];
        }
    }

    private static IReadOnlyList<ServiceDependency> EnumerateActiveDependentServices(
        SafeServiceHandle serviceHandle)
    {
        if (EnumDependentServices(
                serviceHandle,
                ServiceActive,
                IntPtr.Zero,
                0,
                out var bytesNeeded,
                out var servicesReturned))
        {
            return [];
        }

        var error = Marshal.GetLastWin32Error();
        if (error is not (ErrorMoreData or ErrorInsufficientBuffer) || bytesNeeded <= 0)
        {
            if (error == 0)
            {
                return [];
            }

            throw new Win32Exception(
                error,
                FormatWin32Failure("枚举服务依赖项失败", error));
        }

        var buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!EnumDependentServices(
                    serviceHandle,
                    ServiceActive,
                    buffer,
                    bytesNeeded,
                    out _,
                    out servicesReturned))
            {
                error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    FormatWin32Failure("枚举服务依赖项失败", error));
            }

            var result = new List<ServiceDependency>(servicesReturned);
            var itemSize = Marshal.SizeOf<EnumServiceStatusNative>();
            for (var index = 0; index < servicesReturned; index++)
            {
                var item = Marshal.PtrToStructure<EnumServiceStatusNative>(
                    IntPtr.Add(buffer, checked(index * itemSize)));
                var serviceName = Marshal.PtrToStringUni(item.ServiceName);
                var displayName = Marshal.PtrToStringUni(item.DisplayName);
                if (!string.IsNullOrWhiteSpace(serviceName) &&
                    item.Status.CurrentState == ServiceRunning)
                {
                    result.Add(new ServiceDependency(
                        serviceName,
                        string.IsNullOrWhiteSpace(displayName)
                            ? serviceName
                            : displayName));
                }
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static async Task StopNamedServiceAsync(
        SafeServiceHandle manager,
        ServiceDependency service,
        CancellationToken cancellationToken)
    {
        using var handle = OpenService(
            manager,
            service.ServiceName,
            ServiceQueryStatus | ServiceStart | ServiceStop);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                FormatWin32Failure($"无法打开依赖服务 {service.DisplayName}", error));
        }

        await StopServiceAsync(handle, service.DisplayName, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task StartNamedServicesAsync(
        SafeServiceHandle manager,
        IEnumerable<ServiceDependency> services,
        CancellationToken cancellationToken)
    {
        foreach (var service in services)
        {
            using var handle = OpenService(
                manager,
                service.ServiceName,
                ServiceQueryStatus | ServiceStart | ServiceStop);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    FormatWin32Failure($"无法打开依赖服务 {service.DisplayName}", error));
            }

            await StartServiceAsync(handle, service.DisplayName, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryEnsureNamedServicesAsync(
        SafeServiceHandle manager,
        IEnumerable<ServiceDependency> services)
    {
        var recovered = true;
        foreach (var service in services)
        {
            try
            {
                using var handle = OpenService(
                    manager,
                    service.ServiceName,
                    ServiceQueryStatus | ServiceStart | ServiceStop);
                if (handle.IsInvalid)
                {
                    recovered = false;
                    continue;
                }

                recovered &= await TryEnsureServiceRunningAsync(handle)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
                recovered = false;
            }
        }

        return recovered;
    }

    private static async Task StopServiceAsync(
        SafeServiceHandle serviceHandle,
        string displayName,
        CancellationToken cancellationToken)
    {
        var status = QueryServiceStatus(serviceHandle);
        if (status.CurrentState == ServiceStopped)
        {
            return;
        }

        if (status.CurrentState != ServiceRunning)
        {
            throw new InvalidOperationException(
                $"服务 {displayName} 当前状态为 {ServiceStateName(status.CurrentState)}，无法执行停止。");
        }

        if (!ControlService(serviceHandle, ServiceControlStop, out _))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                error,
                FormatWin32Failure($"停止服务 {displayName} 失败", error));
        }

        await WaitForServiceStateAsync(
                serviceHandle,
                ServiceStopped,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task StartServiceAsync(
        SafeServiceHandle serviceHandle,
        string displayName,
        CancellationToken cancellationToken)
    {
        StartServiceOrThrow(serviceHandle, displayName);
        await WaitForServiceStateAsync(
                serviceHandle,
                ServiceRunning,
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ServiceStatusProcess QueryServiceStatus(SafeServiceHandle serviceHandle)
    {
        if (!QueryServiceStatusEx(
                serviceHandle,
                ScStatusProcessInfo,
                out var status,
                Marshal.SizeOf<ServiceStatusProcess>(),
                out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "查询服务状态失败。");
        }

        return status;
    }

    private static async Task<ServiceStatusProcess> WaitForServiceStateAsync(
        SafeServiceHandle serviceHandle,
        uint expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = QueryServiceStatus(serviceHandle);
            if (status.CurrentState == expectedState)
            {
                return status;
            }

            var delayMilliseconds = Math.Clamp(status.WaitHint / 10, 100u, 1_000u);
            await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }

        var finalStatus = QueryServiceStatus(serviceHandle);
        throw new TimeoutException(
            $"等待服务进入 {ServiceStateName(expectedState)} 超时；当前状态为 {ServiceStateName(finalStatus.CurrentState)}。");
    }

    private static void StartServiceOrThrow(
        SafeServiceHandle serviceHandle,
        string displayName = "服务")
    {
        if (StartServiceNative(serviceHandle, 0, IntPtr.Zero))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception(
                error,
                FormatWin32Failure($"启动服务 {displayName} 失败", error));
        }
    }

    private static string FormatWin32Failure(string operation, int errorCode)
    {
        var systemMessage = new Win32Exception(errorCode).Message;
        return $"{operation}（Win32 {errorCode}: {systemMessage}）";
    }

    private static async Task<bool> TryEnsureServiceRunningAsync(SafeServiceHandle serviceHandle)
    {
        try
        {
            var status = QueryServiceStatus(serviceHandle);
            if (status.CurrentState == ServiceRunning)
            {
                return true;
            }

            if (status.CurrentState == ServiceStopPending)
            {
                status = await WaitForServiceStateAsync(
                        serviceHandle,
                        ServiceStopped,
                        TimeSpan.FromSeconds(30),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (status.CurrentState == ServiceStopped)
            {
                StartServiceOrThrow(serviceHandle);
            }
            else if (status.CurrentState != ServiceStartPending)
            {
                return false;
            }

            _ = await WaitForServiceStateAsync(
                    serviceHandle,
                    ServiceRunning,
                    TimeSpan.FromSeconds(30),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            TimeoutException)
        {
            return false;
        }
    }

    private static string ServiceStateName(uint state)
    {
        return state switch
        {
            ServiceStopped => "Stopped",
            ServiceStartPending => "StartPending",
            ServiceStopPending => "StopPending",
            ServiceRunning => "Running",
            _ => $"Unknown({state})"
        };
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private FileStream AcquireStateLock(CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    StateLockPath(),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (timer.Elapsed < StateLockTimeout)
            {
                Thread.Sleep(50);
            }
        }
    }

    private async Task<FileStream> AcquireStateLockAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    StateLockPath(),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (timer.Elapsed < StateLockTimeout)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string PendingPlanPath() => Path.Combine(_stateDirectory, PendingPlanFileName);
    private string StateLockPath() => Path.Combine(_stateDirectory, StateLockFileName);
    private string ClaimedPlanPath(string planId) =>
        Path.Combine(_stateDirectory, $"claimed-cleanup-{planId}.json");

    private sealed record ServiceCleanupDefinition(
        string ServiceName,
        string DisplayName,
        string Impact,
        bool MayDisconnect);

    private sealed record ServiceDependency(
        string ServiceName,
        string DisplayName);

    private sealed record VerifiedCleanupTarget(CleanupTarget Target, Process Process);
    private sealed record McpCpuSample(
        CleanupTarget Target,
        Process Process,
        TimeSpan InitialProcessorTime);
    private sealed record McpPlanMaterial(
        IReadOnlyList<CleanupTarget> Targets,
        IReadOnlyList<McpCleanupGroupEvidence> Evidence)
    {
        public static McpPlanMaterial Empty { get; } = new([], []);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeServiceHandle OpenService(
        SafeServiceHandle serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        SafeServiceHandle service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDependentServices(
        SafeServiceHandle service,
        uint serviceState,
        IntPtr services,
        int bufferSize,
        out int bytesNeeded,
        out int servicesReturned);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(
        SafeServiceHandle service,
        uint control,
        out ServiceStatus status);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "StartServiceW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceNative(
        SafeServiceHandle service,
        uint argumentCount,
        IntPtr argumentVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusNative
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatus Status;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }
}
