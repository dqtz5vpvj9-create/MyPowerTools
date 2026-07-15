using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Google.Protobuf.WellKnownTypes;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia;

public static class ShellRealScreenshotWriter
{
    private const string DefaultInteractionScenario = "default";
    private const string RemoteNotificationsScreenId = "remote-notifications-inbox";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object RenderLock = new();
    private static bool _applicationInitialized;
    private static IBrush _headlessBackground = MptTheme.AppBackground;

    public static string WriteSnapshotSet(string outputDirectory, string theme, string size, string density, string surface = "*")
    {
        return WriteSnapshotSetCore(
            outputDirectory,
            theme,
            size,
            density,
            CreateSampleScreens(),
            "sample-fixture",
            surface,
            new ScreenshotManifestStats(ModuleCount: 2, CommandCount: 5, RunnerConnected: false));
    }

    public static string WriteProductFoundationSnapshotSet(
        string outputDirectory,
        string theme,
        string size,
        string density,
        string surface = "*",
        string scenario = DefaultInteractionScenario)
    {
        var normalizedScenario = NormalizeInteractionScenario(scenario);
        return WriteSnapshotSetCore(
            outputDirectory,
            theme,
            size,
            density,
            CreateProductFoundationScreens(normalizedScenario),
            "product-fixture",
            surface,
            new ScreenshotManifestStats(ModuleCount: 7, CommandCount: 0, RunnerConnected: false),
            normalizedScenario);
    }

    public static async Task<string> WriteSnapshotSetFromHostControlAsync(
        string outputDirectory,
        string theme,
        string size,
        string density,
        HostControlClient client,
        string dataSource,
        CancellationToken cancellationToken = default)
    {
        return await WriteSnapshotSetFromHostControlAsync(
            outputDirectory,
            theme,
            size,
            density,
            client,
            dataSource,
            "*",
            cancellationToken);
    }

    public static async Task<string> WriteSnapshotSetFromHostControlAsync(
        string outputDirectory,
        string theme,
        string size,
        string density,
        HostControlClient client,
        string dataSource,
        string surface,
        CancellationToken cancellationToken = default)
    {
        var data = await ShellHostControlSnapshotData.LoadAsync(client, dataSource, cancellationToken);
        return WriteSnapshotSetFromHostControlData(outputDirectory, theme, size, density, data, surface);
    }


    public static string WriteSnapshotSetFromHostControlData(
        string outputDirectory,
        string theme,
        string size,
        string density,
        ShellHostControlSnapshotData data)
    {
        return WriteSnapshotSetFromHostControlData(outputDirectory, theme, size, density, data, "*");
    }

    public static string WriteSnapshotSetFromHostControlData(
        string outputDirectory,
        string theme,
        string size,
        string density,
        ShellHostControlSnapshotData data,
        string surface)
    {
        return WriteSnapshotSetCore(
            outputDirectory,
            theme,
            size,
            density,
            CreateHostControlScreens(data),
            data.DataSource,
            surface,
            new ScreenshotManifestStats(
                data.Modules.Modules.Count,
                data.Commands.Commands.Count,
                data.DataSource.Contains("runner-hostcontrol", StringComparison.OrdinalIgnoreCase)));
    }

    private static string WriteSnapshotSetCore(
        string outputDirectory,
        string theme,
        string size,
        string density,
        IReadOnlyList<RealScreen> screens,
        string dataSource,
        string surface,
        ScreenshotManifestStats stats,
        string interactionScenario = DefaultInteractionScenario)
    {
        lock (RenderLock)
        {
            Directory.CreateDirectory(outputDirectory);
            var (width, height) = ParseSize(size);
            EnsureApplication(theme);

            var requestedSurface = string.IsNullOrWhiteSpace(surface) ? "*" : surface;
            var normalizedScenario = NormalizeInteractionScenario(interactionScenario);
            var filteredScreens = screens
                .Where(screen => MatchesRealScreenFilter(screen, requestedSurface))
                .Where(screen => normalizedScenario == DefaultInteractionScenario ||
                    string.Equals(screen.Id, RemoteNotificationsScreenId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (filteredScreens.Length == 0)
            {
                throw new InvalidOperationException(
                    normalizedScenario == DefaultInteractionScenario
                        ? $"No real Shell screenshot screen matches surface '{requestedSurface}'."
                        : $"Interaction scenario '{normalizedScenario}' currently supports only the Remote Notifications product page.");
            }

            var mode = ModeForDataSource(dataSource);
            var entries = new JsonArray();
            foreach (var screen in filteredScreens)
            {
                var scenarioSuffix = normalizedScenario == DefaultInteractionScenario
                    ? ""
                    : $".{Sanitize(normalizedScenario)}";
                var fileName = $"real-{screen.Id}{scenarioSuffix}.{Sanitize(theme)}.{Sanitize(density)}.{Sanitize(size)}.png";
                var path = Path.Combine(outputDirectory, fileName);
                ApplyTheme(theme);
                var rendered = Render(screen.CreateView, path, width, height, normalizedScenario);
                var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                var interactionSteps = new JsonArray();
                foreach (var step in rendered.InteractionSteps)
                {
                    interactionSteps.Add(step);
                }

                entries.Add(new JsonObject
                {
                    ["screenId"] = screen.Id,
                    ["page"] = screen.Page,
                    ["surfaceId"] = screen.SurfaceId,
                    ["title"] = screen.Title,
                    ["fileName"] = fileName,
                    ["imagePath"] = Path.GetFullPath(path),
                    ["sha256"] = sha256,
                    ["width"] = rendered.Width,
                    ["height"] = rendered.Height,
                    ["mode"] = mode,
                    ["theme"] = theme,
                    ["density"] = density,
                    ["size"] = size,
                    ["renderer"] = "Avalonia.Headless",
                    ["scenario"] = normalizedScenario,
                    ["interactionSteps"] = interactionSteps,
                    ["dataSource"] = dataSource,
                    ["runnerConnected"] = stats.RunnerConnected,
                    ["moduleCount"] = stats.ModuleCount,
                    ["commandCount"] = stats.CommandCount
                });
            }

            var manifestPath = Path.Combine(outputDirectory, "shell-real-screenshot-manifest.json");
            var manifest = new JsonObject
            {
                ["schemaVersion"] = "1.0",
                ["artifactKind"] = "real-avalonia-screenshot",
                ["surface"] = requestedSurface,
                ["mode"] = mode,
                ["theme"] = theme,
                ["density"] = density,
                ["size"] = size,
                ["scenario"] = normalizedScenario,
                ["dataSource"] = dataSource,
                ["usesHostControlData"] = dataSource.Contains("hostcontrol", StringComparison.OrdinalIgnoreCase),
                ["runnerConnected"] = stats.RunnerConnected,
                ["moduleCount"] = stats.ModuleCount,
                ["commandCount"] = stats.CommandCount,
                ["screenshotCount"] = entries.Count,
                ["screenshots"] = entries
            };
            File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions));
            return manifestPath;
        }
    }

    private static void EnsureApplication(string theme)
    {
        if (!_applicationInitialized)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { ShouldRenderOnUIThread = true })
                .UseSkia()
                .WithInterFont()
                .SetupWithoutStarting();
            _applicationInitialized = true;
        }

        ApplyTheme(theme);
    }

    private static void ApplyTheme(string theme)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Invoke(() => ApplyTheme(theme));
            return;
        }

        var dark = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase);
        _headlessBackground = MptThemeTokens.Brush(
            dark ? MptThemeTokens.ColorAppBackgroundDark : MptThemeTokens.ColorAppBackground);
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
            MptTheme.ApplyPalette(Application.Current, dark);
        }
    }

    private static RenderedFrame Render(
        Func<Control> createView,
        string path,
        int width,
        int height,
        string interactionScenario)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            RenderedFrame? result = null;
            Dispatcher.UIThread.Invoke(() => result = Render(createView, path, width, height, interactionScenario));
            return result ?? throw new InvalidOperationException("Avalonia Headless render did not return a frame.");
        }

        Window? window = null;
        try
        {
            var view = createView();
            window = new Window
            {
                Width = width,
                Height = height,
                Content = CreateOpaqueRenderRoot(view),
                Background = _headlessBackground,
                ShowInTaskbar = false,
                CanResize = false
            };

            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            var interaction = ApplyInteractionScenario(window, view, interactionScenario);
            if (ReferenceEquals(interaction.CaptureTarget, window))
            {
                window = RecreateHeadlessWindow(window, view, width, height);
                interaction = new InteractionResult(
                    window,
                    interaction.Steps.Concat(["recreated-window:full-frame"]).ToArray());
            }
            else
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
            }

            using var bitmap = interaction.CaptureTarget.GetLastRenderedFrame() ?? interaction.CaptureTarget.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("Avalonia Headless did not produce a rendered frame.");
            using var stream = File.Create(path);
            bitmap.Save(stream);
            return new RenderedFrame(
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height,
                interaction.Steps);
        }
        finally
        {
            if (window is not null)
            {
                foreach (var ownedWindow in window.OwnedWindows.ToArray())
                {
                    ownedWindow.Close();
                }
            }

            window?.Close();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        }
    }

    private static Window RecreateHeadlessWindow(
        Window current,
        Control view,
        int width,
        int height)
    {
        if (current.Content is Border renderRoot && ReferenceEquals(renderRoot.Child, view))
        {
            renderRoot.Child = null;
        }
        current.Content = null;
        current.Close();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);

        var replacement = new Window
        {
            Width = width,
            Height = height,
            Content = CreateOpaqueRenderRoot(view),
            Background = _headlessBackground,
            ShowInTaskbar = false,
            CanResize = false
        };
        replacement.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
        return replacement;
    }

    private static Border CreateOpaqueRenderRoot(Control view)
    {
        return new Border
        {
            Background = _headlessBackground,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch,
            Child = view
        };
    }

    private static InteractionResult ApplyInteractionScenario(
        Window window,
        Control view,
        string interactionScenario)
    {
        // Tool-specific interaction scenarios (Remote Notifications scroll/filter/detail/activation)
        // were owned by the tool surface assemblies and are no longer exercised from the Shell.
        return new InteractionResult(window, []);
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
    }

    private static Point CenterInWindow(Control control, Window window)
    {
        var center = new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        return control.TranslatePoint(center, window)
            ?? throw new InvalidOperationException($"Could not translate {control.GetType().Name} coordinates into the headless window.");
    }

    private static IReadOnlyList<RealScreen> CreateSampleScreens()
    {
        return
        [
            new("dashboard", "dashboard", "shell.dashboard", "Dashboard", () => CreateShellChrome("Dashboard", new DashboardView { DataContext = SampleDashboard() })),
            new("command-palette-with-params", "command-palette", "shell.command-palette", "Command Palette With Parameters", CreateCommandPaletteSampleShell),
            new("settings-dirty-state", "settings", "shell.settings-center", "Settings Dirty State", () => CreateShellChrome("Settings", new SettingsCenterView { DataContext = SampleSettings() })),
            new("module-detail-degraded", "module-detail", "shell.module-detail", "Module Detail Degraded", () => CreateShellChrome("Modules", new ModuleDetailView { DataContext = SampleModuleDetail() })),
            new("logs-long-lines", "logs", "shell.logs-viewer", "Logs Long Lines", () => CreateShellChrome("Logs", new LogsView { DataContext = SampleLogs() })),
            new("notifications-list", "notifications", "shell.notification-center", "Notifications List", () => CreateShellChrome("Notifications", new NotificationsView { DataContext = SampleNotifications() })),
            new("packages", "packages", "shell.package-manager", "Packages", () => CreateShellChrome("Packages", new PackageManagerView { DataContext = SamplePackages() })),
            new("diagnostics-wide", "diagnostics", "shell.runtime-diagnostics", "Diagnostics Wide", () => CreateShellChrome("Diagnostics", new DiagnosticsView { DataContext = SampleDiagnostics() }))
        ];
    }

    private static IReadOnlyList<RealScreen> CreateProductFoundationScreens(string interactionScenario)
    {
        return
        [
            new("home-ready", "home", "shell.home", "Home", () => CreateProductShellChrome(
                "Home",
                new HomeView { DataContext = SampleProductHome() })),
            new("general-settings", "general", "shell.general", "General", () => CreateProductShellChrome(
                "Settings",
                new GeneralSettingsView { DataContext = SampleGeneralSettings() })),
            new("tools-catalog", "tools", "shell.tools-catalog", "Tools Catalog", () => CreateProductShellChrome(
                "Tools",
                new ToolCatalogView { DataContext = SampleToolCatalog() }))
        ];
    }

    private static IReadOnlyList<RealScreen> CreateHostControlScreens(ShellHostControlSnapshotData data)
    {
        var selected = data.SelectedModule;
        var settingsValues = JsonStructMapper.ToJsonObject(data.Settings.Values);
        var settings = ShellPageViewModelFactory.FromSettings(
            data.Modules,
            selected,
            data.SettingsSchema.SchemaJson,
            settingsValues,
            PrettyJson(data.Settings.Values),
            data.Settings.Revision,
            ToDateTimeOffsetOrMin(data.Settings.UpdatedAt),
            hotkeys: data.Diagnostics.Hotkeys);

        return
        [
            new("dashboard", "dashboard", "shell.dashboard", "Dashboard", () => CreateShellChrome("Dashboard", new DashboardView
            {
                DataContext = ShellPageViewModelFactory.FromDashboard(data.Dashboard)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("command-palette-with-params", "command-palette", "shell.command-palette", "Command Palette With Parameters", () => CreateShellChrome("Commands", new CommandPaletteView
            {
                DataContext = ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("settings-dirty-state", "settings", "shell.settings-center", "Settings", () => CreateShellChrome("Settings", new SettingsCenterView
            {
                DataContext = settings
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("module-detail-degraded", "module-detail", "shell.module-detail", "Module Detail", () => CreateShellChrome("Modules", new ModuleDetailView
            {
                DataContext = ShellPageViewModelFactory.FromModuleDetail(data.ModuleDetail, data.Commands)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("logs-long-lines", "logs", "shell.logs-viewer", "Logs", () => CreateShellChrome("Logs", new LogsView
            {
                DataContext = ShellPageViewModelFactory.FromLogs(data.Modules, selected, data.Logs)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("notifications-list", "notifications", "shell.notification-center", "Notifications", () => CreateShellChrome("Notifications", new NotificationsView
            {
                DataContext = ShellPageViewModelFactory.FromNotifications(data.Notifications)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("packages", "packages", "shell.package-manager", "Packages", () => CreateShellChrome("Packages", new PackageManagerView
            {
                DataContext = ShellPageViewModelFactory.FromPackages(data.Packages)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource)),
            new("diagnostics-wide", "diagnostics", "shell.runtime-diagnostics", "Diagnostics", () => CreateShellChrome("Diagnostics", new DiagnosticsView
            {
                DataContext = ShellPageViewModelFactory.FromDiagnostics(data.Diagnostics, data.BrokerAudit)
            }, ShellPageViewModelFactory.FromCommands(CommandQuery(data.Commands), data.Commands), data.DataSource))
        ];
    }

    private static ShellChromeView CreateShellChrome(
        string selectedPage,
        Control content,
        CommandPaletteViewModel? commandPalette = null,
        string dataSource = "sample-fixture")
    {
        var chromeViewModel = new ShellChromeViewModel(["Dashboard", "Modules", "Commands", "Settings", "Logs", "Notifications", "Packages", "Diagnostics"])
        {
            StatusText = "Full shell snapshot",
            RunnerStatusText = "Runner connected",
            IsCommandPaletteOpen = string.Equals(selectedPage, "Commands", StringComparison.OrdinalIgnoreCase),
            IsPermissionPromptOpen = false
        };
        chromeViewModel.SelectPage(selectedPage);

        var chrome = new ShellChromeView { DataContext = chromeViewModel };
        SetShellContent(chrome, "ContentHost", content);
        SetShellContent(chrome, "CommandPanel", new CommandPaletteView { DataContext = commandPalette ?? SampleCommandPalette() });
        SetShellContent(chrome, "PermissionPanel", CreatePermissionPrompt());
        SetShellContent(chrome, "AuditPanel", CreateAuditSummary(dataSource));
        return chrome;
    }

    private static ShellChromeView CreateCommandPaletteSampleShell()
    {
        var chrome = CreateProductShellChrome(
            "Home",
            new HomeView { DataContext = SampleProductHome() });

        if (chrome.DataContext is ShellChromeViewModel chromeViewModel)
        {
            chromeViewModel.IsCommandPaletteOpen = true;
        }

        return chrome;
    }

    private static ShellChromeView CreateProductShellChrome(string selectedPage, Control content)
    {
        var chromeViewModel = new ShellChromeViewModel(
            [
                "Home",
                "Tools",
                "Activity",
                "Notifications",
                "ADB Forwarder",
                "ScreenEase",
                "Doubao Agent",
                "SmartBird",
                "Settings",
                "System"
            ])
        {
            StatusText = "Product UI acceptance snapshot",
            RunnerStatusText = "7 tools registered",
            IsCommandPaletteOpen = false,
            IsPermissionPromptOpen = false
        };
        chromeViewModel.SelectPage(selectedPage);

        var chrome = new ShellChromeView { DataContext = chromeViewModel };
        SetShellContent(chrome, "ContentHost", content);
        SetShellContent(chrome, "CommandPanel", new CommandPaletteView { DataContext = SampleCommandPalette() });
        SetShellContent(chrome, "PermissionPanel", CreatePermissionPrompt());
        SetShellContent(chrome, "AuditPanel", CreateAuditSummary("product-fixture"));
        return chrome;
    }

    private static void SetShellContent(ShellChromeView chrome, string name, Control content)
    {
        var host = chrome.FindControl<ContentControl>(name) ?? throw new InvalidOperationException($"ShellChromeView missing {name}.");
        host.Content = content;
    }

    private static Control CreatePermissionPrompt()
    {
        return new Border
        {
            Padding = MptTheme.FieldPadding,
            Margin = MptTheme.PermissionPanelMargin,
            BorderThickness = MptTheme.BorderThickness,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Permission Required" },
                    new TextBlock { Text = "NetworkBroker approval is required for portproxy.apply.", TextWrapping = TextWrapping.Wrap }
                }
            }
        };
    }

    private static Control CreateAuditSummary(string dataSource)
    {
        return new Border
        {
            Padding = MptTheme.FieldPadding,
            Margin = MptTheme.AuditPanelMargin,
            BorderThickness = MptTheme.BorderThickness,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Audit" },
                    new TextBlock { Text = "Snapshot data source is recorded in the screenshot manifest.", TextWrapping = TextWrapping.Wrap }
                }
            }
        };
    }

    private static HomeViewModel SampleProductHome()
    {
        var tools = SampleProductTools();
        return new HomeViewModel(
            tools.Where(tool => tool.ToolId is "remote-notifications" or "adb-forwarder").ToArray(),
            tools.Where(tool => tool.ToolId is "screenease" or "doubao-agent" or "smartbird-thermostat").ToArray(),
            [
                new HomeActivityItemViewModel(
                    "activity-remote-command",
                    "Remote Commands",
                    "Collect Android build information",
                    "Succeeded",
                    "2 minutes ago",
                    "Command completed with a copy-ready result.",
                    Command()),
                new HomeActivityItemViewModel(
                    "activity-portproxy-plan",
                    "ADB Forwarder",
                    "Preview port forwarding plan",
                    "Ready to apply",
                    "18 minutes ago",
                    "Three mappings passed validation and are waiting for approval.",
                    Command())
            ],
            tools.Count);
    }

    private static GeneralSettingsViewModel SampleGeneralSettings()
    {
        return new GeneralSettingsViewModel(
            ShellAppearanceService.SystemTheme,
            _ => Task.CompletedTask,
            () => Task.CompletedTask);
    }

    private static ToolCatalogViewModel SampleToolCatalog()
    {
        return new ToolCatalogViewModel(SampleProductTools());
    }

    private static IReadOnlyList<ToolCardViewModel> SampleProductTools()
    {
        return
        [
            new ToolCardViewModel(
                "remote-notifications",
                "Remote Notifications",
                "Read and manage messages received from remote services.",
                "Communication",
                "RN",
                "Connected",
                "12 unread messages · delivery active",
                ToolAvailability.Available,
                true,
                primaryActionLabel: "Open inbox"),
            new ToolCardViewModel(
                "adb-forwarder",
                "ADB Forwarder",
                "Create, validate, and apply Android port forwarding rules.",
                "Android",
                "ADB",
                "Ready",
                "2 devices · 3 active mappings",
                ToolAvailability.Available,
                true,
                primaryActionLabel: "Forward device"),
            new ToolCardViewModel(
                "remote-commands",
                "Remote Commands",
                "Run saved commands with parameters and inspect their output.",
                "Automation",
                "RC",
                "Paused",
                "Delivery is paused until this workflow is needed again.",
                ToolAvailability.Paused,
                false,
                primaryActionLabel: "Paused"),
            new ToolCardViewModel(
                "process-monitor",
                "Process Monitor",
                "Watch selected processes and review actionable alerts.",
                "Monitoring",
                "PM",
                "Paused",
                "Delivery is paused until this workflow is needed again.",
                ToolAvailability.Paused,
                false,
                primaryActionLabel: "Paused"),
            new ToolCardViewModel(
                "screenease",
                "ScreenEase",
                "Choose display profiles for work, focus, and evening use.",
                "Display",
                "SE",
                "Ready",
                "Profiles, reminders, schedules, overlay, and shortcuts are available.",
                ToolAvailability.Available,
                false,
                primaryActionLabel: "Open ScreenEase"),
            new ToolCardViewModel(
                "doubao-agent",
                "豆包 Computer Use",
                "Check service health and run common controller actions.",
                "Services",
                "DA",
                "Needs attention",
                "The workspace is available; one or more controller services need attention.",
                ToolAvailability.Available,
                false,
                primaryActionLabel: "打开电脑任务"),
            new ToolCardViewModel(
                "smartbird-thermostat",
                "SmartBird 温度管理器",
                "Review temperature status, events, and service policy.",
                "Hardware",
                "ST",
                "Needs attention",
                "The dashboard is available; hardware services are currently offline.",
                ToolAvailability.Available,
                false,
                primaryActionLabel: "打开温度管理器")
        ];
    }

    private static DashboardViewModel SampleDashboard()
    {
        return new DashboardViewModel(
            "7 modules indexed, event seq 42",
            [
                new DashboardCardViewModel(
                    "adb-forwarder",
                    "adb-forwarder",
                    "AdbForwarder",
                    "running",
                    "2 devices visible, 3 port proxy mappings staged.",
                    [new MetricViewModel("Transport", "inproc-dotnet"), new MetricViewModel("Policy", "sidecar fallback")],
                    [Action("adb-forwarder.devices.scan", "Scan", "primary")],
                    Command()),
                new DashboardCardViewModel(
                    "android-tools.remote-commands",
                    "android-tools-suite",
                    "Remote Commands",
                    "degraded",
                    "Sidecar selected; notification endpoint unreachable.",
                    [new MetricViewModel("Transport", "grpc-ipc"), new MetricViewModel("Commands", "12")],
                    [Action("android-tools.remote-commands.catalog", "Catalog", "secondary")],
                    Command())
            ],
            [new ShellAlertViewModel("runtime-policy", "warning", "Runtime policy", "1 module is using an InProc fallback because its sidecar command is missing.")]);
    }

    private static CommandPaletteViewModel SampleCommandPalette()
    {
        var commands = new CommandItemViewModel[]
        {
            new(
                "android-tools.notifications.open",
                "android-tools.notifications",
                "Open Remote notifications",
                "View messages mirrored from Android devices.",
                "safe",
                false,
                "Remote notifications",
                "",
                "",
                false,
                null,
                actionKind: "navigation",
                icon: "notifications",
                category: "Tool"),
            new(
                "adb-forwarder.devices.scan",
                "adb-forwarder",
                "Scan connected devices",
                "Refresh the list of Android devices available to ADB.",
                "safe",
                false,
                "ADB Forwarder",
                "",
                "",
                false,
                null,
                icon: "diagnostics",
                category: "Diagnostics"),
            new(
                "adb-forwarder.portproxy.apply",
                "adb-forwarder",
                "Apply port forwarding",
                "Create a local forwarding rule for the selected device.",
                "safe",
                false,
                "ADB Forwarder",
                "",
                "",
                true,
                null,
                parameters:
                [
                    new CommandParameterViewModel("listenPort", "Listen port", "integer", true, "5555"),
                    new CommandParameterViewModel("connectAddress", "Connect address", "string", true, "127.0.0.1")
                ],
                icon: "network",
                category: "Network"),
            new(
                "android-tools.process-monitor.stop",
                "android-tools.process-monitor",
                "Stop selected process",
                "Request an elevated stop for the chosen Android process.",
                "elevated",
                true,
                "Process monitor",
                "broker approval required",
                "",
                false,
                null,
                icon: "command",
                category: "System"),
            new(
                "screenease.settings.open",
                "screenease",
                "Open ScreenEase settings",
                "Adjust display profiles and automatic switching rules.",
                "safe",
                false,
                "ScreenEase",
                "",
                "",
                false,
                null,
                actionKind: "navigation",
                icon: "settings",
                category: "Settings")
        };

        return new CommandPaletteViewModel("", commands);
    }

    private static SettingsCenterViewModel SampleSettings()
    {
        var adbPath = new SettingsFieldViewModel("adbPath", "ADB path", "string", "Command used for device diagnostics.", "adb", false, [], "");
        var enabled = new SettingsFieldViewModel("autoScan", "Auto scan", "boolean", "Refresh devices when Runner starts.", "true", true, [], "");
        var profile = new SettingsFieldViewModel("profile", "Profile", "enum", "Default connection profile.", "", false, ["normal", "focus", "lab"], "normal");
        var settings = new SettingsCenterViewModel(
            "adb-forwarder",
            "AdbForwarder",
            12,
            """{"adbPath":"adb","autoScan":true,"profile":"normal"}""",
            "Revision 12, staged changes pending.",
            [new ModulePickerItemViewModel("adb-forwarder", "AdbForwarder", true, "Selected", Command())],
            [adbPath, enabled, profile]);

        adbPath.Value = "C:\\Android\\platform-tools\\adb.exe";
        enabled.BooleanValue = false;
        profile.SelectedOption = "focus";
        return settings;
    }

    private static ModuleDetailViewModel SampleModuleDetail()
    {
        return new ModuleDetailViewModel(
            "screenease",
            "screenease",
            "ScreenEase",
            "degraded",
            "Native display writer is unavailable; profile planning remains available.",
            [new MetricViewModel("Transport", "inproc-dotnet"), new MetricViewModel("Policy", "sidecar fallback"), new MetricViewModel("Displays", "2")],
            [new ModulePermissionViewModel("display-profile", "broker", "display.profile", "Apply brightness and color temperature changes.")],
            [new ModuleRequirementViewModel("display.profile", "required", "Native writer required for actual display mutation.")],
            [
                new ModuleDiagnosticItemViewModel("Native writer", "degraded", "screenease-core command was not found."),
                new ModuleDiagnosticItemViewModel("Settings apply", "running", "Active profile loaded from Host SettingsStore.")
            ],
            [SampleCommandItem("screenease.profile.plan", "Plan profile apply", "Preview monitor changes before write.")],
            Command());
    }

    private static LogsViewModel SampleLogs()
    {
        return new LogsViewModel(
            "AdbForwarder",
            [new ModulePickerItemViewModel("adb-forwarder", "AdbForwarder", true, "Selected", Command())],
            [
                new LogLineViewModel("09:14:03", "info", "runtimePolicy preferred sidecar was unavailable; selected inproc fallback."),
                new LogLineViewModel("09:14:04", "warn", "Long adb diagnostic output: device=emulator-5554 state=device transport_id=8 product=sdk_gphone64_x86_64 model=Pixel_8_API_35 repeated context keeps this row wide enough to exercise wrapping."),
                new LogLineViewModel("09:14:05", "error", "Persisted settings apply failed: command timed out after budget.")
            ]);
    }

    private static NotificationsViewModel SampleNotifications()
    {
        return new NotificationsViewModel(
            [
                new NotificationItemViewModel("n1", "2026-07-04 09:12:00", "android-tools.notifications", "info", "Remote phone connected", "Notification bridge is receiving messages.", false),
                new NotificationItemViewModel("n2", "2026-07-04 09:13:20", "smartbird-thermostat", "warning", "Temperature policy changed", "Target temperature updated to 61 C.", false),
                new NotificationItemViewModel("n3", "2026-07-04 09:15:44", "runner", "error", "Sidecar restart blocked", "Restart limit reached for android-tools-suite powertoold.", true)
            ]);
    }

    private static PackageManagerViewModel SamplePackages()
    {
        return new PackageManagerViewModel(
            [
                new PackageSummaryViewModel(
                    "android-tools-suite",
                    "Android Tools Suite",
                    "0.2.0",
                    "lixinrui",
                    "modules/android-tools-suite",
                    "shared/package.hashes.json",
                    "local",
                    "shared/package.signature.json",
                    "trusted",
                    3,
                    1,
                    0,
                    "android-tools.notifications, android-tools.remote-commands, android-tools.process-monitor",
                    [new MetricViewModel("Runtime", "powertoold"), new MetricViewModel("Policy", "sidecar")],
                    [new PackageModuleLinkViewModel("android-tools.notifications", Command())],
                    Command(),
                    Command(),
                    Command())
            ]);
    }

    private static DiagnosticsViewModel SampleDiagnostics()
    {
        return new DiagnosticsViewModel(
            "Collected 2026-07-04 09:16:00",
            [new MetricViewModel("Runner", "0.2.0"), new MetricViewModel("Modules", "7"), new MetricViewModel("Commands", "38")],
            [new MetricViewModel("Root", "C:\\Users\\lixinrui\\AppData\\Local\\MyPowerTools"), new MetricViewModel("Package Root", "C:\\Users\\lixinrui\\repo\\MyPowerTools\\modules")],
            [new RuntimeTransportViewModel("grpc-ipc", "registered", "3"), new RuntimeTransportViewModel("inproc-dotnet", "registered", "4")],
            [
                new RuntimeProcessViewModel(
                    "grpc-ipc",
                    "package:android-tools-suite:runtime:powertoold",
                    "running",
                    31240,
                    "31240",
                    "named-pipe:mypowertools.android-tools-suite.powertoold",
                    "1/4",
                    "automatic",
                    "automatic",
                    "",
                    "android-tools.notifications, android-tools.remote-commands",
                    "2026-07-04 09:10:02",
                    "24: ready",
                    "0 lines",
                    false,
                    true,
                    "Pause",
                    Command(),
                    Command(),
                    Command())
            ],
            [new RuntimeProcessPolicyHistoryItemViewModel("automatic - powertoold", "runner - grpc-ipc - rev 4", "Recovered after settings apply.")],
            [new RuntimeModuleDiagnosticViewModel("AdbForwarder", "running", [new MetricViewModel("Selection", "sidecar unavailable; inproc fallback"), new MetricViewModel("Candidates", "selected:inproc-dotnet")])],
            [new RuntimeCommandHistoryItemViewModel("succeeded - adb-forwarder.devices.scan", "adb-forwarder - 09:14:03", "2 devices")],
            [new BrokerAuditEntryViewModel("approved - portproxy.apply", "adb-forwarder - broker - 09:12:44", "loopback portproxy", "rollback available")]);
    }

    private static CommandItemViewModel SampleCommandItem(string commandId, string title, string subtitle)
    {
        return new CommandItemViewModel(commandId, "screenease", title, subtitle, "normal", false, "ScreenEase", "", "", false, null);
    }

    private static ShellActionViewModel Action(string commandId, string title, string style)
    {
        var isPrimary = string.Equals(style, "primary", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(style, "accent", StringComparison.OrdinalIgnoreCase);
        return new ShellActionViewModel(commandId, title, style, isPrimary, isPrimary ? "MptPrimaryButton" : "", Command());
    }

    private static ICommand Command()
    {
        return new AsyncRelayCommand(() => Task.CompletedTask);
    }

    private static (int Width, int Height) ParseSize(string size)
    {
        var parts = size.Split('x', 'X');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var width) &&
               int.TryParse(parts[1], out var height) &&
               width > 0 &&
               height > 0
            ? (width, height)
            : (1366, 768);
    }

    private static string Sanitize(string value)
    {
        return new string(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-').ToArray());
    }

    private static string CommandQuery(HostProto.ListCommandsResponse commands)
    {
        var command = commands.Commands.FirstOrDefault(item => item.Parameters.Count > 0)
            ?? commands.Commands.FirstOrDefault();
        return command?.ModuleId ?? "";
    }

    private static string PrettyJson(Struct value)
    {
        if (value.Fields.Count == 0)
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value.ToString());
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return value.ToString();
        }
    }

    private static DateTimeOffset ToDateTimeOffsetOrMin(Timestamp? timestamp)
    {
        return timestamp is null ? DateTimeOffset.MinValue : timestamp.ToDateTimeOffset();
    }

    private static bool MatchesRealScreenFilter(RealScreen screen, string filter)
    {
        return string.IsNullOrWhiteSpace(filter) ||
            filter == "*" ||
            string.Equals(screen.SurfaceId, filter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screen.Page, filter, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(screen.Id, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInteractionScenario(string? scenario)
    {
        var normalized = string.IsNullOrWhiteSpace(scenario)
            ? DefaultInteractionScenario
            : scenario.Trim().ToLowerInvariant();
        return normalized is DefaultInteractionScenario or "scroll" or "filter" or "detail" or "activation"
            ? normalized
            : throw new ArgumentOutOfRangeException(
                nameof(scenario),
                scenario,
                "Interaction scenario must be one of: default, scroll, filter, detail, activation.");
    }

    private static string ModeForDataSource(string dataSource)
    {
        if (dataSource.Contains("live-service", StringComparison.OrdinalIgnoreCase))
        {
            return "live-service";
        }

        if (dataSource.Contains("runner-hostcontrol", StringComparison.OrdinalIgnoreCase))
        {
            return "live-runner";
        }

        if (dataSource.Contains("fixture-hostcontrol", StringComparison.OrdinalIgnoreCase))
        {
            return "fixture-hostcontrol";
        }

        return "fixture";
    }

    private sealed record ScreenshotManifestStats(int ModuleCount, int CommandCount, bool RunnerConnected);

    private sealed record RenderedFrame(int Width, int Height, IReadOnlyList<string> InteractionSteps);

    private sealed record InteractionResult(Window CaptureTarget, IReadOnlyList<string> Steps);

    private sealed record RealScreen(string Id, string Page, string SurfaceId, string Title, Func<Control> CreateView);
}

public sealed record ShellHostControlSnapshotData(
    string DataSource,
    HostProto.DashboardSnapshot Dashboard,
    HostProto.ListCommandsResponse Commands,
    HostProto.ListModulesResponse Modules,
    HostProto.ModuleSummary? SelectedModule,
    HostProto.ModuleDetail ModuleDetail,
    HostProto.SettingsSchema SettingsSchema,
    HostProto.SettingsSnapshot Settings,
    IReadOnlyList<HostProto.LogEntry> Logs,
    HostProto.ListNotificationsResponse Notifications,
    HostProto.ListPackagesResponse Packages,
    HostProto.RuntimeDiagnostics Diagnostics,
    HostProto.ListBrokerAuditResponse BrokerAudit)
{
    public static async Task<ShellHostControlSnapshotData> LoadAsync(
        HostControlClient client,
        string dataSource,
        CancellationToken cancellationToken)
    {
        var dashboard = await client.GetDashboardSnapshotAsync(cancellationToken);
        var modules = await client.ListModulesAsync(cancellationToken);
        var commands = await client.ListCommandsAsync("", cancellationToken);
        var selected = PickModule(modules);
        var detail = selected is null
            ? new HostProto.ModuleDetail()
            : await client.GetModuleDetailAsync(selected.ModuleId, cancellationToken);
        var settingsSchema = selected is null
            ? new HostProto.SettingsSchema()
            : await client.GetSettingsSchemaAsync(selected.ModuleId, cancellationToken);
        var settings = selected is null
            ? new HostProto.SettingsSnapshot { UpdatedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) }
            : await client.GetSettingsAsync(selected.ModuleId, cancellationToken);
        var logs = selected is null
            ? Array.Empty<HostProto.LogEntry>()
            : await client.TailLogsAsync(selected.ModuleId, cancellationToken);
        var notifications = await client.ListNotificationsAsync(80, cancellationToken: cancellationToken);
        var packages = await client.ListPackagesAsync(cancellationToken);
        var diagnostics = await client.GetRuntimeDiagnosticsAsync(cancellationToken);
        var audit = await client.ListBrokerAuditAsync(6, cancellationToken: cancellationToken);

        return new ShellHostControlSnapshotData(
            dataSource,
            dashboard,
            commands,
            modules,
            selected,
            detail,
            settingsSchema,
            settings,
            logs,
            notifications,
            packages,
            diagnostics,
            audit);
    }

    private static HostProto.ModuleSummary? PickModule(HostProto.ListModulesResponse modules)
    {
        return modules.Modules
            .Where(module => module.Enabled)
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? modules.Modules
                .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }
}
