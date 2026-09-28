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
/// The tool's real first screen is the conversation, so these checks assert that: a composer with a
/// usable input and a send action at 320/390/768 and on a desktop window, a device picker that is one
/// tap rather than a form, and no settings table in the way. The classic form still has to exist
/// behind the advanced entry.
/// </summary>
public sealed class MobileLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-ft-ui-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTransferModule _module = new();
    private readonly List<Window> _windows = [];

    public MobileLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var window in _windows) window.Close();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    public static TheoryData<int> Widths => new() { 320, 390, 768, 1280 };

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void The_conversation_keeps_a_usable_composer_and_send_action(int width)
    {
        var view = OpenConversation(width, out _);
        var text = TextOf(view);

        // The empty state names the promise and the composer is right there.
        Assert.Contains("文件助手", text);
        Assert.Contains("文件传输助手", text);

        var input = Descendants(view).OfType<TextBox>().First(box => box.IsVisible && box.IsEnabled);
        var send = Descendants(view).OfType<Button>().First(button => (button.Content as string) == "发送");
        Assert.True(input.Bounds.Width > 80, $"the composer input must stay usable at {width}, was {input.Bounds.Width:0}");
        Assert.True(send.Bounds.Height >= 44, $"the send action must be tappable at {width}, was {send.Bounds.Height:0}");
        Assert.False(send.IsEnabled, "an empty composer must not offer a send");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void Every_visible_control_stays_inside_the_window(int width)
    {
        var view = OpenConversation(width, out var window);
        var escaped = new List<string>();
        foreach (var control in Descendants(view).OfType<Control>().Where(control => control is Button or TextBox or CheckBox))
        {
            if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || !control.IsVisible) continue;
            if (control.GetVisualAncestors().OfType<Control>().Any(ancestor => !ancestor.IsVisible)) continue;
            if (control.GetVisualAncestors().OfType<ScrollViewer>().Any(scroller => scroller.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible)) continue;
            var origin = control.TranslatePoint(default, window);
            if (origin is { } point && (point.X < -1 || point.X + control.Bounds.Width > width + 1))
                escaped.Add($"{control.GetType().Name} {control.Name} at {point.X:0.0} width {control.Bounds.Width:0.0}");
        }
        Assert.True(escaped.Count == 0, $"{width}: controls escaped the window: {string.Join("; ", escaped)}");
    }

    [AvaloniaFact]
    public void A_draft_survives_a_sync_arriving_mid_sentence()
    {
        var view = OpenConversation(390, out var window);
        var input = Descendants(view).OfType<TextBox>().First(box => box.IsVisible && box.IsEnabled);
        input.Text = "正在写的草稿";
        input.CaretIndex = 4;

        // Any module refresh re-renders the thread; the composer must keep the text and the caret.
        view.Assistant.PublishOnUi(view.Assistant.Snapshot with { Status = "同步完成" });
        window.UpdateLayout();

        Assert.Equal("正在写的草稿", input.Text);
        Assert.Equal(4, input.CaretIndex);
        Assert.Same(input, Descendants(view).OfType<TextBox>().First(box => box.IsVisible && box.IsEnabled));
    }

    [AvaloniaFact]
    public void The_advanced_form_still_exists_behind_the_conversation()
    {
        var view = OpenConversation(1024, out var window);
        Assert.True(view.IsConversationVisible);
        var names = Descendants(view).OfType<Control>().Select(control => control.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("PairCodeBox", names);

        view.ShowAdvanced(true);
        window.UpdateLayout();

        Assert.False(view.IsConversationVisible);
        names = Descendants(view).OfType<Control>().Select(control => control.Name).ToHashSet(StringComparer.Ordinal);
        // The desktop contract is unchanged: the device picker, pairing box and cloud box all remain.
        Assert.Contains("DevicePicker", names);
        Assert.Contains("PairCodeBox", names);
        Assert.Contains("CloudCodeBox", names);
        Assert.Contains("一键安装并启用 OpenList", TextOf(view));
    }

    [AvaloniaFact]
    public void Back_closes_a_sheet_then_leaves_the_advanced_page_then_declines()
    {
        var view = OpenConversation(390, out var window);
        var setup = Descendants(view).OfType<Button>().First(button => button.Classes.Contains("MptMobileIconButton"));
        Click(window, setup);
        window.UpdateLayout();
        Assert.True(view.IsSheetOpen, "tapping the setup icon should open a sheet");

        Assert.True(view.TryHandleBack(), "back should close the open sheet");
        Assert.False(view.IsSheetOpen);

        view.ShowAdvanced(true);
        window.UpdateLayout();
        Assert.True(view.TryHandleBack(), "back should leave the advanced page for the conversation");
        Assert.True(view.IsConversationVisible);
        Assert.False(view.TryHandleBack(), "the conversation is the root, so the host may leave the tool");
    }

    [AvaloniaFact]
    public void Escape_closes_a_sheet_without_leaving_the_page()
    {
        var view = OpenConversation(390, out var window);
        var setup = Descendants(view).OfType<Button>().First(button => button.Classes.Contains("MptMobileIconButton"));
        Click(window, setup);
        window.UpdateLayout();
        Assert.True(view.IsSheetOpen);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.UpdateLayout();

        Assert.False(view.IsSheetOpen);
    }

    [AvaloniaFact]
    public void The_classic_phone_step_flow_is_still_reachable()
    {
        var view = OpenConversation(390, out var window);
        view.ShowAdvanced(true);
        window.UpdateLayout();

        // On a phone the advanced page is the step flow, which reads the same TransferCore.
        Assert.True(view.IsMobileLayout);
        var classes = Descendants(view.Mobile).SelectMany(control => control.Classes).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(MobileUi.Classes.Root, classes);
        Assert.Contains(MobileUi.Classes.PageTitle, classes);
    }

    [AvaloniaFact]
    public async Task Sharing_a_file_fills_the_composer_instead_of_starting_a_wizard()
    {
        var shared = Path.Combine(_root, "分享的照片.png");
        await File.WriteAllTextAsync(shared, "png");
        var view = OpenConversation(390, out var window);

        var handled = await view.ActivateAsync(new global::MyPowerTools.Abstractions.ToolActivationRequest("file-transfer", "workspace", new Uri(shared).AbsoluteUri));
        window.UpdateLayout();

        Assert.True(handled);
        var text = TextOf(view);
        Assert.True(text.Contains("分享的照片.png"), "share text missing; full text was:\n" + text);
    }

    private TransferView OpenConversation(int width, out Window window)
    {
        var view = new TransferView(_module.Context(_root));
        window = new Window { Width = width, Height = 820, Content = view };
        window.Show();
        _windows.Add(window);
        for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        TestPump.Drain();
        window.UpdateLayout();
        return view;
    }

    private static void Click(Window window, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.NotNull(center);
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.GetLogicalDescendants().OfType<Control>();

    private static string TextOf(Control control) => string.Join(
        "\n",
        Descendants(control).OfType<TextBlock>().Where(block => block.IsVisible).Select(block => block.Text ?? ""));
}
