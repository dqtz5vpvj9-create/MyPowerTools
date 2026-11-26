using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MobileLayout.Tests;

/// <summary>
/// Desktop host contract for managed tool surfaces. The desktop chrome hands pages to a page
/// ScrollViewer, and a surface that declares <c>MptViewportSurface</c> is a full-height layout with its
/// own scrolling and a composer pinned to the bottom. This renders the real
/// <see cref="ShellChromeView"/> and <see cref="ExternalSdkToolView"/> chain in a finite window and
/// measures the resulting bounds: a page scroller that measures such a surface with unbounded height
/// pushes the composer and any centred dialog below the fold, while an unmarked surface (Paste Image
/// style: no ScrollViewer of its own) must keep the ordinary page scrolling instead of being clipped.
/// </summary>
public sealed class DesktopSurfaceViewportTests
{
    private const double WindowWidth = 1180;
    private const double WindowHeight = 780;

    /// <summary>Ordinary desktop pages must keep scrolling through the chrome's page scroller.</summary>
    [AvaloniaFact]
    public void Ordinary_desktop_pages_keep_scrolling_through_the_chrome_scroller()
    {
        var (window, chrome) = ShowChrome();
        try
        {
            var contentHost = ContentHost(chrome);
            contentHost.Content = new Border { Height = 3000, Background = Brushes.Transparent };
            Pump(window);

            var scroller = ScrollerOf(contentHost);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height + 100,
                $"a tall page is no longer scrollable: extent {scroller.Extent.Height:0} viewport {scroller.Viewport.Height:0}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The managed surface is the shell user's screen: it must be bounded by the window, keep its
    /// bottom composer inside the viewport, scroll its own list, and centre its dialog in the visible
    /// area rather than in an unbounded scroll extent.
    /// </summary>
    [AvaloniaFact]
    public void Hosted_managed_surface_fills_the_viewport_and_keeps_its_composer_visible()
    {
        var (window, chrome) = ShowChrome();
        try
        {
            var contentHost = ContentHost(chrome);
            var toolView = new ExternalSdkToolView { DataContext = CreateToolViewModel() };
            contentHost.Content = toolView;
            var surface = new FullHeightSurface();
            toolView.SetManagedSurface(surface);
            Pump(window);

            var scroller = ScrollerOf(contentHost);
            var viewport = scroller.Viewport;
            var viewportOrigin = Origin(scroller, window);

            // 1. The surface height is the viewport, not its full content height.
            Assert.True(
                surface.Bounds.Height <= viewport.Height + 1,
                $"surface measured {surface.Bounds.Height:0}px tall inside a {viewport.Height:0}px viewport "
                + $"(scroll extent {scroller.Extent.Height:0}px)");

            // 2. The composer stays inside the visible viewport instead of below the fold.
            var composerOrigin = Origin(surface.Composer, window);
            var composerBottom = composerOrigin.Y + surface.Composer.Bounds.Height;
            Assert.True(
                composerBottom <= viewportOrigin.Y + viewport.Height + 1,
                $"composer ends at y={composerBottom:0} but the viewport ends at y={viewportOrigin.Y + viewport.Height:0}");

            // 3. The message list scrolls inside the surface (the surface owns its own scrolling).
            Assert.True(
                surface.ListScroller.Extent.Height > surface.ListScroller.Viewport.Height + 100,
                $"the surface list is not scrollable: extent {surface.ListScroller.Extent.Height:0} viewport {surface.ListScroller.Viewport.Height:0}");

            // 4. A centred dialog is centred in the visible area.
            surface.ShowDialog();
            Pump(window);
            var dialogCenter = Origin(surface.Dialog, window).Y + (surface.Dialog.Bounds.Height / 2);
            var viewportCenter = viewportOrigin.Y + (viewport.Height / 2);
            Assert.True(
                Math.Abs(dialogCenter - viewportCenter) <= 2,
                $"dialog centre y={dialogCenter:0} is not the viewport centre y={viewportCenter:0}");

            SaveEvidenceFrame(window, $"desktop-chrome-managed-surface-{WindowWidth:0}x{WindowHeight:0}.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// An unmarked surface (Paste Image has a Border/StackPanel root and no ScrollViewer) must keep the
    /// page scroller: bounding it would clip content the user can no longer reach.
    /// </summary>
    [AvaloniaFact]
    public void Hosted_unmarked_surface_keeps_page_scrolling_and_stays_reachable()
    {
        var (window, chrome) = ShowChrome();
        try
        {
            var contentHost = ContentHost(chrome);
            var toolView = new ExternalSdkToolView { DataContext = CreateToolViewModel() };
            contentHost.Content = toolView;
            var surface = new FullHeightSurface(declaresViewport: false);
            toolView.SetManagedSurface(surface);
            Pump(window);

            var scroller = ScrollerOf(contentHost);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);

            // The surface keeps its natural height and the page scroller can still reach all of it.
            Assert.True(
                surface.Bounds.Height > scroller.Viewport.Height + 100,
                $"an unmarked surface was bounded to {surface.Bounds.Height:0}px in a {scroller.Viewport.Height:0}px viewport");
            Assert.True(
                scroller.Extent.Height >= surface.Bounds.Height - 1,
                $"the page scroller cannot reach the unmarked surface bottom: extent {scroller.Extent.Height:0} surface {surface.Bounds.Height:0}");

            // Scrolling the page to the end must bring the composer into the visible viewport.
            var maximumOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
            scroller.Offset = new Vector(0, maximumOffset);
            Pump(window);
            scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height));
            Pump(window);

            var scrollerOrigin = Origin(scroller, window);
            var composerBottom = Origin(surface.Composer, window).Y + surface.Composer.Bounds.Height;
            var viewportBottom = scrollerOrigin.Y + scroller.Viewport.Height;
            Assert.True(
                composerBottom <= viewportBottom + 1,
                $"scrolling to the end did not reach the composer: it ends at y={composerBottom:0}, viewport bottom y={viewportBottom:0} "
                + $"(offset {scroller.Offset.Y:0}/{Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height):0})");

            SaveEvidenceFrame(window, $"desktop-chrome-unmarked-surface-{WindowWidth:0}x{WindowHeight:0}.png");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The surface arrives asynchronously, and the loader may replace it. Each transition must be
    /// reflected in the page scroll mode, in both directions and without leaving bounded mode behind.
    /// </summary>
    [AvaloniaFact]
    public void Managed_surface_arrival_and_replacement_update_the_page_scroll_mode()
    {
        var (window, chrome) = ShowChrome();
        try
        {
            var contentHost = ContentHost(chrome);
            var toolView = new ExternalSdkToolView { DataContext = CreateToolViewModel() };
            contentHost.Content = toolView;
            Pump(window);
            var scroller = ScrollerOf(contentHost);

            // Before the surface control exists the page keeps its normal scrolling.
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);

            // Async arrival of a self-scrolling surface switches to the bounded frame.
            toolView.SetHostedSurface(new FullHeightSurface());
            Pump(window);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);

            // Replacing it with an unmarked surface restores page scrolling.
            toolView.SetHostedSurface(new FullHeightSurface(declaresViewport: false));
            Pump(window);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);

            // And switching back is picked up again.
            toolView.SetHostedSurface(new FullHeightSurface());
            Pump(window);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);

            // Leaving the tool page resets the frame for the next ordinary page.
            contentHost.Content = new Border { Height = 3000, Background = Brushes.Transparent };
            Pump(window);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height + 100,
                $"page scrolling was not restored: extent {scroller.Extent.Height:0} viewport {scroller.Viewport.Height:0}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Leaving the managed surface restores ordinary page scrolling.</summary>
    [AvaloniaFact]
    public void Leaving_a_hosted_surface_restores_page_scrolling()
    {
        var (window, chrome) = ShowChrome();
        try
        {
            var contentHost = ContentHost(chrome);
            var toolView = new ExternalSdkToolView { DataContext = CreateToolViewModel() };
            contentHost.Content = toolView;
            toolView.SetManagedSurface(new FullHeightSurface());
            Pump(window);
            var scroller = ScrollerOf(contentHost);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);

            contentHost.Content = new Border { Height = 3000, Background = Brushes.Transparent };
            Pump(window);
            Assert.Equal(ScrollBarVisibility.Auto, scroller.VerticalScrollBarVisibility);
            Assert.True(
                scroller.Extent.Height > scroller.Viewport.Height + 100,
                $"page scrolling was not restored: extent {scroller.Extent.Height:0} viewport {scroller.Viewport.Height:0}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Writes the rendered chrome chain for review (registered verification area).</summary>
    private static void SaveEvidenceFrame(Window window, string fileName)
    {
        var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        if (frame is null)
        {
            return;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? AppContext.BaseDirectory;
        var target = Path.Combine(root, "artifacts", ".tmp-android-verify", "m2", "screens", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var stream = File.Create(target);
        frame.Save(stream);
    }

    private static (Window Window, ShellChromeView Chrome) ShowChrome()
    {
        var chrome = new ShellChromeView { DataContext = CreateChromeViewModel() };
        var window = new Window
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Content = chrome
        };
        window.Show();
        Pump(window);
        return (window, chrome);
    }

    private static ShellChromeViewModel CreateChromeViewModel() => new(
        ShellWorkspaceController.PageLabels,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        runtimeModeLabel: "INSTALLED",
        runtimeIdentityText: "MPT Desktop");

    private static ExternalSdkToolViewModel CreateToolViewModel() => new(
        "file-transfer",
        "文件助手",
        "发给自己，或发送到设备",
        "dotnet-surface",
        "文件助手",
        source: null,
        openExternal: false,
        commands: [],
        settingsPath: null,
        handleBridgeRequest: _ => Task.FromResult(""),
        refresh: () => Task.CompletedTask,
        returnToTools: () => Task.CompletedTask);

    private static ContentControl ContentHost(ShellChromeView chrome) =>
        chrome.FindControl<ContentControl>("ContentHost")
        ?? throw new InvalidOperationException("the chrome content host was not found");

    private static ScrollViewer ScrollerOf(Control contentHost) =>
        contentHost.GetVisualAncestors().OfType<ScrollViewer>().First();

    private static Point Origin(Visual control, Visual root) =>
        control.TranslatePoint(default, root) ?? default;

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    /// <summary>
    /// A managed surface with the same layout contract as the file assistant: a fixed header, a
    /// scrolling list that fills the middle, a composer pinned to the bottom, and a centred dialog.
    /// </summary>
    private sealed class FullHeightSurface : UserControl
    {
        public FullHeightSurface(bool declaresViewport = true)
        {
            if (declaresViewport)
            {
                // The opt-in contract: only a surface that owns its own scrolling declares this class.
                Classes.Add(ShellChromeView.ViewportSurfaceClass);
            }

            var rows = new StackPanel { Spacing = 4, Margin = new Thickness(8) };
            for (var index = 0; index < 40; index++)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = $"消息 {index + 1}",
                    Height = 40,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent
                });
            }

            ListScroller = new ScrollViewer
            {
                Content = rows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Composer = new Border
            {
                Height = 72,
                Background = new SolidColorBrush(Color.Parse("#E8F0FE")),
                Child = new TextBlock { Text = "输入…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }
            };
            Dialog = new Border
            {
                Width = 280,
                Height = 140,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#FFFFFF")),
                BorderBrush = new SolidColorBrush(Color.Parse("#0965EE")),
                BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = "确认接收？", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            Dialog.IsVisible = false;

            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            var header = new Border
            {
                Height = 48,
                Background = new SolidColorBrush(Color.Parse("#F1F3F6")),
                Child = new TextBlock { Text = "文件助手", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }
            };
            Grid.SetRow(header, 0);
            layout.Children.Add(header);
            Grid.SetRow(ListScroller, 1);
            layout.Children.Add(ListScroller);
            Grid.SetRow(Composer, 2);
            layout.Children.Add(Composer);

            var dialogLayer = new Grid { Children = { Dialog }, IsHitTestVisible = false };
            Grid.SetRow(dialogLayer, 0);
            Grid.SetRowSpan(dialogLayer, 3);

            Content = new Grid { Children = { layout, dialogLayer } };
        }

        public ScrollViewer ListScroller { get; }
        public Border Composer { get; }
        public Border Dialog { get; }

        public void ShowDialog() => Dialog.IsVisible = true;
    }
}
