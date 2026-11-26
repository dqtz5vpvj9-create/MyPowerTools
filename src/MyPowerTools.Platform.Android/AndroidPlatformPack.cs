using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Android;

public sealed class AndroidPlatformPack : IPlatformPack
{
    private static readonly PlatformId CurrentPlatform = new("android", PlatformId.Current().Architecture);

    public PlatformId Platform => CurrentPlatform;
    public PlatformTrayHost TrayHost => PlatformTrayHost.Shell;
    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry([
        new("secret.store", "sensitive", true, "Android Keystore", "App-scoped encrypted credentials."),
        new("background.activity", "user", true, "Android foreground service", "User-started background work with a persistent notification."),
        new("files.downloads", "user", true, "Android MediaStore", "Publish completed files into Downloads/MPT."),
        new("notification.desktop", "user", true, "Android notifications", "Notification channels and user-controlled notification permission."),
        new("clipboard.image", "sensitive", true, "Android clipboard", "Read copied image content while the app is in the foreground."),
        new("network.ssh", "user", false, "Android", "An Android SSH provider has not been installed."),
        new("service.user", "user", false, "Android", "Desktop process service units require an Android implementation.")
    ]);
    public ISecretStore Secrets { get; } = new AndroidSecretStore();
    public IBackgroundActivityService Background { get; } = new AndroidBackgroundActivityService();
    public IDownloadsService Downloads { get; } = new AndroidDownloadsService();
    public IDisplayService Display => AndroidUnsupportedServices.Display;
    public ITrayService Tray => AndroidUnsupportedServices.Tray;
    public INotificationService Notifications { get; } = new AndroidNotificationService();
    public IClipboardImageService ClipboardImages { get; } = new AndroidClipboardService();
    public IKeyboardShortcutService KeyboardShortcuts => AndroidUnsupportedServices.KeyboardShortcuts;
    public IAutostartService Autostart => AndroidUnsupportedServices.Autostart;
    public IServiceManager Services => AndroidUnsupportedServices.Services;
    public INetworkBroker Network => AndroidUnsupportedServices.Network;
    public IHotkeyService Hotkeys => AndroidUnsupportedServices.Hotkeys;
    public IPrivilegeBroker Privileges => AndroidUnsupportedServices.Privileges;
    public IProcessService Processes => AndroidUnsupportedServices.Processes;
    public ILocalIpc LocalIpc { get; } = new LocalIpcService(CurrentPlatform);
}
