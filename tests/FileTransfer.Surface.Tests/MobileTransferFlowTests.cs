using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// Targeted checks for the phone send flow: file selection, remembered-device choice, progress and
/// cancel, retry after failure, and the relay wording that must not claim delivery. Every terminal
/// state here comes from a module <c>transfer.changed</c> event, never from a timer or a canned value.
/// </summary>
public sealed class MobileTransferFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly TransferCore _core;
    private readonly string _file;

    public MobileTransferFlowTests()
    {
        Directory.CreateDirectory(_root);
        _file = Path.Combine(_root, "周末出游计划.pdf");
        File.WriteAllText(_file, new string('x', 2048));
        _core = new TransferCore(_module.Context(_root)) { BusyConfirmDelay = TimeSpan.Zero };
        _core.Attach();
    }

    public void Dispose()
    {
        _core.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Empty_state_asks_for_a_file_and_offers_no_send()
    {
        await TestPump.RunAsync(() => _core.RefreshAsync());

        Assert.Equal(TransferPhase.SelectFiles, _core.Snapshot.Phase);
        Assert.False(_core.Snapshot.CanSend);
        Assert.Equal(0, _module.CountCalls("send.direct"));
        Assert.Equal("选择要发送的文件。", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Choosing_a_file_moves_to_device_selection_and_refuses_a_missing_path()
    {
        Assert.True(_core.AddFile(_file));
        Assert.False(_core.AddFile(Path.Combine(_root, "not-here.bin")));
        Assert.Single(_core.Snapshot.Files);
        Assert.Equal(TransferPhase.SelectDevice, _core.Snapshot.Phase);
        Assert.Equal("周末出游计划.pdf", _core.Snapshot.FileLabel);

        // One file with no remembered device still cannot send.
        Assert.False(_core.Snapshot.CanSend);

        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.Equal("pc-1", _core.Snapshot.PeerId);
        Assert.True(_core.Snapshot.CanSend);
    }

    [AvaloniaFact]
    public async Task Sending_targets_the_remembered_device_and_reports_progress_then_the_module_result()
    {
        _module.AddPeer("pc-1", "工作电脑", "100.64.0.5");
        _module.AddPeer("mac-2", "MacBook Pro", "100.64.0.6");
        _module.LastPeer = "mac-2";
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.Equal("mac-2", _core.Snapshot.PeerId);

        _core.SelectPeer("pc-1");
        await TestPump.RunAsync(() => _core.SendAsync());

        Assert.Equal("send.direct", _module.Calls[^1].Command);
        Assert.Equal("pc-1", _module.LastArgs("send.direct")["peerId"]!.GetValue<string>());
        Assert.Single(_module.LastArgs("send.direct")["paths"]!.AsArray());
        Assert.Equal(TransferPhase.Transferring, _core.Snapshot.Phase);
        Assert.True(_core.Snapshot.Busy);

        // Progress is the module's own numbers, not a local animation.
        _module.Emit("周末出游计划.pdf", "sending", done: 512, total: 2048);
        TestPump.Drain();
        Assert.Equal(25d, _core.Snapshot.Progress, 3);
        Assert.Contains("512 B", _core.Snapshot.ProgressText);

        _module.Emit("周末出游计划.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync();
        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
        Assert.False(_core.Snapshot.Busy);
        Assert.Contains("1 个文件已完成", _core.Snapshot.Status);
        Assert.Equal(TransferRoute.Direct, _core.Snapshot.Route);
    }

    [AvaloniaFact]
    public async Task A_late_command_response_cannot_overwrite_a_reported_result()
    {
        // The module reports the terminal event before the send command answers; the page must keep
        // the result instead of falling back to "正在传输".
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var send = _core.SendAsync();
        _module.Emit("周末出游计划.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync();
        await TestPump.RunAsync(() => send);

        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
        Assert.False(_core.Snapshot.Busy);
    }

    [AvaloniaFact]
    public async Task Failure_offers_retry_and_retry_reuses_the_same_request()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync());

        _module.Emit("周末出游计划.pdf", "failed", message: "对方拒绝接收。");
        await TestPump.SettleAsync();

        Assert.Equal(TransferPhase.Failed, _core.Snapshot.Phase);
        Assert.True(_core.HasFailure);
        Assert.Contains("对方拒绝接收", _core.Snapshot.Failure);

        await TestPump.RunAsync(() => _core.RetryAsync());
        Assert.Equal(2, _module.CountCalls("send.direct"));
        Assert.Equal(TransferPhase.Transferring, _core.Snapshot.Phase);

        _module.Emit("周末出游计划.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync();
        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
    }

    [AvaloniaFact]
    public async Task Retry_stops_when_the_original_file_is_gone_instead_of_sending_a_ghost()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync());
        _module.Emit("周末出游计划.pdf", "failed", message: "网络中断。");
        await TestPump.SettleAsync();

        File.Delete(_file);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _core.RetryAsync());

        Assert.Equal(1, _module.CountCalls("send.direct"));
        Assert.False(_core.HasFailure);
        Assert.Contains("原文件已不在设备上", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Cancel_is_offered_while_busy_and_is_reported_as_a_cancel_not_a_success()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync());
        Assert.True(_core.Snapshot.Busy);

        await TestPump.RunAsync(() => _core.CancelAsync());
        Assert.Equal(1, _module.CountCalls("cancel"));

        _module.Emit("周末出游计划.pdf", "cancelled", message: "传输已取消。");
        _module.Busy = false;
        await TestPump.SettleAsync();

        Assert.Equal(TransferPhase.Failed, _core.Snapshot.Phase);
        Assert.True(_core.Snapshot.Cancelled);
        // Cancelling is not a failure with a retry prompt: nothing was delivered and nothing broke.
        Assert.False(_core.HasFailure);
    }

    [AvaloniaFact]
    public async Task Relay_upload_reports_stored_and_waiting_never_delivered()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _module.WebDavUrl = "https://openlist.example.test/dav/transfer";
        _module.Username = "mpt-relay";
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.True(_core.Snapshot.RelayConfigured);

        _core.SelectRoute(TransferRoute.Relay);
        await TestPump.RunAsync(() => _core.SendAsync());
        Assert.Equal("send.cloud", _module.Calls[^1].Command);

        _module.Emit("周末出游计划.pdf", "uploading", done: 1024, total: 2048);
        TestPump.Drain();
        Assert.Equal(50d, _core.Snapshot.Progress, 3);

        // "completed" for a relay send means the cloud copy exists. The receiving device has not
        // claimed it, so the phase is a success but the route stays Relay for the page's wording.
        _module.Emit("周末出游计划.pdf", "completed");
        _module.Busy = false;
        await TestPump.SettleAsync();
        Assert.Equal(TransferPhase.Succeeded, _core.Snapshot.Phase);
        Assert.Equal(TransferRoute.Relay, _core.Snapshot.Route);
        Assert.Contains("1 个文件已完成", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Relay_cannot_be_selected_before_a_cloud_account_exists()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());

        _core.SelectRoute(TransferRoute.Relay);
        Assert.Equal(TransferRoute.Direct, _core.Snapshot.Route);
        Assert.Contains("网盘中转还没有配置", _core.Snapshot.Status);

        await TestPump.RunAsync(() => _core.SendAsync());
        Assert.Equal("send.direct", _module.Calls[^1].Command);
    }

    [AvaloniaFact]
    public async Task A_rejected_send_rolls_back_to_the_real_module_state()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _module.FailCommands.Add("send.direct");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _core.SendAsync());
        Assert.Equal("模块拒绝了这次调用。", error.Message);

        // The module says it is not busy, so the page must not show a phantom transfer or leave
        // "正在发送…" on screen after the Shell has surfaced the rejection.
        Assert.False(_core.Snapshot.Busy);
        Assert.Equal(TransferPhase.SelectDevice, _core.Snapshot.Phase);
        Assert.Equal("已选择 1 个文件 · 2 KB", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Pairing_import_adds_the_device_before_the_next_send()
    {
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.Empty(_core.Snapshot.Peers);

        await TestPump.RunAsync(() => _core.CallAsync("pair.import", new JsonObject { ["code"] = "mpt://pair/abcdefghijklmnopqrstuvwxyz0123456789" }));
        await TestPump.RunAsync(() => _core.RefreshAsync());

        Assert.Single(_core.Snapshot.Peers);
        Assert.Equal("Work PC", _core.Snapshot.Peers[0].Name);
        Assert.Equal("Work PC", _core.Snapshot.Peer?.Name);
    }

    [AvaloniaFact]
    public async Task Send_is_refused_without_a_device_and_progress_is_not_started()
    {
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _core.SendAsync());
        Assert.Contains("请先添加并选择接收设备", error.Message);
        Assert.Equal(0, _module.CountCalls("send.direct"));
        Assert.False(_core.Snapshot.Busy);
    }

    [AvaloniaFact]
    public async Task An_incoming_transfer_does_not_hijack_the_send_flow()
    {
        _module.AddPeer("pc-1", "工作电脑");
        _core.AddFile(_file);
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.Equal(TransferPhase.SelectDevice, _core.Snapshot.Phase);

        _module.Emit("会议记录.pdf", "received");
        TestPump.Drain();

        // The result card belongs to the send flow; a received file must not claim it as "sent".
        Assert.Equal(TransferPhase.SelectDevice, _core.Snapshot.Phase);
        Assert.Equal("会议记录.pdf · 已接收", _core.Snapshot.Status);
        Assert.Single(_core.Snapshot.History);
    }
}
