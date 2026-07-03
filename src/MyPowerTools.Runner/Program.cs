using System.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var modulesRoot = GetOption(args, "--modules") ?? Path.Combine(root, "modules");
var once = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
var platform = PlatformId.Current();
var dataRoot = GetOption(args, "--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
var runtimePaths = RuntimePaths.Create(dataRoot);

using var guard = SingleInstanceGuard.Acquire("MyPowerTools.Runner");
if (!guard.OwnsInstance && !once)
{
    Console.WriteLine("MyPowerTools.Runner is already running.");
    return 2;
}

await using var runtime = new MptHostRuntime(new PackageReader(), platform, runtimePaths, CreateTransportRuntimes());
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

var endpoint = IpcEndpoint.RunnerDefault(platform);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.Services.AddSingleton(runtime);

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
var tray = await StartTrayAsync(app, root, args);
try
{
    await app.RunAsync();
}
finally
{
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

static async Task<ITrayService?> StartTrayAsync(WebApplication app, string root, string[] args)
{
    if (args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase) || !OperatingSystem.IsWindows())
    {
        return null;
    }

    var tray = new WindowsPlatformPack().Tray;
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
                StartShell(root);
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

static void StartShell(string root)
{
    var startInfo = CreateShellStartInfo(root);
    Process.Start(startInfo);
}

static ProcessStartInfo CreateShellStartInfo(string root)
{
    var siblingShell = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Shell", "MyPowerTools.Shell.Avalonia.exe"));
    if (File.Exists(siblingShell))
    {
        return new ProcessStartInfo
        {
            FileName = siblingShell,
            UseShellExecute = false
        };
    }

    var debugShell = Path.Combine(root, "src", "MyPowerTools.Shell.Avalonia", "bin", "Debug", "net10.0", "MyPowerTools.Shell.Avalonia.exe");
    if (File.Exists(debugShell))
    {
        return new ProcessStartInfo
        {
            FileName = debugShell,
            UseShellExecute = false
        };
    }

    var shellProject = Path.Combine(root, "src", "MyPowerTools.Shell.Avalonia", "MyPowerTools.Shell.Avalonia.csproj");
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = root,
        UseShellExecute = false
    };
    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(shellProject);
    return startInfo;
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
