using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MyPowerTools.Broker;
using MyPowerTools.HostControl;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.SampleModules.DotNet;
using Proto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class EmbeddedHostControlTests
{
    [Fact]
    public async Task Shared_runtime_serves_catalog_settings_and_commands_without_a_socket()
    {
        await using var fixture = await Fixture.CreateAsync();
        var client = fixture.Client;
        var tools = await client.ListToolsAsync(new Proto.ListToolsRequest { IncludeDisabled = true });
        Assert.Contains(tools.Tools, tool => tool.ToolId == "sample.dotnet");
        var initial = await client.GetSettingsAsync(new Proto.GetSettingsRequest { ModuleId = "sample.dotnet" });
        var settings = await client.UpdateSettingsAsync(new Proto.UpdateSettingsRequest
        {
            ModuleId = "sample.dotnet",
            ExpectedRevision = initial.Revision,
            Patch = new Struct { Fields = { ["enabled"] = Value.ForBool(false) } }
        });
        Assert.False(settings.Values.Fields["enabled"].BoolValue);
        var persisted = await client.GetSettingsAsync(new Proto.GetSettingsRequest { ModuleId = "sample.dotnet" });
        Assert.Equal(settings.Revision, persisted.Revision);
        Assert.False(persisted.Values.Fields["enabled"].BoolValue);
        var success = await client.ExecuteCommandAsync(new Proto.ExecuteCommandRequest
        { InvocationId = "embedded-ping", CommandId = "sample.dotnet.ping" });
        Assert.Equal("succeeded", success.State);
        Assert.Contains("pong", success.Summary);
        var unknown = await client.ExecuteCommandAsync(new Proto.ExecuteCommandRequest
        { InvocationId = "embedded-unknown", CommandId = "sample.dotnet.missing" });
        Assert.Equal("failed", unknown.State);
        Assert.NotEmpty(unknown.ErrorCode);
    }

    [Fact]
    public async Task Disposing_event_subscription_finishes_its_waiting_reader()
    {
        await using var fixture = await Fixture.CreateAsync();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var stream = fixture.Client.SubscribeHostEvents(new Proto.HostEventsRequest { LastEventSeq = ulong.MaxValue });
            var pending = stream.ResponseStream.MoveNext(CancellationToken.None);
            Assert.False(pending.IsCompleted);
            stream.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        var ping = await fixture.Client.PingAsync(new Proto.PingRequest());
        Assert.NotNull(ping);
    }

    [Fact]
    public async Task Unhandled_handler_failure_surfaces_as_an_unknown_rpc_status()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = await Assert.ThrowsAsync<RpcException>(() => fixture.Client.PublishToolEventAsync(
            new Proto.PublishToolEventRequest
            {
                ToolId = "sample.dotnet",
                Topic = "test",
                PayloadJson = "{not-json"
            }).ResponseAsync);
        Assert.Equal(StatusCode.Unknown, failure.StatusCode);
    }

    [Fact]
    public async Task Expired_deadline_surfaces_as_deadline_exceeded_on_event_streams()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stream = fixture.Client.SubscribeHostEvents(
            new Proto.HostEventsRequest { LastEventSeq = ulong.MaxValue },
            deadline: DateTime.UtcNow.AddMilliseconds(-1));
        var failure = await Assert.ThrowsAsync<RpcException>(
            () => stream.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.DeadlineExceeded, failure.StatusCode);
    }

    private sealed class Fixture(string root, MptHostRuntime runtime, Proto.HostControl.HostControlClient client) : IAsyncDisposable
    {
        public Proto.HostControl.HostControlClient Client => client;
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "mpt-embedded", Guid.NewGuid().ToString("N"));
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
            runtime.Load(Path.Combine(root, "packages"));
            await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(root, "audit.jsonl")));
            return new Fixture(root, runtime, new Proto.HostControl.HostControlClient(new EmbeddedHostControlInvoker(service)));
        }
        public async ValueTask DisposeAsync()
        {
            await runtime.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }
}
