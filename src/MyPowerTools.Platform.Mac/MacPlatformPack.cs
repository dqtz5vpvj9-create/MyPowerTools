using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacPlatformPack : IPlatformPack
{
    private static readonly PlatformId CurrentPlatform = new("macos", PlatformId.Current().Architecture);
    private readonly Lazy<IHotkeyService> _hotkeys = new(CreateHotkeyService);
    private readonly Lazy<IKeyboardShortcutService> _keyboardShortcuts = new(CreateKeyboardShortcutService);

    public PlatformId Platform => CurrentPlatform;
    public PlatformTrayHost TrayHost => PlatformTrayHost.Shell;

    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", true, "macOS Status Item", "Native NSStatusItem provider with Codex quota monitoring is available in the Shell UI host."),
        new("hotkey.global", "user", true, "Carbon RegisterEventHotKey", "Global hotkey provider registers Carbon hot keys on a dedicated CFRunLoop thread."),
        new("notification.desktop", "user", true, "UserNotifications", "Native UserNotifications provider available."),
        new("clipboard.image", "sensitive", true, "NSPasteboard", "Native NSPasteboard image and text provider available."),
        new("keyboard.shortcut", "user", true, "CoreGraphics CGEvent", "Sends configurable shortcuts to the frontmost application; requires Accessibility approval."),
        new("network.ssh", "user", true, "macOS OpenSSH", "The system /usr/bin/ssh client is used for SSH transfers."),
        new("web.surface", "user", true, "WKWebView", "Native WKWebView surface provider available."),
        new("autostart.user", "user", true, "launchd agent", "Per-user launchd agent provider available."),
        new("service.user", "user", true, "launchd agent", "Per-user launchd service provider available."),
        new("service.system", "elevated", false, "launchd daemon", "Provider compiles but native implementation is pending."),
        new("privilege.elevated", "elevated", false, "privileged helper", "Provider compiles but native implementation is pending."),
        new("display.profile", "user", true, "CoreGraphics", "Display capability is routed to module native hosts."),
        new("network.portForwarding", "elevated", false, "pfctl", "Provider compiles but native implementation is pending."),
        new("ipc.local", "user", true, "Unix domain socket", "UDS IPC available."),
        new("secret.store", "sensitive", true, "Keychain", "Per-user macOS Keychain provider available."),
        new("process.inspect", "user", true, "managed process API", "Basic process inspection is available through the managed runtime."),
        new("adb.devices", "user", true, "adb CLI", "adb is resolved from PATH and the well-known macOS SDK locations.")
    ]);

    public IDisplayService Display { get; } = new UnsupportedDisplayService("CoreGraphics", "macOS display provider compiles; native DDC/CoreGraphics implementation is pending.");
    public ITrayService Tray { get; } = new MacStatusItemTrayService();
    public ISecretStore Secrets { get; } = new MacKeychainSecretStore();
    public INotificationService Notifications { get; } = new MacUserNotificationService();
    public IClipboardImageService ClipboardImages { get; } = new MacPasteboardImageService();
    public IKeyboardShortcutService KeyboardShortcuts => _keyboardShortcuts.Value;
    public IAutostartService Autostart { get; } = new MacLaunchdAutostartService();
    public IServiceManager Services { get; } = new MacLaunchdServiceManager();
    public INetworkBroker Network { get; } = new UnsupportedNetworkBroker("pfctl", "macOS network broker compiles; native implementation is pending.");
    public IHotkeyService Hotkeys => _hotkeys.Value;
    public IPrivilegeBroker Privileges { get; } = new UnsupportedPrivilegeBroker("privileged helper", "macOS privileged helper provider compiles; native implementation is pending.");
    public IProcessService Processes { get; } = new ManagedProcessService();
    public ILocalIpc LocalIpc { get; } = new LocalIpcService(CurrentPlatform);
    public MacAdbDeviceProvider AdbDevices { get; } = new();

    private static IHotkeyService CreateHotkeyService()
    {
        return OperatingSystem.IsMacOS()
            ? new MacGlobalHotkeyService()
            : new UnsupportedHotkeyService("Carbon RegisterEventHotKey", "macOS global hotkeys require the Carbon framework.");
    }

    private static IKeyboardShortcutService CreateKeyboardShortcutService()
    {
        return OperatingSystem.IsMacOS()
            ? new MacKeyboardShortcutService()
            : new UnsupportedKeyboardShortcutService("CoreGraphics CGEvent", "macOS shortcut injection requires the ApplicationServices framework.");
    }
}

public sealed class UnsupportedDisplayService : IDisplayService
{
    private readonly string _provider;
    private readonly string _message;

    public UnsupportedDisplayService(string provider, string message)
    {
        _provider = provider;
        _message = message;
    }

    public Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DisplaySnapshot> displays =
        [
            new(_provider, _provider, "unsupported", 0, 0, 0, "unknown", false, _message)
        ];
        return Task.FromResult(displays);
    }

    public Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new DisplayWriterStatus(false, "unsupported", _message));
    }

    public Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken)
    {
        return Task.FromResult(new BrokerOperationResult(false, "unsupported", _message));
    }
}
