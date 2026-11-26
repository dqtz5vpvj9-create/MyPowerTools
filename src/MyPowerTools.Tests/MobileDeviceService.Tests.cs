using System.Text.Json.Nodes;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Tests;

/// <summary>
/// The mobile device service is the only place where the file-transfer module's answers become the
/// device page's model. These tests pin the safety-relevant mapping: an address alone is never
/// "online", file pairing never becomes remote control, a relay upload is never "received", and an
/// unavailable module becomes an empty state instead of an exception.
/// </summary>
public sealed class MobileDeviceServiceTests
{
    private const string PeerCheckCommand = "file-transfer.peer.check";
    private const string PeerRemoveCommand = "file-transfer.peers.remove";
    private const string PairImportCommand = "file-transfer.pair.import";
    private const string PairingCommand = "file-transfer.pairing";
    private const string CloudCheckCommand = "file-transfer.cloud.check";

    private static string Inspect(string peers, string history = "[]", string progress = "null", string cloud = "null",
        string addresses = """["100.64.0.7"]""", bool receiving = true) =>
        $$"""
        {
          "settings": { "deviceId": "phone-1a2b3c4d", "listenAddress": "100.64.0.7", "peers": [] },
          "addresses": {{addresses}},
          "receiving": {{receiving.ToString().ToLowerInvariant()}},
          "openListRunning": false,
          "adminUrl": "",
          "busy": false,
          "progress": {{progress}},
          "history": {{history}},
          "localDeviceId": "phone-1a2b3c4d",
          "localName": "MPT 手机 phone-1a2b3c4d",
          "peers": {{peers}},
          "cloud": {{cloud}}
        }
        """;

    private const string UnknownPeer = """
        [{ "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "unknown", "checkedAt": null, "message": "", "supportsControl": false }]
        """;

    [Fact]
    public async Task APeerWithAnAddressButNoRealAnswerIsUnknownAndNeverOnline()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect(UnknownPeer));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        var peer = Assert.Single(snapshot.Peers);
        Assert.Equal(MobilePeerConnectionState.Unknown, peer.ConnectionState);
        Assert.Null(peer.CheckedAt);
        Assert.Equal("100.64.0.9", peer.Address);
        Assert.Equal("书房电脑", peer.Name);
        Assert.Equal("已配对，发送时会自动选择连接方式。", peer.Message);
        // A snapshot must not turn an address into a reachability claim anywhere in the list.
        Assert.DoesNotContain(snapshot.Peers, item => item.ConnectionState == MobilePeerConnectionState.Online);
        Assert.Equal("100.64.0.7", snapshot.LocalAddress);
        Assert.True(snapshot.Receiving);
        Assert.Equal("MPT 手机 phone-1a2b3c4d", snapshot.LocalDeviceName);
    }

    [Fact]
    public async Task OnlyAVerifiedCheckMakesAPeerOnline()
    {
        var commands = new FakeCommands()
            .On("file-transfer.inspect", Inspect("""
                [{ "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "online", "checkedAt": "2026-09-27T20:00:00.0000000+00:00", "message": "对方接收服务已应答。", "supportsControl": false }]
                """))
            .On(PeerCheckCommand, """
                { "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "offline", "checkedAt": "2026-09-27T20:05:00.0000000+00:00", "message": "无法连接对方地址；对方可能未开启接收，或不在同一网络。", "supportsControl": false }
                """);
        var service = new MobileDeviceService(commands.ExecuteAsync);

        var snapshot = await service.GetSnapshotAsync();
        Assert.Equal(MobilePeerConnectionState.Online, Assert.Single(snapshot.Peers).ConnectionState);

        var checkedPeer = await service.CheckPeerAsync("pc-9f8e7d6c");
        Assert.Equal(MobilePeerConnectionState.Offline, checkedPeer.ConnectionState);
        Assert.Contains("未开启接收", checkedPeer.Message);
        // Exactly one explicit check command; nothing runs on a timer or in the background.
        Assert.Single(commands.Calls, call => call.Command == PeerCheckCommand);
        Assert.Equal("pc-9f8e7d6c", commands.Calls.Single(call => call.Command == PeerCheckCommand).Args["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnEndpointWithoutAnAddressStaysUnknownWithANextStep()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect("""
            [{ "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "", "state": "offline", "checkedAt": null, "message": "", "supportsControl": false }]
            """));
        var peer = Assert.Single((await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync()).Peers);
        Assert.Equal(MobilePeerConnectionState.Unknown, peer.ConnectionState);
        Assert.Contains("自动选择连接方式", peer.Message);
    }

    [Fact]
    public async Task FilePairingNeverGrantsRemoteControlEvenWhenThePayloadClaimsIt()
    {
        var commands = new FakeCommands()
            .On("file-transfer.inspect", Inspect("""
                [{ "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "online", "checkedAt": "2026-09-27T20:00:00.0000000+00:00", "message": "", "supportsControl": true }]
                """))
            .On(PeerCheckCommand, """
                { "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "online", "checkedAt": "2026-09-27T20:00:00.0000000+00:00", "message": "", "supportsControl": true }
                """);
        var service = new MobileDeviceService(commands.ExecuteAsync);

        Assert.False(Assert.Single((await service.GetSnapshotAsync()).Peers).SupportsToolControl);
        Assert.False((await service.CheckPeerAsync("pc-9f8e7d6c")).SupportsToolControl);
    }

    [Fact]
    public async Task ARelayUploadWaitsForPickupWhileDirectSendAndReceiveKeepTheirDirection()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect("""[]""", """
            [
              { "name": "旧记录.txt", "done": 10, "total": 10, "state": "completed", "message": "", "time": "2026-09-27T19:00:00.0000000+00:00", "direction": "send", "peer": "pc-9f8e7d6c", "delivery": "" },
              { "name": "报告.pdf", "done": 2048, "total": 2048, "state": "completed", "message": "", "time": "2026-09-27T19:30:00.0000000+00:00", "direction": "send", "peer": "书房电脑", "delivery": "relay-uploaded" },
              { "name": "照片.jpg", "done": 1024, "total": 1024, "state": "completed", "message": "", "time": "2026-09-27T19:40:00.0000000+00:00", "direction": "send", "peer": "书房电脑", "delivery": "direct" },
              { "name": "来件.zip", "done": 64, "total": 64, "state": "received", "message": "", "time": "2026-09-27T19:45:00.0000000+00:00", "direction": "receive", "peer": "书房电脑", "delivery": "local" }
            ]
            """));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        // Newest first, and a successful upload only ever means "waiting for pickup".
        Assert.Equal(new[] { "received", "delivered", "uploaded", "completed" }, snapshot.Activities.Select(item => item.State));
        Assert.Equal("来件.zip", snapshot.Activities[0].Name);
        Assert.Equal("receive", snapshot.Activities[0].Direction);
        Assert.Equal(2048L, snapshot.Activities[2].Bytes);
        Assert.Equal("书房电脑", snapshot.Activities[2].PeerName);
        // A record written by an older module version carries no delivery evidence and claims nothing.
        Assert.Equal("completed", snapshot.Activities[3].State);
    }

    [Fact]
    public async Task AnInFlightTransferIsListedBeforeTheHistory()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect("""[]""", """[]""", """
            { "name": "大文件.iso", "done": 512, "total": 4096, "state": "uploading", "message": "", "time": "2026-09-27T20:00:00.0000000+00:00", "direction": "send", "peer": "书房电脑", "delivery": "" }
            """));
        var activity = Assert.Single((await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync()).Activities);
        Assert.Equal("uploading", activity.State);
        Assert.Equal(4096L, activity.Bytes);
    }

    [Fact]
    public async Task AnUnavailableModuleBecomesAnEmptyStateWithANextStep()
    {
        var commands = new FakeCommands();
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();
        Assert.Empty(snapshot.Peers);
        Assert.Empty(snapshot.Activities);
        Assert.False(snapshot.Receiving);
        Assert.False(snapshot.RelayConfigured);
        Assert.Contains("文件互传暂时不可用", snapshot.Notice);
        Assert.DoesNotContain("   at ", snapshot.Notice);
    }

    [Fact]
    public async Task ASnapshotOnlyReadsTheModuleStateAndNeverTalksToTheRelay()
    {
        // The relay is configured but was never checked: the page must still load from one command.
        var commands = new FakeCommands().On("file-transfer.inspect",
            Inspect(UnknownPeer, cloud: """{ "configured": true, "reachable": null, "checkedAt": null, "message": "" }"""));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        Assert.Equal(["file-transfer.inspect"], commands.Calls.Select(call => call.Command));
        Assert.True(snapshot.RelayConfigured);
        // "Never checked" is not "offline", and RelayChecked says so.
        Assert.False(snapshot.RelayChecked);
        Assert.False(snapshot.RelayRunning);
        Assert.Equal("等待同步。", snapshot.RelayDescription);
    }

    [Fact]
    public async Task AReachableRelayIsReportedAsReadyWithoutPromisingAPickupConfirmation()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect(UnknownPeer,
            cloud: """{ "configured": true, "reachable": true, "checkedAt": "2026-09-27T20:00:00.0000000+00:00", "message": "" }"""));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        Assert.True(snapshot.RelayChecked);
        Assert.True(snapshot.RelayRunning);
        Assert.Equal("同步连接正常。", snapshot.RelayDescription);
        Assert.DoesNotContain("已送达", snapshot.RelayDescription);
    }

    [Fact]
    public async Task AnUnreachableRelayKeepsTheModuleMessage()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", Inspect(UnknownPeer,
            cloud: """{ "configured": true, "reachable": false, "checkedAt": "2026-09-27T20:00:00.0000000+00:00", "message": "连接中转网盘失败：Connection refused (100.64.0.9:15244)" }"""));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        Assert.True(snapshot.RelayChecked);
        Assert.False(snapshot.RelayRunning);
        Assert.Contains("Connection refused", snapshot.RelayDescription);
        Assert.DoesNotContain(commands.Calls, call => call.Command == CloudCheckCommand);
    }

    [Fact]
    public async Task NoRelayCheckHappensWhenTheRelayIsNotConfigured()
    {
        var commands = new FakeCommands().On("file-transfer.inspect",
            Inspect(UnknownPeer, cloud: """{ "configured": false, "reachable": false, "checkedAt": null, "message": "" }"""));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();

        Assert.False(snapshot.RelayConfigured);
        Assert.False(snapshot.RelayRunning);
        Assert.False(snapshot.RelayChecked);
        Assert.Contains("发送后会自动开始同步", snapshot.RelayDescription);
        Assert.DoesNotContain(commands.Calls, call => call.Command == CloudCheckCommand);
    }

    [Fact]
    public async Task AnEndpointThatAnsweredWithoutProvingItsIdentityIsUnknownNotOffline()
    {
        // Covers an older peer that refuses a probe and a stranger that happens to answer.
        var commands = new FakeCommands().On(PeerCheckCommand, """
            { "deviceId": "pc-9f8e7d6c", "name": "书房电脑", "address": "100.64.0.9", "state": "unknown",
              "checkedAt": "2026-09-27T20:05:00.0000000+00:00", "message": "接收密钥不正确，或协议版本不兼容。", "supportsControl": false }
            """);
        var peer = await new MobileDeviceService(commands.ExecuteAsync).CheckPeerAsync("pc-9f8e7d6c");

        Assert.Equal(MobilePeerConnectionState.Unknown, peer.ConnectionState);
        Assert.Contains("不兼容", peer.Message);
        Assert.NotNull(peer.CheckedAt);
        Assert.False(peer.SupportsToolControl);
    }

    [Fact]
    public async Task ImportingACodeSendsItVerbatimAndSurfacesTheModuleRefusal()
    {
        var commands = new FakeCommands().On(PairImportCommand, """{ "paired": "书房电脑", "deviceId": "pc-9f8e7d6c", "address": "100.64.0.9" }""");
        var service = new MobileDeviceService(commands.ExecuteAsync);
        await service.ImportPairingAsync("  mpt://pair/abc  ");
        Assert.Equal("mpt://pair/abc", commands.Calls.Single(call => call.Command == PairImportCommand).Args["code"]!.GetValue<string>());

        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportPairingAsync("   "));

        var refusing = new FakeCommands().On(PairImportCommand, _ => throw new InvalidOperationException("这是本机自己的连接码，请粘贴对方设备生成的连接码。"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MobileDeviceService(refusing.ExecuteAsync).ImportPairingAsync("mpt://pair/self"));
        Assert.Contains("本机自己", error.Message);
    }

    [Fact]
    public async Task RemovingADeviceSendsTheIdentifierAndRejectsAnEmptyOne()
    {
        var commands = new FakeCommands().On(PeerRemoveCommand, """{ "removed": "pc-9f8e7d6c", "name": "书房电脑", "peers": 0 }""");
        var service = new MobileDeviceService(commands.ExecuteAsync);
        await service.RemovePeerAsync("pc-9f8e7d6c");
        Assert.Equal("pc-9f8e7d6c", commands.Calls.Single(call => call.Command == PeerRemoveCommand).Args["deviceId"]!.GetValue<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RemovePeerAsync("  "));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CheckPeerAsync(""));
    }

    [Fact]
    public async Task ThePairingCodeComesFromTheModuleAndAMissingCodeIsAnError()
    {
        var commands = new FakeCommands().On(PairingCommand, """{ "code": "mpt://pair/abc" }""");
        Assert.Equal("mpt://pair/abc", await new MobileDeviceService(commands.ExecuteAsync).GetPairingCodeAsync());

        var empty = new FakeCommands().On(PairingCommand, """{ "code": "" }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MobileDeviceService(empty.ExecuteAsync).GetPairingCodeAsync());
    }

    [Fact]
    public async Task AFailedInspectIsNeverPresentedAsAHealthyEmptyDeviceList()
    {
        var commands = new FakeCommands().On("file-transfer.inspect", _ => throw new InvalidOperationException("Unknown command 'file-transfer.inspect'."));
        var snapshot = await new MobileDeviceService(commands.ExecuteAsync).GetSnapshotAsync();
        Assert.NotNull(snapshot.Notice);
        Assert.Contains("Unknown command", snapshot.Notice);
    }

    private sealed class FakeCommands
    {
        private readonly Dictionary<string, Func<JsonObject, string>> _handlers = new(StringComparer.Ordinal);

        public List<(string Command, JsonObject Args)> Calls { get; } = [];

        public FakeCommands On(string command, string payload) => On(command, _ => payload);

        public FakeCommands On(string command, Func<JsonObject, string> handler)
        {
            _handlers[command] = handler;
            return this;
        }

        public Task<string> ExecuteAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
        {
            Calls.Add((commandId, (JsonObject)args.DeepClone()));
            if (!_handlers.TryGetValue(commandId, out var handler))
                throw new InvalidOperationException($"Unknown command '{commandId}'.");
            return Task.FromResult(handler(args));
        }
    }
}
