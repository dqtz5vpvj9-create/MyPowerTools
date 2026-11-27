namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>Ordered feed endpoints: Tailscale private endpoint first, then public proxy and direct fallback.</summary>
public sealed record MobileModuleUpdateSourceConfig(
    string TailscaleBaseUrl,
    string PublicProxyBaseUrl,
    string DirectFallbackBaseUrl)
{
    public const string DefaultTailscaleBaseUrl = "http://mpt-relay.tail.lixinrui000.cn/mpt/android-modules";
    public const string DefaultPublicProxyBaseUrl = "https://gh-proxy.org/https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/android-modules-latest";
    public const string DefaultDirectFallbackBaseUrl = "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/download/android-modules-latest";

    public static MobileModuleUpdateSourceConfig CreateDefault() => new(
        DefaultTailscaleBaseUrl,
        DefaultPublicProxyBaseUrl,
        DefaultDirectFallbackBaseUrl);
}

public sealed record MobileModuleUpdateState(
    string Summary,
    string Detail,
    bool HasOverrides,
    bool IsBusy,
    IReadOnlyList<string> InstalledOverrides);

public sealed record MobileModuleUpdateResult(
    bool Succeeded,
    string SourceLabel,
    string SourceUrl,
    IReadOnlyList<string> UpdatedModules,
    long DownloadedBytes,
    string Message);

public interface IMobileModuleUpdater
{
    MobileModuleUpdateSourceConfig GetConfig();
    void SaveConfig(MobileModuleUpdateSourceConfig config);
    MobileModuleUpdateState GetState();
    Task<MobileModuleUpdateResult> CheckAndApplyAsync(CancellationToken cancellationToken = default);
    Task<MobileModuleUpdateResult> ResetToBundledAsync(CancellationToken cancellationToken = default);
}

/// <summary>Host-registered bridge so MobileShell stays free of Android SDK references.</summary>
public static class MobileModuleUpdateBridge
{
    public static IMobileModuleUpdater? Current { get; set; }
}
