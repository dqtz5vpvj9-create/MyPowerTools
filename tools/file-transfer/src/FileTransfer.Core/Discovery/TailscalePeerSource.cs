namespace FileTransfer.Core.Discovery;

/// <summary>
/// Turns the tailnet peer list into anonymous candidates. Tailscale knows a peer's node key, host
/// name, OS and Tailnet addresses, but not its MPT device id, so every candidate starts with an empty
/// <see cref="DiscoveredDevice.DeviceId"/> and stays unpaired until the v3 hello proves which MPT
/// device answered. Peers that are online rank before offline ones, and a peer that never answers
/// simply never becomes a device.
/// </summary>
public sealed class TailscalePeerSource : IDiscoveryCandidateSource
{
    private readonly TailscaleStatusReader _reader;

    public TailscalePeerSource(TailscaleStatusReader? reader = null) => _reader = reader ?? new TailscaleStatusReader();

    public string Name => "Tailscale";

    public async Task<CandidateSourceResult> CollectAsync(DiscoveryRequest request, CancellationToken token)
    {
        var read = await _reader.ReadAsync(token);
        if (read.Status is null)
            return new CandidateSourceResult([], Prefix(read, "Tailscale"), false);

        var options = request.Options;
        var diagnostics = new List<string>(read.Diagnostics);
        var candidates = new List<DiscoveryCandidate>();
        var skipped = 0;
        foreach (var peer in read.Status.Peers)
        {
            var address = Preferred(peer.Addresses);
            if (address is null) { skipped++; continue; }
            if (options.LocalDevice is not null &&
                DiscoveryAddresses.Normalize(options.LocalDevice.Address) == DiscoveryAddresses.Normalize(address))
                continue;
            var name = DiscoveryAddresses.IsValidName(peer.HostName) ? peer.HostName : "Tailscale 设备";
            candidates.Add(new DiscoveryCandidate(
                new DiscoveredDevice("", name, address, options.Port, Platform(peer.Os)),
                false,
                DiscoverySource.Tailscale)
            {
                Rank = peer.Online || peer.Active ? 2 : 3,
                Hint = peer.Online || peer.Active ? "Tailscale 在线" : "Tailscale 离线",
            });
        }

        if (!read.Status.Running)
            diagnostics.Add($"Tailscale 后端状态是 {read.Status.BackendState}，节点可能都无法应答。");
        if (skipped > 0)
            diagnostics.Add($"{skipped} 个 Tailscale 节点没有可用的 Tailnet 地址，已跳过。");
        diagnostics.Add(candidates.Count == 0
            ? "Tailscale 上没有其他节点。"
            : $"Tailscale 返回 {candidates.Count} 个候选节点。");
        return new CandidateSourceResult(candidates, diagnostics, true);
    }

    /// <summary>Tailscale's own OS values map onto the platform string MPT shows.</summary>
    public static string Platform(string? os) => os?.Trim().ToLowerInvariant() switch
    {
        "windows" => "windows",
        "macos" => "macos",
        "linux" => "linux",
        "android" => "android",
        "ios" => "ios",
        var other when other is not null && DiscoveryAddresses.IsValidPlatform(other) => other,
        _ => "",
    };

    private static string? Preferred(IReadOnlyList<string> addresses) =>
        addresses.FirstOrDefault(address => DiscoveryAddresses.IsUsableCandidate(address) && address.Contains('.'))
        ?? addresses.FirstOrDefault(DiscoveryAddresses.IsUsableCandidate);

    private static IReadOnlyList<string> Prefix(TailscaleReadResult read, string source)
    {
        var diagnostics = new List<string> { $"{source} 不可用，本次没有 Tailnet 候选。" };
        diagnostics.AddRange(read.Diagnostics);
        return diagnostics;
    }
}
