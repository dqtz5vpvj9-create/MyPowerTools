using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Evidence against the real local runtime. On a machine where the Runner is not running (CI, a
/// Linux build host) the test asserts the honest failure mode instead: the gateway must report an
/// explicit host-unavailable error and must never fabricate a catalog or a result.
/// </summary>
public sealed class RealHostControlTests
{
    private static async Task<bool> RunnerReachableAsync(HostControlClientBridge bridge)
    {
        using var window = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { return await bridge.PingAsync(window.Token); }
        catch (Exception) { return false; }
    }

    [Fact]
    public async Task RealCatalog_WhenTheRunnerIsAvailable_OtherwiseAnExplicitHostUnavailableError()
    {
        var bridge = new HostControlClientBridge();
        var reachable = await RunnerReachableAsync(bridge);
        Console.WriteLine(reachable
            ? "[real-host] Runner reachable: reading the real HostControl catalog."
            : "[real-host] Runner not reachable on this machine: verifying the explicit failure path only.");

        var directory = TestPaths.NewDirectory("real-host");
        var secrets = new InMemorySecretStore();
        await using var service = new RemoteToolGatewayService(directory, secrets, bridge, new RemoteToolGatewayOptions
        {
            ModuleId = "remote-tool-gateway",
            DeviceName = Environment.MachineName,
            Platform = OperatingSystem.IsWindows() ? "windows" : "linux",
            AllowLoopbackTransport = true
        });
        await service.InitializeAsync(CancellationToken.None);
        await service.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);
        var created = await service.CreateGrantAsync("验收手机", ["input-monitor.inspect"], false, CancellationToken.None);
        var grantId = created["grantId"]!.GetValue<string>();
        var token = await secrets.ReadAsync(SecretReference.Create("remote-tool-gateway", "grant-" + grantId), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(token));
        using var client = new GatewayClient(service.ListenerPort, token!);

        var response = await client.GetAsync(ControlWire.CatalogPath);
        if (!reachable)
        {
            await HttpAssert.AssertErrorAsync(response, System.Net.HttpStatusCode.BadGateway, ControlErrorCodes.HostUnavailable);
            return;
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var catalog = await HttpAssert.JsonAsync(response);
        var commands = catalog["commands"]!.AsArray();
        Assert.NotEmpty(commands);
        Console.WriteLine($"[real-host] catalog: {catalog["tools"]!.AsArray().Count} tools, {commands.Count} commands");

        // One real read-only command, executed through the gateway's own HostControl path.
        var candidate = commands
            .Select(item => item!.AsObject())
            .FirstOrDefault(item =>
                !HttpAssert.ReadBool(item, "requiresElevation") &&
                HttpAssert.ReadString(item, "dangerLevel").Length == 0 &&
                (HttpAssert.ReadString(item, "commandId").EndsWith(".inspect", StringComparison.Ordinal) ||
                 HttpAssert.ReadString(item, "commandId").EndsWith(".status", StringComparison.Ordinal)));
        if (candidate is null)
        {
            Console.WriteLine("[real-host] no read-only .inspect/.status command in the live catalog; catalog read verified only.");
            return;
        }

        var commandId = HttpAssert.ReadString(candidate, "commandId");
        var invocationId = "realhost" + Guid.NewGuid().ToString("N")[..16];
        var submit = await client.PostAsync(ControlWire.InvocationsPath,
            new System.Text.Json.Nodes.JsonObject { ["invocationId"] = invocationId, ["commandId"] = commandId, ["args"] = new System.Text.Json.Nodes.JsonObject() }.ToJsonString());
        Assert.True(submit.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.Accepted
            or System.Net.HttpStatusCode.Forbidden, await submit.Content.ReadAsStringAsync());
        Console.WriteLine($"[real-host] {commandId} -> {submit.StatusCode}");
    }
}
