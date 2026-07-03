namespace MyPowerTools.Runtime;

public sealed class ModuleSupervisor
{
    private const int InterventionFailureThreshold = 3;
    private readonly Dictionary<string, ModuleSupervisorRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeModuleSupervisionSnapshot Observe(RuntimeModuleRecord module, ModuleStatusSnapshot status)
    {
        var moduleId = module.Module.Manifest.Id;
        _records.TryGetValue(moduleId, out var previous);

        var failing = IsFailureState(status.State);
        var consecutiveFailures = failing ? (previous?.ConsecutiveFailureCount ?? 0) + 1 : 0;
        var observationCount = (previous?.ObservationCount ?? 0) + 1;
        var observedAt = DateTimeOffset.UtcNow;
        var supervisorState = ResolveSupervisorState(status.State, consecutiveFailures);
        var action = ResolveAction(module, status, consecutiveFailures);

        var record = new ModuleSupervisorRecord(
            moduleId,
            status.State,
            status.Summary,
            observationCount,
            consecutiveFailures,
            supervisorState,
            action,
            observedAt);
        _records[moduleId] = record;
        return ToSnapshot(record);
    }

    public RuntimeModuleSupervisionSnapshot Snapshot(RuntimeModuleRecord module)
    {
        if (_records.TryGetValue(module.Module.Manifest.Id, out var record))
        {
            return ToSnapshot(record);
        }

        return Observe(module, module.Status);
    }

    public IReadOnlyList<RuntimeModuleSupervisionSnapshot> Snapshots(IEnumerable<RuntimeModuleRecord> modules)
    {
        return modules
            .Select(Snapshot)
            .OrderBy(snapshot => snapshot.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsFailureState(string state)
    {
        return state.Equals("degraded", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("error", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSupervisorState(string moduleState, int consecutiveFailures)
    {
        if (moduleState.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "disabled";
        }

        if (consecutiveFailures >= InterventionFailureThreshold)
        {
            return "intervention-needed";
        }

        if (consecutiveFailures > 0)
        {
            return "watching";
        }

        return "healthy";
    }

    private static string ResolveAction(RuntimeModuleRecord module, ModuleStatusSnapshot status, int consecutiveFailures)
    {
        if (status.State.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Enable the module before health monitoring resumes.";
        }

        if (status.State.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return "Install a compatible entrypoint or required platform capability.";
        }

        if (!IsFailureState(status.State))
        {
            return "No action required.";
        }

        var transport = module.Entrypoint?.Kind ?? "none";
        var prefix = consecutiveFailures >= InterventionFailureThreshold
            ? $"Observed {consecutiveFailures} consecutive unhealthy samples. "
            : "";

        return transport switch
        {
            "grpc-ipc" => prefix + "Inspect runtime process diagnostics, restart the sidecar pool after fixing it, or pause the pool during maintenance.",
            "http" => prefix + "Check the HTTP facade endpoint, service process, and module settings before retrying the health check.",
            "inproc-dotnet" => prefix + "Review module logs and settings, then rerun status refresh.",
            "jsonrpc-stdio" => prefix + "Inspect the stdio command, working directory, and module logs before retrying.",
            _ => prefix + "Review module diagnostics and logs before retrying."
        };
    }

    private static RuntimeModuleSupervisionSnapshot ToSnapshot(ModuleSupervisorRecord record)
    {
        return new RuntimeModuleSupervisionSnapshot(
            record.ModuleId,
            record.LastState,
            record.LastSummary,
            record.ObservationCount,
            record.ConsecutiveFailureCount,
            record.SupervisorState,
            record.NextAction,
            record.LastObservedAt);
    }

    private sealed record ModuleSupervisorRecord(
        string ModuleId,
        string LastState,
        string LastSummary,
        int ObservationCount,
        int ConsecutiveFailureCount,
        string SupervisorState,
        string NextAction,
        DateTimeOffset LastObservedAt);
}
