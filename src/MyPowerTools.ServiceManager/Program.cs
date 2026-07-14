using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.ServiceManager.Client;
using MyPowerTools.ServiceManager.Server;

// The ServiceManager is an independent, long-running process: the single execution plane
// for Service Units. Shell and Runner are clients. A Runner restart never affects units.
var dataRoot = GetOption(args, "--data-root")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");

var deployRoot = GetOption(args, "--deploy-root")
    ?? Path.Combine(dataRoot, "ServiceManager");

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(deployRoot);

var platform = PlatformId.Current();
var endpoint = IpcEndpoint.ServiceManagerDefault(platform);

// Separate auth material from HostControl; both live under the same data root.
var token = ServiceManagerAdminClient.SharedTokenStore.GetOrCreateToken(dataRoot);

using var guard = SingleInstanceGuard.Acquire("MyPowerTools.ServiceManager");
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

// Re-adopt any still-running units, then autostart the rest. Never restarts a live process.
await engine.ReconcileAsync(CancellationToken.None);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new IpcAuthOptions(ServiceManagerAdminClient.AuthHeaderName, token));
builder.Services.AddGrpc(options => options.Interceptors.Add<BearerTokenAuthServerInterceptor>());
builder.Services.AddSingleton(engine);
// IHostApplicationLifetime is registered automatically by the host; the gRPC Shutdown RPC
// consumes it to trigger a graceful stop that leaves units running for re-adoption.

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
app.MapGrpcService<ServiceManagerGrpcService>();
app.MapGet("/", () => $"MyPowerTools.ServiceManager is running on {endpoint.Transport}:{endpoint.Address}.");

Console.WriteLine($"MyPowerTools.ServiceManager serving on {endpoint.Transport}:{endpoint.Address}");
Console.WriteLine($"MyPowerTools.ServiceManager deploy root: {deployRoot}");
Console.WriteLine($"MyPowerTools.ServiceManager reconciled {catalog.Manifests.Count} unit manifest(s).");

try
{
    await app.RunAsync();
}
finally
{
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
