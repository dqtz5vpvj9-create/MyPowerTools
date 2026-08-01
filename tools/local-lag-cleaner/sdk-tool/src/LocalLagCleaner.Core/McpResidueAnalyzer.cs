namespace LocalLagCleaner.MyPowerTools;

public static class McpResidueAnalyzer
{
    private static readonly string[] DirectMarkers =
    [
        "doubao-computer-use",
        "doubao_computer_use",
        "computer-use-local",
        "computer_use_local"
    ];

    private static readonly HashSet<string> BridgeProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "python",
        "python3",
        "pythonw",
        "mcp-server",
        "uv",
        "uvx",
        "node",
        "pwsh",
        "powershell"
    };
    private static readonly HashSet<string> TrustedLaunchingProcessNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "codex",
            "chatgpt",
            "mypowertools.runner",
            "mypowertools.shell.avalonia",
            "code",
            "cursor",
            "claude",
            "doubao",
            "doubao-agent",
            "trae",
            "windsurf"
        };

    public static IReadOnlyList<McpProcessGroup> Analyze(
        IReadOnlyList<ProcessAnalysisInput> processes,
        LagCleanerOptions options,
        DateTimeOffset capturedAtUtc)
    {
        options = options.Normalize();
        var byId = processes.ToDictionary(item => item.ProcessId);
        var children = processes
            .GroupBy(item => item.ParentProcessId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.ProcessId).ToArray());
        var directMatches = processes
            .Where(IsDirectMatch)
            .Select(item => item.ProcessId)
            .ToHashSet();
        var matched = new HashSet<int>(directMatches);

        foreach (var directProcessId in directMatches)
        {
            var currentProcessId = directProcessId;
            var visitedAncestors = new HashSet<int>();
            while (byId.TryGetValue(currentProcessId, out var current) &&
                   byId.TryGetValue(current.ParentProcessId, out var parent) &&
                   IsBridgeProcess(parent.Name) &&
                   visitedAncestors.Add(parent.ProcessId))
            {
                matched.Add(parent.ProcessId);
                currentProcessId = parent.ProcessId;
            }

            var pendingDescendants = new Stack<int>();
            pendingDescendants.Push(directProcessId);
            while (pendingDescendants.Count > 0)
            {
                var parentProcessId = pendingDescendants.Pop();
                if (!children.TryGetValue(parentProcessId, out var childIds))
                {
                    continue;
                }

                foreach (var childId in childIds)
                {
                    if (byId.TryGetValue(childId, out var child) &&
                        (IsBridgeProcess(child.Name) || directMatches.Contains(childId)) &&
                        matched.Add(childId))
                    {
                        pendingDescendants.Push(childId);
                    }
                }
            }
        }

        var roots = matched
            .Where(processId =>
                !byId.TryGetValue(processId, out var process) ||
                !matched.Contains(process.ParentProcessId))
            .Select(processId => byId[processId])
            .OrderByDescending(item => item.StartTimeUtc ?? DateTimeOffset.MinValue)
            .ToArray();

        var groups = new List<McpProcessGroup>();
        foreach (var root in roots)
        {
            var members = DescendantsOf(root.ProcessId, matched, children)
                .Select(processId => byId[processId])
                .ToArray();
            var newestStart = members
                .Where(item => item.StartTimeUtc.HasValue)
                .Select(item => item.StartTimeUtc)
                .Max();
            var youngestAge = newestStart.HasValue
                ? Math.Max(0, (capturedAtUtc - newestStart.Value).TotalMinutes)
                : 0;
            var cpu = members.Sum(item => item.CpuPercentOneCore);
            var hasReliableAge = newestStart.HasValue;
            var oldEnough = hasReliableAge && youngestAge >= options.StaleMcpMinutes;
            var idle = cpu <= options.IdleMcpCorePercentThreshold;
            var newerSameParent = roots
                .Where(item =>
                    item.ParentProcessId == root.ParentProcessId &&
                    item.ProcessId != root.ProcessId &&
                    IsStrictlyNewer(item, root))
                .OrderByDescending(item => item.StartTimeUtc)
                .ToArray();
            var parentStillAlive = byId.TryGetValue(root.ParentProcessId, out var parent);
            var hasOrphanEvidence = root.ParentProcessId <= 0 || !parentStillAlive;
            var newerForPreservation = hasOrphanEvidence
                ? roots
                    .Where(item =>
                        item.ProcessId != root.ProcessId &&
                        IsStrictlyNewer(item, root))
                    .ToArray()
                : newerSameParent;
            var preserved =
                newerForPreservation.Length < options.PreserveNewestMcpGroups;
            var hasTrustedLiveParent = parentStillAlive &&
                                       parent is not null &&
                                       IsTrustedLaunchingParent(root, parent, matched);
            var hasSupersededEvidence =
                hasTrustedLiveParent &&
                newerSameParent.Length >= options.PreserveNewestMcpGroups;
            var evidence = hasOrphanEvidence
                ? McpCleanupEvidenceKind.OrphanedParent
                : hasSupersededEvidence
                    ? McpCleanupEvidenceKind.SupersededByNewerSameParent
                    : McpCleanupEvidenceKind.None;
            var confidence = evidence switch
            {
                McpCleanupEvidenceKind.OrphanedParent => FindingConfidence.High,
                McpCleanupEvidenceKind.SupersededByNewerSameParent => FindingConfidence.Medium,
                _ => FindingConfidence.Low
            };
            var candidate =
                !preserved &&
                oldEnough &&
                idle &&
                evidence != McpCleanupEvidenceKind.None;
            var supersedingSessions = hasSupersededEvidence
                ? newerSameParent
                    .Take(options.PreserveNewestMcpGroups)
                    .Select(item =>
                    {
                        var marker = DescendantsOf(item.ProcessId, matched, children)
                            .Select(processId => byId[processId])
                            .First(process => directMatches.Contains(process.ProcessId));
                        return new McpSessionIdentity(
                            item.ProcessId,
                            item.ParentProcessId,
                            item.Name,
                            item.StartTimeUtc)
                        {
                            MarkerProcessId = marker.ProcessId,
                            MarkerParentProcessId = marker.ParentProcessId,
                            MarkerProcessName = marker.Name,
                            MarkerStartTimeUtc = marker.StartTimeUtc
                        };
                    })
                    .ToArray()
                : [];
            var reason = preserved
                ? hasOrphanEvidence
                    ? $"仍属于全局最新的 {options.PreserveNewestMcpGroups} 组保留会话。"
                    : $"同一父进程下仍属于最新的 {options.PreserveNewestMcpGroups} 组保留会话。"
                : !hasReliableAge
                    ? "无法读取完整启动时间，证据不足，禁止自动清理。"
                : !oldEnough
                    ? $"会话仅存活 {youngestAge:n0} 分钟，未达到 {options.StaleMcpMinutes} 分钟门槛。"
                    : !idle
                        ? $"采样 CPU 为单核 {cpu:n1}%，仍处于活跃状态。"
                        : evidence == McpCleanupEvidenceKind.OrphanedParent
                            ? $"高可信孤儿证据：父进程 {root.ParentProcessId} 已退出；已静置 {youngestAge:n0} 分钟，采样 CPU 为单核 {cpu:n1}%。"
                            : evidence == McpCleanupEvidenceKind.SupersededByNewerSameParent
                                ? $"中可信同父替代证据：可信父进程 {root.ParentProcessId} 下已有 {newerSameParent.Length} 组更新会话；已静置 {youngestAge:n0} 分钟，采样 CPU 为单核 {cpu:n1}%。"
                                : $"父进程 {root.ParentProcessId} 仍存活，且同父更新会话不足 {options.PreserveNewestMcpGroups} 组，证据不足。";
            groups.Add(new McpProcessGroup(
                root.ProcessId,
                root.Name,
                root.StartTimeUtc,
                members.Select(item => item.ProcessId).Order().ToArray(),
                newestStart,
                youngestAge,
                cpu,
                members.Aggregate(0UL, (total, item) => total + item.PrivateBytes),
                members.Sum(item => Math.Max(0, item.HandleCount)),
                candidate,
                reason)
            {
                ParentProcessId = root.ParentProcessId,
                ParentProcessName = parentStillAlive && parent is not null ? parent.Name : "",
                ParentStartTimeUtc = parentStillAlive && parent is not null ? parent.StartTimeUtc : null,
                Members = members
                    .Select(item => new McpProcessMember(
                        item.ProcessId,
                        item.ParentProcessId,
                        item.Name,
                        item.StartTimeUtc,
                        directMatches.Contains(item.ProcessId)))
                    .OrderBy(item => item.ProcessId)
                    .ToArray(),
                CleanupEvidence = evidence,
                EvidenceConfidence = confidence,
                SupersedingSessions = supersedingSessions,
                MinimumCleanupAgeMinutes = options.StaleMcpMinutes
            });
        }

        return groups;
    }

    public static bool IsDirectMatch(ProcessAnalysisInput process)
    {
        var identity = $"{process.Name}\n{process.ExecutablePath}\n{process.CommandLine}";
        return DirectMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTrustedLaunchingProcessName(string name)
    {
        var normalized = (name ?? "").Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return TrustedLaunchingProcessNames.Contains(normalized);
    }

    private static bool IsBridgeProcess(string name)
    {
        return BridgeProcessNames.Contains(Path.GetFileNameWithoutExtension(name));
    }

    private static bool IsStrictlyNewer(
        ProcessAnalysisInput candidate,
        ProcessAnalysisInput current)
    {
        return candidate.StartTimeUtc.HasValue &&
               current.StartTimeUtc.HasValue &&
               candidate.StartTimeUtc.Value > current.StartTimeUtc.Value;
    }

    private static bool IsTrustedLaunchingParent(
        ProcessAnalysisInput root,
        ProcessAnalysisInput parent,
        IReadOnlySet<int> matched)
    {
        return parent.ProcessId > 0 &&
               parent.ProcessId != root.ProcessId &&
               !matched.Contains(parent.ProcessId) &&
               !IsBridgeProcess(parent.Name) &&
               IsTrustedLaunchingProcessName(parent.Name) &&
               parent.StartTimeUtc.HasValue &&
               root.StartTimeUtc.HasValue &&
               parent.StartTimeUtc.Value <= root.StartTimeUtc.Value;
    }

    private static IReadOnlyList<int> DescendantsOf(
        int rootProcessId,
        IReadOnlySet<int> matched,
        IReadOnlyDictionary<int, int[]> children)
    {
        var result = new List<int>();
        var pending = new Stack<int>();
        pending.Push(rootProcessId);
        while (pending.Count > 0)
        {
            var processId = pending.Pop();
            if (!matched.Contains(processId) || result.Contains(processId))
            {
                continue;
            }

            result.Add(processId);
            if (!children.TryGetValue(processId, out var childIds))
            {
                continue;
            }

            foreach (var childId in childIds)
            {
                pending.Push(childId);
            }
        }

        return result;
    }
}

public sealed record ProcessAnalysisInput(
    int ProcessId,
    int ParentProcessId,
    string Name,
    DateTimeOffset? StartTimeUtc,
    double CpuPercentOneCore,
    ulong PrivateBytes,
    int HandleCount,
    string ExecutablePath,
    string CommandLine);
