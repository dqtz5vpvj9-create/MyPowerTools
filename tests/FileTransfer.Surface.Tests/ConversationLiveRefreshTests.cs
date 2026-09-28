using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
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
                text => text.Text?.Contains("已保存到 工作电脑") == true);
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
