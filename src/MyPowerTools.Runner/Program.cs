using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runner;
using MyPowerTools.Runtime;
using MyPowerTools.ServiceManager.Client;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
PrependAndroidPlatformToolsToPath(root);
var modulesRoot = GetOption(args, "--modules") ?? Path.Combine(root, "modules");
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var platform = PlatformId.Current();
var platformPack = PlatformPackFactory.Create();
var initialEnabledModules = ResolveInitialEnabledModules(args, platform);
var dataRoot = GetOption(args, "--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
var runtimePaths = RuntimePaths.Create(dataRoot);
using var daemonConsole = DaemonProcessConsole.Initialize("runner", runtimePaths.Logs, args);
// ServiceManager token discovery must follow the Runner's selected data root.
// Isolated installs pass --data-root without mutating the parent environment, so
// bind the process-local SDK setting before capability providers create clients.
Environment.SetEnvironmentVariable(ServiceManagerAdminClient.DataRootEnvironmentVariable, runtimePaths.Root);
var developmentToolRoots = ToolDiscoveryConfiguration.Resolve(root, dataRoot, GetOptions(args, "--tool-dir"));
// Default drop folder for custom tools and quick web panels ({ "title", "url" } files).
// Ensuring it exists gives users a discoverable location and lets the catalog watcher attach.
try
{
    Directory.CreateDirectory(ToolDiscoveryConfiguration.CustomToolsDirectory(dataRoot));
}
catch (IOException)
{
    // Tool discovery roots are optional; startup must not fail on an unwritable data root.
}
var hostControlToken = HostControlAuthTokenStore.GetOrCreateToken(runtimePaths.Root);
var runnerInstanceName = GetOption(args, "--instance-name") ?? "MyPowerTools.Runner";
var endpointAddress = GetOption(args, "--endpoint-address");

using var guard = SingleInstanceGuard.Acquire(runnerInstanceName);
if (!guard.OwnsInstance && !once)
{
    Console.WriteLine("MyPowerTools.Runner is already running.");
    return 2;
}

await using var runtime = new MptHostRuntime(
    new PackageReader(),
    platform,
    runtimePaths,
    CreateTransportRuntimes(),
    CreateCapabilityProviders(platformPack),
    initialEnabledModules);
runtime.Load(modulesRoot, developmentToolRoots);
await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

if (once)
{
    await runtime.RefreshHealthAsync(CancellationToken.None);
    var snapshot = runtime.GetDashboardSnapshot();
    Console.WriteLine($"MyPowerTools.Runner indexed {snapshot.Cards.Count} modules from {modulesRoot}");
    foreach (var card in snapshot.Cards)
    {
        Console.WriteLine($"{card.ModuleId} [{card.State}] {card.Summary}");
    }

    return 0;
}

runtime.StartModuleEventPump();

// Hot-reload development tools (tool.json folders and *.mpt.json quick web panels)
// when their files change on disk. Debounced; a manual Refresh remains as fallback.
DevelopmentToolCatalogWatcher? toolCatalogWatcher = null;
if (!args.Contains("--no-watch", StringComparer.OrdinalIgnoreCase))
{
    toolCatalogWatcher = new DevelopmentToolCatalogWatcher(
        developmentToolRoots,
        async cancellationToken =>
        {
            await runtime.RefreshToolCatalogAsync(cancellationToken);
            Console.WriteLine("Tool catalog refreshed (development tool files changed).");
        });
    toolCatalogWatcher.Start();
    if (toolCatalogWatcher.WatchedRootCount > 0)
    {
        Console.WriteLine($"Watching {toolCatalogWatcher.WatchedRootCount} tool director{(toolCatalogWatcher.WatchedRootCount == 1 ? "y" : "ies")} for changes.");
    }
}

var endpoint = string.IsNullOrWhiteSpace(endpointAddress)
    ? IpcEndpoint.RunnerDefault(platform)
    : new IpcEndpoint(
        OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
        endpointAddress);
var builder = WebApplication.CreateBuilder(args);
daemonConsole.ConfigureHostLogging(builder.Logging);
builder.WebHost.SuppressStatusMessages(true);
builder.Services.AddSingleton(new HostControlAuthOptions(hostControlToken));
builder.Services.AddGrpc(options => options.Interceptors.Add<HostControlAuthServerInterceptor>());
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton(new PackageStore(modulesRoot, Path.Combine(root, "schemas")));

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

    var socketPath = endpoint.Address;
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
    options.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<HostControlGrpcService>();
app.MapGet("/", () => $"MyPowerTools.Runner {ProtocolConstants.HostVersion} is running.");

Console.WriteLine($"MyPowerTools.Runner serving HostControl on {endpoint.Transport}:{endpoint.Address}");
var tray = await StartTrayAsync(app, root, runtimePaths.Root, args, platformPack);
var hotkeys = await StartHotkeysAsync(root, runtimePaths.Root, args, platformPack, runtime);
if (!args.Contains("--no-shell-prewarm", StringComparer.OrdinalIgnoreCase))
{
    TryPrewarmShell(root, runtimePaths.Root);
}
// No tool-specific Supervisor is constructed in the Runner entry point. Long-running tool
// services (Doubao, Remote Notifications, etc.) are managed as ServiceManager units, and
// on-demand tool runtimes are selected by manifest via the transport runtime hosts.
try
{
    await app.RunAsync();
}
finally
{
    if (toolCatalogWatcher is not null)
    {
        await toolCatalogWatcher.DisposeAsync();
    }

    await runtime.StopModuleEventPumpAsync();
    if (hotkeys is not null)
    {
        await hotkeys.DisposeAsync();
    }

    if (tray is not null)
    {
        await tray.DisposeAsync();
    }
}

return 0;

static void PrependAndroidPlatformToolsToPath(string root)
{
    var platformTools = Path.Combine(root, "Tools", "AndroidPlatformTools");
    if (!Directory.Exists(platformTools))
    {
        return;
    }

    var inheritedPath = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable(
        "PATH",
        string.IsNullOrWhiteSpace(inheritedPath)
            ? platformTools
            : platformTools + Path.PathSeparator + inheritedPath);
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

static IReadOnlyList<string> GetOptions(string[] args, string name)
{
    var values = new List<string>();
    for (var index = 0; index < args.Length - 1; index++)
    {
        if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
        {
            values.Add(args[++index]);
        }
    }
    return values;
}

static IReadOnlyList<string>? ResolveInitialEnabledModules(string[] args, PlatformId platform)
{
    var requested = GetOptions(args, "--default-enabled-module");
    if (requested.Count > 0)
    {
        return requested;
    }

    var configured = Environment.GetEnvironmentVariable("MPT_DEFAULT_ENABLED_MODULES");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    return string.Equals(platform.OperatingSystem, "macos", StringComparison.OrdinalIgnoreCase)
        ? ["android-tools.notifications"]
        : null;
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

static IReadOnlyDictionary<string, object> CreateCapabilityProviders(IPlatformPack platformPack)
{
    var providers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
    {
        ["service.units"] = new ServiceUnitClientFactory(ServiceManagerAdminClient.ForDefaultEndpoint())
    };

    if (platformPack.Capabilities.Resolve("display.profile").Supported)
    {
        providers["display.profile"] = platformPack.Display;
    }
    if (platformPack.Capabilities.Resolve("notification.desktop").Supported)
    {
        providers["notification.desktop"] = platformPack.Notifications;
    }
    if (platformPack.Capabilities.Resolve("clipboard.image").Supported)
    {
        providers["clipboard.image"] = platformPack.ClipboardImages;
    }
    if (platformPack.Capabilities.Resolve("keyboard.shortcut").Supported)
    {
        providers["keyboard.shortcut"] = platformPack.KeyboardShortcuts;
    }

    return providers;
}

static async Task<ITrayService?> StartTrayAsync(WebApplication app, string root, string dataRoot, string[] args, IPlatformPack platform)
{
    if (args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase) ||
        platform.TrayHost != PlatformTrayHost.Runner ||
        !platform.Capabilities.Resolve("tray").Supported)
    {
        return null;
    }

    var tray = platform.Tray;
    var result = await tray.StartAsync(
        new TrayOptions(
            "MyPowerTools",
            "MyPowerTools",
            Path.Combine(root, "assets", "MyPowerTools.ico"),
            [
                new TrayMenuItem("open-shell", "Open MyPowerTools", IsDefault: true),
                new TrayMenuItem("exit-application", "Exit MyPowerTools", SeparatorBefore: true)
            ]),
        (invocation, cancellationToken) =>
        {
            if (invocation.ActionId == "open-shell")
            {
                StartShell(root, dataRoot);
            }

            if (invocation.ActionId == "exit-application")
            {
                StartShell(root, dataRoot, shutdownShell: true);
                app.Lifetime.StopApplication();
            }

            return Task.CompletedTask;
        },
        CancellationToken.None);

    if (result.Success)
    {
        Console.WriteLine($"MyPowerTools.Runner tray {result.State}: {result.Message}");
        return tray;
    }

    Console.WriteLine($"MyPowerTools.Runner tray {result.State}: {result.Message}");
    await tray.DisposeAsync();
    return null;
}

static async Task<IHotkeyService?> StartHotkeysAsync(string root, string dataRoot, string[] args, IPlatformPack platform, MptHostRuntime runtime)
{
    if (args.Contains("--no-hotkeys", StringComparer.OrdinalIgnoreCase) ||
        !platform.Capabilities.Resolve("hotkey.global").Supported)
    {
        return null;
    }

    var hotkeys = platform.Hotkeys;
    var synchronizer = new RunnerHotkeySynchronizer(hotkeys, runtime);
    hotkeys.Pressed += (_, invocation) =>
    {
        var hotkeyReceivedUtc = DateTimeOffset.UtcNow;
        if (string.Equals(invocation.Id, "command-palette", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                StartShell(root, dataRoot, focusCommandPalette: true);
                Console.WriteLine($"MyPowerTools.Runner hotkey invoked: {invocation.Gesture}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MyPowerTools.Runner hotkey action failed: {ex.Message}");
            }

            return;
        }

        var request = synchronizer.CreateCommandRequest(invocation);
        if (request is null)
        {
            return;
        }
        request.Args["__mptHotkeyReceivedUtc"] = hotkeyReceivedUtc.ToString("O");
        request.Args["__mptHotkeyGesture"] = invocation.Gesture;

        _ = Task.Run(async () =>
        {
            try
            {
                request.Args["__mptCommandDispatchUtc"] = DateTimeOffset.UtcNow.ToString("O");
                var result = await runtime.ExecuteCommandAsync(request, CancellationToken.None);
                Console.WriteLine($"MyPowerTools.Runner hotkey command {request.CommandId}: {result.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MyPowerTools.Runner hotkey command {request.CommandId} failed: {ex.Message}");
            }
        });
    };

    var result = await hotkeys.RegisterAsync(
        new HotkeyRegistration("command-palette", "Ctrl+Alt+Space", "runner", "Open the command palette."),
            CancellationToken.None);
    Console.WriteLine($"MyPowerTools.Runner hotkey {result.State}: {result.Message}");

    await SyncModuleHotkeysAsync(synchronizer, CancellationToken.None);
    _ = Task.Run(() => WatchRuntimeHotkeyBindingsAsync(runtime, synchronizer));

    return hotkeys;
}

static async Task WatchRuntimeHotkeyBindingsAsync(
    MptHostRuntime runtime,
    RunnerHotkeySynchronizer synchronizer)
{
    var lastEventSeq = 0UL;
    while (true)
    {
        foreach (var evt in runtime.HostEventsSince(lastEventSeq))
        {
            lastEventSeq = evt.Seq;
            if (RunnerHotkeySynchronizer.RequiresHotkeySync(evt.Type))
            {
                await SyncModuleHotkeysAsync(synchronizer, CancellationToken.None);
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }
}

static async Task SyncModuleHotkeysAsync(
    RunnerHotkeySynchronizer synchronizer,
    CancellationToken cancellationToken)
{
    foreach (var sync in await synchronizer.SyncAsync(cancellationToken))
    {
        Console.WriteLine($"MyPowerTools.Runner module hotkey {sync.Operation} {sync.Result.State}: {sync.Result.Message}");
    }
}

static void TryPrewarmShell(string root, string dataRoot)
{
    if (IsShellResident())
    {
        return;
    }

    try
    {
        StartShell(root, dataRoot, prewarm: true);
        Console.WriteLine("MyPowerTools.Runner started hidden Shell prewarm.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"MyPowerTools.Runner Shell prewarm failed: {ex.Message}");
    }
}

static bool IsShellResident()
{
    try
    {
        var mutexName = OperatingSystem.IsWindows() ? @"Local\MyPowerTools.Shell" : "MyPowerTools.Shell";
        if (!Mutex.TryOpenExisting(mutexName, out var shellMutex))
        {
            return false;
        }

        shellMutex.Dispose();
        return true;
    }
    catch (WaitHandleCannotBeOpenedException)
    {
        return false;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

static void StartShell(
    string root,
    string dataRoot,
    bool focusCommandPalette = false,
    bool prewarm = false,
    bool shutdownShell = false)
{
    var startInfo = CreateShellStartInfo(
        root,
        dataRoot,
        focusCommandPalette,
        prewarm,
        shutdownShell);
    Process.Start(startInfo);
}

static ProcessStartInfo CreateShellStartInfo(
    string root,
    string dataRoot,
    bool focusCommandPalette = false,
    bool prewarm = false,
    bool shutdownShell = false)
{
    var siblingLauncher = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ExecutableName("MyPowerTools")));
    if (!focusCommandPalette && !prewarm && !shutdownShell && File.Exists(siblingLauncher))
    {
        var launcherStartInfo = new ProcessStartInfo
        {
            FileName = siblingLauncher,
            WorkingDirectory = Path.GetDirectoryName(siblingLauncher)!,
            UseShellExecute = false
        };
        launcherStartInfo.ArgumentList.Add("--data-root");
        launcherStartInfo.ArgumentList.Add(dataRoot);
        return launcherStartInfo;
    }

    var siblingShell = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Shell", ExecutableName("MyPowerTools.Shell.Avalonia")));
    if (File.Exists(siblingShell))
    {
        var siblingStartInfo = new ProcessStartInfo
        {
            FileName = siblingShell,
            UseShellExecute = false
        };
        AddShellArguments(siblingStartInfo, focusCommandPalette, prewarm, shutdownShell);
        siblingStartInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
        return siblingStartInfo;
    }

    var debugShell = Path.Combine(root, "artifacts", "build", "bin", "MyPowerTools.Shell.Avalonia", "debug", ExecutableName("MyPowerTools.Shell.Avalonia"));
    if (File.Exists(debugShell))
    {
        var debugStartInfo = new ProcessStartInfo
        {
            FileName = debugShell,
            UseShellExecute = false
        };
        AddShellArguments(debugStartInfo, focusCommandPalette, prewarm, shutdownShell);
        debugStartInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
        return debugStartInfo;
    }

    var shellProject = Path.Combine(root, "src", "MyPowerTools.Shell.Avalonia", "MyPowerTools.Shell.Avalonia.csproj");
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = root,
        UseShellExecute = false
    };
    startInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(shellProject);
    AddShellArguments(
        startInfo,
        focusCommandPalette,
        prewarm,
        shutdownShell,
        throughDotnetRun: true);
    return startInfo;
}

static void AddShellArguments(
    ProcessStartInfo startInfo,
    bool focusCommandPalette,
    bool prewarm,
    bool shutdownShell,
    bool throughDotnetRun = false)
{
    if (!focusCommandPalette && !prewarm && !shutdownShell)
    {
        return;
    }

    if (throughDotnetRun)
    {
        startInfo.ArgumentList.Add("--");
    }

    if (focusCommandPalette)
    {
        startInfo.ArgumentList.Add("--command-palette");
    }

    if (prewarm)
    {
        startInfo.ArgumentList.Add("--prewarm");
    }

    if (shutdownShell)
    {
        startInfo.ArgumentList.Add("--shutdown-shell");
    }
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

static string ExecutableName(string baseName) =>
    OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
