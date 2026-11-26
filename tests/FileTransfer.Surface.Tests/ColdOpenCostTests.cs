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
/// Cold-open cost and scroll-notification behaviour of the shared file-transfer surface.
///
/// The Android cold open runs on the UI thread inside one host job, and the tool page cannot paint
/// until that job returns: the surface must therefore build only the presentation the user actually
/// opens, and a ScrollChanged notification -- which is raised from inside the ScrollViewer's arrange
/// pass -- must never write layout inputs back into the pass that raised it. Neither test uses a
/// timer or a sleep: they count what the open built and compare control identities, so a regression
/// shows up as an extra control tree or a rebuilt list, not as a flaky duration.
/// </summary>
public sealed class ColdOpenCostTests
{
    private const int PhoneWidth = 390;
    private const int WideWidth = 1000;

    [AvaloniaTheory]
    [InlineData(PhoneWidth)]
    [InlineData(WideWidth)]
    public void Opening_the_assistant_builds_only_the_conversation(int width)
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, width);
        try
        {
            Assert.True(view.IsConversationVisible);
            Assert.Same(view.Conversation, view.Content);
            // The conversation is the screen the hero action opens; the classic form and the phone
            // step flow are behind the advanced entry and must not be part of the first open.
            Assert.False(view.IsMobilePresentationBuilt);
            Assert.False(view.IsDesktopPresentationBuilt);
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void The_advanced_page_still_builds_what_it_shows_on_demand()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            view.ShowAdvanced(true);
            host.Settle();
            Assert.True(view.IsMobilePresentationBuilt);
            Assert.True(view.IsMobileLayout);
            Assert.Same(view.Mobile, view.Content);
            // The phone step flow really rendered: the first step's own action is on screen.
            Assert.Contains(view.GetLogicalDescendants().OfType<Button>(),
                button => Equals(button.Content, "选择文件") || Text(button).Contains("选择文件"));

            view.ShowAdvanced(false);
            host.Settle();
            Assert.False(view.IsMobileLayout);
            Assert.Same(view.Conversation, view.Content);
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void A_wide_host_builds_the_wide_form_on_demand_and_not_the_phone_flow()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, WideWidth);
        try
        {
            Assert.False(view.IsDesktopPresentationBuilt);
            view.ShowAdvanced(true);
            host.Settle();
            Assert.True(view.IsDesktopPresentationBuilt);
            Assert.False(view.IsMobilePresentationBuilt);
            Assert.False(view.IsMobileLayout);
            Assert.NotNull(Named<ComboBox>(view, "DevicePicker"));
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void Scrolling_the_thread_does_not_rebuild_the_conversation_list()
    {
        var module = Data();
        module.DraftPreferences["conversationKey"] = "device:laptop";
        module.DraftPreferences["drafts"] = new JsonObject
        {
            ["device:laptop"] = new JsonObject
            {
                ["draftText"] = "",
                ["attachmentPaths"] = new JsonArray(),
                ["targetDeviceId"] = "laptop",
                ["conversationKey"] = "device:laptop",
                ["scrollOffset"] = 0
            }
        };
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            var row = ConversationRow(view, "LIS-IMAC");
            Click(host.Window, row);
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height,
                $"the thread must be scrollable for this case: extent={scroll.Extent.Height:0.0} viewport={scroll.Viewport.Height:0.0}");
            var listRow = ConversationRow(view, "文件传输助手");

            // A real scroll notification (offset change) must capture the draft without rebuilding the
            // directory rows: rebuilding them inside the notification invalidates the arrange pass it
            // came from, and the rows are the thread's siblings in the same page.
            scroll.Offset = new Vector(0, 12);
            host.Settle();
            Assert.Same(listRow, ConversationRow(view, "文件传输助手"));
            Assert.Equal(12d, scroll.Offset.Y);
        }
        finally { host.Dispose(); }
    }

    [AvaloniaFact]
    public void A_new_message_while_scrolled_into_history_shows_the_cue_and_follows_on_tap()
    {
        var module = Data();
        module.DraftPreferences["conversationKey"] = "device:laptop";
        module.DraftPreferences["drafts"] = new JsonObject
        {
            ["device:laptop"] = new JsonObject
            {
                ["draftText"] = "",
                ["attachmentPaths"] = new JsonArray(),
                ["targetDeviceId"] = "laptop",
                ["conversationKey"] = "device:laptop",
                ["scrollOffset"] = 0
            }
        };
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, PhoneWidth);
        try
        {
            Click(host.Window, ConversationRow(view, "LIS-IMAC"));
            var scroll = view.Conversation.ThreadScroll;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the thread must be scrollable for this case");

            // The reader scrolls into history. That position is theirs until they follow again.
            scroll.Offset = new Vector(0, 24);
            host.Settle();
            Assert.Equal(24d, scroll.Offset.Y);

            // Arriving content grows the thread while the reader owns the position: the page must offer
            // the cue instead of pulling the view to the bottom under their finger.
            module.AssistantItems.Add(Text("device:laptop", "后来的消息"));
            module.EmitAssistantChanged();
            host.Settle();
            var cue = Cue(view);
            Assert.NotNull(cue);
            Assert.True(cue!.IsVisible);
            Assert.Equal(24d, scroll.Offset.Y);

            // Tapping the cue is the user's own decision to follow the end.
            Click(host.Window, cue);
            Assert.False(Cue(view)?.IsVisible ?? false);
            Assert.True(scroll.Offset.Y > 24);
        }
        finally { host.Dispose(); }
    }

    // ---- helpers -----------------------------------------------------------------------------

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

    private static Button? Cue(Control view) =>
        view.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button =>
            (button.Content as string)?.Contains("新消息", StringComparison.Ordinal) == true);

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"control '{name}' of type {typeof(T).Name} was not found");

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

    /// <summary>Runs the queued dispatcher work and layout passes without any wall-clock wait.</summary>
    internal static void Settle(Window window)
    {
        for (var pass = 0; pass < 8; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private sealed class Host : IDisposable
    {
        public Window Window { get; }

        public Host(Control view, int width)
        {
            Window = new Window { Width = width, Height = 844, Content = view };
            Window.Show();
            ColdOpenCostTests.Settle(Window);
        }

        public void Settle() => ColdOpenCostTests.Settle(Window);

        public void Dispose() => Window.Close();
    }
}
