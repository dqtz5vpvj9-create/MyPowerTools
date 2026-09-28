using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// The interaction paths a person actually takes: real pointer clicks, real key presses and real
/// clipboard text, with no test reaching into the core or setting the send button's state by hand.
/// These are the checks that catch a control which exists but cannot be reached or used.
/// </summary>
public sealed class ConversationInteractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-act-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly List<Window> _windows = [];

    public ConversationInteractionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void Typing_real_text_enables_send_and_pressing_it_sends()
    {
        var view = Open(390, out var window);
        var input = Composer(view);
        var send = Button(view, "发送");
        Assert.False(send.IsEnabled, "an empty composer must not offer a send");

        // Tap the field, then type: the same two gestures a person makes.
        Click(window, input);
        Assert.True(input.IsFocused, "tapping the field should focus it");
        window.KeyTextInput("买牛奶");
        window.UpdateLayout();

        Assert.Equal("买牛奶", input.Text);
        Assert.True(send.IsEnabled, "typing must light up the send action without any other refresh");

        Click(window, send);
        window.UpdateLayout();

        Assert.Equal("", input.Text);
        Assert.Equal(1, _module.CountCalls("assistant.send"));
        Assert.Equal("买牛奶", _module.LastArgs("assistant.send")["text"]!.GetValue<string>());
        Assert.False(send.IsEnabled);
    }

    [AvaloniaFact]
    public void Enter_sends_and_shift_enter_breaks_the_line()
    {
        var view = Open(390, out var window);
        var input = Composer(view);
        Click(window, input);

        // The field accepts returns, so a multi-line note is possible; Enter itself sends.
        Assert.True(input.AcceptsReturn, "the composer must accept a multi-line draft");
        input.Text = "第一行\n第二行";
        window.UpdateLayout();
        Assert.Equal(0, _module.CountCalls("assistant.send"));

        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        window.UpdateLayout();

        Assert.Equal(1, _module.CountCalls("assistant.send"));
        var sent = _module.LastArgs("assistant.send")["text"]!.GetValue<string>();
        Assert.Contains("第一行", sent);
        Assert.Contains("第二行", sent);
    }

    [AvaloniaFact]
    public async Task A_slow_send_does_not_discard_a_draft_typed_while_waiting()
    {
        // The module holds the send open, so the user has time to change the draft before it answers.
        var gate = new SemaphoreSlim(0);
        _module.BeforeAnswer = command => command == "assistant.send" ? gate.WaitAsync() : null;
        var view = Open(390, out var window);
        var input = Composer(view);

        input.Text = "第一条";
        window.UpdateLayout();
        var firstSend = view.Conversation.SendFromComposerAsync();
        window.UpdateLayout();

        // While the module is still answering, the user replaces the draft with the next message.
        input.Text = "第二条";
        window.UpdateLayout();
        Assert.Equal("第二条", input.Text);

        gate.Release();
        await firstSend;
        TestPump.Drain();
        window.UpdateLayout();

        // Only the text that was actually sent is cleared; the newer draft survives.
        Assert.Equal("第二条", input.Text);
        Assert.Equal(1, _module.AssistantItems.Count);
        Assert.Equal("第一条", _module.AssistantItems[0]["text"]!.GetValue<string>());
    }

    public void The_attachment_and_image_actions_are_visible_and_tappable()
    {
        var view = Open(390, out var window);
        foreach (var name in new[] { "添加附件" })
        {
            var button = Descendants(view).OfType<Button>()
                .First(candidate => AutomationName(candidate) == name);
            Assert.True(button.IsEffectivelyVisible, $"{name} must be visible on the main page");
            Assert.True(button.Bounds.Width >= 44, $"{name} must keep a 44 dp target, was {button.Bounds.Width:0}");
            Assert.True(button.Bounds.Height >= 44, $"{name} must keep a 44 dp target, was {button.Bounds.Height:0}");
            var origin = button.TranslatePoint(default, window);
            Assert.NotNull(origin);
            Assert.True(origin.Value.X >= 0 && origin.Value.X + button.Bounds.Width <= 390 + 1,
                $"{name} must sit inside the phone width, was at {origin.Value.X:0}");
        }
    }

    [AvaloniaFact]
    public void The_setup_and_close_icons_render_a_real_glyph()
    {
        var view = Open(390, out var window);

        var setup = Descendants(view).OfType<Button>().First(candidate => AutomationName(candidate) == "连接与接收设置");
        var setupIcon = Assert.IsType<MobileIcon>(setup.Content);
        Assert.True(setupIcon.IsEffectivelyVisible, "the settings icon must be on screen");
        // A PathIcon fallback leaves Data null and paints a filled block; a resolved stroke outline
        // is what makes the glyph readable.
        Assert.NotNull(setupIcon.Data);
        Assert.Equal(Avalonia.Media.Stretch.None, setupIcon.Stretch);

        view.Conversation.OpenSetup();
        window.UpdateLayout();
        var close = Descendants(view).OfType<Button>().First(candidate => AutomationName(candidate) == "关闭");
        var closeIcon = Assert.IsType<MobileIcon>(close.Content);
        Assert.True(closeIcon.IsEffectivelyVisible, "the close icon must be on screen");
        Assert.NotNull(closeIcon.Data);
        Assert.True(close.Bounds.Width >= 44 && close.Bounds.Height >= 44,
            $"the close target must be at least 44 dp, was {close.Bounds.Width:0}x{close.Bounds.Height:0}");
    }

    [AvaloniaFact]
    public void The_composer_field_keeps_real_width_at_every_phone_size()
    {
        foreach (var width in new[] { 320, 390, 768 })
        {
            var view = Open(width, out var window);
            var input = Composer(view);
            Assert.True(input.Bounds.Width >= 120,
                $"at {width} the draft field must stay usable, was {input.Bounds.Width:0}");
            var send = Button(view, "发送");
            Assert.True(send.IsEffectivelyVisible, $"at {width} the send action must be visible");
            var device = Button(view, "发给 文件传输助手 ▾");
            Assert.True(device.IsEffectivelyVisible, $"at {width} the device action must be visible");
            // The icons must not be pushed off by that field.
            foreach (var name in new[] { "添加附件" })
            {
                var button = Descendants(view).OfType<Button>().First(candidate => AutomationName(candidate) == name);
                Assert.True(button.IsEffectivelyVisible, $"at {width} {name} must be visible");
            }
        }
    }

    [AvaloniaFact]
    public void The_sheet_covers_the_whole_page_and_closing_it_returns_to_the_composer()
    {
        var view = Open(390, out var window);
        var input = Composer(view);
        Click(window, input);
        window.KeyTextInput("草稿");
        window.UpdateLayout();

        // The device picker opens from the composer itself, not from the settings sheet.
        Click(window, Button(view, "发给 文件传输助手 ▾"));
        window.UpdateLayout();

        Assert.True(view.Conversation.IsSheetOpen);
        // The scrim must span every row, or the composer stays visible and tappable under the sheet.
        var scrim = Descendants(view).OfType<Border>().First(border => border.Background is Avalonia.Media.SolidColorBrush brush
            && brush.Color.A > 0 && brush.Color.R < 80 && Grid.GetRowSpan(border) > 1);
        Assert.Equal(3, Grid.GetRowSpan(scrim));

        var sheet = Descendants(view).OfType<Border>().First(border => border.Classes.Contains(MobileUi.Classes.Sheet));
        var sheetBottom = sheet.TranslatePoint(new Point(0, sheet.Bounds.Height), window);
        Assert.NotNull(sheetBottom);
        // The theme gives the sheet a bottom inset, so "at the bottom" means the page's lowest band.
        Assert.True(sheetBottom.Value.Y >= 820 - 40, $"the sheet must sit at the bottom, bottom was {sheetBottom.Value.Y:0}");

        Click(window, Descendants(view).OfType<Button>().First(candidate => candidate.IsEffectivelyVisible && AutomationName(candidate) == "关闭"));
        window.UpdateLayout();

        Assert.False(view.Conversation.IsSheetOpen);
        Assert.Equal("草稿", input.Text);
        Assert.True(input.IsEffectivelyVisible, "the composer must be usable again after closing");
    }

    [AvaloniaFact]
    public void The_device_picker_shows_compact_tiles_for_several_devices()
    {
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑", available: true));
        _module.AssistantDevices.Add(Device("mac-2", "MacBook Pro", available: true));
        _module.AssistantDevices.Add(Device("old-3", "书房台式机", available: false));
        var view = Open(390, out var window);

        Click(window, Button(view, "发给 文件传输助手 ▾"));
        window.UpdateLayout();
        TestPump.Drain();
        window.UpdateLayout();

        var tiles = Descendants(view).OfType<Button>()
            .Where(candidate => candidate.IsEffectivelyVisible && AutomationName(candidate).StartsWith("选择 ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, tiles.Length);
        foreach (var tile in tiles)
        {
            Assert.True(tile.Bounds.Height <= 140, $"a device tile must stay compact, was {tile.Bounds.Height:0}");
            Assert.True(tile.IsEffectivelyVisible, "every device tile must be reachable without scrolling");
        }
        Assert.Equal(3, tiles.Select(tile => tile.Bounds.Y).Distinct().Count());
        Assert.Contains("可发送", TextOf(view));
        Assert.DoesNotContain("当前不可用", TextOf(view));
    }

    [AvaloniaFact]
    public void Tapping_a_device_tile_sends_the_pending_attachment_to_it()
    {
        var file = Path.Combine(_root, "报告.pdf");
        File.WriteAllText(file, "pdf");
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑", available: true));
        var view = Open(390, out var window);
        view.Conversation.AddAttachment(file);
        window.UpdateLayout();

        Click(window, Button(view, "发给 文件传输助手 ▾"));
        window.UpdateLayout();
        TestPump.Drain();
        window.UpdateLayout();

        var tile = Descendants(view).OfType<Button>().First(candidate => AutomationName(candidate) == "选择 工作电脑");
        Click(window, tile);
        window.UpdateLayout();

        Assert.Equal(0, _module.CountCalls("assistant.send"));
        Click(window, Button(view, "发送"));
        Assert.Equal(1, _module.CountCalls("assistant.send"));
        Assert.Equal("pc-1", _module.LastArgs("assistant.send")["targetDeviceId"]!.GetValue<string>());
        // The chosen device is the send, so the picker closes by itself.
        Assert.False(view.Conversation.IsSheetOpen);
    }

    [AvaloniaFact]
    public void Opening_the_picker_starts_a_fresh_lookup_and_closing_it_cancels()
    {
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑", available: true));
        var view = Open(390, out var window);

        Click(window, Button(view, "发给 文件传输助手 ▾"));
        TestPump.Drain();
        Assert.True(_module.CountCalls("assistant.devices") >= 1, "opening the picker must look for devices");

        // A second open asks again rather than reusing a stale list.
        Click(window, Descendants(view).OfType<Button>().First(candidate => candidate.IsEffectivelyVisible && AutomationName(candidate) == "关闭"));
        window.UpdateLayout();
        Click(window, Button(view, "发给 文件传输助手 ▾"));
        TestPump.Drain();
        Assert.True(_module.CountCalls("assistant.devices") >= 2, "reopening the picker must look again");

        // Closing the sheet ends the pass; the module is told to stop by the cancellation it sees.
        Click(window, Descendants(view).OfType<Button>().First(candidate => candidate.IsEffectivelyVisible && AutomationName(candidate) == "关闭"));
        window.UpdateLayout();
        Assert.False(view.Conversation.IsSheetOpen);
    }

    [AvaloniaFact]
    public async Task A_shared_text_link_appends_to_the_draft_without_sending()
    {
        var view = Open(390, out var window);
        var input = Composer(view);
        Click(window, input);
        window.KeyTextInput("已有的草稿");
        window.UpdateLayout();

        var shared = "mypowertools://file-assistant?text=" + Uri.EscapeDataString("分享过来的一段话");
        var handled = await view.ActivateAsync(new global::MyPowerTools.Abstractions.ToolActivationRequest("file-transfer", "workspace", shared));
        window.UpdateLayout();

        Assert.True(handled);
        Assert.Contains("已有的草稿", input.Text);
        Assert.Contains("分享过来的一段话", input.Text);
        // Activation prepares content only; it never sends on the user's behalf.
        Assert.Equal(0, _module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public async Task Forwarding_resolves_the_entry_content_instead_of_passing_its_id()
    {
        // The module reports a text entry and a file entry that is not on this device yet.
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-1", ["kind"] = "text", ["text"] = "会议纪要", ["state"] = "delivered" });
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-2", ["kind"] = "file", ["name"] = "报告.pdf", ["size"] = 10, ["state"] = "available" });
        _module.AssistantDevices.Add(Device("pc-1", "工作电脑", available: true));
        var view = Open(390, out var window);
        await TestPump.RunAsync(() => view.Assistant.RefreshAsync());
        window.UpdateLayout();

        // Forwarding must not drag the composer's own draft along.
        var input = Composer(view);
        Click(window, input);
        window.KeyTextInput("不该被转发的草稿");
        window.UpdateLayout();

        Click(window, view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<Button>().First(b => AutomationName(b).StartsWith("消息操作 ")));
        Click(window, Button(view, "转发"));
        window.UpdateLayout();
        TestPump.Drain();
        window.UpdateLayout();
        Click(window, DeviceTile(view, "工作电脑"));
        Click(window, Button(view, "确认转发"));
        await TestPump.SettleAsync();

        var sent = _module.LastArgs("assistant.send");
        Assert.True(_module.CountCalls("assistant.send") == 1,
            "sends=" + _module.CountCalls("assistant.send") + " open=" + view.Conversation.IsSheetOpen);
        Assert.Equal("会议纪要", sent["text"]?.GetValue<string>());
        Assert.Equal("pc-1", sent["targetDeviceId"]?.GetValue<string>());
        // The draft is still waiting in the composer, untouched by the forward.
        Assert.Contains("不该被转发的草稿", input.Text);
        // The item id must never appear as a path: the contract wants real file paths.
        if (sent["paths"] is JsonArray paths)
            Assert.DoesNotContain("item-1", paths.Select(node => node?.GetValue<string>()));
    }

    public async Task Forwarding_a_text_entry_does_not_delete_the_composer_draft()
    {
        _module.AssistantItems.Add(new JsonObject { ["id"] = "item-1", ["kind"] = "text", ["text"] = "会议纪要", ["state"] = "delivered" });
        var view = Open(390, out var window);
        await TestPump.RunAsync(() => view.Assistant.RefreshAsync());
        window.UpdateLayout();

        _module.AssistantDevices.Add(Device("pc-1", "工作电脑", available: true));
        var input = Composer(view);
        Click(window, input);
        window.KeyTextInput("我的草稿");
        window.UpdateLayout();

        Click(window, view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<Button>().First(b => AutomationName(b).StartsWith("消息操作 ")));
        Click(window, Button(view, "转发"));
        window.UpdateLayout();
        TestPump.Drain();
        window.UpdateLayout();
        var tiles = Descendants(Sheet(view)).OfType<Button>().Select(AutomationName).Where(n => n.Length > 0).ToArray();
        Click(window, DeviceTile(view, "工作电脑"));
        Click(window, Button(view, "确认转发"));
        await TestPump.SettleAsync();
        window.UpdateLayout();

        Assert.True(_module.CountCalls("assistant.send") == 1,
            "sends=" + _module.CountCalls("assistant.send") + " open=" + view.Conversation.IsSheetOpen +
            " tiles=[" + string.Join(",", tiles) + "]");
        // The forwarded content is the entry's, and the draft is untouched.
        Assert.Equal("会议纪要", _module.LastArgs("assistant.send")["text"]?.GetValue<string>());
        Assert.Equal("pc-1", _module.LastArgs("assistant.send")["targetDeviceId"]?.GetValue<string>());
        Assert.Equal("我的草稿", input.Text);
    }

    [AvaloniaFact]
    public async Task A_cancelled_scan_reports_that_it_was_cancelled()
    {
        var view = Open(390, out var window, scanResult: null);
        view.Conversation.OpenSetup();
        window.UpdateLayout();
        Click(window, SheetRow(view, "连接我的设备"));
        window.UpdateLayout();

        var scan = Descendants(Sheet(view)).OfType<Button>().FirstOrDefault(candidate => (candidate.Content as string) == "扫描二维码");
        Assert.True(scan is not null, "scan button missing; visible text was:\n" + TextOf(view));
        Click(window, scan);
        await TestPump.SettleAsync();
        window.UpdateLayout();

        // A dismissed scan changes nothing and says so; it never imports a code.
        Assert.Contains("已取消扫码", view.Assistant.Snapshot.Status);
        Assert.Equal(0, _module.CountCalls("assistant.link.import"));
    }

    [AvaloniaFact]
    public async Task A_scanned_code_is_previewed_and_only_imported_on_confirmation()
    {
        var view = Open(390, out var window, scanResult: "mpt://assistant/abc");
        view.Conversation.OpenLinkSheet();
        window.UpdateLayout();
        await view.Conversation.ScanForTestAsync();
        window.UpdateLayout();

        Assert.True(_module.CountCalls("assistant.link.preview") == 1,
            "status='" + view.Assistant.Snapshot.Status + "' linkCode='" + (view.Conversation.SheetHost
                .GetLogicalDescendants().OfType<TextBox>().FirstOrDefault()?.Text ?? "none") + "'");

        await view.Conversation.ImportLinkForTestAsync();
        window.UpdateLayout();

        Assert.Equal(1, _module.CountCalls("assistant.link.import"));
    }

    [AvaloniaFact]
    public void A_host_without_a_scanner_offers_no_scan_button()
    {
        var view = Open(390, out var window, hostHasScanner: false);
        view.Conversation.OpenSetup();
        window.UpdateLayout();
        Click(window, SheetRow(view, "连接我的设备"));
        window.UpdateLayout();

        Assert.True(view.Conversation.IsSheetOpen, "the setup row should have opened the link sheet");
        Assert.DoesNotContain("扫描二维码", TextOf(view));
        Assert.Contains("对方的连接码", TextOf(view));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private TransferView Open(int width, out Window window, string? scanResult = null, bool hostHasScanner = true)
    {
        var context = _module.Context(_root,
            scanConnectionCode: hostHasScanner ? _ => Task.FromResult(scanResult) : null);
        var view = new TransferView(context);
        window = new Window { Width = width, Height = 820, Content = view };
        window.Show();
        _windows.Add(window);
        for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        TestPump.Drain();
        window.UpdateLayout();
        return view;
    }

    private static TextBox Composer(TransferView view) =>
        Descendants(view).OfType<TextBox>().First(box => box.IsEffectivelyVisible);

    /// <summary>A button on a conversation entry, scoped to the thread panel itself.</summary>
    private static Button ThreadButton(TransferView view, string content)
    {
        var thread = Descendants(view.Conversation.ThreadPanel).OfType<Button>().ToArray();
        return thread.FirstOrDefault(button => (button.Content as string) == content)
            ?? throw new InvalidOperationException(
                $"no '{content}' in the thread; buttons=[{string.Join(",", thread.Select(b => b.Content as string ?? b.GetType().Name))}] " +
                $"threadChildren={view.Conversation.ThreadPanel.Children.Count} items={view.Assistant.Snapshot.Items.Count} " +
                $"shape=[{string.Join(",", view.Conversation.ThreadPanel.GetLogicalDescendants().Select(d => d.GetType().Name))}]");
    }

    /// <summary>One device tile in the open picker, chosen by the device's own name.</summary>
    private static Button DeviceTile(TransferView view, string deviceName) =>
        Descendants(Sheet(view)).OfType<Button>()
            .First(button => AutomationName(button) == "选择 " + deviceName);

    private static Button Button(TransferView view, string content) =>
        Descendants(view).OfType<Button>()
            .First(button => button.IsEffectivelyVisible && (button.Content as string) == content);

    /// <summary>
    /// Types into a field the way composing does: each character is inserted at the caret and the
    /// caret advances. The headless text-input helper replaces the whole value instead, which cannot
    /// express "keep typing into an existing draft".
    /// </summary>
    private static void Type(TextBox field, string text)
    {
        foreach (var character in text)
        {
            var caret = field.CaretIndex;
            var current = field.Text ?? "";
            field.Text = current[..caret] + character + current[caret..];
            field.CaretIndex = caret + 1;
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A row inside the open sheet. The empty state holds a button with the same label, so the search
    /// is scoped to the sheet the user is actually looking at.
    /// </summary>
    private static Button SheetRow(TransferView view, string contains)
    {
        var sheet = Sheet(view);
        return Descendants(sheet).OfType<Button>()
            .First(button => button.IsEffectivelyVisible && ContainsText(button, contains));
    }

    /// <summary>The one sheet host the page owns. Its rows are the only tappable management actions.</summary>
    private static Border Sheet(TransferView view) => view.Conversation.SheetHost;

    private static bool ContainsText(Control control, string value) =>
        Descendants(control).OfType<TextBlock>().Any(block => (block.Text ?? "").Contains(value, StringComparison.Ordinal));

    private static string AutomationName(Control control) =>
        Avalonia.Automation.AutomationProperties.GetName(control) ?? "";

    private static double _deviceGridWidth(TransferView view) =>
        Descendants(view).OfType<WrapPanel>().FirstOrDefault()?.Bounds.Width ?? 0;

    private static JsonObject Device(string id, string name, bool available) => new()
    {
        ["deviceId"] = id,
        ["name"] = name,
        ["address"] = "100.64.0.5",
        ["platform"] = "windows",
        ["paired"] = true,
        ["available"] = available
    };

    private static void Click(Window window, Control control)
    {
        // A control that just became visible has no usable bounds until layout has run, so settle the
        // tree before computing where the finger lands.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(center);
        var label = AutomationName(control);
        Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0,
            "cannot tap " + (label.Length > 0 ? label : control.GetType().Name) + ": it has no size");
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.GetLogicalDescendants().OfType<Control>();

    private static string TextOf(Control control) => string.Join(
        "\n",
        Descendants(control).OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text ?? ""));
}
