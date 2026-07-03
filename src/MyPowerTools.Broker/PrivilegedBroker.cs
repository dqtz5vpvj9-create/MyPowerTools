using MyPowerTools.Protocol;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;

namespace MyPowerTools.Broker;

public sealed class PrivilegedBroker
{
    private readonly AuditLog _auditLog;

    public PrivilegedBroker(AuditLog? auditLog = null)
    {
        _auditLog = auditLog ?? new AuditLog(Path.Combine(Path.GetTempPath(), "MyPowerTools", "broker-audit.jsonl"));
    }

    public IReadOnlyList<BrokerAuditEntry> Audit => _auditLog.ReadAll();

    public BrokerDecision Evaluate(string actionId, string permissionLevel, string reason, string moduleId = "host", string scope = "")
    {
        var requiresBroker = permissionLevel is "elevated" or "service" or "broker";
        var decision = new BrokerDecision(actionId, requiresBroker, requiresBroker ? MptErrorCodes.PermissionRequired : "", reason);
        _auditLog.Append(new BrokerAuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, moduleId, actionId, permissionLevel, scope, reason, decision.RequiresBroker, "evaluated", ""));
        return decision;
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
