using MyPowerTools.Broker;

if (args.Length < 2 ||
    !string.Equals(args[0], "portproxy", StringComparison.OrdinalIgnoreCase) ||
    !string.Equals(args[1], "execute-request", StringComparison.OrdinalIgnoreCase))
{
    return 2;
}

var brokerRoot = Path.GetFullPath(AppContext.BaseDirectory);
var auditPath = Path.Combine(brokerRoot, "Audit", "broker-audit.jsonl");
return await AdbPortProxyApprovalExecutor.ExecuteAsync(
    args[2..],
    new AuditLog(auditPath, brokerRoot));
