using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FileTransfer.Surface.Tests;

public sealed class ConversationV2Tests
{
    [AvaloniaFact]
    public void Receive_page_exports_the_ordinary_file_code_without_sharing_the_conversation()
    {
        var module = new FakeTransferModule();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        view.Conversation.OpenSetup();
        var receive = view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => b.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "接收文件"));
        Click(host.Window, receive);
        var export = view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "显示我的文件互传码"));
        Click(host.Window, export);
        Assert.Equal(1, module.CountCalls("pairing"));
        Assert.Equal(0, module.CountCalls("assistant.link.export"));
        var qr = view.Conversation.SheetHost.GetLogicalDescendants()
            .OfType<global::MyPowerTools.AvaloniaSdk.Controls.MptQrCode>().Single();
        Assert.StartsWith("mpt://pair/", qr.Value);
        Assert.Contains(view.Conversation.SheetHost.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == "仅允许向这台设备发送文件和文字");
        Assert.Contains(view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>(),
            b => Equals(b.Content, "复制连接码"));
    }

    [AvaloniaTheory]
    [InlineData("配对码里的投递密钥与服务器不一致，请让收件设备重新出示配对码。", "重新出示连接码")]
    [InlineData("文件超过中转单文件上限，无法投递。", "压缩或拆分")]
    [InlineData("中转空间不足，请先在收件设备上清理已接收的文件。", "清理已接收")]
    [InlineData("待发副本不存在。", "重新选择原文件")]
    [InlineData("System.IO.IOException: https://private.example/path?token=SECRET at /private/file.txt", "可以重试或取消")]
    public void Failed_message_shows_actionable_private_details_and_can_be_cancelled(string error, string expected)
    {
        var module = new FakeTransferModule();
        var item = Item(0, false);
        item["state"] = "failed";
        item["error"] = error;
        module.AssistantItems.Add(item);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        void OpenMenu() => Click(host.Window, view.GetLogicalDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b)?.StartsWith("消息操作 ") == true));
        Button Action(string text) => view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, text));
        OpenMenu();
        Assert.True(Action("取消发送").IsEnabled);
        Click(host.Window, Action("查看详情"));
        var details = string.Join("\n", view.Conversation.SheetHost.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        Assert.Contains(expected, details);
        Assert.DoesNotContain("SECRET", details);
        Assert.DoesNotContain("private.example", details);
        Assert.DoesNotContain("/private/file.txt", details);
        view.Conversation.TryHandleBack();
        OpenMenu();
        Click(host.Window, Action("取消发送"));
        Assert.Equal("row0", module.LastArgs("assistant.cancel")["itemId"]!.GetValue<string>());
        Assert.Equal("cancelled", module.AssistantItems[0]["state"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Cached_target_is_selectable_during_discovery_and_selection_does_not_send()
    {
        var module = new FakeTransferModule();
        module.AddPeer("laptop", "LIS-IMAC", "");
        var pending = new TaskCompletionSource();
        module.BeforeAnswer = command => command == "assistant.devices" ? pending.Task : null;
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        view.Conversation.OpenDevicePicker();
        host.Settle();
        var target = view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "打开会话 LIS-IMAC");
        Click(host.Window, target);
        Assert.Equal("laptop", view.Conversation.SelectedTargetDeviceId);
        Assert.Equal(0, module.CountCalls("assistant.send"));
        Composer(view).Text = "交给电脑的文字";
        host.Settle();
        Click(host.Window, view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "发送")));
        Assert.Equal("laptop", module.LastArgs("assistant.send")["targetDeviceId"]!.GetValue<string>());
        pending.SetResult();
        await Task.Yield();
    }

    [AvaloniaFact]
    public void Enter_is_newline_and_control_enter_sends_to_selected_scope()
    {
        var module = new FakeTransferModule();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        var input = Composer(view);
        Assert.Equal("消息内容", AutomationProperties.GetName(input));
        input.Focus();
        input.Text = "第一行";
        input.CaretIndex = input.Text.Length;
        host.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(0, module.CountCalls("assistant.send"));
        Assert.Contains('\n', input.Text);
        host.Window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        Assert.Equal(1, module.CountCalls("assistant.send"));
        Assert.Null(module.LastArgs("assistant.send")["targetDeviceId"]);
    }

    [AvaloniaFact]
    public void History_reading_is_not_interrupted_by_incoming_message()
    {
        var module = History(false);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        var scroll = view.Conversation.ThreadPanel.GetVisualAncestors().OfType<ScrollViewer>().First();
        scroll.Offset = default;
        host.Settle();
        var before = scroll.Offset;
        module.AssistantItems.Add(Item(99, false));
        module.EmitAssistantChanged();
        host.Settle();
        Assert.Equal(before, scroll.Offset);
        var newer = view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "新消息 ↓"));
        Assert.True(newer.IsVisible);
        Click(host.Window, newer);
        Assert.True(scroll.Offset.Y > before.Y);
    }

    [AvaloniaTheory]
    [InlineData(false, 5, 390)]
    [InlineData(false, 5, 320)]
    [InlineData(true, 4, 390)]
    [InlineData(true, 4, 320)]
    public void Compact_conversation_meets_density_and_has_one_send_action(bool files, int requiredVisible, int width)
    {
        var module = History(files);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view, width);
        var scroll = view.Conversation.ThreadPanel.GetVisualAncestors().OfType<ScrollViewer>().First();
        using (var rendered = host.Window.CaptureRenderedFrame()) { }
        host.Settle();
        var visible = view.Conversation.ThreadPanel.Children.Count(row =>
        {
            var p = row.TranslatePoint(default, scroll)!.Value;
            return p.Y >= -.5 && p.Y + row.Bounds.Height <= scroll.Viewport.Height + .5;
        });
        Assert.True(visible >= requiredVisible, $"Only {visible} complete rows fit; expected {requiredVisible}.");
        Assert.Single(view.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "发送"));
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("发给自己") == true);
        Assert.DoesNotContain(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<Button>(), b => Equals(b.Content, "转发到设备"));
        foreach (var text in view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>())
        {
            var position = text.TranslatePoint(default, scroll)!.Value;
            Assert.True(position.X >= -.5 && position.X + text.Bounds.Width <= scroll.Viewport.Width + .5,
                $"Clipped text '{text.Text}' at {position.X}..{position.X + text.Bounds.Width}, viewport {scroll.Viewport.Width}");
        }
        Save(host.Window, (files ? "files-" : "chat-") + width);
        if (!files)
        {
            view.Conversation.OpenDevicePicker();
            host.Settle();
            Save(host.Window, "targets-390");
        }
    }

    [AvaloniaFact]
    public async Task Closing_and_reopening_restores_unsent_text_attachments_and_target()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-v2-draft-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "待发内容");
        try
        {
            var module = new FakeTransferModule();
            module.AddPeer("laptop", "LIS-IMAC");
            var first = new TransferView(module.Context(Path.GetTempPath()));
            using (var host = new Host(first))
            {
                first.Conversation.OpenDevicePicker(); host.Settle();
                Click(host.Window, first.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "打开会话 LIS-IMAC"));
                Composer(first).Text = "重开后还在的草稿";
                first.Conversation.AddAttachment(path);
            }
            var second = new TransferView(module.Context(Path.GetTempPath()));
            using var reopened = new Host(second);
            Assert.Equal("重开后还在的草稿", Composer(second).Text);
            Assert.Equal("laptop", second.Conversation.SelectedTargetDeviceId);
            Assert.Equal(1, second.Conversation.AttachmentCount);
            Assert.Equal(0, module.CountCalls("assistant.send"));
            await second.Conversation.SendFromComposerAsync();
            Assert.Equal("laptop", module.LastArgs("assistant.send")["targetDeviceId"]!.GetValue<string>());
            Assert.Equal("", module.DraftPreferences["draftText"]!.GetValue<string>());
            Assert.Empty(module.DraftPreferences["attachmentPaths"]!.AsArray());
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Ordinary_pair_activation_only_previews_until_user_confirms()
    {
        var module = new FakeTransferModule();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        await view.ActivateAsync(new global::MyPowerTools.Abstractions.ToolActivationRequest("file-transfer", "workspace", "mpt://pair/test"));
        host.Settle();
        Assert.Equal("添加设备", view.Conversation.SheetTitle);
        Assert.Equal(1, module.CountCalls("pair.preview"));
        Assert.Equal(0, module.CountCalls("pair.import"));
        Assert.Equal(0, module.CountCalls("assistant.link.import"));
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "确认添加")));
        Assert.Equal(1, module.CountCalls("pair.import"));
        Assert.Equal(0, module.CountCalls("assistant.link.import"));
    }

    [AvaloniaFact]
    public void Removed_target_never_silently_sends_draft_to_the_shared_conversation()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["draftText"] = "不能改发其他地方";
        module.DraftPreferences["targetDeviceId"] = "removed";
        module.DraftPreferences["targetUsable"] = false;
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        Assert.False(view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "发送")).IsEnabled);
        Assert.Equal("removed", view.Conversation.SelectedTargetDeviceId);
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public void Discovery_diagnostics_do_not_replace_send_feedback_or_disable_remembered_targets()
    {
        var module = new FakeTransferModule
        {
            AssistantDiscoveryState = "unsupported",
            AssistantDevicesMessage = "Tailscale 不可用，本次没有 Tailnet 候选。"
        };
        module.AddPeer("laptop", "LIS-IMAC", "");
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        view.Assistant.PublishOnUi(view.Assistant.Snapshot with { Status = "发送未完成，请重新添加这台设备。" });
        view.Conversation.OpenDevicePicker();
        host.Settle();
        Assert.Contains("Tailscale", view.Assistant.Snapshot.DiscoveryMessage);
        Assert.Equal("发送未完成，请重新添加这台设备。", view.Assistant.Snapshot.Status);
        var target = view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "打开会话 LIS-IMAC");
        Assert.True(target.IsEnabled);
        Click(host.Window, target);
        Assert.Equal("laptop", view.Conversation.SelectedTargetDeviceId);
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && (t.Text?.Contains("Tailscale") == true || t.Text?.Contains("Tailnet") == true));
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "发送未完成，请重新添加这台设备。");
    }

    private static FakeTransferModule History(bool files)
    {
        var module = new FakeTransferModule { AssistantLinked = true };
        module.AddPeer("laptop", "LIS-IMAC");
        for (var i = 0; i < 10; i++) module.AssistantItems.Add(Item(i, files));
        return module;
    }

    private static JsonObject Item(int i, bool file) => new()
    {
        ["id"] = "row" + i, ["kind"] = file ? "file" : "text", ["text"] = i % 2 == 0 ? "项目资料收到了，谢谢。" : "请看这一版，已更新。",
        ["name"] = file ? $"项目资料 · 修订{i}.pdf" : null, ["size"] = file ? 24000 : 0,
        ["senderDeviceId"] = i % 2 == 0 ? "mpt-phone" : "laptop", ["senderName"] = "LIS-IMAC",
        ["conversationKey"] = "shared", ["targetDeviceId"] = null, ["state"] = i % 2 == 0 ? "delivered" : "available",
        ["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(i - 10).ToString("O"), ["receipts"] = new JsonArray()
    };

    private static TextBox Composer(Control view) => view.GetLogicalDescendants().OfType<TextBox>().First(t => AutomationProperties.GetName(t) == "消息内容");
    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }
    private static void Save(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("MPT_V2_SCREENSHOTS");
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        using var bitmap = window.CaptureRenderedFrame();
        bitmap!.Save(Path.Combine(dir, name + ".png"));
    }
    private sealed class Host : IDisposable
    {
        public Window Window { get; }
        public Host(Control view, int width = 390)
        {
            Window = new Window { Width = width, Height = 844, Content = view };
            Window.Show(); Settle();
            if (view is TransferView transfer) ConversationTestNavigation.Open(Window, transfer);
        }
        public void Settle() { for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); } }
        public void Dispose() => Window.Close();
    }
}
