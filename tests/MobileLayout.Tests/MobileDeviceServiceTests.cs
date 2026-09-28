using System.Text.Json.Nodes;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MobileLayout.Tests;

public sealed class MobileDeviceServiceTests
{
    [Theory]
    [InlineData("unknown", false, false)]
    [InlineData("available", true, true)]
    [InlineData("unavailable", true, false)]
    public async Task Public_sync_state_is_independent_of_local_address_and_old_cloud_settings(
        string state, bool checkedNow, bool running)
    {
        var calls = new List<string>();
        var service = new MobileDeviceService((command, args, token) =>
        {
            calls.Add(command);
            return Task.FromResult(new JsonObject
            {
                ["receiving"] = true,
                ["cloud"] = new JsonObject { ["configured"] = false },
                ["assistantRelay"] = new JsonObject { ["configured"] = true, ["public"] = true, ["state"] = state }
            }.ToJsonString());
        });
        var snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.Receiving);
        Assert.True(snapshot.RelayConfigured);
        Assert.Equal(checkedNow, snapshot.RelayChecked);
        Assert.Equal(running, snapshot.RelayRunning);
        Assert.True(string.IsNullOrEmpty(snapshot.LocalAddress));
        Assert.Equal(new[] { "file-transfer.inspect" }, calls);
    }

    [Fact]
    public async Task Local_code_export_does_not_request_network_inspection()
    {
        var calls = new List<string>();
        var service = new MobileDeviceService((command, args, token) =>
        {
            calls.Add(command);
            return Task.FromResult("{\"code\":\"mpt://pair/local-code\"}");
        });
        Assert.Equal("mpt://pair/local-code", await service.GetPairingCodeAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "file-transfer.pairing" }, calls);
    }
}
