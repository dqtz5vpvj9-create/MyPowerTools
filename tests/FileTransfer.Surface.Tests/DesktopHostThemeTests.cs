using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FileTransfer.Surface.Tests;

/// <summary>
/// How the tool renders on the real desktop host: Fluent plus the desktop product theme and no mobile
/// theme anywhere in the application. A user's Windows screenshot showed every <c>MptMobile*</c> class
/// unstyled there — blank icons, default buttons, a full-width sheet — because the shipped app only
/// adds the mobile theme on Android. These tests fail if the tool ever stops carrying its own scoped
/// copy, and they pin the desktop layout: a bounded conversation column and a centred dialog instead of
/// a phone sheet.
/// </summary>
public sealed class DesktopHostThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-desktop-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly List<Window> _windows = [];

    public DesktopHostThemeTests()
    {
        Directory.CreateDirectory(_root);
        _module.AssistantIdentityName = "测试电脑";
    }

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public void The_host_really_has_no_mobile_theme_loaded()
    {
        // Guards the suite: if the mobile theme were loaded globally these tests would pass for the
        // wrong reason and could not catch the desktop regression.
        Assert.False(Application.Current!.TryGetResource("MptMobileBackgroundBrush", ThemeVariant.Light, out _),
            "the host must not preload the mobile theme");
    }

    [AvaloniaFact]
    public void The_page_styles_itself_from_its_own_scoped_theme()
    {
        var view = Open(1024, 800, out var window);

        Assert.True(view.Conversation.HasScopedMobileTheme, "the page must load the mobile theme into its own styles");
        // Scoped, not injected: the application stays untouched.
        Assert.False(Application.Current!.TryGetResource("MptMobileBackgroundBrush", ThemeVariant.Light, out _),
            "the tool must not inject the theme into the application");
        Assert.True(view.Conversation.TryFindResource("MptMobileBackgroundBrush", ThemeVariant.Light, out var inside));
        Assert.NotNull(inside);
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void Every_icon_renders_a_real_glyph_and_keeps_its_touch_target()
    {
        var view = Open(1024, 800, out var window);

        var icons = view.GetVisualDescendants().OfType<MobileIcon>().Where(icon => icon.IsEffectivelyVisible).ToArray();
        Assert.True(icons.Length >= 2, $"the page must show its toolbar icons, found {icons.Length}");
        foreach (var icon in icons)
        {
            Assert.True(icon.Data is not null, $"Missing icon geometry: {icon.IconKey}");
            Assert.True(icon.Stroke is not null && icon.StrokeThickness > 0,
                "a stroke icon without a stroke paints nothing");
            Assert.True(icon.Bounds.Width >= 16 && icon.Bounds.Height >= 16,
                $"{icon.IconKey} icon collapsed to {icon.Bounds.Width:0}x{icon.Bounds.Height:0}");
        }

        foreach (var button in Descendants(view).OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("MptMobileIconButton")))
            Assert.True(button.Bounds.Width >= 44 && button.Bounds.Height >= 44,
                $"an icon button lost its target: {button.Bounds.Width:0}x{button.Bounds.Height:0}");
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_primary_action_keeps_its_mobile_button_styling()
    {
        var view = Open(1024, 800, out var window);
        var send = Descendants(view).OfType<Button>().First(button => (button.Content as string) == "发送");

        // Without the scoped theme this was a default grey Fluent button; the accent fill and the 48 dp
        // height come from the mobile theme's own class.
        Assert.True(send.Classes.Contains("MptMobilePrimary"));
        Assert.True(send.Bounds.Height >= 44, $"the primary action must stay 44 dp, was {send.Bounds.Height:0}");
        Assert.True(send.Background is ISolidColorBrush, "the primary action must keep the theme's accent fill");
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void Desktop_directory_and_chat_keep_messages_readable_on_a_wide_window()
    {
        _module.AssistantItems.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = "wide-message", ["kind"] = "text", ["text"] = "这是一段需要在宽窗口中保持可读行宽的消息。",
            ["state"] = "available", ["senderDeviceId"] = "mpt-phone", ["receipts"] = new System.Text.Json.Nodes.JsonArray()
        });
        var view = Open(1600, 900, out var window);
        var message = Assert.Single(view.Conversation.ThreadPanel.Children);
        Assert.InRange(message.Bounds.Width, 160, 560);

        var column = view.Conversation.ConversationColumnBounds;
        Assert.True(column.Width <= window.Width - 290, $"the conversation must leave room for the directory, was {column.Width:0}");
        Assert.True(column.Width >= 320, $"the conversation must stay readable, was {column.Width:0}");
        var origin = view.Conversation.ConversationOriginIn(window);
        Assert.NotNull(origin);
        Assert.True(origin.Value.X > 100, $"the chat must follow the directory, started at {origin.Value.X:0}");
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public async Task A_desktop_sheet_is_a_bounded_centred_dialog_not_a_full_width_panel()
    {
        var view = Open(1600, 900, out var window);
        await TestPump.RunAsync(() => view.Conversation.OpenLinkSheetForTestAsync());
        window.UpdateLayout();

        var sheet = view.Conversation.SheetHost;
        Assert.True(sheet.Bounds.Width <= 521, $"a desktop dialog must be bounded, was {sheet.Bounds.Width:0}");
        Assert.Equal(VerticalAlignment.Center, sheet.VerticalAlignment);
        var origin = sheet.TranslatePoint(default, window);
        Assert.NotNull(origin);
        Assert.True(origin.Value.X > 300, $"the dialog must be centred, started at {origin.Value.X:0}");
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_sheet_stays_a_bottom_sheet_on_a_phone()
    {
        var view = Open(390, 800, out var window);
        var sheet = view.Conversation.SheetHost;

        Assert.Equal(VerticalAlignment.Bottom, sheet.VerticalAlignment);
        Assert.True(sheet.MaxWidth > 390, "a phone sheet uses the full width");
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void The_copy_is_device_neutral_on_a_desktop()
    {
        var view = Open(1600, 900, out var window);
        var text = TextOf(view);

        // The tool runs on Windows and macOS too, so its own copy must not address a phone.
        Assert.DoesNotContain("这台手机", text);
        Assert.DoesNotContain("我的手机", text);
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public async Task The_desktop_dialog_still_shows_the_full_symbol()
    {
        var view = Open(1600, 900, out var window);
        await TestPump.RunAsync(() => view.Conversation.OpenLinkSheetForTestAsync());
        window.UpdateLayout();

        var symbol = view.Conversation.QrSymbol;
        Assert.NotNull(symbol);
        Assert.True(symbol!.Bounds.Width >= 240,
            $"a desktop dialog has room for the full symbol, was {symbol.Bounds.Width:0}");
        Assert.Equal(0, view.Conversation.SheetScroll.Offset.Y);
        window.UpdateLayout();
    }

    private TransferView Open(int width, int height, out Window window)
    {
        var view = new TransferView(_module.Context(_root));
        window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        _windows.Add(window);
        for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        TestPump.Drain();
        window.UpdateLayout();
        ConversationTestNavigation.Open(window, view);
        return view;
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.GetLogicalDescendants().OfType<Control>();

    private static string TextOf(Control control) => string.Join(
        "\n",
        Descendants(control).OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text ?? ""));
}
