using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.UI;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var command = args.FirstOrDefault() ?? "help";

return command switch
{
    "check" => UiCheck(args.Skip(1).ToArray(), root),
    "snapshot" => UiSnapshot(args.Skip(1).ToArray(), root),
    "screenshot" => UiScreenshot(args.Skip(1).ToArray(), root),
    "shell-snapshot" => UiShellSnapshot(args.Skip(1).ToArray(), root),
    _ => Help()
};

static int UiCheck(string[] args, string root)
{
    var packageDir = args.FirstOrDefault() ?? Path.Combine(root, "modules");
    var reader = new PackageReader();
    var gate = new UiSurfaceGate();
    var issues = reader.DiscoverPackages(Path.GetFullPath(packageDir))
        .SelectMany(gate.CheckPackage)
        .Concat(gate.CheckShellSource(root))
        .ToArray();

    foreach (var issue in issues)
    {
        Console.WriteLine($"{issue.Severity}: {issue.Path}: {issue.Message}");
    }

    if (issues.Length == 0) Console.WriteLine("UI gate passed.");
    return issues.Any(issue => issue.Severity == "error") ? 1 : 0;
}

static int UiSnapshot(string[] args, string root)
{
    var packageDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
        ? args[0]
        : Path.Combine(root, "modules");
    var output = GetOption(args, "--out") ?? Path.Combine(root, "artifacts", "ui-snapshots");
    var request = Request(args, "1920x1080");
    var path = new UiSurfaceGate().WriteSnapshotSet(Path.GetFullPath(packageDir), output, request);
    Console.WriteLine(path);
    return 0;
}

static int UiScreenshot(string[] args, string root)
{
    var normalized = new List<string>();
    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (arg is "--mode" or "--page")
        {
            index++;
            continue;
        }
        normalized.Add(arg);
    }

    var mode = GetOption(args, "--mode");
    if (string.Equals(mode, "fixture", StringComparison.OrdinalIgnoreCase)) normalized.Add("--fixture-only");
    if (string.Equals(mode, "live-runner", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "live", StringComparison.OrdinalIgnoreCase))
    {
        normalized.Add("--live-runner");
    }

    var page = GetOption(args, "--page");
    if (!string.IsNullOrWhiteSpace(page))
    {
        if (string.Equals(mode, "fixture", StringComparison.OrdinalIgnoreCase) && IsProductUiPage(page)) normalized.Add("--product-foundation");
        normalized.Add("--surface");
        normalized.Add(MapUiPageToSurface(page));
    }

    return UiShellSnapshot(normalized.ToArray(), root);
}

static int UiShellSnapshot(string[] args, string root)
{
    var output = GetOption(args, "--out") ?? Path.Combine(root, "artifacts", "shell-ui-snapshots");
    var request = Request(args, "1366x768");
    var productFoundation = HasFlag(args, "--product-foundation");
    var liveRunner = HasFlag(args, "--live") || HasFlag(args, "--live-runner");
    var scenario = GetOption(args, "--scenario") ?? "default";
    var liveNotifications = liveRunner && request.Surface is "remote-notifications" or "remote-notifications-inbox" or "android-tools.notifications.inbox";
    if (!productFoundation && !liveNotifications && !string.Equals(scenario, "default", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Headless interaction scenarios require --product-foundation or the live Remote Notifications page.");
    }

    var contractPath = productFoundation || liveRunner ? null : new UiSurfaceGate().WriteShellSnapshotSet(output, request);
    var renderedPath = productFoundation
        ? ShellRealScreenshotWriter.WriteProductFoundationSnapshotSet(output, request.Theme, request.Size, request.Density, request.Surface, scenario)
        : liveRunner
            ? UiShellSnapshotLiveAsync(args, root, output, request).GetAwaiter().GetResult()
            : ShellRealScreenshotWriter.WriteSnapshotSet(output, request.Theme, request.Size, request.Density, request.Surface);
    if (contractPath is not null) Console.WriteLine(contractPath);
    Console.WriteLine(renderedPath);
    return 0;
}

static UiSnapshotRequest Request(string[] args, string defaultSize) => new(
    GetOption(args, "--surface") ?? "*",
    GetOption(args, "--theme") ?? "light",
    GetOption(args, "--size") ?? defaultSize,
    GetOption(args, "--density") ?? "normal");

static async Task<string> UiShellSnapshotLiveAsync(string[] args, string root, string output, UiSnapshotRequest request)
{
    if (!HasFlag(args, "--fixture-only"))
    {
        using var runnerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using var client = HostControlClient.ForDefaultEndpoint();
            await client.PingAsync(runnerTimeout.Token);
            return await ShellRealScreenshotWriter.WriteSnapshotSetFromHostControlAsync(
                output, request.Theme, request.Size, request.Density, client, "runner-hostcontrol", request.Surface, runnerTimeout.Token);
        }
        catch (Exception ex) when (!HasFlag(args, "--runner-only") && ex is not OperationCanceledException)
        {
            Console.WriteLine($"Runner HostControl unavailable for live screenshot: {ex.Message}");
        }
    }

    using var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
    return await WriteShellSnapshotFromFixtureHostControlAsync(args, root, output, request, fixtureTimeout.Token);
}

static async Task<string> WriteShellSnapshotFromFixtureHostControlAsync(
    string[] args,
    string root,
    string output,
    UiSnapshotRequest request,
    CancellationToken cancellationToken)
{
    var dataRoot = GetOption(args, "--data-root") ?? Path.Combine(Path.GetTempPath(), "MyPowerTools", "shell-live-snapshot", Guid.NewGuid().ToString("N"));
    var modulesRoot = Path.GetFullPath(GetOption(args, "--modules") ?? Path.Combine(root, "modules"));
    var endpoint = CreateFixtureHostControlEndpoint();
    await using var runtime = CreateRuntime(dataRoot);
    runtime.Load(modulesRoot);
    await runtime.RefreshDynamicCommandsAsync(cancellationToken);
    await runtime.RefreshHealthAsync(cancellationToken);
    await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(1500), cancellationToken);

    var builder = WebApplication.CreateBuilder(Array.Empty<string>());
    builder.Logging.ClearProviders();
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(runtime);
    builder.Services.AddSingleton(CreateDefaultAuditLog());
    if (OperatingSystem.IsWindows())
    {
        builder.WebHost.UseNamedPipes(MptNamedPipePolicy.Configure);
    }
    builder.WebHost.ConfigureKestrel(options =>
    {
        if (endpoint.Transport == IpcTransport.NamedPipe)
        {
            options.ListenNamedPipe(endpoint.Address, listen => listen.Protocols = HttpProtocols.Http2);
            return;
        }
        if (File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
        Directory.CreateDirectory(Path.GetDirectoryName(endpoint.Address)!);
        options.ListenUnixSocket(endpoint.Address, listen => listen.Protocols = HttpProtocols.Http2);
    });

    await using var app = builder.Build();
    app.MapGrpcService<HostControlGrpcService>();
    await app.StartAsync(cancellationToken);
    try
    {
        using var client = HostControlClient.ForEndpoint(endpoint);
        await client.PingAsync(cancellationToken);
        return await ShellRealScreenshotWriter.WriteSnapshotSetFromHostControlAsync(
            output, request.Theme, request.Size, request.Density, client, "fixture-hostcontrol", request.Surface, cancellationToken);
    }
    finally
    {
        await app.StopAsync(CancellationToken.None);
        if (endpoint.Transport == IpcTransport.UnixDomainSocket && File.Exists(endpoint.Address)) File.Delete(endpoint.Address);
    }
}

static MptHostRuntime CreateRuntime(string dataRoot)
{
    var paths = RuntimePaths.Create(Path.GetFullPath(dataRoot));
    return new MptHostRuntime(new PackageReader(), PlatformId.Current(), paths,
        [new InProcDotNetModuleHost(), new GrpcIpcModuleRuntime(), new StdioCompatModuleHost()],
        CreateCapabilityProviders());
}

static IReadOnlyDictionary<string, object> CreateCapabilityProviders()
{
    if (!OperatingSystem.IsWindows()) return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    var platform = new WindowsPlatformPack();
    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["display.profile"] = platform.Display };
}

static AuditLog CreateDefaultAuditLog() => new(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", "broker-audit.jsonl"));

static IpcEndpoint CreateFixtureHostControlEndpoint() => OperatingSystem.IsWindows()
    ? new IpcEndpoint(IpcTransport.NamedPipe, $"mypowertools.shell.snapshot.{Guid.NewGuid():N}")
    : new IpcEndpoint(IpcTransport.UnixDomainSocket, Path.Combine(Path.GetTempPath(), $"mypowertools.shell.snapshot.{Guid.NewGuid():N}.sock"));

static string MapUiPageToSurface(string page) => page.Trim().ToLowerInvariant() switch
{
    "home" or "dashboard" => "shell.home",
    "general" or "general-settings" => "shell.general",
    "tools" or "tools-catalog" => "shell.tools-catalog",
    "remote-notifications" or "remote-notifications-inbox" => "android-tools.notifications.inbox",
    "adb-forwarder" or "adb-forwarder-forward" => "adb-forwarder.forward",
    "adb-forwarder-rules" => "adb-forwarder.rules",
    "screenease" or "screenease-profiles" => "screenease.profiles",
    "doubao" or "doubao-agent" => "doubao-agent.services",
    "smartbird" or "smartbird-thermostat" => "smartbird-thermostat.overview",
    "command-palette" or "commands" => "shell.command-palette",
    "module-detail" or "modules" => "shell.module-detail",
    "settings" or "settings-center" => "shell.settings-center",
    "logs" or "logs-viewer" => "shell.logs-viewer",
    "notifications" or "notification-center" => "shell.notification-center",
    "packages" or "package-manager" => "shell.package-manager",
    "diagnostics" or "runtime-diagnostics" => "shell.runtime-diagnostics",
    _ => page
};

static bool IsProductUiPage(string page) => page.Trim().ToLowerInvariant() is
    "home" or "general" or "general-settings" or "tools" or "tools-catalog" or
    "remote-notifications" or "remote-notifications-inbox" or "adb-forwarder" or
    "adb-forwarder-forward" or "adb-forwarder-rules" or "screenease" or
    "screenease-profiles" or "doubao" or "doubao-agent" or "smartbird" or "smartbird-thermostat";

static string? GetOption(string[] values, string name)
{
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase)) return values[index + 1];
    }
    return null;
}

static bool HasFlag(string[] values, string name) => values.Any(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    return Directory.GetCurrentDirectory();
}

static int Help()
{
    Console.WriteLine("mpt-visual-test check <package-dir>");
    Console.WriteLine("mpt-visual-test snapshot [package-dir] [--surface <id>] [--out <dir>]");
    Console.WriteLine("mpt-visual-test screenshot [--mode fixture|live-runner] [--page <page>] [--out <dir>]");
    Console.WriteLine("mpt-visual-test shell-snapshot [--live-runner|--product-foundation] [--surface <id>] [--out <dir>]");
    return 2;
}
