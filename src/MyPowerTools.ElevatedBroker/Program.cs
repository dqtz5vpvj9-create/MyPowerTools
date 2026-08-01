using MyPowerTools.Broker;

if (args.Length < 2)
{
    return 2;
}

var brokerRoot = Path.GetFullPath(AppContext.BaseDirectory);
var auditPath = Path.Combine(brokerRoot, "Audit", "broker-audit.jsonl");
var auditRoot = WindowsProtectedExecutable.IsProtectedLocation(
    Path.GetFullPath(Environment.ProcessPath ?? ""),
    out _)
    ? brokerRoot
    : null;
var audit = new AuditLog(auditPath, auditRoot);
if (string.Equals(args[0], "portproxy", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "execute-request", StringComparison.OrdinalIgnoreCase))
{
    return await AdbPortProxyApprovalExecutor.ExecuteAsync(
        args[2..],
        audit);
}

if (string.Equals(args[0], "diagnostics", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "file-handles", StringComparison.OrdinalIgnoreCase))
{
    return await SystemFileHandleDiagnosticExecutor.ExecuteAsync(
        args[2..],
        audit);
}

return 2;
