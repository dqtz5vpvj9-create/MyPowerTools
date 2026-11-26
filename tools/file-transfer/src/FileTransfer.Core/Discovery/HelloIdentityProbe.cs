using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// The protocol v3 hello client: TCP connect, one hello frame, one answer, then close. It never
/// writes anything on the peer and never sends a pairing secret.
/// <para>
/// A device becomes <see cref="IdentityProbeStatus.Available"/> only here, and a paired candidate
/// additionally has to prove the expected device id, so a stale Tailnet address that now belongs to
/// somebody else cannot be presented as the remembered device.
/// </para>
/// </summary>
public sealed class HelloIdentityProbe : IIdentityProbe
{
    private readonly DiscoveryOptions _options;

    public HelloIdentityProbe(DiscoveryOptions? options = null) => _options = options ?? DiscoveryOptions.Default;

    public async Task<IdentityProbeResult> ProbeAsync(DiscoveryCandidate candidate, CancellationToken token)
    {
        var device = candidate.Device;
        // Refuse locally: a public or malformed candidate never opens a socket.
        if (!DiscoveryAddresses.TryParse(device.Address, out var ip) || !DiscoveryAddresses.IsTailnetOrLoopback(ip))
            return IdentityProbeResult.Invalid(candidate, "地址不是 Tailnet 地址，已跳过身份查询。");
        if (!DiscoveryAddresses.IsValidPort(device.Port))
            return IdentityProbeResult.Invalid(candidate, "端口无效，已跳过身份查询。");
        if (_options.SelfDeviceId.Length > 0 && device.DeviceId == _options.SelfDeviceId)
            return IdentityProbeResult.Invalid(candidate, "这是本机自己的地址，已跳过身份查询。");

        try
        {
            using var client = new TcpClient(ip.AddressFamily);
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(_options.ConnectTimeout);
                await client.ConnectAsync(ip, device.Port, connect.Token);
            }

            using var exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
            exchange.CancelAfter(_options.ProbeTimeout);
            var stream = client.GetStream();
            await HelloProtocol.WriteRequestAsync(stream, exchange.Token);
            var reply = await HelloProtocol.ReadReplyAsync(stream, exchange.Token);

            if (!reply.Ok)
                return new IdentityProbeResult(IdentityProbeStatus.Rejected, device,
                    string.IsNullOrWhiteSpace(reply.Message)
                        ? "对方拒绝了身份查询；可能是旧版接收器，或接收密钥已变更。"
                        : reply.Message!);

            var invalid = HelloProtocol.ValidateReply(reply);
            if (invalid.Length > 0)
                return new IdentityProbeResult(IdentityProbeStatus.LegacyProtocol, device,
                    "对方应答不是完整的 v3 身份帧：" + invalid);

            if (candidate.Paired && device.DeviceId.Length > 0 && reply.DeviceId != device.DeviceId)
                return new IdentityProbeResult(IdentityProbeStatus.IdentityMismatch, device,
                    $"该地址应答的设备是 {reply.Name}（{reply.DeviceId}），不是已配对的 {device.Name}；请重新导入对方连接码。");

            return new IdentityProbeResult(IdentityProbeStatus.Available,
                HelloProtocol.ToDevice(reply, device.Address, device.Port),
                $"对方接收服务已应答（{reply.Name}）。");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new IdentityProbeResult(IdentityProbeStatus.Unreachable, device,
                $"对方在 {_options.ProbeTimeout.TotalSeconds:0.#} 秒内没有完成身份应答。");
        }
        catch (SocketException)
        {
            return new IdentityProbeResult(IdentityProbeStatus.Unreachable, device,
                "无法连接对方地址；对方可能未开启接收，或不在同一网络。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ObjectDisposedException)
        {
            return new IdentityProbeResult(IdentityProbeStatus.Unreachable, device,
                "对方没有返回可识别的 MPT 身份应答：" + ex.Message);
        }
    }
}
