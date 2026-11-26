using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// No-op work in the conversation surface: an idle page, a repeated sync and a follow that is already
/// at the end must not replace controls, dispatch again, or write the module's preference store.
///
/// These tests count observable work (module commands, scroll notifications, control identities)
/// instead of measuring time, so they stay deterministic on a machine with no render loop.
/// </summary>
public sealed class NoOpWorkTests
{
    private const int PhoneWidth = 390;

    [AvaloniaFact]
    public void Idle_after_returning_to_the_list_repeats_no_work()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            Assert.True(view.Conversation.TryHandleBack());
            host.Settle();

            var scroll = view.Conversation.ThreadScroll;
            var scrollEvents = 0;
            scroll.ScrollChanged += (_, _) => scrollEvents++;
            var callsBefore = module.Calls.Count;
            var offsetBefore = scroll.Offset.Y;

            // No input, no events: the page must settle and stay settled.
            for (var round = 0; round < 30; round++) host.Settle();

            Assert.Equal(callsBefore, module.Calls.Count);
            Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
            Assert.Equal(0, scrollEvents);
            Assert.Equal(offsetBefore, scroll.Offset.Y);
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void A_repeated_sync_with_no_change_keeps_the_directory_rows()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            var row = ConversationRow(view, "LIS-IMAC");
            var callsBefore = module.Calls.Count;
            for (var round = 0; round < 5; round++) { view.Conversation.Sync(); host.Settle(); }

            // Displaying the same conversations again must not replace their controls.
            Assert.Same(row, ConversationRow(view, "LIS-IMAC"));
            Assert.Equal(callsBefore, module.Calls.Count);

            // A real change still rebuilds: the guard may not hide an update.
            module.AssistantItems.Add(Text("device:laptop", "新消息预览"));
            module.EmitAssistantChanged();
            host.Settle();
            Assert.Contains("新消息预览", Text(view));
            Assert.True(module.CountCalls("assistant.inspect") > 0);
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void A_follow_that_is_already_at_the_end_writes_no_preferences()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(AtEnd(scroll), $"the conversation opens following the newest entry: {Describe(scroll)}");

            // Growth while following queues the follow job; the view is already at the end when it runs,
            // so it must not touch the offset, the draft or the module's store.
            var savesBefore = module.CountCalls("assistant.preferences.update");
            view.Conversation.ThreadPanel.Children.Add(new Border { Height = 120 });
            host.Settle();

            Assert.True(AtEnd(scroll), $"a follow that ran must leave the view at the end: {Describe(scroll)}");
            Assert.Equal(savesBefore, module.CountCalls("assistant.preferences.update"));
        }
        finally { host.Dispose(); }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static bool AtEnd(ScrollViewer scroll) =>
        scroll.Extent.Height <= scroll.Viewport.Height + 1 ||
        scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1;

    private static string Describe(ScrollViewer scroll) =>
        $"offset={scroll.Offset.Y:0.0} extent={scroll.Extent.Height:0.0} viewport={scroll.Viewport.Height:0.0}";

    private static FakeTransferModule Data()
    {
        var module = new FakeTransferModule { AssistantLinked = true };
        module.AddPeer("laptop", "LIS-IMAC");
        module.AddPeer("phone2", "我的手机");
        module.AssistantItems.Add(Text("shared", "公屏独有内容"));
        for (var index = 0; index < 24; index++) module.AssistantItems.Add(Text("device:laptop", "私聊消息 " + index));
        return module;
    }

    private static JsonObject Text(string conversationKey, string text) => new()
    {
        ["id"] = "row" + Guid.NewGuid().ToString("N")[..8],
        ["conversationKey"] = conversationKey,
        ["kind"] = "text",
        ["text"] = text,
        ["senderDeviceId"] = "laptop",
        ["senderName"] = "LIS-IMAC",
        ["state"] = "available",
        ["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        ["receipts"] = new JsonArray()
    };

    private static Button ConversationRow(Control view, string name) =>
        view.GetLogicalDescendants().OfType<Button>().First(button =>
            AutomationProperties.GetName(button) == "打开会话 " + name);

    private static string Text(Control control) =>
        string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? ""));

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        ColdOpenCostTests.Settle(window);
    }

    private sealed class Host : IDisposable
    {
        public Window Window { get; }

        public Host(Control view, int width)
        {
            Window = new Window { Width = width, Height = 844, Content = view };
            Window.Show();
            Settle();
        }

        public void Settle() => ColdOpenCostTests.Settle(Window);

        public void Dispose() => Window.Close();
    }
}
