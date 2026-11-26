using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// Behaviour of the file-assistant conversation against the fixed command contract. Every state here
/// comes from the fake module's JSON, so a passing test proves the page consumed the contract; the
/// fake never reports a transfer the module could not report, and no test asserts a simulated success.
/// </summary>
public sealed class AssistantConversationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-conv-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly AssistantCore _core;

    public AssistantConversationTests()
    {
        Directory.CreateDirectory(_root);
        _core = new AssistantCore(_module.Context(_root));
        _core.Attach();
    }

    public void Dispose()
    {
        _core.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Sending_text_without_a_device_writes_to_the_shared_session()
    {
        await TestPump.RunAsync(() => _core.RefreshAsync());
        Assert.True(_core.Snapshot.IsEmpty);

        IReadOnlyList<string> ids = [];
        await TestPump.RunAsync(async () => ids = await _core.SendAsync("买牛奶"));

        Assert.Single(ids);
        var item = Assert.Single(_core.Snapshot.Items);
        Assert.Equal(AssistantItemKind.Text, item.Kind);
        Assert.Equal("买牛奶", item.DisplayName);
        // No device was chosen and none is required: that is the whole point of "send to myself".
        Assert.Null(_module.LastArgs("assistant.send")["targetDeviceId"]);
        Assert.Equal("queued", _module.AssistantItems[0]["state"]!.GetValue<string>());
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("{\"credential\":\"private-value\"")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Invalid_inspect_responses_keep_the_visible_conversation_and_report_failure(string response)
    {
        await TestPump.RunAsync(() => _core.SendAsync("保留已发送的消息"));
        var original = Assert.Single(_core.Snapshot.Items);
        _module.RawResponses["assistant.inspect"] = response;

        await TestPump.RunAsync(() => _core.RefreshAsync());

        Assert.Equal(original.Id, Assert.Single(_core.Snapshot.Items).Id);
        Assert.Contains("无效响应", _core.Snapshot.Status);
        Assert.DoesNotContain("private-value", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task A_refused_send_is_reported_and_shows_no_entry()
    {
        _module.RefuseAssistantSend = true;
        await TestPump.RunAsync(() => _core.RefreshAsync());

        IReadOnlyList<string> ids = [];
        await TestPump.RunAsync(async () => ids = await _core.SendAsync("会失败的发送"));

        Assert.Empty(ids);
        Assert.Empty(_core.Snapshot.Items);
        // The page must not show an entry for something the module did not persist.
        Assert.Contains("没有被接受", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task An_empty_send_is_refused_before_it_reaches_the_module()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _core.SendAsync("  "));
        Assert.Equal(0, _module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public async Task A_file_goes_to_one_named_device_and_the_first_time_needs_the_peer_to_accept()
    {
        var file = Path.Combine(_root, "合同.pdf");
        await File.WriteAllTextAsync(file, new string('x', 2048));
        _module.AssistantDevices.Add(new JsonObject
        {
            ["deviceId"] = "pc-1", ["name"] = "工作电脑", ["address"] = "100.64.0.5",
            ["platform"] = "windows", ["paired"] = false, ["available"] = true
        });
        _core.PickerOpen = true;
        await TestPump.RunAsync(() => _core.DiscoverAsync());

        var device = Assert.Single(_core.Snapshot.Devices);
        Assert.False(device.Paired);
        Assert.Equal("当前可连接", device.StateText);

        await TestPump.RunAsync(() => _core.SendAsync(null, [file], device.DeviceId));

        Assert.Equal("pc-1", _module.LastArgs("assistant.send")["targetDeviceId"]!.GetValue<string>());
        Assert.Equal("sending", _module.AssistantItems[0]["state"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task An_inbound_request_is_answered_explicitly_and_rejection_leaves_no_trust()
    {
        _module.AssistantRequests.Add(new JsonObject
        {
            ["requestId"] = "req-1",
            ["deviceId"] = "pc-1",
            ["name"] = "工作电脑",
            ["itemNames"] = new JsonArray("报告.pdf"),
            ["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O")
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var request = Assert.Single(_core.Snapshot.PendingRequests);
        Assert.Equal("报告.pdf", request.ItemText);

        await TestPump.RunAsync(() => _core.RespondAsync(request.RequestId, accept: false, remember: false));

        Assert.Empty(_core.Snapshot.PendingRequests);
        Assert.False(_module.LastArgs("assistant.receive.respond")["accept"]!.GetValue<bool>());
        // Refusing must not remember the device; only an explicit choice may.
        Assert.False(_module.LastArgs("assistant.receive.respond")["remember"]!.GetValue<bool>());
        Assert.Contains("已拒绝", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Accepting_with_remember_is_the_only_path_that_stores_trust()
    {
        _module.AssistantRequests.Add(new JsonObject
        {
            ["requestId"] = "req-2", ["deviceId"] = "pc-1", ["name"] = "工作电脑",
            ["itemNames"] = new JsonArray("照片.png")
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.RespondAsync("req-2", accept: true, remember: true));

        Assert.True(_module.LastArgs("assistant.receive.respond")["accept"]!.GetValue<bool>());
        Assert.True(_module.LastArgs("assistant.receive.respond")["remember"]!.GetValue<bool>());
        Assert.Contains("已接受", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task An_offline_session_still_records_the_entry_and_says_it_is_local_only()
    {
        _module.AssistantRelayState = "unconfigured";
        await TestPump.RunAsync(() => _core.RefreshAsync());

        await TestPump.RunAsync(() => _core.SendAsync("断网时写的"));

        // The entry exists locally and the page explicitly refuses to claim other devices can see it.
        Assert.Single(_core.Snapshot.Items);
        Assert.Equal(AssistantRelayState.Unconfigured, _core.Snapshot.Relay);
        Assert.True(_core.Snapshot.RelayBlocked);
        Assert.Contains("待发内容会保存在本机", _core.Snapshot.RelayText);
        Assert.False(_core.Snapshot.CanSync);
    }

    [AvaloniaFact]
    public async Task A_queued_entry_recovers_when_the_relay_comes_back()
    {
        _module.AssistantRelayState = "unavailable";
        _module.AssistantRelayMessage = "连接中转网盘超时。";
        await TestPump.RunAsync(() => _core.RefreshAsync());
        await TestPump.RunAsync(() => _core.SendAsync("等待恢复"));

        Assert.Equal(AssistantRelayState.Unavailable, _core.Snapshot.Relay);
        Assert.Contains("超时", _core.Snapshot.RelayText);

        // The relay answers again, and the same pending entry is reported as stored.
        _module.AssistantRelayState = "available";
        _module.AssistantRelayMessage = "";
        _module.AssistantItems[0]["state"] = "stored";
        await TestPump.RunAsync(() => _core.SyncAsync());

        Assert.Equal(AssistantRelayState.Available, _core.Snapshot.Relay);
        Assert.True(_core.Snapshot.CanSync);
        Assert.Equal(AssistantItemState.Stored, Assert.Single(_core.Snapshot.Items).State);
        // "Stored" is the relay saving it, never a claim that a device received it.
        Assert.Equal("已发送，等待接收", Assert.Single(_core.Snapshot.Items).StateText);
    }

    [AvaloniaFact]
    public async Task A_delivered_entry_shows_which_device_reported_it()
    {
        _module.AssistantRelayState = "available";
        _module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "item-1", ["kind"] = "file", ["name"] = "报告.pdf", ["size"] = 4096,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["senderDeviceId"] = "mpt-phone", ["senderName"] = "我的手机",
            ["state"] = "delivered", ["bytesDone"] = 4096,
            ["receipts"] = new JsonArray(new JsonObject { ["deviceId"] = "pc-1", ["deviceName"] = "工作电脑", ["at"] = DateTimeOffset.UtcNow.ToString("O") })
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var item = Assert.Single(_core.Snapshot.Items);
        Assert.Equal("已送达 1 台设备", item.StateText);
        Assert.Equal("工作电脑", item.ReceiptText);
    }

    [AvaloniaTheory]
    [InlineData("cloud-quark", "夸克网盘")]
    [InlineData("cloud-baidu", "百度网盘")]
    [InlineData("cloud", "网盘中转")]
    [InlineData("direct", "设备直传")]
    [InlineData("tail-relay", "Tailscale 中转")]
    [InlineData("public-relay", "公网中转")]
    [InlineData("webdav", "WebDAV 中转")]
    [InlineData(null, "")]
    [InlineData("future-route", "")]
    public async Task File_route_comes_from_the_item_and_does_not_change_delivery_semantics(string? route, string label)
    {
        // A configured Quark-looking URL must never fill in a missing/unknown item route.
        _module.WebDavUrl = "https://openlist.example.test/dav/quark";
        _module.AssistantRelayState = "available";
        var json = new JsonObject
        {
            ["id"] = "route-item", ["kind"] = "file", ["name"] = "报告.pdf", ["size"] = 100,
            ["state"] = "stored", ["bytesDone"] = 100, ["targetDeviceId"] = "another-device"
        };
        if (route is not null) json["transportRoute"] = route;
        _module.AssistantItems.Add(json);
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var item = Assert.Single(_core.Snapshot.Items);
        Assert.Equal(route, item.TransportRoute);
        Assert.Equal(label, item.TransportRouteLabel);
        Assert.Equal("已发送，等待接收", item.StateText);
        Assert.Equal(label.Length > 0 ? label + " · 已发送，等待接收" : "已发送，等待接收", item.TransferStateText);
        Assert.Empty(item.ReceiptText);
    }

    [AvaloniaFact]
    public async Task Text_entry_does_not_claim_a_file_payload_route()
    {
        _module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "text-route", ["kind"] = "text", ["text"] = "会议纪要",
            ["state"] = "available", ["transportRoute"] = "cloud-quark"
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());
        var item = Assert.Single(_core.Snapshot.Items);
        Assert.Equal("", item.TransportRouteLabel);
        Assert.Equal("已接收", item.TransferStateText);
    }

    [AvaloniaFact]
    public async Task A_stored_entry_without_a_receipt_is_not_called_delivered()
    {
        _module.AssistantRelayState = "available";
        _module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "item-1", ["kind"] = "file", ["name"] = "报告.pdf", ["size"] = 4096,
            ["state"] = "stored", ["bytesDone"] = 4096, ["receipts"] = new JsonArray()
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var item = Assert.Single(_core.Snapshot.Items);
        Assert.Equal("已发送，等待接收", item.StateText);
        Assert.Equal("", item.ReceiptText);
    }

    [AvaloniaFact]
    public async Task One_failed_entry_offers_retry_under_the_same_id()
    {
        _module.AssistantRelayState = "available";
        _module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "item-9", ["kind"] = "file", ["name"] = "大文件.zip", ["size"] = 100,
            ["state"] = "failed", ["bytesDone"] = 10, ["error"] = "网络中断。"
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var item = Assert.Single(_core.Snapshot.Items);
        Assert.True(item.CanRetry);
        Assert.Contains("可重试", item.StateText);

        await TestPump.RunAsync(() => _core.RetryAsync("item-9"));

        Assert.Equal(1, _module.CountCalls("assistant.retry"));
        Assert.Equal("item-9", _module.LastArgs("assistant.retry")["itemId"]!.GetValue<string>());
        Assert.Equal("item-9", _module.AssistantItems[0]["id"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Cancelling_is_per_entry_and_never_turns_a_delivered_item_into_a_cancel()
    {
        _module.AssistantRelayState = "available";
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-1", ["kind"] = "file", ["name"] = "a.bin", ["size"] = 10, ["state"] = "sending" });
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-2", ["kind"] = "file", ["name"] = "b.bin", ["size"] = 10, ["state"] = "delivered" });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        var sending = _core.Snapshot.Items.First(item => item.Id == "item-1");
        var delivered = _core.Snapshot.Items.First(item => item.Id == "item-2");
        Assert.True(sending.CanCancel);
        Assert.False(delivered.CanCancel);

        await TestPump.RunAsync(() => _core.CancelAsync("item-1"));

        Assert.Equal("item-1", _module.LastArgs("assistant.cancel")["itemId"]!.GetValue<string>());
        Assert.Equal("cancelled", _module.AssistantItems[0]["state"]!.GetValue<string>());
        Assert.Equal("delivered", _module.AssistantItems[1]["state"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Opening_a_file_uses_the_path_the_module_downloaded()
    {
        _module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "item-3", ["kind"] = "file", ["name"] = "远端.pdf", ["size"] = 20, ["state"] = "available"
        });
        await TestPump.RunAsync(() => _core.RefreshAsync());
        var item = Assert.Single(_core.Snapshot.Items);
        Assert.True(item.NeedsDownload);
        Assert.False(item.CanOpen);

        AssistantOpenResult? opened = null;
        await TestPump.RunAsync(async () => opened = await _core.OpenAsync("item-3"));

        Assert.NotNull(opened);
        Assert.True(opened.HasPath);
        Assert.True(File.Exists(opened.Path));
        Assert.Equal("available", _module.AssistantItems[0]["state"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Opening_a_text_entry_returns_its_text_not_a_file()
    {
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-4", ["kind"] = "text", ["text"] = "会议纪要", ["state"] = "delivered" });
        await TestPump.RunAsync(() => _core.RefreshAsync());

        AssistantOpenResult? opened = null;
        await TestPump.RunAsync(async () => opened = await _core.OpenAsync("item-4"));

        Assert.NotNull(opened);
        Assert.True(opened.HasText);
        Assert.False(opened.HasPath);
        Assert.Equal("会议纪要", opened.Text);
    }

    [AvaloniaFact]
    public async Task An_empty_discovery_result_is_reported_as_empty_not_as_a_failure()
    {
        _core.PickerOpen = true;
        await TestPump.RunAsync(() => _core.DiscoverAsync());

        Assert.Equal(AssistantDiscoveryState.Completed, _core.Snapshot.Discovery);
        Assert.Empty(_core.Snapshot.Devices);
        Assert.Contains("没有找到", _core.Snapshot.DiscoveryMessage);
    }

    [AvaloniaFact]
    public async Task A_failed_discovery_keeps_the_previous_device_list()
    {
        _module.AssistantDevices.Add(new JsonObject
        {
            ["deviceId"] = "pc-1", ["name"] = "工作电脑", ["platform"] = "windows", ["paired"] = true, ["available"] = true
        });
        _core.PickerOpen = true;
        await TestPump.RunAsync(() => _core.DiscoverAsync());
        Assert.Single(_core.Snapshot.Devices);

        _module.FailCommands.Add("assistant.devices");
        await TestPump.RunAsync(() => _core.DiscoverAsync());

        Assert.Equal(AssistantDiscoveryState.Failed, _core.Snapshot.Discovery);
        // A failed scan must not erase devices the module already told us about.
        Assert.Single(_core.Snapshot.Devices);
        Assert.Contains("查找设备失败", _core.Snapshot.DiscoveryMessage);
    }

    [AvaloniaFact]
    public async Task The_module_event_triggers_one_re_read_of_the_session()
    {
        await TestPump.RunAsync(() => _core.RefreshAsync());
        var before = _module.CountCalls("assistant.inspect");

        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-5", ["kind"] = "text", ["text"] = "来自电脑", ["state"] = "delivered" });
        _module.EmitAssistantChanged();
        await Task.Delay(120);
        TestPump.Drain();

        Assert.True(_module.CountCalls("assistant.inspect") > before, "the event should cause a re-read");
        Assert.Contains("来自电脑", _core.Snapshot.Items.Select(item => item.DisplayName));
    }

    [AvaloniaFact]
    public async Task A_module_without_the_session_commands_is_reported_as_unsupported()
    {
        _module.MissingCommands.Add("assistant.inspect");
        await TestPump.RunAsync(() => _core.RefreshAsync());

        Assert.True(_core.IsUnsupported);
        Assert.Contains("还没有会话功能", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task Linking_previews_without_storing_and_imports_only_on_confirmation()
    {
        var preview = "";
        await TestPump.RunAsync(async () => preview = await _core.PreviewLinkAsync("mpt://pair/abc"));

        Assert.Contains("我的电脑", preview);
        Assert.Contains("文件会话", preview);
        // A preview must not join anything; the credential only lands in the secret store on import.
        Assert.Equal(0, _module.CountCalls("assistant.link.import"));

        await TestPump.RunAsync(() => _core.ImportLinkAsync("mpt://pair/abc"));

        Assert.Equal(1, _module.CountCalls("assistant.link.import"));
        Assert.True(_core.Snapshot.Identity.Linked);
        Assert.Contains("已加入", _core.Snapshot.Status);
    }

    [AvaloniaFact]
    public async Task An_empty_link_code_is_refused_locally()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _core.PreviewLinkAsync("   "));
        Assert.Equal(0, _module.CountCalls("assistant.link.preview"));
    }
}
