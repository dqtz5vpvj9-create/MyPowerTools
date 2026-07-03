using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Linux;

public sealed class LinuxPlatformPack
{
    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", false, "AppIndicator", "Provider compiles but desktop integration depends on distribution packages."),
        new("hotkey.global", "user", false, "X11/Wayland", "Provider compiles but compositor-specific implementation is pending."),
        new("notification.desktop", "user", false, "freedesktop notifications", "Provider compiles but native implementation is pending."),
        new("autostart.user", "user", false, "systemd user", "Provider compiles but native implementation is pending."),
        new("service.user", "user", false, "systemd user", "Provider compiles but native implementation is pending."),
        new("service.system", "elevated", false, "systemd", "Provider compiles but native implementation is pending."),
        new("privilege.elevated", "elevated", false, "polkit", "Provider compiles but native implementation is pending."),
        new("display.profile", "user", false, "Wayland/X11/DDC", "Provider compiles but native implementation is pending."),
        new("network.portForwarding", "elevated", false, "nftables/iptables", "Provider compiles but native implementation is pending."),
        new("ipc.local", "user", true, "Unix domain socket", "UDS IPC available."),
        new("secret.store", "sensitive", false, "Secret Service", "Provider compiles but native implementation is pending."),
        new("process.inspect", "user", true, "procfs", "Basic process inspection can be implemented from procfs."),
        new("adb.device", "user", false, "adb CLI", "Provider compiles but adb discovery is pending.")
    ]);

    public IDisplayService Display { get; } = new UnsupportedDisplayService("Wayland/X11/DDC", "Linux display provider compiles; compositor and DDC implementation is pending.");
    public ITrayService Tray { get; } = new UnsupportedTrayService("AppIndicator", "Distribution-specific AppIndicator integration is pending.");
    public ISecretStore Secrets { get; } = new UnsupportedSecretStore("Secret Service", "Linux Secret Service provider compiles; native implementation is pending.");
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
