namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// Complete platform capability composition consumed by host processes. Platform-specific
/// implementations stay behind this boundary so the Runner and Shell never select native
/// providers individually.
/// </summary>
public interface IPlatformPack
{
    PlatformId Platform { get; }
    ICapabilityRegistry Capabilities { get; }
    IDisplayService Display { get; }
    ITrayService Tray { get; }
    ISecretStore Secrets { get; }
    INotificationService Notifications { get; }
    IClipboardImageService ClipboardImages { get; }
    IAutostartService Autostart { get; }
    IServiceManager Services { get; }
    INetworkBroker Network { get; }
    IHotkeyService Hotkeys { get; }
    IPrivilegeBroker Privileges { get; }
    IProcessService Processes { get; }
    ILocalIpc LocalIpc { get; }
    PlatformTrayHost TrayHost { get; }
}

public enum PlatformTrayHost
{
    Runner,
    Shell
}
