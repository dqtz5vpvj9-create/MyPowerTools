using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.UI;
using MyPowerTools.Broker;
using MyPowerTools.Cli;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Shell.Avalonia;
using System.Globalization;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var command = args.FirstOrDefault() ?? "help";

return command switch
{
    "validate" => Validate(args.Skip(1).ToArray(), root),
    "inspect" => Inspect(args.Skip(1).ToArray(), root),
    "run" => RunCommand(args.Skip(1).ToArray(), root),
    "package" => Package(args.Skip(1).ToArray(), root),
    "install" => Install(args.Skip(1).ToArray(), root),
    "uninstall" => Uninstall(args.Skip(1).ToArray(), root),
    "update" => Install(args.Skip(1).ToArray(), root),
    "rollback" => Rollback(args.Skip(1).ToArray(), root),
    "repair" => Repair(args.Skip(1).ToArray(), root),
    "runner" => Runner(args.Skip(1).ToArray(), root),
    "module" => Module(args.Skip(1).ToArray(), root),
    "ui" => Ui(args.Skip(1).ToArray(), root),
    "broker" => Broker(args.Skip(1).ToArray(), root),
    "diagnostics" => Diagnostics(args.Skip(1).ToArray(), root),
    "doctor" => Doctor(root),
    _ => Help()
};

static int Validate(string[] args, string root)
{
    if (args.FirstOrDefault() == "contracts")
    {
        return ValidateContracts(args.Skip(1).ToArray(), root);
    }

    var packageDir = args.FirstOrDefault() ?? Path.Combine(root, "modules");
    var schemaDir = GetOption(args, "--schemas") ?? Path.Combine(root, "schemas");
    var validator = new SchemaPackageValidator(schemaDir);
    var reports = validator.ValidatePackageRoot(Path.GetFullPath(packageDir));
    return PrintReports(reports);
}

static int ValidateContracts(string[] args, string root)
{
    var packageDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
        ? args[0]
        : Path.Combine(root, "modules");
    var schemaDir = GetOption(args, "--schemas") ?? Path.Combine(root, "schemas");
    var dataRoot = GetOption(args, "--data-root") ?? Path.Combine(Path.GetTempPath(), "MyPowerTools", "contract-validation", Guid.NewGuid().ToString("N"));
    var runtime = CreateRuntime(dataRoot);
    try
    {
        var report = new ModuleContractValidator(schemaDir, runtime)
            .ValidateAsync(Path.GetFullPath(packageDir), CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        foreach (var module in report.Modules.OrderBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"contract: {module.ModuleId} state={module.State} commands={module.CommandCount} surfaces={module.SurfaceCount} dashboard={module.HasDashboardSurface} settings={module.SettingsState} logs={module.LogsState}");
        }

        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
        }

        if (report.IsValid)
        {
            Console.WriteLine($"Module contract validation passed: {report.PackageCount} packages, {report.ModuleCount} modules.");
        }

        return report.IsValid ? 0 : 1;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static int Inspect(string[] args, string root)
{
    var packageDir = args.FirstOrDefault() ?? Path.Combine(root, "modules");
    var reader = new PackageReader();
    var packages = reader.DiscoverPackages(Path.GetFullPath(packageDir));
    foreach (var package in packages)
    {
        Console.WriteLine($"{package.Package.Id} {package.Package.Version} - {package.Package.DisplayName}");
        foreach (var module in package.Modules)
        {
            var entrypoints = string.Join(", ", module.Manifest.Entrypoints.Select(entry => $"{entry.Kind}:{entry.Priority}"));
            Console.WriteLine($"  {module.Manifest.Id} - {module.Manifest.DisplayName} [{entrypoints}]");
            var capabilities = module.Manifest.Capabilities.Count == 0
                ? "none"
                : string.Join(", ", module.Manifest.Capabilities.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            Console.WriteLine($"    capabilities: {capabilities}");
            foreach (var requirement in module.Manifest.Requires.OrderBy(requirement => requirement.Capability, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"    requires: {requirement.Capability} {(requirement.Required ? "required" : "optional")} - {requirement.Reason ?? ""}");
            }

            if (module.Manifest.Permissions.Count == 0)
            {
                Console.WriteLine("    permissions: none");
                continue;
            }

            foreach (var permission in module.Manifest.Permissions.OrderBy(permission => permission.Id, StringComparer.OrdinalIgnoreCase))
            {
                var capability = string.IsNullOrWhiteSpace(permission.Capability) ? "" : $" capability={permission.Capability}";
                Console.WriteLine($"    permission: {permission.Id} level={permission.Level}{capability} reason={permission.Reason}");
            }
        }
    }

    return 0;
}

static int RunCommand(string[] args, string root)
{
    var commandId = args.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(commandId))
    {
        Console.WriteLine("mpt run <command-id>");
        return 2;
    }

    var runtime = CreateRuntime();
    try
    {
        runtime.Load(Path.Combine(root, "modules"));
        runtime.RefreshDynamicCommandsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var result = runtime.ExecuteCommand(new CommandRequest(Guid.NewGuid().ToString("N"), commandId, new JsonObject()));
        Console.WriteLine($"{result.State}: {result.Output}{result.Error?.Message}");
        return result.Success ? 0 : 1;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static int Package(string[] args, string root)
{
    var subcommand = args.FirstOrDefault() ?? "hash";
    return subcommand switch
    {
        "hash" => PackageHash(args.Skip(1).ToArray(), root),
        "sign-local" => PackageSignLocal(args.Skip(1).ToArray(), root),
        "trust" => PackageTrust(args.Skip(1).ToArray(), root),
        _ => Help()
    };
}

static int PackageHash(string[] args, string root)
{
    var packageDir = GetPositionalArgs(args).FirstOrDefault() ?? Path.Combine(root, "modules");
    var integrity = new PackageIntegrity();
    var reader = new PackageReader();
    var packages = reader.DiscoverPackages(Path.GetFullPath(packageDir));
    foreach (var package in packages)
    {
        var path = integrity.WriteHashManifest(package.Directory, PackageTrustVerifier.ResolveHashManifestPath(package));
        Console.WriteLine(path);
    }

    return 0;
}

static int PackageSignLocal(string[] args, string root)
{
    var packageDir = GetPositionalArgs(args).FirstOrDefault() ?? Path.Combine(root, "modules");
    var trust = new PackageTrustVerifier();
    var reader = new PackageReader();
    var packages = reader.DiscoverPackages(Path.GetFullPath(packageDir));
    foreach (var package in packages)
    {
        var path = trust.WriteLocalSignatureHook(package.Directory);
        Console.WriteLine(path);
    }

    return 0;
}

static int PackageTrust(string[] args, string root)
{
    var packageDir = GetPositionalArgs(args).FirstOrDefault() ?? Path.Combine(root, "modules");
    var policy = HasFlag(args, "--strict")
        ? PackageTrustPolicy.StrictSigned
        : PackageTrustPolicy.LocalDevelopment;
    var trust = new PackageTrustVerifier();
    var reader = new PackageReader();
    var packages = reader.DiscoverPackages(Path.GetFullPath(packageDir));
    var hasErrors = false;
    foreach (var package in packages)
    {
        var report = trust.Verify(package.Directory, policy);
        Console.WriteLine($"{report.PackageId}: {report.State} policy={report.Policy} signature={report.SignaturePath}");
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"  {issue.Severity}: {issue.Path}: {issue.Message}");
            hasErrors |= issue.Severity == "error";
        }
    }

    return hasErrors ? 1 : 0;
}

static int Install(string[] args, string root)
{
    var packageDir = GetPositionalArgs(args).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(packageDir))
    {
        Console.WriteLine("mpt install <package-dir> [--store-root <dir>]");
        return 2;
    }

    var store = CreateStore(root, GetOption(args, "--store-root"));
    var result = store.Install(Path.GetFullPath(packageDir));
    PrintInstallResult(result);
    return result.Success ? 0 : 1;
}

static int Uninstall(string[] args, string root)
{
    var packageId = GetPositionalArgs(args).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(packageId))
    {
        Console.WriteLine("mpt uninstall <package-id> [--store-root <dir>]");
        return 2;
    }

    var result = CreateStore(root, GetOption(args, "--store-root")).Uninstall(packageId);
    PrintInstallResult(result);
    return result.Success ? 0 : 1;
}

static int Rollback(string[] args, string root)
{
    var packageId = GetPositionalArgs(args).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(packageId))
    {
        Console.WriteLine("mpt rollback <package-id> [--store-root <dir>]");
        return 2;
    }

    var result = CreateStore(root, GetOption(args, "--store-root")).Rollback(packageId);
    PrintInstallResult(result);
    return result.Success ? 0 : 1;
}

static int Repair(string[] args, string root)
{
    var packageId = GetPositionalArgs(args).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(packageId))
    {
        Console.WriteLine("mpt repair <package-id> [--store-root <dir>]");
        return 2;
    }

    var issues = CreateStore(root, GetOption(args, "--store-root")).Repair(packageId);
    foreach (var issue in issues)
    {
        Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
    }

    if (issues.Count == 0)
    {
        Console.WriteLine("repair check passed.");
    }

    return issues.Any(issue => issue.Severity == "error") ? 1 : 0;
}

static int Runner(string[] args, string root)
{
    var subcommand = args.FirstOrDefault() ?? "autostart";
    return subcommand switch
    {
        "autostart" => RunnerAutostart(args.Skip(1).ToArray(), root),
        "process" => RunnerProcess(args.Skip(1).ToArray()),
        _ => Help()
    };
}

static int RunnerAutostart(string[] args, string root)
{
    var action = GetPositionalArgs(args).FirstOrDefault() ?? "status";
    var id = GetOption(args, "--id") ?? "MyPowerTools.Runner";
    var reason = GetOption(args, "--reason") ?? $"CLI runner autostart {action}";
    var platform = new WindowsPlatformPack();
    var broker = new AutostartBroker(platform.Autostart, CreateDefaultAuditLog());

    if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
    {
        var status = broker.GetAsync("runner", id, reason, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"{status.Name}: {status.State} {status.Detail}");
        return status.State == "invalid" ? 2 : 0;
    }

    if (string.Equals(action, "enable", StringComparison.OrdinalIgnoreCase))
    {
        var command = GetOption(args, "--command") ?? ResolveRunnerAutostartCommand(root);
        if (HasFlag(args, "--dry-run"))
        {
            Console.WriteLine($"{id}: dry-run enable {command}");
            return 0;
        }

        var result = broker.EnableAsync("runner", id, command, reason, CancellationToken.None).GetAwaiter().GetResult();
        PrintBrokerResult(result);
        return result.Success ? 0 : 1;
    }

    if (string.Equals(action, "disable", StringComparison.OrdinalIgnoreCase))
    {
        if (HasFlag(args, "--dry-run"))
        {
            Console.WriteLine($"{id}: dry-run disable");
            return 0;
        }

        var result = broker.DisableAsync("runner", id, reason, CancellationToken.None).GetAwaiter().GetResult();
        PrintBrokerResult(result);
        return result.Success ? 0 : 1;
    }

    Console.WriteLine("mpt runner autostart [status|enable|disable] [--id <id>] [--command <command>] [--dry-run]");
    return 2;
}

static int RunnerProcess(string[] args)
{
    var positional = GetPositionalArgs(args);
    var action = positional.FirstOrDefault() ?? "restart";
    if (!string.Equals(action, "restart", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("mpt runner process <restart|pause|resume> <transport-kind> <pool-key> [--reason <reason>] [--until <iso-8601>] [--duration-minutes <minutes>]");
        return 2;
    }

    var transportKind = positional.ElementAtOrDefault(1);
    var poolKey = positional.ElementAtOrDefault(2);
    using var client = HostControlClient.ForDefaultEndpoint();
    if (string.Equals(transportKind, ".", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(poolKey))
    {
        var diagnostics = client.GetRuntimeDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var process = diagnostics.Processes.FirstOrDefault();
        if (process is null)
        {
            Console.WriteLine("No runtime process pool is active.");
            return 1;
        }

        transportKind = process.TransportKind;
        poolKey = process.PoolKey;
    }

    if (string.IsNullOrWhiteSpace(transportKind) || string.IsNullOrWhiteSpace(poolKey))
    {
        Console.WriteLine("mpt runner process <restart|pause|resume> <transport-kind> <pool-key>|. [--reason <reason>] [--until <iso-8601>] [--duration-minutes <minutes>]");
        return 2;
    }

    if (string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(action, "resume", StringComparison.OrdinalIgnoreCase))
    {
        var paused = string.Equals(action, "pause", StringComparison.OrdinalIgnoreCase);
        var reason = GetOption(args, "--reason") ?? "CLI runtime process policy change";
        if (!TryGetPolicyExpiresAt(args, paused, out var expiresAt, out var error))
        {
            Console.WriteLine(error);
            return 2;
        }

        var policy = client.SetRuntimeProcessRestartPolicyAsync(transportKind, poolKey, paused, reason, CancellationToken.None, source: "cli", expiresAt: expiresAt)
            .GetAwaiter()
            .GetResult();
        Console.WriteLine($"{policy.TransportKind} {policy.PoolKey}: {policy.State} {policy.Message}");
        if (policy.ExpiresAt is not null)
        {
            Console.WriteLine($"expires: {policy.ExpiresAt.ToDateTimeOffset():O}");
        }

        if (policy.ModuleIds.Count > 0)
        {
            Console.WriteLine($"modules: {string.Join(",", policy.ModuleIds)}");
        }

        return policy.Success ? 0 : 1;
    }

    var result = client.RestartRuntimeProcessAsync(transportKind, poolKey, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    Console.WriteLine($"{result.TransportKind} {result.PoolKey}: {result.State} {result.Message}");
    if (result.ModuleIds.Count > 0)
    {
        Console.WriteLine($"modules: {string.Join(",", result.ModuleIds)}");
    }

    return result.Success ? 0 : 1;
}

static int Module(string[] args, string root)
{
    var subcommand = args.FirstOrDefault() ?? "list";
    return subcommand switch
    {
        "list" => ModuleList(args.Skip(1).ToArray(), root),
        "enable" => ModuleEnable(args.Skip(1).ToArray(), root, enabled: true),
        "disable" => ModuleEnable(args.Skip(1).ToArray(), root, enabled: false),
        _ => Help()
    };
}

static int ModuleList(string[] args, string root)
{
    var runtime = CreateRuntime(GetOption(args, "--data-root"));
    try
    {
        runtime.Load(GetPackageRoot(args, root));
        var includeDisabled = HasFlag(args, "--include-disabled");
        foreach (var module in runtime.ListModules(includeDisabled).OrderBy(module => module.Module.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var enabled = module.Status.State == "disabled" ? "disabled" : "enabled";
            Console.WriteLine($"{module.Module.Manifest.Id} {enabled} {module.Status.State} - {module.Module.Manifest.DisplayName}");
        }

        return 0;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static int ModuleEnable(string[] args, string root, bool enabled)
{
    var moduleId = GetPositionalArgs(args).FirstOrDefault();
    if (string.IsNullOrWhiteSpace(moduleId))
    {
        Console.WriteLine("mpt module <enable|disable> <module-id> [--modules <package-root>] [--data-root <dir>]");
        return 2;
    }

    var runtime = CreateRuntime(GetOption(args, "--data-root"));
    try
    {
        runtime.Load(GetPackageRoot(args, root));
        var detail = runtime.SetModuleEnabled(moduleId, enabled);
        Console.WriteLine($"{detail.ModuleId}: {(enabled ? "enabled" : "disabled")} ({detail.State})");
        return 0;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static int Ui(string[] args, string root)
{
    var subcommand = args.FirstOrDefault() ?? "check";
    return subcommand switch
    {
        "check" => UiCheck(args.Skip(1).ToArray(), root),
        "snapshot" => UiSnapshot(args.Skip(1).ToArray(), root),
        "shell-snapshot" => UiShellSnapshot(args.Skip(1).ToArray(), root),
        _ => Help()
    };
}

static int UiCheck(string[] args, string root)
{
    var packageDir = args.FirstOrDefault() ?? Path.Combine(root, "modules");
    var reader = new PackageReader();
    var gate = new UiSurfaceGate();
    var issues = reader.DiscoverPackages(Path.GetFullPath(packageDir))
        .SelectMany(gate.CheckPackage)
        .ToArray();

    foreach (var issue in issues)
    {
        Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
    }

    if (issues.Length == 0)
    {
        Console.WriteLine("UI gate passed.");
    }

    return issues.Any(issue => issue.Severity == "error") ? 1 : 0;
}

static int UiSnapshot(string[] args, string root)
{
    var packageDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
        ? args[0]
        : Path.Combine(root, "modules");
    var output = GetOption(args, "--out") ?? Path.Combine(root, "artifacts", "ui-snapshots");
    var request = new UiSnapshotRequest(
        GetOption(args, "--surface") ?? "*",
        GetOption(args, "--theme") ?? "light",
        GetOption(args, "--size") ?? "1920x1080",
        GetOption(args, "--density") ?? "normal");
    var path = new UiSurfaceGate().WriteSnapshotSet(Path.GetFullPath(packageDir), output, request);
    Console.WriteLine(path);
    return 0;
}

static int UiShellSnapshot(string[] args, string root)
{
    var output = GetOption(args, "--out") ?? Path.Combine(root, "artifacts", "shell-ui-snapshots");
    var request = new UiSnapshotRequest(
        GetOption(args, "--surface") ?? "*",
        GetOption(args, "--theme") ?? "light",
        GetOption(args, "--size") ?? "1366x768",
        GetOption(args, "--density") ?? "normal");
    var path = new UiSurfaceGate().WriteShellSnapshotSet(output, request);
    var realPath = ShellRealScreenshotWriter.WriteSnapshotSet(output, request.Theme, request.Size, request.Density);
    Console.WriteLine(path);
    Console.WriteLine(realPath);
    return 0;
}

static int Broker(string[] args, string root)
{
    var subcommand = args.FirstOrDefault() ?? "audit";
    if (subcommand == "audit")
    {
        var audit = CreateDefaultAuditLog();
        foreach (var entry in audit.ReadAll())
        {
            Console.WriteLine($"{entry.Time:O} {entry.ModuleId} {entry.ActionId} {entry.Result} {entry.Scope}");
        }

        return 0;
    }

    if (subcommand == "portproxy")
    {
        return BrokerPortProxy(args.Skip(1).ToArray()).GetAwaiter().GetResult();
    }

    if (subcommand == "secret")
    {
        return BrokerSecret(args.Skip(1).ToArray()).GetAwaiter().GetResult();
    }

    return Help();
}

static int Diagnostics(string[] args, string root)
{
    var runtime = CreateRuntime(GetOption(args, "--data-root"));
    try
    {
        runtime.Load(GetPackageRoot(args, root));
        runtime.RefreshHealthAsync(CancellationToken.None).GetAwaiter().GetResult();
        runtime.RefreshDynamicCommandsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var diagnostics = runtime.GetRuntimeDiagnostics();

        Console.WriteLine($"runner: {diagnostics.RunnerVersion}");
        Console.WriteLine($"hostControlProtocol: {diagnostics.HostControlProtocolVersion}");
        Console.WriteLine($"moduleProtocol: {diagnostics.ModuleProtocolVersion}");
        Console.WriteLine($"platform: {diagnostics.PlatformRid}");
        Console.WriteLine($"dotnet: {diagnostics.DotNetVersion}");
        Console.WriteLine($"eventSeq: {diagnostics.CurrentEventSeq}");
        Console.WriteLine($"packages: {diagnostics.Counts.PackageCount}");
        Console.WriteLine($"modules: {diagnostics.Counts.ModuleCount} enabled={diagnostics.Counts.EnabledModuleCount} disabled={diagnostics.Counts.DisabledModuleCount}");
        Console.WriteLine($"commands: {diagnostics.Counts.CommandCount} dynamic={diagnostics.Counts.DynamicCommandCount}");
        Console.WriteLine($"notifications: {diagnostics.Counts.NotificationCount}");
        Console.WriteLine($"paths.root: {diagnostics.Paths.Root}");
        Console.WriteLine($"paths.packageRoot: {diagnostics.Paths.PackageRoot}");
        foreach (var transport in diagnostics.Transports)
        {
            Console.WriteLine($"transport: {transport.Kind} registered={transport.RuntimeRegistered} modules={transport.ModuleCount}");
        }

        foreach (var process in diagnostics.Processes)
        {
            var modules = string.Join(",", process.ModuleIds);
            var reason = string.IsNullOrWhiteSpace(process.PolicyReason) ? "" : $" reason={process.PolicyReason}";
            var expires = process.PolicyExpiresAt is null ? "" : $" expires={process.PolicyExpiresAt.Value:O}";
            Console.WriteLine($"process: {process.TransportKind} pool={process.PoolKey} state={process.State} pid={process.ProcessId} starts={process.StartCount}/{process.RestartLimit} policy={process.RestartPolicy}{reason}{expires} endpoint={process.Endpoint} modules={modules}");
        }

        foreach (var entry in diagnostics.ProcessPolicyHistory)
        {
            var modules = string.Join(",", entry.ModuleIds);
            var expires = entry.ExpiresAt is null ? "" : $" expires={entry.ExpiresAt.Value:O}";
            Console.WriteLine($"process-policy: rev={entry.Revision} time={entry.Time:O} source={entry.Source} {entry.TransportKind} pool={entry.PoolKey} policy={entry.RestartPolicy} reason={entry.Reason}{expires} modules={modules}");
        }

        foreach (var module in diagnostics.Modules)
        {
            Console.WriteLine($"module: {module.ModuleId} state={module.State} transport={module.TransportKind} selection=\"{module.TransportSelectionReason}\" diagnostics={module.DiagnosticCount} supervisor={module.SupervisorState} failures={module.ConsecutiveFailureCount} observations={module.ObservationCount} action={module.SupervisorAction}");
            foreach (var selection in module.TransportSelectionDiagnostics)
            {
                Console.WriteLine($"module-transport: {module.ModuleId} {selection}");
            }
        }

        return 0;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static async Task<int> BrokerPortProxy(string[] args)
{
    var subcommand = args.FirstOrDefault() ?? "list";
    var platform = new WindowsPlatformPack();
    if (subcommand == "list")
    {
        var rules = await platform.Network.ListPortProxyRulesAsync(CancellationToken.None);
        foreach (var rule in rules)
        {
            Console.WriteLine($"{rule.ListenAddress}:{rule.ListenPort} -> {rule.ConnectAddress}:{rule.ConnectPort}");
        }

        Console.WriteLine($"{rules.Count} portproxy rule(s).");
        return 0;
    }

    var moduleId = GetOption(args, "--module") ?? "cli";
    var reason = GetOption(args, "--reason") ?? $"CLI broker portproxy {subcommand}";
    var listenAddress = GetOption(args, "--listen-address");
    var listenPort = GetIntOption(args, "--listen-port");
    if (string.IsNullOrWhiteSpace(listenAddress) || listenPort is null)
    {
        Console.WriteLine("mpt broker portproxy <apply|remove> --listen-address <address> --listen-port <port> [--connect-address <address> --connect-port <port>] [--module <id>] [--reason <text>]");
        return 2;
    }

    var broker = new NetworkBroker(platform.Network, CreateDefaultAuditLog());

    if (subcommand == "apply")
    {
        var connectAddress = GetOption(args, "--connect-address");
        var connectPort = GetIntOption(args, "--connect-port");
        if (string.IsNullOrWhiteSpace(connectAddress) || connectPort is null)
        {
            Console.WriteLine("mpt broker portproxy apply --listen-address <address> --listen-port <port> --connect-address <address> --connect-port <port>");
            return 2;
        }

        var currentRules = await platform.Network.ListPortProxyRulesAsync(CancellationToken.None);
        var existing = currentRules.FirstOrDefault(rule =>
            string.Equals(rule.ListenAddress, listenAddress, StringComparison.OrdinalIgnoreCase) &&
            rule.ListenPort == listenPort.Value);
        var changeSet = new PortProxyChangeSet(
            [new PortProxyRule(listenAddress, listenPort.Value, connectAddress, connectPort.Value)],
            existing is null ? [] : [existing]);
        var result = await broker.ApplyChangeSetAsync(moduleId, changeSet, reason, CancellationToken.None);
        PrintBrokerResult(result);
        return result.Success ? 0 : 1;
    }

    if (subcommand == "remove")
    {
        var currentRules = await platform.Network.ListPortProxyRulesAsync(CancellationToken.None);
        var existing = currentRules.FirstOrDefault(rule =>
            string.Equals(rule.ListenAddress, listenAddress, StringComparison.OrdinalIgnoreCase) &&
            rule.ListenPort == listenPort.Value);
        if (existing is null)
        {
            Console.WriteLine("noop: no matching portproxy rule found.");
            return 0;
        }

        var result = await broker.ApplyChangeSetAsync(moduleId, new PortProxyChangeSet([], [existing]), reason, CancellationToken.None);
        PrintBrokerResult(result);
        return result.Success ? 0 : 1;
    }

    return Help();
}

static async Task<int> BrokerSecret(string[] args)
{
    var subcommand = args.FirstOrDefault() ?? "self-test";
    if (!string.Equals(subcommand, "self-test", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("mpt broker secret self-test [--module <id>] [--name <name>] [--reason <text>]");
        return 2;
    }

    var moduleId = GetOption(args, "--module") ?? "cli.secret-self-test";
    var name = GetOption(args, "--name") ?? $"self-test-{Guid.NewGuid():N}";
    var reason = GetOption(args, "--reason") ?? "CLI broker secret self-test";
    var secret = $"mpt-secret-self-test-{Guid.NewGuid():N}";
    var platform = new WindowsPlatformPack();
    var broker = new SecretBroker(platform.Secrets, CreateDefaultAuditLog());
    SecretReference? reference = null;

    try
    {
        reference = await broker.SaveAsync(moduleId, name, secret, reason, CancellationToken.None);
        var roundTrip = await broker.ReadAsync(moduleId, reference, reason, CancellationToken.None);
        if (!string.Equals(roundTrip, secret, StringComparison.Ordinal))
        {
            Console.WriteLine($"{reference.Uri}: failed read verification.");
            return 1;
        }

        await broker.DeleteAsync(moduleId, reference, reason, CancellationToken.None);
        var afterDelete = await broker.ReadAsync(moduleId, reference, reason, CancellationToken.None);
        if (afterDelete is not null)
        {
            Console.WriteLine($"{reference.Uri}: failed delete verification.");
            return 1;
        }

        Console.WriteLine($"{reference.Uri}: secret store self-test passed.");
        reference = null;
        return 0;
    }
    finally
    {
        if (reference is not null)
        {
            await broker.DeleteAsync(moduleId, reference, reason, CancellationToken.None);
        }
    }
}

static int Doctor(string root)
{
    var platform = PlatformId.Current();
    Console.WriteLine($"repo: {root}");
    Console.WriteLine($"platform: {platform.Rid}");
    Console.WriteLine($"dotnet: {Environment.Version}");
    var reports = new SchemaPackageValidator(Path.Combine(root, "schemas")).ValidatePackageRoot(Path.Combine(root, "modules"));
    Console.WriteLine($"packages: {reports.Count} checked, errors: {reports.SelectMany(report => report.Issues).Count(issue => issue.Severity == "error")}");
    var runtime = CreateRuntime();
    try
    {
        runtime.Load(Path.Combine(root, "modules"));
        Console.WriteLine($"modules: {runtime.Modules.Count}");
        return reports.All(report => report.IsValid) ? 0 : 1;
    }
    finally
    {
        DisposeRuntime(runtime);
    }
}

static AuditLog CreateDefaultAuditLog()
{
    var auditPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", "broker-audit.jsonl");
    return new AuditLog(auditPath);
}

static MptHostRuntime CreateRuntime(string? dataRoot = null)
{
    var paths = string.IsNullOrWhiteSpace(dataRoot)
        ? RuntimePaths.CreateDefault()
        : RuntimePaths.Create(Path.GetFullPath(dataRoot));
    return new MptHostRuntime(new PackageReader(), PlatformId.Current(), paths, CreateTransportRuntimes());
}

static void DisposeRuntime(MptHostRuntime runtime)
{
    runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static IModuleTransportRuntime[] CreateTransportRuntimes()
{
    return
    [
        new InProcDotNetModuleHost(),
        new GrpcIpcModuleRuntime(),
        new StdioCompatModuleHost()
    ];
}

static PackageStore CreateStore(string root, string? storeRoot = null)
{
    var packageStoreRoot = string.IsNullOrWhiteSpace(storeRoot)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "packages")
        : Path.GetFullPath(storeRoot);
    return new PackageStore(packageStoreRoot, Path.Combine(root, "schemas"));
}

static void PrintInstallResult(PackageInstallResult result)
{
    Console.WriteLine($"{result.PackageId}: {(result.Success ? "success" : "failed")} {result.TargetPath}");
    foreach (var issue in result.Issues)
    {
        Console.WriteLine($"  {issue.Severity}: {issue.Message}");
    }
}

static string ResolveRunnerAutostartCommand(string root)
{
    var siblingRunner = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Runner", "MyPowerTools.Runner.exe"));
    if (File.Exists(siblingRunner))
    {
        return QuoteCommand(siblingRunner);
    }

    var releaseRunner = Path.Combine(root, "artifacts", "release", "win-x64", "Runner", "MyPowerTools.Runner.exe");
    if (File.Exists(releaseRunner))
    {
        return QuoteCommand(releaseRunner);
    }

    var debugRunner = Path.Combine(root, "src", "MyPowerTools.Runner", "bin", "Debug", "net10.0", "MyPowerTools.Runner.exe");
    if (File.Exists(debugRunner))
    {
        return QuoteCommand(debugRunner);
    }

    return $"dotnet run --project {QuoteCommand(Path.Combine(root, "src", "MyPowerTools.Runner", "MyPowerTools.Runner.csproj"))}";
}

static string QuoteCommand(string path)
{
    return $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

static int PrintReports(IReadOnlyList<PackageValidationReport> reports)
{
    var hasErrors = false;
    foreach (var report in reports)
    {
        Console.WriteLine($"{Path.GetFileName(report.PackageDirectory)}: {(report.IsValid ? "valid" : "invalid")}");
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"  {issue.Severity}: {issue.Path}: {issue.Message}");
            hasErrors |= issue.Severity == "error";
        }
    }

    return hasErrors ? 1 : 0;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static int? GetIntOption(string[] args, string name)
{
    var value = GetOption(args, name);
    return int.TryParse(value, out var parsed) ? parsed : null;
}

static bool HasFlag(string[] args, string name)
{
    return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

static bool TryGetPolicyExpiresAt(string[] args, bool paused, out DateTimeOffset? expiresAt, out string error)
{
    expiresAt = null;
    error = "";
    var until = GetOption(args, "--until");
    var duration = GetOption(args, "--duration-minutes");
    if (!paused || (string.IsNullOrWhiteSpace(until) && string.IsNullOrWhiteSpace(duration)))
    {
        return true;
    }

    if (!string.IsNullOrWhiteSpace(until) && !string.IsNullOrWhiteSpace(duration))
    {
        error = "Use either --until or --duration-minutes.";
        return false;
    }

    if (!string.IsNullOrWhiteSpace(duration))
    {
        if (!int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
        {
            error = "--duration-minutes must be a positive integer.";
            return false;
        }

        expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
        return true;
    }

    if (!DateTimeOffset.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
    {
        error = "--until must be an ISO-8601 date/time.";
        return false;
    }

    expiresAt = parsed.ToUniversalTime();
    if (expiresAt.Value <= DateTimeOffset.UtcNow)
    {
        error = "--until must be in the future.";
        return false;
    }

    return true;
}

static string GetPackageRoot(string[] args, string root)
{
    return Path.GetFullPath(GetOption(args, "--modules") ?? Path.Combine(root, "modules"));
}

static IReadOnlyList<string> GetPositionalArgs(string[] args)
{
    var values = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            values.Add(args[i]);
            continue;
        }

        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            i++;
        }
    }

    return values;
}

static void PrintBrokerResult(BrokerOperationResult result)
{
    Console.WriteLine($"{result.State}: {result.Message}");
}

static int Help()
{
    Console.WriteLine("mpt validate <package-dir> [--schemas <schema-dir>]");
    Console.WriteLine("mpt validate contracts [package-root] [--schemas <schema-dir>] [--data-root <dir>]");
    Console.WriteLine("mpt inspect <package-dir>");
    Console.WriteLine("mpt run <command-id>");
    Console.WriteLine("mpt package hash <package-dir>");
    Console.WriteLine("mpt package sign-local <package-dir>");
    Console.WriteLine("mpt package trust <package-dir> [--strict]");
    Console.WriteLine("mpt install <package-dir> [--store-root <dir>]");
    Console.WriteLine("mpt uninstall <package-id> [--store-root <dir>]");
    Console.WriteLine("mpt update <package-dir> [--store-root <dir>]");
    Console.WriteLine("mpt rollback <package-id> [--store-root <dir>]");
    Console.WriteLine("mpt repair <package-id> [--store-root <dir>]");
    Console.WriteLine("mpt runner autostart [status|enable|disable] [--id <id>] [--command <command>] [--dry-run]");
    Console.WriteLine("mpt runner process <restart|pause|resume> <transport-kind> <pool-key>|. [--reason <reason>] [--until <iso-8601>] [--duration-minutes <minutes>]");
    Console.WriteLine("mpt module list [--include-disabled] [--modules <package-root>] [--data-root <dir>]");
    Console.WriteLine("mpt module enable <module-id> [--modules <package-root>] [--data-root <dir>]");
    Console.WriteLine("mpt module disable <module-id> [--modules <package-root>] [--data-root <dir>]");
    Console.WriteLine("mpt ui check <package-dir>");
    Console.WriteLine("mpt ui snapshot [package-dir] [--surface <id|kind>] [--theme <theme>] [--size <width>x<height>] [--density <density>] [--out <dir>]");
    Console.WriteLine("mpt ui shell-snapshot [--surface <id|kind>] [--theme <theme>] [--size <width>x<height>] [--density <density>] [--out <dir>] (writes contract and real Avalonia screenshots)");
    Console.WriteLine("mpt broker audit");
    Console.WriteLine("mpt broker secret self-test [--module <id>] [--name <name>]");
    Console.WriteLine("mpt broker portproxy list");
    Console.WriteLine("mpt broker portproxy apply --listen-address <address> --listen-port <port> --connect-address <address> --connect-port <port>");
    Console.WriteLine("mpt broker portproxy remove --listen-address <address> --listen-port <port>");
    Console.WriteLine("mpt diagnostics [--modules <package-root>] [--data-root <dir>]");
    Console.WriteLine("mpt doctor");
    return 2;
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            return directory.FullName;
        }

        if (Directory.Exists(Path.Combine(directory.FullName, "modules")) &&
            Directory.Exists(Path.Combine(directory.FullName, "schemas")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return Directory.GetCurrentDirectory();
}
