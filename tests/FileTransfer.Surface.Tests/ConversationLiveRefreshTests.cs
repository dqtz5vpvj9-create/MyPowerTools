using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FileTransfer.Surface.Tests;

public sealed class ConversationLiveRefreshTests
{
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(1200)]
    public async Task Sending_with_history_keeps_the_new_message_in_the_visible_thread(int width)
    {
        var module = WithHistory();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = width, Height = 820, Content = view };
        try
        {
            window.Show();
            Settle(window);
            ConversationTestNavigation.Open(window, view);
            view.Conversation.GetLogicalDescendants().OfType<TextBox>().First(t => t.PlaceholderText == "写点文字，或添加文件…").Text = "刚刚发送的新消息";
            await view.Conversation.SendFromComposerAsync();
            Settle(window);

            Assert.Equal(13, view.Assistant.Snapshot.Items.Count);
            AssertVisibleAtEnd(view, "刚刚发送的新消息");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(1200)]
    public void Incoming_event_and_receipt_update_the_visible_thread_without_reentry(int width)
    {
        var module = WithHistory();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = width, Height = 820, Content = view };
        try
        {
            window.Show();
            Settle(window);
            ConversationTestNavigation.Open(window, view);
            var item = Message("new", "电脑刚发来的消息", DateTimeOffset.UtcNow);
            // Real module inspect returns newest first; the UI must project it as a conversation.
            module.AssistantItems.Insert(0, item);
            module.EmitAssistantChanged();
            Settle(window);

            Assert.Equal(13, view.Assistant.Snapshot.Items.Count);
            AssertVisibleAtEnd(view, "电脑刚发来的消息");
            item["state"] = "available";
            module.EmitAssistantChanged();
            Settle(window);
            Assert.Equal("已接收", view.Assistant.Snapshot.Items.Last().StateText);
            item["state"] = "delivered";
            item["receipts"] = new JsonArray(new JsonObject
            {
                ["deviceId"] = "pc", ["deviceName"] = "工作电脑", ["at"] = DateTimeOffset.UtcNow.ToString("O")
            });
            module.EmitAssistantChanged();
            Settle(window);
            AssertVisibleAtEnd(view, "电脑刚发来的消息");
            Assert.Contains(view.Conversation.ThreadPanel.Children.Last().GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("已送达") == true);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(1200)]
    public void Shared_send_shows_queue_then_sent_then_real_receipts(int width)
    {
        var module = new FakeTransferModule();
        var item = new JsonObject
        {
            ["id"] = "forwarded-file", ["kind"] = "file", ["name"] = "转发文档.pdf",
            ["size"] = 1024, ["senderDeviceId"] = "mpt-phone", ["state"] = "queued",
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["receipts"] = new JsonArray()
        };
        module.AssistantItems.Add(item);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = width, Height = 820, Content = view };
        void Expect(string status)
        {
            module.EmitAssistantChanged();
            Settle(window);
            var captions = view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>();
            Assert.Contains(captions, text => text.Text == status);
            Assert.DoesNotContain(captions, text => text.Text == "已保存到本机");
        }
        try
        {
            window.Show();
            Settle(window);
            ConversationTestNavigation.Open(window, view);
            Expect("等待发送");
            item["state"] = "sending";
            item["bytesDone"] = 512;
            Expect("发送中 50%");
            item["state"] = "stored";
            item["transportRoute"] = "cloud-quark";
            Expect("夸克网盘 · 已发送，等待接收");
            item["receipts"] = new JsonArray(new JsonObject { ["deviceId"] = "mpt-phone" });
            Expect("夸克网盘 · 已发送，等待接收");
            item["receipts"] = new JsonArray(new JsonObject { ["deviceId"] = "pc-1" },
                new JsonObject { ["deviceId"] = "pc-1" });
            Expect("夸克网盘 · 已送达 1 台设备");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Receipt_wrapping_and_keyboard_resize_keep_the_latest_row_visible()
    {
        var module = WithHistory();
        var latest = Message("new", "最新消息", DateTimeOffset.UtcNow);
        module.AssistantItems.Insert(0, latest);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = 390, Height = 820, Content = view };
        try
        {
            window.Show();
            Settle(window);
            ConversationTestNavigation.Open(window, view);
            AssertVisibleAtEnd(view, "最新消息");
            // Receipt-only refresh keeps the item count unchanged but makes many rows taller.
            foreach (var item in module.AssistantItems)
            {
                item["state"] = "delivered";
                item["receipts"] = new JsonArray(new JsonObject
                {
                    ["deviceId"] = "pc", ["deviceName"] = "名字很长的工作电脑与另外一台已经接收成功的电脑"
                });
            }
            module.EmitAssistantChanged();
            Settle(window);
            AssertVisibleAtEnd(view, "最新消息");
            window.Height = 650;
            Settle(window);
            AssertVisibleAtEnd(view, "最新消息");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320, false)]
    [InlineData(1200, false)]
    [InlineData(320, true)]
    [InlineData(1200, true)]
    public void Actual_route_updates_the_small_status_row_and_remains_after_completion(int width, bool outgoing)
    {
        var module = new FakeTransferModule();
        var item = new JsonObject
        {
            ["id"] = "route-live", ["kind"] = "file", ["name"] = "设计文档.pdf", ["size"] = 100,
            ["senderDeviceId"] = outgoing ? "mpt-phone" : "ubuntu",
            ["senderName"] = outgoing ? "我的手机" : "Ubuntu",
            ["state"] = outgoing ? "sending" : "downloading", ["bytesDone"] = 45,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["receipts"] = new JsonArray()
        };
        module.AssistantItems.Add(item);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = width, Height = 820, Content = view };
        var prefix = outgoing ? "" : "Ubuntu · ";
        var progress = outgoing ? "发送中 45%" : "接收中 45%";
        void AssertStatus(string expected)
        {
            var caption = Assert.Single(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == expected);
            var position = caption.TranslatePoint(default, window);
            Assert.NotNull(position);
            Assert.InRange(position.Value.X, 0, width - caption.Bounds.Width + 1);
        }
        try
        {
            window.Show();
            Settle(window);
            ConversationTestNavigation.Open(window, view);
            AssertStatus(prefix + progress);

            // Route-only updates must invalidate the existing row, even when its percentage is unchanged.
            item["transportRoute"] = "cloud-quark";
            module.EmitAssistantChanged();
            Settle(window);
            AssertStatus(prefix + "夸克网盘 · " + progress);

            item["state"] = outgoing ? "delivered" : "available";
            item["bytesDone"] = 100;
            item["localPath"] = "/received/design.pdf";
            module.EmitAssistantChanged();
            Settle(window);
            AssertStatus(prefix + "夸克网盘 · " + (outgoing ? "已送达" : "已接收"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320, false)]
    [InlineData(1200, false)]
    [InlineData(320, true)]
    [InlineData(1200, true)]
    public void Incoming_failure_and_retry_keep_receive_wording_and_the_same_item(int width, bool privateConversation)
    {
        var module = new FakeTransferModule();
        module.AddPeer("ubuntu", "Ubuntu");
        var item = new JsonObject
        {
            ["id"] = "incoming-failed", ["kind"] = "file", ["name"] = "接收测试.bin", ["size"] = 100,
            ["senderDeviceId"] = "ubuntu", ["senderName"] = "Ubuntu", ["targetDeviceId"] = "mpt-phone",
            ["conversationKey"] = privateConversation ? "device:ubuntu" : "shared", ["state"] = "failed",
            ["error"] = "HTTP failure: https://private.example/secret", ["transportRoute"] = "cloud-quark"
        };
        module.AssistantItems.Add(item);
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = width, Height = 820, Content = view };
        Button MenuButton(string text) => view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, text));
        void OpenMenu() => ConversationTestNavigation.Click(window, view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "消息操作 接收测试.bin"));
        try
        {
            window.Show(); Settle(window);
            ConversationTestNavigation.Open(window, view, privateConversation ? "Ubuntu" : "文件传输助手");
            Assert.Contains(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Ubuntu · 夸克网盘 · 接收失败，可重试");
            OpenMenu();
            Assert.True(MenuButton("重试接收").IsEnabled);
            Assert.True(MenuButton("取消接收").IsEnabled);
            ConversationTestNavigation.Click(window, MenuButton("查看详情"));
            var detail = string.Join("\n", view.Conversation.SheetHost.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
            Assert.Contains("接收未完成", detail);
            Assert.DoesNotContain("发送", detail);
            Assert.DoesNotContain("private.example", detail);
            view.Conversation.TryHandleBack();
            OpenMenu();
            ConversationTestNavigation.Click(window, MenuButton("重试接收"));
            Assert.Equal("incoming-failed", module.LastArgs("assistant.retry")["itemId"]!.GetValue<string>());
            Assert.Single(module.AssistantItems);
            Assert.Contains(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Ubuntu · 夸克网盘 · 等待接收");
            item["state"] = "queued";
            module.EmitAssistantChanged();
            Settle(window);
            Assert.Contains(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Ubuntu · 夸克网盘 · 等待接收");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("cloud-quark", "夸克网盘", "登录已失效（/api/fs/get，code=403），请重新登录并检查空间。")]
    [InlineData("cloud-baidu", "百度网盘", "登录已失效（/api/fs/get，code=403），请重新登录并检查空间。")]
    [InlineData("cloud-quark", "夸克网盘", "网盘上传失败，请检查账号权限与剩余空间。")]
    [InlineData("cloud-baidu", "百度网盘", "网盘目录无法写入（HTTP 507），请检查登录与空间。")]
    [InlineData("cloud", "网盘中转", "网盘读取失败（HTTP 403）。")]
    [InlineData("cloud", "网盘中转", "网盘读取失败（HTTP 410）。")]
    public void Cloud_failure_details_keep_receive_direction_and_the_retry_item_id(string route, string routeName, string error)
    {
        var module = new FakeTransferModule();
        module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "cloud-failure", ["kind"] = "file", ["name"] = "云端附件.bin", ["size"] = 100,
            ["senderDeviceId"] = "sender", ["senderName"] = "Ubuntu", ["conversationKey"] = "shared",
            ["state"] = "failed", ["error"] = error, ["transportRoute"] = route
        });
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = 320, Height = 820, Content = view };
        Button Action(string label) => view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, label));
        void OpenMenu() => ConversationTestNavigation.Click(window, view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "消息操作 云端附件.bin"));
        try
        {
            window.Show(); Settle(window);
            ConversationTestNavigation.Open(window, view);
            Assert.Contains(view.Conversation.ThreadPanel.GetLogicalDescendants().OfType<TextBlock>(),
                text => text.Text == "Ubuntu · " + routeName + " · 接收失败，可重试");
            OpenMenu();
            Assert.True(Action("重试接收").IsEnabled);
            ConversationTestNavigation.Click(window, Action("查看详情"));
            var details = string.Join("\n", view.Conversation.SheetHost.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
            Assert.Contains("接收未完成", details);
            Assert.DoesNotContain("发送", details);
            view.Conversation.TryHandleBack();
            OpenMenu();
            ConversationTestNavigation.Click(window, Action("重试接收"));
            Assert.Equal("cloud-failure", module.LastArgs("assistant.retry")["itemId"]!.GetValue<string>());
            Assert.Single(module.AssistantItems);
        }
        finally { window.Close(); }
    }

    private static FakeTransferModule WithHistory()
    {
        var module = new FakeTransferModule();
        for (var i = 11; i >= 0; i--)
            module.AssistantItems.Add(Message("old-" + i, "旧消息 " + i, DateTimeOffset.UtcNow.AddHours(-24).AddMinutes(i)));
        return module;
    }

    private static JsonObject Message(string id, string text, DateTimeOffset at) => new()
    {
        ["id"] = id, ["kind"] = "text", ["text"] = text, ["state"] = "queued",
        ["createdAt"] = at.ToString("O"), ["senderDeviceId"] = "mpt-phone", ["receipts"] = new JsonArray()
    };

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 6; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void AssertVisibleAtEnd(TransferView view, string expected)
    {
        var thread = view.Conversation.ThreadPanel;
        var text = Assert.Single(thread.Children.Last().GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == expected);
        var scroll = thread.GetVisualAncestors().OfType<ScrollViewer>().First();
        var position = text.TranslatePoint(new Point(), scroll);
        Assert.NotNull(position);
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "History must overflow to exercise scrolling.");
        Assert.InRange(position.Value.Y, 0, scroll.Viewport.Height - text.Bounds.Height);
    }
}
