using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Abstractions;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MobileLayout.Tests;
public sealed class PageLayoutTests
{
    private static readonly AsyncRelayCommand Noop = new(() => Task.CompletedTask);
    private static readonly ToolCardViewModel Tool = new("file-transfer", "文件互传", "通过 Tailscale 直传或 OpenList 网盘中转发送文件。", "文件", "⇄", "Ready", "Windows · macOS · Android", ToolAvailability.Available, true);
    private static readonly ModulePickerItemViewModel Module = new("file-transfer", "文件互传", true, "Selected", Noop);
    private static readonly MetricViewModel[] Metrics = [new("Device", "MPT Android"), new("Directory", "/storage/emulated/0/Download/MyPowerTools/")];

    public static TheoryData<int> Widths => new() { 320, 360, 390, 768 };

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void Shared_pages_keep_interactive_controls_inside_the_phone_viewport(int width)
    {
        foreach (var (name, view) in SharedPages()) VerifyPage(name, view, width);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Widths))]
    public void All_tool_surfaces_can_lay_out_at_mobile_width(int width)
    {
        // Constructors are exercised without starting device services. Populated shared pages
        // and the table-card test cover bindings; these checks cover each Surface's actual XAML.
        UserControl[] surfaces =
        [
            new AdbForwarder.Surface.Views.AdbForwarderView(),
            new DoubaoAgent.Surface.Views.DoubaoAgentView(),
            new ImeManager.Tool.ImeManagerView(),
            new InputMonitor.Surface.Views.InputMonitorView(),
            new LocalLagCleaner.Tool.LocalLagCleanerView(),
            new NssmManager.Tool.NssmManagerView(),
            new PasteImage.Surface.Views.PasteImageView(),
            new RemoteCommands.Surface.Views.RemoteCommandsView(),
            new RemoteNotifications.Surface.Views.RemoteNotificationsView(),
            new ScreenEase.Surface.Views.ScreenEaseView(),
            new SmartBird.Surface.Views.SmartBirdThermostatView(),
            CreateFileTransfer()
        ];
        var failures = new List<string>();
        foreach (var view in surfaces)
        {
            try { VerifyPage(view.GetType().Name, view, width); }
            catch (Exception ex) { failures.Add(Describe(view.GetType().Name, ex)); }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Keeps the surface's own frames in the report. Assertion helpers strip everything below the
    /// test frame, which hides whether the throw came from the tool's layout code or the shared one.
    /// </summary>
    private static string Describe(string name, Exception exception) =>
        name + ": " + exception.GetType().Name + ": " + exception.Message + "\n" + exception.StackTrace;

    private static UserControl CreateFileTransfer()
    {
        var context = new MptAvaloniaSurfaceContext("file-transfer", "workspace", "/mnt/cache/data-cache/mpt-mobile-test-transfer", "light",
            (command, _, _) => Task.FromResult(new CommandExecutionResult("layout", command, "completed", true,
                """{"settings":{"receiveDirectory":"Downloads/MyPowerTools","deviceId":"My Android","listenAddress":"100.64.0.2","webDavUrl":"https://openlist.example.test/dav/transfer","username":"mobile","peers":[{"name":"Windows desktop","deviceId":"desktop"}],"lastPeer":"desktop"},"receiving":true,"history":[]}""")),
            (_, _, _) => Task.CompletedTask, null!, _ => { });
        var view = new FileTransfer.Surface.TransferView(context);
        foreach (var expander in view.GetLogicalDescendants().OfType<Expander>()) expander.IsExpanded = true;
        return view;
    }

    private static IEnumerable<(string Name, UserControl View)> SharedPages()
    {
        yield return ("Home", new HomeView { DataContext = new HomeViewModel([Tool], [Tool], [], 1) });
        yield return ("Tools", new ToolCatalogView { DataContext = new ToolCatalogViewModel([Tool]) });
        yield return ("General", new GeneralSettingsView { DataContext = new GeneralSettingsViewModel("system", _ => Task.CompletedTask, () => Task.CompletedTask) });
        yield return ("Settings", new SettingsCenterView { DataContext = new SettingsCenterViewModel("file-transfer", "文件互传", 1, "{}", "Ready", [Module],
            [new("serverUrl", "中转服务器", "text", "用于传输文件的 OpenList 服务地址", "https://openlist.example.test", false, [], "")]) });
        yield return ("Logs", new LogsView { DataContext = new LogsViewModel("文件互传", [Module], [new("21:45:03", "Info", "文件已经发送到手机，可以在收件箱中打开。", ModuleId: "file-transfer")]) });
        yield return ("Notifications", new NotificationsView { DataContext = new NotificationsViewModel([new("1", "21:45", "file-transfer", "Info", "收到了一个文件", "来自 Windows 的设计文档已保存到下载目录。", false)]) });
        yield return ("Packages", new PackageManagerView { DataContext = new PackageManagerViewModel([new("file-transfer", "文件互传", "0.1.0", "MyPowerTools", "/data/user/0/com.mypowertools/files/modules/file-transfer", "", "", "", "Trusted", 1, 0, 0, "file-transfer", Metrics, [new("file-transfer", Noop)], Noop, Noop, Noop)]) });
        yield return ("Diagnostics", new DiagnosticsView { DataContext = new DiagnosticsViewModel("MPT Android", Metrics, Metrics, [], [], [], [], [], []) });
        yield return ("Services", new ServicesView { DataContext = new ServicesViewModel("后台服务", []) });
        yield return ("System", new SystemHubView { DataContext = new SystemHubViewModel([new("Logs", "日志", "查看所有工具的活动和错误。", "≡", "Ready")]) });
        yield return ("Permission", new PermissionPromptView { DataContext = new PermissionPromptViewModel(Metrics, Noop) });
        yield return ("Commands", new CommandPaletteView { DataContext = new CommandPaletteViewModel("file", []) });
        yield return ("Modules", new ModulesView { DataContext = new ModulesViewModel([new("file-transfer", "file-transfer", "文件互传", "Ready", "发送和接收文件", true, "builtin", "网络与所选文件", "Disable", false, Noop, Noop, Noop, Noop)]) });
        yield return ("ToolHost", new ToolHostView());
        yield return ("ExternalTool", new ExternalSdkToolView());
        yield return ("ModuleDetail", new ModuleDetailView());
        yield return ("Shortcuts", new ShortcutCenterView());
        yield return ("Audit", new BrokerAuditView { DataContext = new BrokerAuditViewModel([]) });
        yield return ("Unavailable", new UnavailablePageView());
        yield return ("Dashboard", new DashboardView());
    }

    private static void VerifyPage(string name, UserControl view, int width)
    {
        var host = new Border { Child = view, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Stretch };
        host.Resources["MptLayoutPackageCardWidth"] = (double)width - 32;
        host.Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.Shell.Avalonia/"))
        { Source = new Uri("avares://MyPowerTools.Shell.Avalonia/Styles/Mobile.axaml") });
        var scroller = new ScrollViewer { Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var window = new Window { Width = width, Height = 800, Content = scroller };
        try
        {
            window.Show();
            for (var pass = 0; pass < 4; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            var escaped = new List<string>();
            foreach (var control in view.GetVisualDescendants().OfType<Control>().Where(c => c is Button or TextBox or ComboBox))
            {
                if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || !control.IsVisible || control.GetVisualAncestors().OfType<Control>().Any(c => !c.IsVisible)) continue;
                if (control.GetVisualAncestors().OfType<ScrollViewer>().Any(s => s.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible)) continue;
                var origin = control.TranslatePoint(default, window);
                if (origin is { } point && (point.X < -1 || point.X + control.Bounds.Width > width + 1))
                    escaped.Add($"{control.GetType().Name} {control.Name} at {point.X:0.0} width {control.Bounds.Width:0.0}");
            }
            var directory = Environment.GetEnvironmentVariable("MPT_MOBILE_SCREENSHOTS");
            if (!string.IsNullOrEmpty(directory) && width <= 390)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(directory, $"{name}-{width}.png"));
                if (name == "TransferView")
                {
                    scroller.Offset = new Vector(0, Math.Max(0, scroller.Extent.Height - 800));
                    window.UpdateLayout();
                    using var bottom = window.CaptureRenderedFrame();
                    bottom?.Save(Path.Combine(directory, $"{name}-settings-{width}.png"));
                }
            }
            Assert.True(escaped.Count == 0, $"{name} at {width}: {string.Join("; ", escaped)}");
        }
        finally { window.Close(); }
    }
}
