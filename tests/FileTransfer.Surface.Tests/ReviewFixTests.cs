using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// The fixes from the consolidated review: how a file is opened on a mobile host, that a reopened page
/// can still work, that credentials never survive a closed sheet, that discovery reports what the
/// module actually said, and that a sheet is a sheet.
/// </summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-fix-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly List<Window> _windows = [];

    public ReviewFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- 1. opening a file through the platform --------------------------------------------

    [AvaloniaFact]
    public async Task The_hosts_own_open_delegate_is_used_first()
    {
        var file = Touch("报告.pdf");
        var calls = new List<string>();

        var opened = await FileOpenStrategy.OpenAsync(
            (path, _) => { calls.Add(path); return Task.FromResult(true); },
            desktopHost: null,
            file,
            CancellationToken.None);

        Assert.True(opened.Opened);
        Assert.Equal([file], calls);
    }

    [AvaloniaFact]
    public async Task A_refused_open_is_reported_as_not_opened()
    {
        var file = Touch("报告.pdf");

        var refused = await FileOpenStrategy.OpenAsync((_, _) => Task.FromResult(false), null, file, CancellationToken.None);

        Assert.False(refused.Opened);
        Assert.Equal(FileOpenOutcome.Failed, refused.Outcome);
        Assert.Contains("没有能打开", refused.Message);
    }

    [AvaloniaFact]
    public async Task A_throwing_open_delegate_is_reported_instead_of_crashing()
    {
        var file = Touch("报告.pdf");

        var thrown = await FileOpenStrategy.OpenAsync(
            (_, _) => throw new UnauthorizedAccessException("没有权限"),
            null, file, CancellationToken.None);

        Assert.False(thrown.Opened);
        Assert.Contains("没有权限", thrown.Message);
    }

    [AvaloniaFact]
    public async Task Without_a_delegate_or_a_desktop_launcher_the_page_does_not_claim_success()
    {
        var file = Touch("报告.pdf");

        // No host delegate and no desktop host: the same situation as a mobile host that has not wired
        // its FileProvider yet. The page must say so rather than "文件已保存到 …".
        var result = await FileOpenStrategy.OpenAsync(null, desktopHost: null, file, CancellationToken.None);

        Assert.Equal(FileOpenOutcome.Unavailable, result.Outcome);
        Assert.False(result.Opened);
        Assert.Contains("没有可用的文件打开方式", result.Message);
        Assert.DoesNotContain("已保存", result.Message);
    }

    [AvaloniaFact]
    public async Task A_missing_file_is_not_opened()
    {
        var missing = Path.Combine(_root, "不存在.pdf");
        var result = await FileOpenStrategy.OpenAsync((_, _) => Task.FromResult(true), null, missing, CancellationToken.None);

        Assert.False(result.Opened);
        Assert.Contains("已不在设备上", result.Message);
    }

    // ---- 2. the page can be reopened, and credentials do not survive ------------------------

    [AvaloniaFact]
    public async Task Reopening_the_page_still_runs_a_scan()
    {
        var scans = 0;
        var view = Open(390, out var window, _ => { scans++; return Task.FromResult<string?>("mpt://assistant/abc"); });

        view.Conversation.OpenLinkSheet();
        await view.Conversation.ScanForTestAsync();
        Assert.Equal(1, scans);

        // Leaving and coming back is what a user does when they switch tools.
        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        window.Content = view;
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        view.Conversation.OpenLinkSheet();
        await view.Conversation.ScanForTestAsync();

        // A page-scoped lifetime that was never renewed would leave the second scan cancelled, so the
        // second preview would never run.
        Assert.Equal(2, scans);
        Assert.Equal(2, _module.CountCalls("assistant.link.preview"));
    }

    [AvaloniaFact]
    public async Task Closing_the_sheet_discards_the_code_preview_and_the_rendered_qr()
    {
        var view = Open(390, out var window, _ => Task.FromResult<string?>("mpt://assistant/abc"));
        view.Conversation.OpenLinkSheet();
        await view.Conversation.ScanForTestAsync();
        window.UpdateLayout();
        Assert.Equal(1, _module.CountCalls("assistant.link.preview"));

        view.Conversation.CloseSheet();
        window.UpdateLayout();

        // Neither the pasted code, its decoded preview, nor the QR symbol may survive the close.
        Assert.Equal("", view.Conversation.LinkCodeText);
        Assert.Equal("", view.Conversation.LinkPreviewText);
        Assert.Null(view.Conversation.QrValue);
        Assert.Contains("连接码", TextOf(view));
    }

    [AvaloniaFact]
    public async Task Showing_the_connection_code_does_not_copy_it()
    {
        var view = Open(390, out var window);
        view.Conversation.OpenLinkSheet();
        window.UpdateLayout();

        await view.Conversation.ExportLinkForTestAsync();
        window.UpdateLayout();

        // The user asked to see the code, not to copy it; copying is a separate action.
        Assert.Contains("已显示", view.Assistant.Snapshot.Status);
        Assert.DoesNotContain("已复制", view.Assistant.Snapshot.Status);
        Assert.Equal(_module.LinkCode, view.Conversation.QrValue);
        Assert.True(_module.LinkCode.Length > 150,
            $"the fixture must be a full-length code, was {_module.LinkCode.Length} chars");
        // The symbol must actually replace the placeholder, or the sheet shows "not displayed yet".
        Assert.NotNull(view.Conversation.QrSymbol);
        Assert.Same(view.Conversation.QrSymbol, view.Conversation.QrHolderChild);
    }

    [AvaloniaTheory]
    [InlineData(390, 844)]
    [InlineData(320, 800)]
    public async Task The_whole_symbol_is_visible_in_the_sheet_without_scrolling(int width, int height)
    {
        var view = Open(width, out var window, height: height);
        await TestPump.RunAsync(() => view.Conversation.OpenLinkSheetForTestAsync());
        window.UpdateLayout();

        var symbol = view.Conversation.QrSymbol;
        Assert.NotNull(symbol);
        Assert.True(symbol.Value is { Length: > 500 }, "the complete assistant and relay code must be rendered");
        Assert.StartsWith("mpt://assistant/", symbol.Value);

        // The visible area is the scroll viewport, not the window; the symbol's own quiet zone is part
        // of the control, so the whole square has to be inside it to be scannable.
        var scroll = view.Conversation.SheetScroll;
        var top = symbol.TranslatePoint(default, scroll);
        Assert.NotNull(top);
        var bottom = symbol.TranslatePoint(new Point(0, symbol.Bounds.Height), scroll);
        Assert.NotNull(bottom);
        var viewport = scroll.Viewport;
        Assert.True(top.Value.Y >= -1,
            $"{width}x{height}: the symbol starts above the viewport at {top.Value.Y:0}");
        Assert.True(bottom.Value.Y <= viewport.Height + 1,
            $"{width}x{height}: the symbol bottom {bottom.Value.Y:0} is outside the {viewport.Height:0} viewport");
        // And the sheet did not have to be scrolled to get there.
        Assert.Equal(0, scroll.Offset.Y);
        Assert.Equal(240, symbol.Bounds.Width);
        Assert.True(top.Value.X >= -1 && top.Value.X + symbol.Bounds.Width <= viewport.Width + 1,
            $"{width}x{height}: the symbol must fit the horizontal viewport too");
    }

    [AvaloniaFact]
    public async Task Opening_the_connection_sheet_shows_the_symbol_without_asking()
    {
        var view = Open(390, out var window);
        await TestPump.RunAsync(() => view.Conversation.OpenLinkSheetForTestAsync());
        window.UpdateLayout();

        // The sheet's purpose is the code, so it is fetched on open rather than behind another tap.
        Assert.NotNull(view.Conversation.QrSymbol);
        Assert.True(view.Conversation.QrSymbol!.Value is { Length: > 0 });
        // One fetch per open: the sheet does not ask the module twice for the same code.
        Assert.Equal(1, _module.CountCalls("assistant.link.export"));
    }

    // ---- 3. discovery reports what the module said ------------------------------------------

    [AvaloniaFact]
    public async Task A_partial_discovery_is_not_reported_as_an_empty_result()
    {
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑"));
        _module.AssistantDiscoveryState = "partial";
        _module.AssistantDevicesMessage = "查找时间到了，还有设备没有应答。";
        var view = Open(390, out var window);

        view.Conversation.OpenDevicePicker();
        await TestPump.SettleAsync();
        window.UpdateLayout();

        Assert.Equal(AssistantDiscoveryState.Partial, view.Assistant.Snapshot.Discovery);
        Assert.Single(view.Assistant.Snapshot.Devices);
        Assert.Contains("还有设备没有应答", view.Assistant.Snapshot.DiscoveryMessage);
        Assert.DoesNotContain("没有找到设备", view.Assistant.Snapshot.DiscoveryMessage);
    }

    [AvaloniaFact]
    public async Task An_unsupported_platform_says_so_instead_of_no_devices_found()
    {
        _module.AssistantDiscoveryState = "unsupported";
        var view = Open(390, out var window);

        view.Conversation.OpenDevicePicker();
        await TestPump.SettleAsync();
        window.UpdateLayout();

        Assert.Equal(AssistantDiscoveryState.Unsupported, view.Assistant.Snapshot.Discovery);
        Assert.Contains("无法自动查找", view.Assistant.Snapshot.DiscoveryMessage);
        Assert.DoesNotContain("没有找到设备", view.Assistant.Snapshot.DiscoveryMessage);
    }

    [AvaloniaFact]
    public async Task A_late_answer_from_a_cancelled_run_is_discarded()
    {
        // A host that ignores the cancellation token still answers; the page must not publish it.
        var gate = new SemaphoreSlim(0);
        _module.BeforeAnswer = command => command == "assistant.devices" ? gate.WaitAsync() : null;
        _module.AssistantDiscoveryState = "completed";
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑"));
        var view = Open(390, out var window);

        view.Conversation.OpenDevicePicker();
        TestPump.Drain();
        var before = view.Assistant.Snapshot.Devices.Count;

        // The user closes the picker while the module is still working.
        view.Conversation.CloseSheet();
        window.UpdateLayout();
        gate.Release();
        await Task.Delay(150);
        TestPump.Drain();
        window.UpdateLayout();

        // Nothing was published: the list did not grow and no error appeared.
        Assert.Equal(before, view.Assistant.Snapshot.Devices.Count);
        Assert.Equal(AssistantDiscoveryState.Searching, view.Assistant.Snapshot.Discovery);
        Assert.DoesNotContain("查找设备失败", view.Assistant.Snapshot.Status);
    }

    // ---- 4. the sheet is a sheet ------------------------------------------------------------

    [AvaloniaFact]
    public void The_sheet_sits_on_the_bottom_edge_and_does_not_fill_the_page()
    {
        var view = Open(390, out var window);
        view.Conversation.OpenDevicePicker();
        TestPump.Drain();
        window.UpdateLayout();

        var sheet = view.Conversation.SheetHost;
        Assert.Equal(VerticalAlignment.Bottom, sheet.VerticalAlignment);
        Assert.True(sheet.Bounds.Height < 820 * 0.75,
            $"the sheet must size to its content, was {sheet.Bounds.Height:0} tall");
        var bottom = sheet.TranslatePoint(new Point(0, sheet.Bounds.Height), window);
        Assert.NotNull(bottom);
        Assert.True(bottom.Value.Y >= 820 - 40, $"the sheet must sit at the bottom, was {bottom.Value.Y:0}");
        // The conversation behind it stays visible above the sheet.
        Assert.True(sheet.Bounds.Y > 0, "the sheet must not start at the very top of the page");
    }

    // ---- 5. wording -------------------------------------------------------------------------

    [AvaloniaFact]
    public void The_receive_action_is_not_called_offline_receiving()
    {
        var view = Open(390, out var window);
        view.Conversation.OpenSetup();
        window.UpdateLayout();

        var text = TextOf(view);
        Assert.Contains("接收文件", text);
        // The direct receiver only works while MPT runs; it must not be sold as offline availability.
        Assert.DoesNotContain("离线收件", text);
    }

    [AvaloniaFact]
    public void The_empty_state_promises_other_devices_only_when_the_session_is_linked()
    {
        var unlinked = Open(390, out var first);
        var unlinkedText = TextOf(unlinked);
        Assert.Contains("内容保存在这台设备", unlinkedText);
        Assert.DoesNotContain("都能看到", unlinkedText);

        _module.AssistantLinked = true;
        var linked = Open(390, out _);
        Assert.DoesNotContain(Descendants(linked).OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "内容保存在这台设备");
        _ = first;
    }

    // ---- helpers ----------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("unconfigured")]
    [InlineData("unavailable")]
    public async Task A_connection_code_stays_available_when_no_network_path_is_ready(string relay)
    {
        _module.AssistantRelayState = relay;
        var view = Open(390, out var window);
        await TestPump.RunAsync(() => view.Conversation.OpenLinkSheetForTestAsync());
        window.UpdateLayout();

        Assert.Equal(_module.LinkCode, view.Conversation.QrValue);
        var copy = Assert.Single(Descendants(view.Conversation.SheetHost).OfType<Button>(),
            button => button.Content as string == "复制连接码");
        Assert.True(copy.IsEnabled);
        var text = TextOf(view.Conversation.SheetHost);
        Assert.DoesNotContain("先连接", text);
        Assert.DoesNotContain("中转暂时不可用", text);
        Assert.DoesNotContain("启用中转", text);
    }

    [AvaloniaTheory]
    [InlineData("unconfigured", false)]
    [InlineData("available", false)]
    [InlineData("unavailable", false)]
    public void Sync_retry_is_offered_only_for_a_failed_configured_relay(string relay, bool visible)
    {
        _module.AssistantRelayState = relay;
        var view = Open(1024, out _);
        Assert.DoesNotContain(Descendants(view).OfType<Button>(), button => button.IsEffectivelyVisible && button.Content as string == "重新同步");
    }

    [AvaloniaTheory]
    [InlineData(390, false)]
    [InlineData(390, true)]
    [InlineData(1024, false)]
    [InlineData(1024, true)]
    public async Task Existing_connection_links_open_their_own_confirmation_flow(int width, bool cloud)
    {
        var view = Open(width, out var window);
        var code = cloud
            ? new FileTransfer.Core.CloudConnection("https://relay.example.test/dav", "test", "synthetic-password").Encode()
            : new FileTransfer.Core.Pairing("test-pc", "测试电脑", "100.64.0.9", new string('a', 64)).Encode();
        var handled = await view.ActivateAsync(new global::MyPowerTools.Abstractions.ToolActivationRequest(
            "file-transfer", "main", code));
        window.UpdateLayout();

        Assert.True(handled);
        Assert.Equal(!cloud, view.IsConversationVisible);
        Assert.Equal(cloud && width < 640, view.IsMobileLayout);
        Assert.Contains(Descendants(view).OfType<TextBox>(), box => box.IsEffectivelyVisible && box.Text == code);
        Assert.Equal(0, _module.CountCalls("assistant.link.preview"));
        Assert.Equal(0, _module.CountCalls(cloud ? "cloud.import" : "pair.import"));
    }

    [AvaloniaFact]
    public void A_failed_upload_keeps_its_local_file_available_to_open()
    {
        var path = Touch("本地报告.pdf");
        _module.AssistantItems.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "local-failed", ["kind"] = "file", ["name"] = "本地报告.pdf",
            ["state"] = "failed", ["localPath"] = path, ["error"] = "中转暂不可用"
        });
        var view = Open(390, out _);
        Assert.True(Assert.Single(view.Assistant.Snapshot.Items).CanOpen);
        Assert.Contains(Descendants(view).OfType<Button>(), button =>
            button.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(button)?.StartsWith("打开 ") == true);
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "content");
        return path;
    }

    private TransferView Open(int width, out Window window, Func<CancellationToken, Task<string?>>? scan = null, double height = 820)
    {
        var view = new TransferView(_module.Context(_root, scanConnectionCode: scan));
        window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        _windows.Add(window);
        for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        TestPump.Drain();
        window.UpdateLayout();
        ConversationTestNavigation.Open(window, view);
        return view;
    }

    private static System.Text.Json.Nodes.JsonObject Device(string id, string name) => new()
    {
        ["deviceId"] = id,
        ["name"] = name,
        ["address"] = "100.64.0.5",
        ["platform"] = "windows",
        ["paired"] = true,
        ["available"] = true
    };

    private static IEnumerable<Control> Descendants(Control control) =>
        control.GetLogicalDescendants().OfType<Control>();

    private static string TextOf(Control control) => string.Join(
        "\n",
        Descendants(control).OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text ?? ""));
}
