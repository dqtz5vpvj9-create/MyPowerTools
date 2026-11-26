using System.Text.Json.Nodes;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// Import, storage and credential rules: the token only enters the platform secret store, the preview
/// never saves anything, and a hand-edited record can never widen the address boundary.
/// </summary>
public sealed class ImportFlowTests
{
    [Fact]
    public async Task PreviewParsesButSavesNothing()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        var code = ModuleHarness.BuildCode(gateway.Endpoint, gateway.Token, "grant-1", "工作电脑");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandImportPreview,
            new JsonObject { [MobileToolControlOptions.ArgumentCode] = code });

        Assert.True(result.Success);
        var payload = ModuleHarness.Payload(result);
        Assert.True(payload["ok"]!.GetValue<bool>());
        Assert.Equal(gateway.Endpoint, payload["endpoint"]!.GetValue<string>());
        Assert.Equal("grant-1", payload["grantId"]!.GetValue<string>());
        Assert.Equal("工作电脑", payload["deviceName"]!.GetValue<string>());
        Assert.False(payload["alreadyImported"]!.GetValue<bool>());

        Assert.Empty(harness.Secrets.Values);
        Assert.False(File.Exists(harness.DevicesPath));
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task PreviewReportsTheRefusalForANonTailnetCode()
    {
        await using var harness = await ModuleHarness.CreateAsync();
        var code = ModuleHarness.BuildCode("http://10.1.2.3:49541", "token", "grant-1", "电脑");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandImportPreview,
            new JsonObject { [MobileToolControlOptions.ArgumentCode] = code });

        Assert.True(result.Success);
        var payload = ModuleHarness.Payload(result);
        Assert.False(payload["ok"]!.GetValue<bool>());
        Assert.NotEqual("", payload["error"]!.GetValue<string>());
        Assert.Empty(harness.Secrets.Values);
    }

    [Fact]
    public async Task ConfirmationIsRequiredBeforeAnythingIsSaved()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        var code = ModuleHarness.BuildCode(gateway.Endpoint, gateway.Token, "grant-1", "工作电脑");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandImportConfirm,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentCode] = code,
                [MobileToolControlOptions.ArgumentAccepted] = false
            });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.ValidationFailed, result.Error?.Code);
        Assert.Empty(harness.Secrets.Values);
        Assert.False(File.Exists(harness.DevicesPath));
    }

    [Fact]
    public async Task ConfirmedImportKeepsTheTokenOnlyInTheSecretStore()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());

        var payload = await harness.ImportAsync(gateway);
        Assert.Equal("grant-1", payload["imported"]!.GetValue<string>());

        Assert.Equal(gateway.Token, Assert.Single(harness.Secrets.Values).Value);
        Assert.Contains("secret://mobile-tool-control/grant-1.token", harness.Secrets.Values.Keys.Single(), StringComparison.Ordinal);

        var devicesFile = await File.ReadAllTextAsync(harness.DevicesPath);
        Assert.DoesNotContain(gateway.Token, devicesFile, StringComparison.Ordinal);
        Assert.Contains("grant-1", devicesFile, StringComparison.Ordinal);
        Assert.Contains(gateway.Endpoint, devicesFile, StringComparison.Ordinal);

        var status = await harness.RunAsync(MobileToolControlOptions.CommandStatus);
        Assert.DoesNotContain(gateway.Token, status.Output, StringComparison.Ordinal);
        var statusPayload = ModuleHarness.Payload(status);
        var device = Assert.Single(statusPayload["devices"]!.AsArray());
        Assert.True(device!["credentialConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ReimportingTheSameGrantReplacesTheTokenWithoutDuplicatingTheDevice()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Token = "rotated-token";

        await harness.ImportAsync(gateway);

        var status = ModuleHarness.Payload(await harness.RunAsync(MobileToolControlOptions.CommandStatus));
        Assert.Single(status["devices"]!.AsArray());
        Assert.Equal("rotated-token", Assert.Single(harness.Secrets.Values).Value);
    }

    [Fact]
    public async Task RemovingADeviceDeletesTheRecordAndTheCredential()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandDevicesRemove,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.True(result.Success);
        Assert.Empty(harness.Secrets.Values);
        var status = ModuleHarness.Payload(await harness.RunAsync(MobileToolControlOptions.CommandStatus));
        Assert.Empty(status["devices"]!.AsArray());
    }

    [Fact]
    public async Task ADeviceRecordPointingOutsideTheTailnetIsRefusedWithoutAnyRequest()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);

        // Hand-edit the record the way a restored backup or a text editor could.
        var document = JsonNode.Parse(await File.ReadAllTextAsync(harness.DevicesPath))!.AsObject();
        document["devices"]!.AsArray()[0]!["endpoint"] = "http://192.168.1.50:49541";
        await File.WriteAllTextAsync(harness.DevicesPath, document.ToJsonString());

        harness.Module.EndpointPolicy = TailnetEndpointPolicy.Instance;
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentRefresh] = true
            });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.EndpointRefused, result.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task AMissingCredentialIsReportedInsteadOfSendingAnAnonymousRequest()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        harness.Secrets.Clear();
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unauthorized, result.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task AnUnknownDeviceIsReportedForEveryDeviceCommand()
    {
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        var deviceId = new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "missing" };

        foreach (var command in new[]
                 {
                     MobileToolControlOptions.CommandCatalog,
                     MobileToolControlOptions.CommandDevicesCheck,
                     MobileToolControlOptions.CommandDevicesRemove
                 })
        {
            var result = await harness.RunAsync(command, deviceId.DeepClone().AsObject());
            Assert.False(result.Success);
            Assert.Equal(MobileToolControlErrorCodes.UnknownDevice, result.Error?.Code);
        }
    }
}
