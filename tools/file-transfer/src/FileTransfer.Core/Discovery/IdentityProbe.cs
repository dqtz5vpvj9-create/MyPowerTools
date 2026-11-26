namespace FileTransfer.Core.Discovery;

/// <summary>Result class of one v3 hello exchange.</summary>
public enum IdentityProbeStatus
{
    /// <summary>The device answered a complete v3 identity frame and, when it was a paired candidate, proved the expected id.</summary>
    Available = 0,
    /// <summary>The endpoint answered, but its device id is not the paired device's id; never send to it.</summary>
    IdentityMismatch = 1,
    /// <summary>The endpoint refused the identity query (older receiver, rotated secret, or a stranger MPT receiver).</summary>
    Rejected = 2,
    /// <summary>The endpoint answered something that is not a complete v3 identity frame.</summary>
    LegacyProtocol = 3,
    /// <summary>No usable answer: refused connection, timeout, or a broken frame.</summary>
    Unreachable = 4,
    /// <summary>Refused locally before any socket was opened (public address, bad port, self).</summary>
    InvalidCandidate = 5,
}

/// <summary>
/// One identity probe result. <see cref="Device"/> on <see cref="IdentityProbeStatus.Available"/>
/// carries the peer's own id, name and platform together with the address and port that actually
/// answered, so M4 can send to a proven endpoint.
/// </summary>
public sealed record IdentityProbeResult(IdentityProbeStatus Status, DiscoveredDevice Device, string Message)
{
    public bool Available => Status == IdentityProbeStatus.Available;

    public static IdentityProbeResult Invalid(DiscoveryCandidate candidate, string message) =>
        new(IdentityProbeStatus.InvalidCandidate, candidate.Device, message);
}

/// <summary>
/// Injectable identity probe. The default is <see cref="HelloIdentityProbe"/>, which speaks the
/// contract's protocol v3 hello over TCP.
/// </summary>
public interface IIdentityProbe
{
    Task<IdentityProbeResult> ProbeAsync(DiscoveryCandidate candidate, CancellationToken token);
}
