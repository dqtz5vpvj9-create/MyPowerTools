namespace FileTransfer.Core.Discovery;

/// <summary>
/// A device candidate or a discovered device. The five fields are exactly the record named by
/// docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md, so M4 hands it to the Surface without a mapping layer.
/// </summary>
/// <param name="DeviceId">
/// MPT device id (the inbox name used by the receiver). Empty while a candidate from Tailscale or
/// from the LAN protocol has not answered the v3 identity frame yet: those sources cannot know it.
/// </param>
/// <param name="Name">Human readable device name; never a credential.</param>
/// <param name="Address">
/// Tailnet address only (100.64.0.0/10 or fd7a:115c:a1e0::/48). Loopback is accepted for tests and
/// for a same-machine receiver. A public address is rejected before any socket is opened.
/// </param>
/// <param name="Port">MPT receiver port. The production default is <see cref="TransferFiles.Port"/>.</param>
/// <param name="Platform">windows, macos, linux, android or ios; empty when a source cannot tell.</param>
public sealed record DiscoveredDevice(string DeviceId, string Name, string Address, int Port, string Platform);
