using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacPlatformPack
{
    private static readonly PlatformId Platform = new("macos", PlatformId.Current().Architecture);

    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", false, "macOS Status Item", "Provider compiles but native implementation is pending."),
        new("hotkey.global", "user", false, "Event tap", "Provider compiles but native implementation is pending."),
        new("notification.desktop", "user", false, "UserNotifications", "Provider compiles but native implementation is pending."),
        new("autostart.user", "user", false, "launchd agent", "Provider compiles but native implementation is pending."),
        new("service.user", "user", false, "launchd agent", "Provider compiles but native implementation is pending."),
        new("service.system", "elevated", false, "launchd daemon", "Provider compiles but native implementation is pending."),
        new("privilege.elevated", "elevated", false, "privileged helper", "Provider compiles but native implementation is pending."),
        new("display.profile", "user", false, "CoreGraphics", "Provider compiles but native implementation is pending."),
        new("network.portForwarding", "elevated", false, "pfctl", "Provider compiles but native implementation is pending."),
        new("ipc.local", "user", true, "Unix domain socket", "UDS IPC available."),
        new("secret.store", "sensitive", false, "Keychain", "Provider compiles but native implementation is pending."),
        new("process.inspect", "user", true, "managed process API", "Basic process inspection is available through the managed runtime."),
        new("adb.device", "user", false, "adb CLI", "Provider compiles but adb discovery is pending.")
    ]);

    public IDisplayService Display { get; } = new UnsupportedDisplayService("CoreGraphics", "macOS display provider compiles; native DDC/CoreGraphics implementation is pending.");
    public ITrayService Tray { get; } = new UnsupportedTrayService("macOS Status Item", "Native status item integration is pending.");
    public ISecretStore Secrets { get; } = new UnsupportedSecretStore("Keychain", "macOS Keychain provider compiles; native implementation is pending.");
    public INotificationService Notifications { get; } = new UnsupportedNotificationService("UserNotifications", "macOS notification provider compiles; native implementation is pending.");
    public IAutostartService Autostart { get; } = new UnsupportedAutostartService("launchd agent", "macOS launchd autostart provider compiles; native implementation is pending.");
    public IServiceManager Services { get; } = new UnsupportedServiceManager("launchd", "macOS launchd service provider compiles; native implementation is pending.");
    public INetworkBroker Network { get; } = new UnsupportedNetworkBroker("pfctl", "macOS network broker compiles; native implementation is pending.");
    public IHotkeyService Hotkeys { get; } = new UnsupportedHotkeyService("Event tap", "macOS global hotkey provider compiles; native implementation is pending.");
    public IPrivilegeBroker Privileges { get; } = new UnsupportedPrivilegeBroker("privileged helper", "macOS privileged helper provider compiles; native implementation is pending.");
    public IProcessService Processes { get; } = new ManagedProcessService();
    public ILocalIpc LocalIpc { get; } = new LocalIpcService(Platform);
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
