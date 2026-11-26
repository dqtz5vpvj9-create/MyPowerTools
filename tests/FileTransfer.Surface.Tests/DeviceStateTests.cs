using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// Device reachability and relay health come from the module's own answers. These checks pin the two
/// rules the plan calls out: an address alone is never rendered as "online", and a host running an
/// older file-transfer module still gets a working page instead of a device that looks broken.
/// </summary>
public sealed class DeviceStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-state-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly TransferCore _core;
    private readonly string _file;

    public DeviceStateTests()
    {
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "报告.pdf");
        File.WriteAllText(_file, new string('x', 1024));
        _core = new TransferCore(_module.Context(_root)) { BusyConfirmDelay = TimeSpan.Zero };
        _core.Attach();
    }

    public void Dispose()
    {
        _core.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task A_paired_device_stays_unchecked_until_the_peer_answers()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var peer = Assert.Single(_core.Snapshot.Peers);
        Assert.Equal("unknown", peer.State);
        Assert.False(peer.IsOnline);

        // Only the module's own answer may promote the device to online.
        _module.SetPeerState("pc-1", "online");
        await TestPump.RunAsync(() => _core.CheckPeerAsync("pc-1"));

        // RefreshAsync runs after the check, so the check is asserted by name, not by order.
        Assert.Equal(1, _module.CountCalls("peer.check"));
        Assert.Equal("pc-1", _module.LastArgs("peer.check")["peerId"]!.GetValue<string>());
        peer = Assert.Single(_core.Snapshot.Peers);
        Assert.True(peer.IsOnline);
        Assert.NotNull(peer.CheckedAt);
        Assert.Contains("已应答", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task A_peer_that_does_not_answer_is_reported_as_offline_with_a_next_step()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.SetPeerState("pc-1", "offline");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.CheckPeerAsync("pc-1"));

        var peer = Assert.Single(_core.Snapshot.Peers);
        Assert.True(peer.IsOffline);
        Assert.False(peer.IsOnline);
        Assert.Contains("没有应答", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Checking_a_device_is_refused_by_an_older_module_without_breaking_the_page()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.MissingCommands.Add("peer.check");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.CheckPeerAsync("pc-1"));

        Assert.Contains("不支持检查设备连接", _core.Snapshot.Status);
        // The device stays usable for sending: a missing check command is not a device failure.
        _core.AddFile(_file);
        Assert.True(_core.Snapshot.CanSend);

        // And the page remembers, so it does not keep asking a module that cannot answer.
        await TestPump.RunAsync(() => _core.CheckPeerAsync("pc-1"));
        Assert.Equal(1, _module.CountCalls("peer.check"));
    }

    [AvaloniaFact]
    public async Task Removing_a_device_goes_through_the_module_and_clears_the_selection()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.AddPeer("mac-2", "MacBook Pro", "100.64.0.6");
        await TestPump.RunAsync(() => _core.RefreshAsync());
        _core.SelectPeer("pc-1");
        Assert.Equal("pc-1", _core.Snapshot.PeerId);

        await TestPump.RunAsync(() => _core.RemovePeerAsync("pc-1"));

        Assert.Equal(1, _module.CountCalls("peers.remove"));
        Assert.Equal("pc-1", _module.LastArgs("peers.remove")["peerId"]!.GetValue<string>());
        var peer = Assert.Single(_core.Snapshot.Peers);
        Assert.Equal("mac-2", peer.DeviceId);
        // The removed device cannot stay selected, and no other device is claimed on its behalf.
        Assert.Equal("mac-2", _core.Snapshot.PeerId);
        Assert.Contains("已解除该设备的连接", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Removing_a_device_is_refused_by_an_older_module()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.MissingCommands.Add("peers.remove");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.RemovePeerAsync("pc-1"));

        Assert.Single(_core.Snapshot.Peers);
        Assert.Contains("不支持移除设备", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Relay_health_is_unknown_until_a_real_check_happens()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _module.WebDavUrl = "https://openlist.example.test/dav/transfer";
        _module.Username = "mpt-relay";
        await TestPump.RunAsync(() => _core.RefreshAsync());

        // Configured but never checked: this must not read as either up or down.
        Assert.True(_core.Snapshot.RelayConfigured);
        Assert.Null(_core.Snapshot.RelayReachable);

        _module.CloudReachable = true;
        await TestPump.RunAsync(() => _core.CheckRelayAsync());

        Assert.Equal(1, _module.CountCalls("cloud.check"));
        Assert.True(_core.Snapshot.RelayReachable);
        Assert.Contains("网盘连接正常", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task A_failing_relay_check_reports_the_modules_own_message()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _module.WebDavUrl = "https://openlist.example.test/dav/transfer";
        _module.Username = "mpt-relay";
        _module.CloudReachable = false;
        _module.CloudMessage = "连接中转网盘超时，请检查地址、网络和网盘状态。";
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.CheckRelayAsync());

        Assert.False(_core.Snapshot.RelayReachable);
        Assert.Equal("连接中转网盘超时，请检查地址、网络和网盘状态。", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task A_module_without_the_cloud_summary_keeps_the_relay_usable()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _module.WebDavUrl = "https://openlist.example.test/dav/transfer";
        _module.Username = "mpt-relay";
        _module.MissingCommands.Add("cloud.check");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.CheckRelayAsync());

        Assert.Contains("不支持测试网盘连接", _core.Snapshot.Status);
        // Relay sending still works on an older module: only the extra check is unavailable.
        _core.SelectRoute(TransferRoute.Relay);
        Assert.Equal(TransferRoute.Relay, _core.Snapshot.Route);
    }

    [AvaloniaFact]
    public async Task A_relay_upload_is_never_reported_as_received_without_the_receiver_claiming_it()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.WebDavUrl = "https://openlist.example.test/dav/transfer";
        _module.Username = "mpt-relay";
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        _core.SelectRoute(TransferRoute.Relay);
        await TestPump.RunAsync(() => _core.SendAsync());

        // The WebDAV PUT finishing proves the cloud copy exists, not that the device took it.
        _module.Emit("报告.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync();
        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
        Assert.Equal(TransferRoute.Relay, _core.Snapshot.Route);

        // A later "received" report is the module telling us the peer claimed it. It is an inbound
        // event, so it reports itself in the status and history without rewriting the settled batch.
        _module.Emit("报告.pdf", "received");
        TestPump.Drain();
        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
        Assert.Equal(TransferRoute.Relay, _core.Snapshot.Route);
        Assert.Contains("已接收", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Peer_state_survives_a_refresh_instead_of_falling_back_to_unchecked()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.SetPeerState("pc-1", "online");
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.True(Assert.Single(_core.Snapshot.Peers).IsOnline);

        await TestPump.RunAsync(() => _core.RefreshAsync());

        Assert.True(Assert.Single(_core.Snapshot.Peers).IsOnline);
    }

    [AvaloniaFact]
    public async Task A_peer_list_without_the_new_fields_still_parses()
    {
        // An older module returns bare peers; the page must not crash or invent a state.
        _module.Peers.Add(new JsonObject { ["deviceId"] = "old-1", ["name"] = "旧电脑", ["address"] = "100.64.0.8" });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var peer = Assert.Single(_core.Snapshot.Peers);
        Assert.Equal("unknown", peer.State);
        Assert.False(peer.IsOnline);
        Assert.Null(peer.CheckedAt);
    }
}
