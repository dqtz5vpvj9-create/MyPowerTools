using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Broker;

public sealed class PrivilegedBroker : IPrivilegeBroker
{
    private const string PermissionRequiredErrorCode = "MPT_PERMISSION_REQUIRED";

    private static readonly HashSet<string> BrokeredPermissionLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "elevated",
        "service",
        "serviceUser",
        "serviceSystem",
        "sensitive",
        "broker"
    };

    private readonly AuditLog _auditLog;

    public PrivilegedBroker(AuditLog? auditLog = null)
    {
        _auditLog = auditLog ?? new AuditLog(Path.Combine(Path.GetTempPath(), "MyPowerTools", "broker-audit.jsonl"));
    }

    public IReadOnlyList<BrokerAuditEntry> Audit => _auditLog.ReadAll();

    public BrokerDecision Evaluate(string actionId, string permissionLevel, string reason, string moduleId = "host", string scope = "")
    {
        var normalizedLevel = permissionLevel.Trim();
        var requiresBroker = BrokeredPermissionLevels.Contains(normalizedLevel);
        var decision = new BrokerDecision(actionId, requiresBroker, requiresBroker ? PermissionRequiredErrorCode : "", reason);
        _auditLog.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, actionId, normalizedLevel, scope, reason, decision.RequiresBroker, "evaluated", ""));
        return decision;
    }

    public Task<PrivilegeDecision> EvaluateAsync(PrivilegeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = Evaluate(request.ActionId, request.PermissionLevel, request.Reason, request.ModuleId, request.Scope);
        return Task.FromResult(new PrivilegeDecision(
            decision.RequiresBroker,
            decision.RequiresBroker ? "permission-required" : "allowed",
            decision.Reason,
            decision.ErrorCode));
    }
}

public sealed record BrokerDecision(string ActionId, bool RequiresBroker, string ErrorCode, string Reason);

public sealed record BrokerAuditEntry(
    string AuditId,
    DateTimeOffset Time,
    string ModuleId,
    string ActionId,
    string PermissionLevel,
    string Scope,
    string Reason,
    bool RequiresBroker,
    string Result,
    string Rollback);
