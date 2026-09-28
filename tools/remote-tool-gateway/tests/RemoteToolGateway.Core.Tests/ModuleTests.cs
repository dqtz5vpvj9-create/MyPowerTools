using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;
using RemoteToolGateway.MyPowerTools;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Module-level contract: default-off listener, command surface, settings validation and events.
/// The runtime executor is injected through the internal test seam, so no production module can
/// swap it through a manifest, preference or HTTP field.
/// </summary>
public sealed class ModuleTests
{
    private static ModuleContext Context(string directory, InMemorySecretStore secrets) => new(
        HostVersion: "0.0.0-test",
        ProtocolVersion: "1.0",
        PackageId: RemoteToolGatewayModule.ModuleId,
        ModuleId: RemoteToolGatewayModule.ModuleId,
        DataDirectory: directory,
        CacheDirectory: directory,
        LogDirectory: directory,
        Platform: "windows",
        GrantedCapabilities: ["secret.store"],
        CapabilityProviders: new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["secret.store"] = secrets
        });

    private static async Task<(RemoteToolGatewayModule Module, FakeHostControlBridge Bridge, InMemorySecretStore Secrets, string Directory)> StartAsync()
    {
        var directory = TestPaths.NewDirectory("module");
        var secrets = new InMemorySecretStore();
        var bridge = new FakeHostControlBridge();
        var module = new RemoteToolGatewayModule
        {
            BridgeOverride = bridge,
            AllowLoopbackTransportForTests = true
        };
        var result = await module.InitializeAsync(Context(directory, secrets), CancellationToken.None);
        Assert.True(result.Ok);
        return (module, bridge, secrets, directory);
    }

    private static CommandRequest Request(string commandId, JsonObject? args = null) =>
        new(Guid.NewGuid().ToString("N"), commandId, args ?? new JsonObject());

    private static JsonObject Parse(CommandExecutionResult result)
    {
        Assert.True(result.Success, result.Error?.Message);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    [Fact]
    public async Task Module_StartsWithNoListener_AndTheCommandStartsAndStopsIt()
    {
        var (module, bridge, _, _) = await StartAsync();
        try
        {
            bridge.WithCommand("demo.status");
            Assert.False(module.Service.ListenerRunning);
            var status = await module.GetStatusAsync(CancellationToken.None);
            Assert.Contains("监听未开启", status.Summary);

            var settings = await module.GetSettingsAsync(CancellationToken.None);
            Assert.False(settings.Values["listenerEnabled"]!.GetValue<bool>());
            Assert.Equal(RemoteToolGatewayModule.DefaultPort, settings.Values["port"]!.GetValue<int>());

            var started = Parse(await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.listener.start", new JsonObject { ["address"] = "127.0.0.1", ["port"] = 0 }),
                CancellationToken.None));
            Assert.True(started["running"]!.GetValue<bool>());
            Assert.True(module.Service.ListenerRunning);

            var afterStart = await module.GetSettingsAsync(CancellationToken.None);
            Assert.True(afterStart.Values["listenerEnabled"]!.GetValue<bool>());

            var stopped = Parse(await module.ExecuteCommandAsync(Request("remote-tool-gateway.listener.stop"), CancellationToken.None));
            Assert.False(stopped["running"]!.GetValue<bool>());
            Assert.False(module.Service.ListenerRunning);
            var afterStop = await module.GetSettingsAsync(CancellationToken.None);
            Assert.False(afterStop.Values["listenerEnabled"]!.GetValue<bool>());
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Module_DisposeStopsTheListener()
    {
        var (module, _, _, _) = await StartAsync();
        await module.ExecuteCommandAsync(
            Request("remote-tool-gateway.listener.start", new JsonObject { ["address"] = "127.0.0.1", ["port"] = 0 }),
            CancellationToken.None);
        Assert.True(module.Service.ListenerRunning);

        await module.DisposeAsync(CancellationToken.None);
        Assert.False(module.Service.ListenerRunning);
    }

    [Fact]
    public async Task Module_CreatesAGrantOnlyWithAListener_AndTheCodeIsValid()
    {
        var (module, bridge, secrets, directory) = await StartAsync();
        try
        {
            bridge.WithCommand("input-monitor.status", "input-monitor");
            var withoutListener = await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.create", new JsonObject { ["deviceName"] = "我的手机" }),
                CancellationToken.None);
            Assert.False(withoutListener.Success);
            Assert.Contains("开启监听", withoutListener.Error!.Message);

            await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.listener.start", new JsonObject { ["address"] = "127.0.0.1", ["port"] = 0 }),
                CancellationToken.None);
            var created = Parse(await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.create", new JsonObject
                {
                    ["deviceName"] = "我的手机",
                    ["allowElevated"] = false,
                    ["commandIds"] = new JsonArray("input-monitor.status")
                }),
                CancellationToken.None));

            var code = created["code"]!.GetValue<string>();
            Assert.StartsWith(ControlConnectionCode.Scheme, code);
            Assert.Equal("我的手机", created["deviceName"]!.GetValue<string>());
            Assert.StartsWith("http://127.0.0.1:", created["endpoint"]!.GetValue<string>());
            var grantId = created["grantId"]!.GetValue<string>();
            var token = await secrets.ReadAsync(SecretReference.Create(module.Id, "grant-" + grantId), CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(token));

            // The token only exists in the secret store and inside the one-time code.
            var grantsFile = await File.ReadAllTextAsync(Path.Combine(directory, "grants.json"));
            Assert.DoesNotContain(token!, grantsFile);

            var inspect = Parse(await module.ExecuteCommandAsync(Request("remote-tool-gateway.inspect"), CancellationToken.None));
            Assert.DoesNotContain(token!, inspect.ToJsonString());
            Assert.Single(inspect["grants"]!.AsArray());
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Module_RevokeCancelsAndRemovesTheGrant()
    {
        var (module, bridge, _, _) = await StartAsync();
        try
        {
            bridge.WithCommand("demo.status");
            await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.listener.start", new JsonObject { ["address"] = "127.0.0.1", ["port"] = 0 }),
                CancellationToken.None);
            var created = Parse(await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.create", new JsonObject { ["deviceName"] = "手机", ["commandIds"] = new JsonArray("demo.status") }),
                CancellationToken.None));

            var revoked = Parse(await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.revoke", new JsonObject { ["grantId"] = created["grantId"]!.GetValue<string>() }),
                CancellationToken.None));
            Assert.Equal(created["grantId"]!.GetValue<string>(), revoked["revoked"]!.GetValue<string>());

            var inspect = Parse(await module.ExecuteCommandAsync(Request("remote-tool-gateway.inspect"), CancellationToken.None));
            Assert.Empty(inspect["grants"]!.AsArray());

            var missing = await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.code", new JsonObject { ["grantId"] = created["grantId"]!.GetValue<string>() }),
                CancellationToken.None);
            Assert.False(missing.Success);
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ModuleSettings_RejectNonTailnetAddressesAndReservedPorts()
    {
        var (module, _, _, _) = await StartAsync();
        try
        {
            var badAddress = await module.ValidateSettingsAsync(new SettingsPatch(
                module.Id, 1, new JsonObject { ["listenAddress"] = "192.168.1.20" }), CancellationToken.None);
            Assert.False(badAddress.Ok);

            var wildcard = await module.ValidateSettingsAsync(new SettingsPatch(
                module.Id, 1, new JsonObject { ["listenAddress"] = "0.0.0.0" }), CancellationToken.None);
            Assert.False(wildcard.Ok);

            var fileTransferPort = await module.ValidateSettingsAsync(new SettingsPatch(
                module.Id, 1, new JsonObject { ["port"] = RemoteToolGatewayService.FileTransferPort }), CancellationToken.None);
            Assert.False(fileTransferPort.Ok);

            var good = await module.ValidateSettingsAsync(new SettingsPatch(
                module.Id, 1, new JsonObject { ["listenAddress"] = "100.64.0.2", ["port"] = 49541, ["listenerEnabled"] = false }),
                CancellationToken.None);
            Assert.True(good.Ok, string.Join(";", good.Messages));

            var unknown = await module.ValidateSettingsAsync(new SettingsPatch(
                module.Id, 1, new JsonObject { ["allowLoopback"] = true }), CancellationToken.None);
            Assert.False(unknown.Ok);
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ModuleSettings_StartAndStopTheListener()
    {
        var (module, _, _, _) = await StartAsync();
        try
        {
            var port = FreePort();
            var applied = await module.ApplySettingsAsync(new SettingsSnapshotDocument(
                module.Id, 1, new JsonObject
                {
                    ["listenAddress"] = "127.0.0.1",
                    ["port"] = port,
                    ["listenerEnabled"] = true
                }, DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.True(applied.Values["listenerEnabled"]!.GetValue<bool>());
            Assert.True(module.Service.ListenerRunning);
            Assert.Equal(port, module.Service.ListenerPort);

            await module.ApplySettingsAsync(new SettingsSnapshotDocument(
                module.Id, 2, new JsonObject
                {
                    ["listenAddress"] = "127.0.0.1",
                    ["port"] = port,
                    ["listenerEnabled"] = false
                }, DateTimeOffset.UtcNow), CancellationToken.None);
            Assert.False(module.Service.ListenerRunning);
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Module_PublishesEventsForGrantAndConfirmationChanges()
    {
        var (module, bridge, _, _) = await StartAsync();
        try
        {
            bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
            using var subscription = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var events = new List<MptModuleEvent>();
            var reader = Task.Run(async () =>
            {
                await foreach (var item in module.SubscribeEventsAsync(new EventCursor(0), subscription.Token))
                {
                    lock (events) events.Add(item);
                    if (events.Count >= 2) break;
                }
            });

            await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.listener.start", new JsonObject { ["address"] = "127.0.0.1", ["port"] = 0 }),
                CancellationToken.None);
            var created = Parse(await module.ExecuteCommandAsync(
                Request("remote-tool-gateway.grant.create", new JsonObject
                {
                    ["deviceName"] = "手机",
                    ["allowElevated"] = true,
                    ["commandIds"] = new JsonArray("svc.restart")
                }),
                CancellationToken.None));
            Assert.NotNull(created["code"]);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (events)
                {
                    if (events.Any(item => item.Type == "remote-tool-gateway.grants.changed")) break;
                }

                await Task.Delay(50);
            }

            lock (events)
            {
                Assert.Contains(events, item => item.Type == "remote-tool-gateway.listener.changed");
                Assert.Contains(events, item => item.Type == "remote-tool-gateway.grants.changed");
            }

            subscription.Cancel();
            try { await reader; } catch (OperationCanceledException) { }
        }
        finally { await module.DisposeAsync(CancellationToken.None); }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
