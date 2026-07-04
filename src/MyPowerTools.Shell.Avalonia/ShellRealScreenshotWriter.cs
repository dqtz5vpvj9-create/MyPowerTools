using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Styling;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MyPowerTools.Shell.Avalonia;

public static class ShellRealScreenshotWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static bool _applicationInitialized;

    public static string WriteSnapshotSet(string outputDirectory, string theme, string size, string density)
    {
        Directory.CreateDirectory(outputDirectory);
        var (width, height) = ParseSize(size);
        EnsureApplication(theme);

        var entries = new JsonArray();
        foreach (var screen in CreateScreens())
        {
            var fileName = $"real-{screen.Id}.{Sanitize(theme)}.{Sanitize(density)}.{Sanitize(size)}.png";
            var path = Path.Combine(outputDirectory, fileName);
            ApplyTheme(theme);
            Render(screen.CreateView(), path, width, height);
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

            entries.Add(new JsonObject
            {
                ["screenId"] = screen.Id,
                ["title"] = screen.Title,
                ["fileName"] = fileName,
                ["sha256"] = sha256,
                ["width"] = width,
                ["height"] = height,
                ["renderer"] = "Avalonia.Headless"
            });
        }

        var manifestPath = Path.Combine(outputDirectory, "shell-real-screenshot-manifest.json");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["artifactKind"] = "real-avalonia-screenshot",
            ["theme"] = theme,
            ["density"] = density,
            ["size"] = size,
            ["screenshotCount"] = entries.Count,
            ["screenshots"] = entries
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(JsonOptions));
        return manifestPath;
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
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }

    private static void Render(Control view, string path, int width, int height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            ShowInTaskbar = false,
            CanResize = false
        };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
        var bitmap = window.CaptureRenderedFrame() ?? window.GetLastRenderedFrame()
            ?? throw new InvalidOperationException("Avalonia Headless did not produce a rendered frame.");
        using (var stream = File.Create(path))
        {
            bitmap.Save(stream);
        }

        window.Close();
    }

    private static IReadOnlyList<RealScreen> CreateScreens()
    {
        return
        [
            new("dashboard", "Dashboard", () => new DashboardView { DataContext = SampleDashboard() }),
            new("command-palette-with-params", "Command Palette With Parameters", () => new CommandPaletteView { DataContext = SampleCommandPalette() }),
            new("settings-dirty-state", "Settings Dirty State", () => new SettingsCenterView { DataContext = SampleSettings() }),
            new("module-detail-degraded", "Module Detail Degraded", () => new ModuleDetailView { DataContext = SampleModuleDetail() }),
            new("logs-long-lines", "Logs Long Lines", () => new LogsView { DataContext = SampleLogs() }),
            new("notifications-list", "Notifications List", () => new NotificationsView { DataContext = SampleNotifications() }),
            new("packages", "Packages", () => new PackageManagerView { DataContext = SamplePackages() }),
            new("diagnostics-wide", "Diagnostics Wide", () => new DiagnosticsView { DataContext = SampleDiagnostics() })
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
        var command = new CommandItemViewModel(
            "adb-forwarder.portproxy.apply",
            "adb-forwarder",
            "Apply port proxy plan",
            "Writes selected ADB forwarding rules through the broker.",
            "elevated",
            true,
            "AdbForwarder",
            "broker approval required",
            "",
            true,
            null,
            null,
            [
                new CommandParameterViewModel("listenPort", "Listen port", "integer", true, "5555"),
                new CommandParameterViewModel("connectAddress", "Connect address", "string", true, "127.0.0.1"),
                new CommandParameterViewModel("dryRun", "Dry run", "boolean", false, "true")
            ]);
        return new CommandPaletteViewModel("port proxy", [command]);
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
        return new ShellActionViewModel(commandId, title, style, Command());
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

    private sealed record RealScreen(string Id, string Title, Func<Control> CreateView);
}
