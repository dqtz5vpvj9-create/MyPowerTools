using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Loads the packaged module exactly like the Runner does: real MptHostRuntime, real in-proc module
/// host, real HostControl service, module package staged by build.ps1. This is the load path that a
/// packaged release uses, so a manifest, dependency or capability mistake fails here instead of on
/// a user's machine.
/// </summary>
public sealed class ModuleHostIntegrationTests
{
    private static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "MyPowerTools.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        throw new Xunit.Sdk.XunitException("Repository root was not found from " + AppContext.BaseDirectory);
    }

    [Fact]
    public async Task PackagedModule_LoadsInTheRealHost_ListsItsCommands_AndRefusesLoopbackInProductionMode()
    {
        var staged = Path.Combine(RepositoryRoot(), "tools", "remote-tool-gateway", "artifacts", "package");
        if (!Directory.Exists(staged))
        {
            throw new Xunit.Sdk.XunitException(
                "Stage the tool package first: pwsh -NoLogo -NoProfile -File tools/remote-tool-gateway/build.ps1");
        }

        var root = TestPaths.NewDirectory("module-host");
        var package = Path.Combine(root, "packages", "remote-tool-gateway");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(package, Path.GetRelativePath(staged, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")),
            [new InProcDotNetModuleHost()],
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["secret.store"] = new InMemorySecretStore()
            });
        try
        {
            runtime.Load(Path.Combine(root, "packages"));
            await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(root, "audit.jsonl")));
            HostControlClient.EmbeddedInvoker = new EmbeddedHostControlInvoker(service);
            using var client = HostControlClient.ForDefaultEndpoint();

            // The module initialized: its static commands are in the real catalog.
            var commands = await client.ListCommandsAsync("", CancellationToken.None);
            var ids = commands.Commands.Select(command => command.CommandId).ToArray();
            Assert.Contains("remote-tool-gateway.inspect", ids);
            Assert.Contains("remote-tool-gateway.listener.start", ids);
            Assert.Contains("remote-tool-gateway.grant.create", ids);
            Assert.Contains("remote-tool-gateway.confirmation.resolve", ids);

            // Default state: no listener, no grants.
            var inspect = await client.ExecuteCommandAsync("modulehost-inspect", "remote-tool-gateway.inspect",
                new JsonObject { ["includeCatalog"] = false }, CancellationToken.None);
            Assert.True(string.Equals(inspect.State, "succeeded", StringComparison.OrdinalIgnoreCase), inspect.ErrorMessage);
            var payload = JsonNode.Parse(inspect.Summary)!.AsObject();
            Assert.False(payload["listener"]!["running"]!.GetValue<bool>());
            Assert.Empty(payload["grants"]!.AsArray());
            Assert.Empty(payload["pending"]!.AsArray());

            // The packaged module has no test seam: a loopback bind is refused as a non-Tailnet address.
            var loopback = await client.ExecuteCommandAsync("modulehost-loopback", "remote-tool-gateway.listener.start",
                new JsonObject { ["address"] = "127.0.0.1", ["port"] = 49999 }, CancellationToken.None);
            Assert.False(loopback.State.Equals("succeeded", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("Tailscale", loopback.Summary);
            Console.WriteLine($"[module-host] loopback start refused: {loopback.Summary}");
        }
        finally
        {
            HostControlClient.EmbeddedInvoker = null;
            await runtime.DisposeAsync();
            TestPaths.TryDelete(root);
        }
    }
}
