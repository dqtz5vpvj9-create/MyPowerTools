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
/// Follow-the-newest intent in the conversation thread, focused on the deferred follow job added by
/// the cold-open repair (AssistantView.QueueFollowThreadEnd).
///
/// The job runs one dispatcher turn after the scroll notification, so it must decide from the reader's
/// intent, not from the geometry it finds: content that grew (an appended entry, receipt text wrapping,
/// an image finishing decode) changes the extent and the offset stays where the reader left it. Only a
/// moved offset means the view itself moved while the job was pending, and only then may the reader's
/// position win. Both cases are asserted without timers: the tests drive real layout and inspect the
/// resulting offset.
/// </summary>
public sealed class FollowIntentTests
{
    private const int PhoneWidth = 390;

    /// <summary>
    /// FAILS on the reviewed candidate: the callback vetoes the follow whenever the view is no longer
    /// at the end, and growing content alone moves the end away from the offset the reader is on.
    /// </summary>
    [AvaloniaFact]
    public void Following_the_end_survives_content_that_grows_without_a_user_scroll()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"the thread must be scrollable: extent={scroll.Extent.Height:0.0} viewport={scroll.Viewport.Height:0.0}");
            Assert.True(AtEnd(scroll), $"the conversation opens following the newest entry: {Describe(scroll)}");

            // A new entry arrives while the reader is at the newest one. No user scroll happens: only
            // the extent grows, and the newest entry must stay on screen exactly as it did before the
            // follow job was deferred.
            module.AssistantItems.Add(Text("device:laptop", "最新一条"));
            module.EmitAssistantChanged();
            host.Settle();

            Assert.True(AtEnd(scroll), $"a growing thread must keep following with no user scroll: {Describe(scroll)}");
        }
        finally { host.Dispose(); }
    }

    /// <summary>The other shape of the same defect: the viewport shrinks (Android keyboard) and the reader never scrolled.</summary>
    [AvaloniaFact]
    public void Following_the_end_survives_a_viewport_shrink_without_a_user_scroll()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(AtEnd(scroll), $"the conversation opens following the newest entry: {Describe(scroll)}");

            // The keyboard opens: the thread viewport shrinks and the offset does not move.
            host.Window.Height = 420;
            host.Settle();

            Assert.True(AtEnd(scroll), $"a shrinking viewport must keep following with no user scroll: {Describe(scroll)}");
        }
        finally { host.Dispose(); }
    }

    /// <summary>
    /// The reader reached the newest entry with a real scroll, which leaves a concrete offset, and then
    /// content grows with no further scroll. The follow job must still reach the end.
    ///
    /// The distinction matters because Avalonia re-coerces a <c>ScrollToEnd()</c> offset as the extent
    /// changes -- that sentinel keeps tracking the end on its own and would hide this case. A concrete
    /// offset (what a drag, a fling, or a restored position leaves) does not track anything, so the job
    /// has to decide from intent instead of from the geometry it happens to find.
    /// </summary>
    [AvaloniaFact]
    public void Following_the_end_survives_content_that_grows_after_a_real_scroll_to_the_end()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"the thread must be scrollable: extent={scroll.Extent.Height:0.0} viewport={scroll.Viewport.Height:0.0}");

            // A real scroll to the newest entry leaves a concrete offset, exactly like a drag or fling.
            scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
            host.Settle();
            Assert.True(AtEnd(scroll), $"a scroll to the end must keep following: {Describe(scroll)}");

            // Content grows under that following view: only the extent changes.
            view.Conversation.ThreadPanel.Children.Add(new Border { Height = 320 });
            host.Window.UpdateLayout();
            Assert.False(AtEnd(scroll), $"the growth must move the end away from the reader for this case to mean anything: {Describe(scroll)}");

            // The queued follow job has not run yet; once it does, the newest entry must be back in view.
            ColdOpenCostTests.Settle(host.Window);
            Assert.True(AtEnd(scroll), $"a follow queued by growth alone must still reach the end: {Describe(scroll)}");
        }
        finally { host.Dispose(); }
    }

    /// <summary>
    /// Must pass before and after the correction: when the view really moved while the follow job was
    /// pending, the reader's own position wins instead of being pulled back to the end.
    /// </summary>
    [AvaloniaFact]
    public void A_user_scroll_away_while_the_follow_is_pending_wins()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(AtEnd(scroll), $"the conversation opens following the newest entry: {Describe(scroll)}");

            // Growing content queues the follow during the layout pass; it does not run yet.
            view.Conversation.ThreadPanel.Children.Add(new Border { Height = 320 });
            host.Window.UpdateLayout();

            // The reader scrolls away before that queued job runs: the view moved, so it must win.
            scroll.Offset = new Vector(0, 0);
            ColdOpenCostTests.Settle(host.Window);

            Assert.Equal(0d, scroll.Offset.Y);
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
