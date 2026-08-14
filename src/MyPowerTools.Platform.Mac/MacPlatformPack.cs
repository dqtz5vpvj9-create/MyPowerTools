using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacPlatformPack : IPlatformPack
{
    private static readonly PlatformId CurrentPlatform = new("macos", PlatformId.Current().Architecture);

    public PlatformId Platform => CurrentPlatform;
    public PlatformTrayHost TrayHost => PlatformTrayHost.Shell;

    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", true, "macOS Status Item", "Native NSStatusItem provider with Codex quota monitoring is available in the Shell UI host."),
        new("hotkey.global", "user", false, "Event tap", "Provider compiles but native implementation is pending."),
        new("notification.desktop", "user", true, "UserNotifications", "Native UserNotifications provider available."),
        new("clipboard.image", "sensitive", true, "NSPasteboard", "Native NSPasteboard image and text provider available."),
        new("keyboard.shortcut", "user", false, "CoreGraphics", "Provider compiles but native implementation is pending."),
        new("network.ssh", "user", true, "macOS OpenSSH", "The system /usr/bin/ssh client is used for SSH transfers."),
        new("web.surface", "user", true, "WKWebView", "Native WKWebView surface provider available."),
        new("autostart.user", "user", true, "launchd agent", "Per-user launchd agent provider available."),
        new("service.user", "user", true, "launchd agent", "Per-user launchd service provider available."),
        new("service.system", "elevated", false, "launchd daemon", "Provider compiles but native implementation is pending."),
        new("privilege.elevated", "elevated", false, "privileged helper", "Provider compiles but native implementation is pending."),
        new("display.profile", "user", false, "CoreGraphics", "Provider compiles but native implementation is pending."),
        new("network.portForwarding", "elevated", false, "pfctl", "Provider compiles but native implementation is pending."),
        new("ipc.local", "user", true, "Unix domain socket", "UDS IPC available."),
        new("secret.store", "sensitive", true, "Keychain", "Per-user macOS Keychain provider available."),
        new("process.inspect", "user", true, "managed process API", "Basic process inspection is available through the managed runtime."),
        new("adb.device", "user", false, "adb CLI", "Provider compiles but adb discovery is pending.")
    ]);

    public IDisplayService Display { get; } = new UnsupportedDisplayService("CoreGraphics", "macOS display provider compiles; native DDC/CoreGraphics implementation is pending.");
    public ITrayService Tray { get; } = new MacStatusItemTrayService();
    public ISecretStore Secrets { get; } = new MacKeychainSecretStore();
    public INotificationService Notifications { get; } = new MacUserNotificationService();
    public IClipboardImageService ClipboardImages { get; } = new MacPasteboardImageService();
    public IKeyboardShortcutService KeyboardShortcuts { get; } = new UnsupportedKeyboardShortcutService("CoreGraphics", "macOS keyboard shortcut provider compiles; native implementation is pending.");
    public IAutostartService Autostart { get; } = new MacLaunchdAutostartService();
    public IServiceManager Services { get; } = new MacLaunchdServiceManager();
    public INetworkBroker Network { get; } = new UnsupportedNetworkBroker("pfctl", "macOS network broker compiles; native implementation is pending.");
    public IHotkeyService Hotkeys { get; } = new UnsupportedHotkeyService("Event tap", "macOS global hotkey provider compiles; native implementation is pending.");
    public IPrivilegeBroker Privileges { get; } = new UnsupportedPrivilegeBroker("privileged helper", "macOS privileged helper provider compiles; native implementation is pending.");
    public IProcessService Processes { get; } = new ManagedProcessService();
    public ILocalIpc LocalIpc { get; } = new LocalIpcService(CurrentPlatform);
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
