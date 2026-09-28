namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>
/// Honest stand-in used until the paired-device service is wired into the phone shell. It reports no
/// local device, no peers, no relay and no transfer history, and every write fails with a clear
/// message, so the device pages show a real empty state instead of example equipment.
/// </summary>
/// <remarks>
/// Integration point for the device task: replace <c>new UnavailableMobileDeviceService()</c> in
/// <see cref="MobileShellServices.CreateDefault"/> with <c>new MobileDeviceService()</c> once
/// <c>Services/Mobile/MobileDeviceService.cs</c> lands. The shell only consumes
/// <see cref="IMobileDeviceService"/> and <see cref="MobileDeviceModels"/>, so no view changes are
/// needed.
/// </remarks>
public sealed class UnavailableMobileDeviceService : IMobileDeviceService
{
    public const string NotConnectedNotice =
        "设备服务尚未接入：暂时无法读取配对设备、网盘中转和传输记录。";

    private const string WriteFailure = "设备服务尚未接入，无法完成该操作。";

    public Task<MobileDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new MobileDeviceSnapshot(
            LocalDeviceName: "",
            Receiving: false,
            Peers: [],
            RelayConfigured: false,
            RelayRunning: false,
            RelayDescription: null,
            Activities: [],
            Notice: NotConnectedNotice));

    public Task ImportPairingAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(WriteFailure));

    public Task RemovePeerAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromException(new InvalidOperationException(WriteFailure));

    public Task<string> GetPairingCodeAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(new InvalidOperationException(WriteFailure));

    public Task<MobilePeerInfo> CheckPeerAsync(string deviceId, CancellationToken cancellationToken = default) =>
        Task.FromException<MobilePeerInfo>(new InvalidOperationException(WriteFailure));
}
