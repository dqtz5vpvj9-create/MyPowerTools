using MyPowerTools.Broker;
using MyPowerTools.ElevatedBroker;

if (args.Length < 2)
{
    return 2;
}

if (string.Equals(args[0], "input-remap", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "install", StringComparison.OrdinalIgnoreCase))
{
    var dataRoot = GetOption(args, "--data-root")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools");
    var sourcePath = GetOption(args, "--source");
    if (string.IsNullOrWhiteSpace(sourcePath))
    {
        return 2;
    }

    return WindowsInputRemapTaskInstaller.Install(dataRoot, sourcePath);
}

if (string.Equals(args[0], "input-remap", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "uninstall", StringComparison.OrdinalIgnoreCase))
{
    var dataRoot = GetOption(args, "--data-root")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools");
    return WindowsInputRemapTaskInstaller.Uninstall(dataRoot);
}

if (string.Equals(args[0], "input-remap", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "win-space-shift", StringComparison.OrdinalIgnoreCase))
{
    var dataRoot = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
    for (var index = 2; index + 1 < args.Length; index++)
    {
        if (string.Equals(args[index], "--data-root", StringComparison.OrdinalIgnoreCase))
        {
            dataRoot = args[index + 1];
            break;
        }
    }

    dataRoot ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyPowerTools");
    using var remapper = new WindowsWinSpaceShiftRemapper(dataRoot);
    remapper.Start();
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
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

if (string.Equals(args[0], "cleanup", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "local-lag-cleaner", StringComparison.OrdinalIgnoreCase))
{
    return await LocalLagCleanerCleanupExecutor.ExecuteAsync(
        args[2..],
        audit);
}

if (string.Equals(args[0], "nssm-service", StringComparison.OrdinalIgnoreCase) &&
    string.Equals(args[1], "execute-request", StringComparison.OrdinalIgnoreCase))
{
    return await NssmServiceApprovalExecutor.ExecuteAsync(
        args[2..],
        audit);
}

return 2;

static string? GetOption(string[] commandLine, string name)
{
    for (var index = 0; index + 1 < commandLine.Length; index++)
    {
        if (string.Equals(commandLine[index], name, StringComparison.OrdinalIgnoreCase))
        {
            return commandLine[index + 1];
        }
    }

    return null;
}
