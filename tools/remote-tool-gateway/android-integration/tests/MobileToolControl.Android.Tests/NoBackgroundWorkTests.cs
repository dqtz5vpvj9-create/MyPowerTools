using System.Text.Json.Nodes;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// "默认不连接不轮询": initialize, enable, start, status and device listing must not open a socket,
/// and the catalog cache is a lazy short-lived document cache rather than a background refresh.
/// </summary>
public sealed class NoBackgroundWorkTests
{
    [Fact]
    public async Task LifecycleAndLocalCommandsNeverTouchTheNetwork()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();

        var context = new MyPowerTools.Abstractions.ModuleContext(
            "test-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            harness.DataDirectory,
            harness.DataDirectory,
            harness.DataDirectory,
            "android-arm64",
            ["secret.store"],
            null);

        await harness.Module.EnableAsync(context, CancellationToken.None);
        await harness.Module.StartAsync(context, CancellationToken.None);
        await harness.Module.GetStatusAsync(CancellationToken.None);
        await harness.RunAsync(MobileToolControlOptions.CommandStatus);
        await harness.RunAsync(MobileToolControlOptions.CommandDevicesList);

        Assert.Equal(0, gateway.RequestCount);

        await harness.Module.StopAsync(context, CancellationToken.None);
        await harness.Module.DisableAsync(context, CancellationToken.None);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task TheCatalogCacheServesRepeatsUntilAnExplicitRefresh()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();

        var deviceId = new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" };
        var first = await harness.RunAsync(MobileToolControlOptions.CommandCatalog, deviceId.DeepClone().AsObject());
        var second = await harness.RunAsync(MobileToolControlOptions.CommandCatalog, deviceId.DeepClone().AsObject());
        Assert.Equal(1, gateway.RequestCount);
        Assert.False(ModuleHarness.Payload(first)["fromCache"]!.GetValue<bool>());
        Assert.True(ModuleHarness.Payload(second)["fromCache"]!.GetValue<bool>());

        var refreshed = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentRefresh] = true
            });
        Assert.Equal(2, gateway.RequestCount);
        Assert.False(ModuleHarness.Payload(refreshed)["fromCache"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CheckConnectionIsTheOnlyWayADeviceBecomesReachable()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        var payload = await harness.ImportAsync(gateway);

        // Importing leaves the device unprobed and does not connect.
        var device = payload["devices"]!.AsArray()[0]!;
        Assert.Equal("imported", device["lastState"]!.GetValue<string>());
        Assert.Equal("尚未检查", device["lastDetail"]!.GetValue<string>());
        Assert.Equal(0, gateway.RequestCount);

        var check = await harness.RunAsync(
            MobileToolControlOptions.CommandDevicesCheck,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.True(check.Success);
        Assert.Equal(1, gateway.RequestCount);
        Assert.True(ModuleHarness.Payload(check)["reachable"]!.GetValue<bool>());

        var status = ModuleHarness.Payload(await harness.RunAsync(MobileToolControlOptions.CommandStatus));
        Assert.Equal("reachable", status["devices"]!.AsArray()[0]!["lastState"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisableThenEnableThenDisableAgainCancelsTheSecondCallToo()
    {
        await using var gateway = GatewayDouble.Start();
        gateway.Catalog = new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(),
            ["commands"] = new JsonArray(new JsonObject
            {
                ["commandId"] = "input-monitor.stats",
                ["title"] = "输入监测",
                ["allowed"] = true,
                ["parameters"] = new JsonArray()
            })
        };
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        var context = new MyPowerTools.Abstractions.ModuleContext(
            "test-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            harness.DataDirectory,
            harness.DataDirectory,
            harness.DataDirectory,
            "android-arm64",
            ["secret.store"],
            null);

        static JsonObject InvokeArgs(string invocationId) => new()
        {
            [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
            [MobileToolControlOptions.ArgumentCommandId] = "input-monitor.stats",
            [MobileToolControlOptions.ArgumentInvocationId] = invocationId,
            [MobileToolControlOptions.ArgumentArgs] = new JsonObject()
        };

        // First cycle: disable while the submit is in flight.
        gateway.ResponseDelay = TimeSpan.FromSeconds(5);
        var firstCall = harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-first"));
        await Task.Delay(250);
        await harness.Module.DisableAsync(context, CancellationToken.None);
        var first = await firstCall;
        Assert.False(first.Success);

        // While stopped, a new command is refused without touching the network.
        gateway.ResponseDelay = TimeSpan.Zero;
        gateway.Reset();
        var stopped = await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-stopped"));
        Assert.False(stopped.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unavailable, stopped.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);

        // Re-enabling rebuilds the lifetime: the next call must run to completion instead of being
        // born cancelled by the previous disable.
        await harness.Module.EnableAsync(context, CancellationToken.None);
        var afterEnable = await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-second"));
        Assert.True(afterEnable.Success, afterEnable.Error?.Message);

        // Second cycle: disable again while that new call is in flight.
        gateway.ResponseDelay = TimeSpan.FromSeconds(5);
        var thirdCall = harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-third"));
        await Task.Delay(250);
        await harness.Module.DisableAsync(context, CancellationToken.None);
        var third = await thirdCall;
        Assert.False(third.Success);
        Assert.Equal(MobileToolControlErrorCodes.Cancelled, third.Error?.Code);
    }

    [Fact]
    public async Task DisablingRefusesEveryCommandAndSendingAgainWorksAfterStart()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        var context = new MyPowerTools.Abstractions.ModuleContext(
            "test-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            harness.DataDirectory,
            harness.DataDirectory,
            harness.DataDirectory,
            "android-arm64",
            ["secret.store"],
            null);

        await harness.Module.DisableAsync(context, CancellationToken.None);
        gateway.Reset();
        var refused = await harness.RunAsync(MobileToolControlOptions.CommandDevicesList);
        Assert.False(refused.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unavailable, refused.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);

        await harness.Module.StartAsync(context, CancellationToken.None);
        var allowed = await harness.RunAsync(MobileToolControlOptions.CommandDevicesList);
        Assert.True(allowed.Success);
    }

    [Fact]
    public async Task StoppingTheModuleCancelsAnInvocationThatIsStillInFlight()
    {
        await using var gateway = GatewayDouble.Start();
        gateway.Catalog = new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(),
            ["commands"] = new JsonArray(new JsonObject
            {
                ["commandId"] = "input-monitor.stats",
                ["title"] = "输入监测",
                ["allowed"] = true,
                ["parameters"] = new JsonArray()
            })
        };
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Responder = _ => new GatewayResponse(200, "{}");
        gateway.ResponseDelay = TimeSpan.FromSeconds(30);

        var context = new MyPowerTools.Abstractions.ModuleContext(
            "test-host",
            "1.0",
            MobileToolControlOptions.PackageId,
            MobileToolControlOptions.ModuleId,
            harness.DataDirectory,
            harness.DataDirectory,
            harness.DataDirectory,
            "android-arm64",
            ["secret.store"],
            null);

        var pending = harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentCommandId] = "input-monitor.stats",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-slow",
                [MobileToolControlOptions.ArgumentArgs] = new JsonObject()
            });

        await Task.Delay(300);
        await harness.Module.StopAsync(context, CancellationToken.None);

        // The call ends as a cancellation (surfaced as a failed command) instead of hanging forever.
        var result = await pending;
        Assert.False(result.Success);
    }
}
