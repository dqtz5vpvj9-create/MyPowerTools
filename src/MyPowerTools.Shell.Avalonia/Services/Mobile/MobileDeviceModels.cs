namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

public enum MobilePeerConnectionState
{
    Unknown,
    Online,
    Offline
}

/// <summary>A remembered device; an address alone does not establish reachability or control permission.</summary>
public sealed record MobilePeerInfo(
    string DeviceId,
    string Name,
    string Address,
    MobilePeerConnectionState ConnectionState,
    DateTimeOffset? CheckedAt = null,
    bool SupportsToolControl = false);

public sealed record MobileTransferActivity(
    string Id,
    string Name,
    string State,
    string Direction,
    string? PeerName,
    long Bytes,
    DateTimeOffset? Timestamp,
    string? Message = null);

public sealed record MobileDeviceSnapshot(
    string LocalDeviceName,
    bool Receiving,
    IReadOnlyList<MobilePeerInfo> Peers,
    bool RelayConfigured,
    bool RelayRunning,
    string? RelayDescription,
    IReadOnlyList<MobileTransferActivity> Activities,
    string? Notice = null);

/// <summary>Mobile presentation facade over the existing module command and HostControl boundary.</summary>
public interface IMobileDeviceService
{
    Task<MobileDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task ImportPairingAsync(string code, CancellationToken cancellationToken = default);
    Task RemovePeerAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<string> GetPairingCodeAsync(CancellationToken cancellationToken = default);
    Task<MobilePeerInfo> CheckPeerAsync(string deviceId, CancellationToken cancellationToken = default);
}
