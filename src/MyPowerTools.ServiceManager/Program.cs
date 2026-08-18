using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Win32;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.ServiceManager.Client;
using MyPowerTools.ServiceManager.Server;

// The ServiceManager is an independent, long-running process: the single execution plane
// for Service Units. Shell and Runner are clients. A Runner restart never affects units.
var dataRoot = GetOption(args, "--data-root")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
using var daemonConsole = DaemonProcessConsole.Initialize(
    "servicemanager",
    Path.Combine(dataRoot, "logs"),
    args);

// Utility modes: register/unregister the ServiceManager as a Windows login autostart entry
// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). These run before the host starts and exit.
if (args.Contains("--register-autostart", StringComparer.OrdinalIgnoreCase))
{
    return RegisterAutostart(dataRoot);
}

if (args.Contains("--unregister-autostart", StringComparer.OrdinalIgnoreCase))
{
    return UnregisterAutostart();
}

var deployRoot = GetOption(args, "--deploy-root")
    ?? Path.Combine(dataRoot, "ServiceManager");

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(deployRoot);

var platform = PlatformId.Current();
var endpointAddress = GetOption(args, "--endpoint-address");
var endpoint = string.IsNullOrWhiteSpace(endpointAddress)
    ? IpcEndpoint.ServiceManagerDefault(platform)
    : new IpcEndpoint(
        platform.OperatingSystem == "windows" ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
        endpointAddress);

// Separate auth material from HostControl; both live under the same data root.
var token = ServiceManagerAdminClient.SharedTokenStore.GetOrCreateToken(dataRoot);

var instanceName = GetOption(args, "--instance-name") ?? "MyPowerTools.ServiceManager";
using var guard = SingleInstanceGuard.Acquire(instanceName);
if (!guard.OwnsInstance)
{
    Console.WriteLine("MyPowerTools.ServiceManager is already running.");
    return 2;
}

var stateRoot = Path.Combine(dataRoot, "state");
Directory.CreateDirectory(stateRoot);

var catalog = new ServiceUnitCatalog(deployRoot);
var eventBus = new UnitEventBus();
var stateStore = new UnitStateStore(stateRoot);
var engine = new ServiceManagerEngine(catalog, eventBus, stateStore);

var builder = WebApplication.CreateBuilder(args);
daemonConsole.ConfigureHostLogging(builder.Logging);
builder.WebHost.SuppressStatusMessages(true);
builder.Services.AddSingleton(new IpcAuthOptions(ServiceManagerAdminClient.AuthHeaderName, token));
builder.Services.AddGrpc(options => options.Interceptors.Add<BearerTokenAuthServerInterceptor>());
builder.Services.AddSingleton(engine);
// IHostApplicationLifetime is registered automatically by the host; the gRPC Shutdown RPC
// consumes it to trigger a graceful stop that leaves units running for re-adoption.

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

await using var app = builder.Build();
app.MapGrpcService<ServiceManagerGrpcService>();
app.MapGet("/", () => $"MyPowerTools.ServiceManager is running on {endpoint.Transport}:{endpoint.Address}.");

Console.WriteLine($"MyPowerTools.ServiceManager serving on {endpoint.Transport}:{endpoint.Address}");
Console.WriteLine($"MyPowerTools.ServiceManager deploy root: {deployRoot}");

await app.StartAsync();
try
{
    // Expose the control plane before probing workers. A broken unit can consume its full
    // readiness timeout without making lifecycle/status RPCs unavailable. Reload RPCs serialize
    // with this startup reconciliation through ServiceManagerEngine's reload gate.
    await engine.ReconcileAsync(app.Lifetime.ApplicationStopping);
    Console.WriteLine($"MyPowerTools.ServiceManager reconciled {catalog.Manifests.Count} unit manifest(s).");
    await app.WaitForShutdownAsync();
}
finally
{
    await app.StopAsync(CancellationToken.None);
    await engine.DisposeAsync();
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

// Registers the ServiceManager to launch at user login via the HKCU Run key. This keeps the
// process alive across logoff/logon and reboots independently of the Shell and Runner. The
// entry launches headless (no tray) against the same data root so it re-adopts running units.
static int RegisterAutostart(string? dataRoot)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("Autostart registration is Windows-only.");
        return 0;
    }

    dataRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
    var exePath = Environment.ProcessPath!;
    var argList = new List<string> { "--headless", "--data-root", $"\"{dataRoot}\"" };
    var command = $"\"{exePath}\" {string.Join(' ', argList)}";

    try
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key.SetValue("MyPowerTools.ServiceManager", command, RegistryValueKind.String);
        Console.WriteLine($"Registered autostart: {command}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to register autostart: {ex.Message}");
        return 1;
    }
}

static int UnregisterAutostart()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("Autostart registration is Windows-only.");
        return 0;
    }

    try
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("MyPowerTools.ServiceManager", throwOnMissingValue: false);
        Console.WriteLine("Unregistered autostart.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to unregister autostart: {ex.Message}");
        return 1;
    }
}
