using System.Net;
using System.Text.Json.Nodes;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.SampleModules.DotNet;
using RemoteToolGateway.Core;

// The embedded runtime hook is process-global by design (it is how the Android host embeds the
// same service), so the tests that use it never run in parallel with the real-endpoint probe.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// End-to-end evidence with the unmodified production bridge: a real <see cref="HostControlGrpcService"/>
/// over a real <see cref="MptHostRuntime"/> (the same embedding the Android host uses) is attached to
/// the published HostControl client, and the gateway then reads the real catalog and executes a real
/// module command through <see cref="HostControlClientBridge"/>. Nothing in the gateway is faked here.
/// </summary>
public sealed class EmbeddedRuntimeTests
{
    [Fact]
    public async Task Gateway_ExecutesARealModuleCommand_ThroughTheRealHostControlClient()
    {
        var root = TestPaths.NewDirectory("embedded");
        var package = Path.Combine(root, "packages", "sample");
        Directory.CreateDirectory(Path.Combine(package, "ui"));
        File.Copy(typeof(SampleDotNetModule).Assembly.Location, Path.Combine(package, "MyPowerTools.SampleModules.DotNet.dll"));
        await File.WriteAllTextAsync(Path.Combine(package, "module.json"), """
        {"schemaVersion":"1.0","id":"sample.dotnet","packageId":"sample-dotnet","displayName":"Sample","version":"0.2.0","moduleSdk":"1.0",
         "entrypoints":[{"kind":"inproc-dotnet","priority":100,"assembly":"MyPowerTools.SampleModules.DotNet.dll","type":"MyPowerTools.SampleModules.DotNet.SampleDotNetModule"}],
         "capabilities":["status","commands","settings"],"tools":["ui/tool.json"]}
        """);
        await File.WriteAllTextAsync(Path.Combine(package, "ui", "tool.json"), """
        {"schemaVersion":"1.0","toolId":"sample.dotnet","ownerModuleId":"sample.dotnet","title":"Sample","type":"generic-module",
         "primaryRouteId":"main","routes":[{"routeId":"main","surfaceId":"sample.dotnet.dashboard","title":"Sample","surface":{"kind":"generic-module"}}]}
        """);

        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(Path.Combine(root, "data")), [new InProcDotNetModuleHost()]);
        try
        {
            runtime.Load(Path.Combine(root, "packages"));
            await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(root, "audit.jsonl")));
            HostControlClient.EmbeddedInvoker = new EmbeddedHostControlInvoker(service);

            var gateway = new RemoteToolGatewayService(
                Path.Combine(root, "gateway"),
                new InMemorySecretStore(),
                new HostControlClientBridge(),
                new RemoteToolGatewayOptions
                {
                    ModuleId = "remote-tool-gateway",
                    DeviceName = "嵌入测试电脑",
                    Platform = PlatformId.Current().ToString(),
                    AllowLoopbackTransport = true
                });
            await gateway.InitializeAsync(CancellationToken.None);
            await gateway.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);
            var created = await gateway.CreateGrantAsync("真实链路手机", ["sample.dotnet.ping"], false, CancellationToken.None);
            var grantId = created["grantId"]!.GetValue<string>();
            var token = await gateway.Grants.ReadTokenAsync(grantId, CancellationToken.None);

            using var client = new GatewayClient(gateway.ListenerPort, token!);

            // 1. The catalog comes from the real runtime, not from a fixture.
            var catalogResponse = await client.GetAsync(ControlWire.CatalogPath);
            Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
            var catalog = await HttpAssert.JsonAsync(catalogResponse);
            var commands = catalog["commands"]!.AsArray().Select(item => item!.AsObject()).ToArray();
            var ping = commands.Single(item => item["commandId"]!.GetValue<string>() == "sample.dotnet.ping");
            Assert.Equal("sample.dotnet", ping["moduleId"]!.GetValue<string>());
            Assert.True(ping["allowed"]!.GetValue<bool>());

            // 2. A real command executes through the real client and returns its real result.
            var submit = await client.PostAsync(ControlWire.InvocationsPath, new JsonObject
            {
                ["invocationId"] = "real-invoke-000001",
                ["commandId"] = "sample.dotnet.ping",
                ["args"] = new JsonObject()
            }.ToJsonString());
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
            var invocation = await HttpAssert.JsonAsync(submit);
            Assert.True(invocation["terminal"]!.GetValue<bool>());
            Assert.Equal("succeeded", invocation["state"]!.GetValue<string>());
            Assert.Contains("pong from SampleDotNetModule", invocation["result"]!["summary"]!.GetValue<string>());

            // 3. Cancel of an unknown invocation is a real HostControl answer, not an exception.
            var cancellation = await new HostControlClientBridge().CancelAsync("real-unknown-000001", CancellationToken.None);
            Assert.Equal("real-unknown-000001", cancellation.InvocationId);
            Console.WriteLine($"[embedded] cancel of an unknown invocation -> accepted={cancellation.Accepted} state='{cancellation.State}'");

            await gateway.DisposeAsync();
        }
        finally
        {
            HostControlClient.EmbeddedInvoker = null;
            await runtime.DisposeAsync();
            TestPaths.TryDelete(root);
        }
    }
}
