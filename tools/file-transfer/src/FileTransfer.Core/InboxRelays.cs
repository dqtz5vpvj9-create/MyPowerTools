namespace FileTransfer.Core;

/// <summary>Fixed inbox transports. A route id is persisted; credentials never choose a URL.</summary>
public sealed record InboxRelay(string Id, Uri Address);

public static class InboxRelays
{
    public const string Tail = "tail";
    public const string Public = "public";
    public static readonly Uri TailAddress = new("http://mpt-relay.tail.lixinrui000.cn");
    internal static Func<IReadOnlyList<InboxRelay>>? EndpointsOverride { get; set; }
    internal static TimeSpan? FallbackDelayOverride { get; set; }
    public static TimeSpan FallbackDelay => FallbackDelayOverride ?? TimeSpan.FromSeconds(30);

    public static IReadOnlyList<InboxRelay> All => EndpointsOverride?.Invoke()
        // Existing test relay seams remain local and never dial the production Tail endpoint.
        ?? (PublicRelayClient.BaseAddressOverride is not null
            ? [new(Public, PublicRelayClient.BaseAddress)]
            : [new(Tail, TailAddress), new(Public, PublicRelayClient.ProductionBaseAddress)]);

    public static bool IsTrustedHttp(Uri address) => address.Scheme == Uri.UriSchemeHttp
        && address.Host.Equals(TailAddress.Host, StringComparison.OrdinalIgnoreCase)
        && address.Port == TailAddress.Port && string.IsNullOrEmpty(address.UserInfo);
}
