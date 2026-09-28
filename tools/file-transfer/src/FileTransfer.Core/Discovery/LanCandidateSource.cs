using System.Net.Sockets;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// The active half of LAN discovery: sends one bounded who-is solicitation and collects the
/// <c>here</c> answers that carry the peers' Tailnet addresses. The answers are only candidates —
/// each one still has to pass the v3 hello probe before <see cref="DeviceDiscovery"/> reports it as
/// available. Files never travel over this channel.
/// </summary>
public sealed class LanCandidateSource : IDiscoveryCandidateSource
{
    private readonly LanDiscoveryChannel? _channel;

    /// <param name="channel">
    /// A shared channel (normally the running <see cref="DiscoveryBeacon"/>'s). When null, or when the
    /// shared channel is not listening, the source binds its own for the duration of the window and
    /// stops it afterwards; a channel it did not start is left running.
    /// </param>
    public LanCandidateSource(LanDiscoveryChannel? channel = null) => _channel = channel;

    public string Name => "局域网发现";

    public async Task<CandidateSourceResult> CollectAsync(DiscoveryRequest request, CancellationToken token)
    {
        var options = request.Options;
        var local = options.LocalDevice;
        if (local is null || !DiscoveryAddresses.IsValidDeviceId(local.DeviceId) || !DiscoveryAddresses.IsUsableCandidate(local.Address))
            return CandidateSourceResult.Unsupported("本机没有可用的 Tailnet 地址或设备标识，未发送局域网发现请求。");

        var owns = _channel is null || !_channel.IsListening;
        var channel = _channel ?? new LanDiscoveryChannel(options);
        var found = new Dictionary<string, DiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);
        var gate = new object();
        var nonce = LanDiscoveryProtocol.NewNonce();
        var droppedBefore = channel.DroppedDatagrams;

        void OnReceived(LanDiscoveryProtocol.Announcement announcement)
        {
            // Only a unicast answer to this solicitation counts: never a who-is, never another nonce.
            if (!announcement.IsAnswer || !string.Equals(announcement.Nonce, nonce, StringComparison.Ordinal)) return;
            if (announcement.Device.DeviceId == local.DeviceId) return;
            var key = DiscoveryAddresses.Normalize(announcement.Device.Address) + ":" + announcement.Device.Port;
            lock (gate)
            {
                if (found.ContainsKey(key)) return;
                found[key] = new DiscoveryCandidate(
                    announcement.Device,
                    request.IsKnownDeviceId(announcement.Device.DeviceId),
                    DiscoverySource.Lan)
                { Rank = 1, Hint = "局域网发现" };
            }
        }

        channel.Received += OnReceived;
        try
        {
            if (owns && !await channel.StartAsync(token))
                return CandidateSourceResult.Unsupported(channel.Fault);
            var attempts = Math.Max(1, options.LanSolicitationAttempts);
            var wait = options.LanReplyWindow / attempts;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                await channel.SendAsync(LanDiscoveryProtocol.CreateWhoIs(local, nonce), null, token);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            }
        }
        catch (OperationCanceledException)
        {
            // The window ended or the caller cancelled; whatever arrived is still a valid partial answer.
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ObjectDisposedException)
        {
            return CandidateSourceResult.Unsupported("局域网发现发送失败：" + ex.Message);
        }
        finally
        {
            channel.Received -= OnReceived;
            if (owns) await channel.StopAsync();
        }

        List<DiscoveryCandidate> candidates;
        lock (gate) candidates = found.Values.ToList();
        var diagnostics = new List<string>();
        var dropped = channel.DroppedDatagrams - droppedBefore;
        if (candidates.Count == 0)
            diagnostics.Add(dropped > 0
                ? $"局域网发现没有收到有效应答（忽略无效报文 {dropped} 条）。"
                : "局域网发现没有收到应答；对方可能没有开启“可被发现”，或当前网络阻止多播。");
        else
            diagnostics.Add($"局域网发现收到 {candidates.Count} 个候选。");
        if (dropped > 0 && candidates.Count > 0) diagnostics.Add($"局域网发现忽略无效报文 {dropped} 条。");
        return new CandidateSourceResult(candidates, diagnostics, true);
    }
}
