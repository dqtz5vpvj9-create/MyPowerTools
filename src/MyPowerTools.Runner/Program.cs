using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Protocol;
using MyPowerTools.Runner;
using MyPowerTools.Runtime;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var modulesRoot = GetOption(args, "--modules") ?? Path.Combine(root, "modules");
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var platform = PlatformId.Current();
var windowsPlatform = OperatingSystem.IsWindows() ? new WindowsPlatformPack() : null;
var dataRoot = GetOption(args, "--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
var runtimePaths = RuntimePaths.Create(dataRoot);
var hostControlToken = HostControlAuthTokenStore.GetOrCreateToken(runtimePaths.Root);

using var guard = SingleInstanceGuard.Acquire("MyPowerTools.Runner");
if (!guard.OwnsInstance && !once)
{
    Console.WriteLine("MyPowerTools.Runner is already running.");
    return 2;
}

await using var runtime = new MptHostRuntime(new PackageReader(), platform, runtimePaths, CreateTransportRuntimes(), CreateCapabilityProviders(windowsPlatform));
runtime.Load(modulesRoot);
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

var endpoint = IpcEndpoint.RunnerDefault(platform);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new HostControlAuthOptions(hostControlToken));
builder.Services.AddGrpc(options => options.Interceptors.Add<HostControlAuthServerInterceptor>());
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton(new PackageStore(modulesRoot, Path.Combine(root, "schemas")));

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
var tray = await StartTrayAsync(app, root, runtimePaths.Root, args, windowsPlatform);
var hotkeys = await StartHotkeysAsync(root, runtimePaths.Root, args, windowsPlatform, runtime);
try
{
    await app.RunAsync();
}
finally
{
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

static IModuleTransportRuntime[] CreateTransportRuntimes()
{
    return
    [
        new InProcDotNetModuleHost(),
        new GrpcIpcModuleRuntime(),
        new StdioCompatModuleHost()
    ];
}

static IReadOnlyDictionary<string, object> CreateCapabilityProviders(WindowsPlatformPack? windowsPlatform)
{
    if (windowsPlatform is null || !OperatingSystem.IsWindows())
    {
        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
    {
        ["display.profile"] = windowsPlatform.Display
    };
}

static async Task<ITrayService?> StartTrayAsync(WebApplication app, string root, string dataRoot, string[] args, WindowsPlatformPack? platform)
{
    if (args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase) ||
        platform is null ||
        !OperatingSystem.IsWindows())
    {
        return null;
    }

    var tray = platform.Tray;
    var result = await tray.StartAsync(
        new TrayOptions(
            "MyPowerTools.Runner",
            "MyPowerTools Runner",
            null,
            [
                new TrayMenuItem("open-shell", "Open MyPowerTools", IsDefault: true),
                new TrayMenuItem("quit-runner", "Quit Runner", SeparatorBefore: true)
            ]),
        (invocation, cancellationToken) =>
        {
            if (invocation.ActionId == "open-shell")
            {
                StartShell(root, dataRoot);
            }

            if (invocation.ActionId == "quit-runner")
            {
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

static async Task<IHotkeyService?> StartHotkeysAsync(string root, string dataRoot, string[] args, WindowsPlatformPack? platform, MptHostRuntime runtime)
{
    if (args.Contains("--no-hotkeys", StringComparer.OrdinalIgnoreCase) ||
        platform is null ||
        !OperatingSystem.IsWindows())
    {
        return null;
    }

    var hotkeys = platform.Hotkeys;
    var synchronizer = new RunnerHotkeySynchronizer(hotkeys, runtime);
    hotkeys.Pressed += (_, invocation) =>
    {
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

        _ = Task.Run(async () =>
        {
            try
            {
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

static void StartShell(string root, string dataRoot, bool focusCommandPalette = false)
{
    var startInfo = CreateShellStartInfo(root, dataRoot, focusCommandPalette);
    Process.Start(startInfo);
}

static ProcessStartInfo CreateShellStartInfo(string root, string dataRoot, bool focusCommandPalette = false)
{
    var siblingShell = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Shell", "MyPowerTools.Shell.Avalonia.exe"));
    if (File.Exists(siblingShell))
    {
        var siblingStartInfo = new ProcessStartInfo
        {
            FileName = siblingShell,
            UseShellExecute = false
        };
        AddShellArguments(siblingStartInfo, focusCommandPalette);
        siblingStartInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
        return siblingStartInfo;
    }

    var debugShell = Path.Combine(root, "src", "MyPowerTools.Shell.Avalonia", "bin", "Debug", "net10.0", "MyPowerTools.Shell.Avalonia.exe");
    if (File.Exists(debugShell))
    {
        var debugStartInfo = new ProcessStartInfo
        {
            FileName = debugShell,
            UseShellExecute = false
        };
        AddShellArguments(debugStartInfo, focusCommandPalette);
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
    AddShellArguments(startInfo, focusCommandPalette, throughDotnetRun: true);
    return startInfo;
}

static void AddShellArguments(ProcessStartInfo startInfo, bool focusCommandPalette, bool throughDotnetRun = false)
{
    if (!focusCommandPalette)
    {
        return;
    }

    if (throughDotnetRun)
    {
        startInfo.ArgumentList.Add("--");
    }

    startInfo.ArgumentList.Add("--command-palette");
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
