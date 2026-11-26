using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FileTransfer.Core;

public static class TransferFiles
{
    public const int Port = 47165;
    public const string PartialPrefix = ".mpt-";
    public const string PartialSuffix = ".part";

    /// <summary>
    /// Returns true for Tailscale CGNAT IPv4 (<c>100.64.0.0/10</c>) and Tailscale ULA IPv6
    /// (<c>fd7a:115c:a1e0::/48</c>).
    /// </summary>
    public static bool IsTailAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }

        return bytes.Length == 16
            && bytes[0] == 0xFD
            && bytes[1] == 0x7A
            && bytes[2] == 0x11
            && bytes[3] == 0x5C
            && bytes[4] == 0xA1
            && bytes[5] == 0xE0;
    }

    /// <summary>
    /// Returns true for private local-network addresses: RFC1918 IPv4 (<c>10.0.0.0/8</c>,
    /// <c>172.16.0.0/12</c>, <c>192.168.0.0/16</c>), IPv4 link-local (<c>169.254.0.0/16</c>),
    /// IPv6 unique-local (<c>fc00::/7</c>), and IPv6 link-local (<c>fe80::/10</c>).
    /// Explicitly excludes public internet addresses, unspecified (<c>0.0.0.0</c>, <c>::</c>),
    /// loopback, multicast, and broadcast addresses.
    /// </summary>
    public static bool IsLanAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast)
            || address.IsIPv6Multicast)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] is >= 224 and <= 239) return false; // IPv4 multicast 224.0.0.0/4
            return IsRfc1918Bytes(bytes)
                || (bytes[0] == 169 && bytes[1] == 254);     // IPv4 link-local 169.254.0.0/16
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;                     // fc00::/7 IPv6 ULA
            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) return true; // fe80::/10 IPv6 link-local
        }

        return false;
    }

    /// <summary>
    /// Returns true for any address permitted on the direct TCP/UDP transfer path:
    /// Tailscale (<c>100.64.0.0/10</c>, <c>fd7a:115c:a1e0::/48</c>), private LAN
    /// (RFC1918, IPv6 ULA, link-local), or loopback. Public internet addresses,
    /// unspecified, multicast, and broadcast addresses are always rejected.
    /// </summary>
    public static bool IsPrivateDirectAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IsTailAddress(address) || IsLanAddress(address) || IPAddress.IsLoopback(address);
    }

    private static bool IsRfc1918Bytes(byte[] bytes) =>
        bytes.Length == 4
        && (bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168));

    /// <summary>
    /// Test seam: a machine with no usable direct interface is a real state (offline devices,
    /// isolated CI hosts), so tests replace the enumeration instead of inventing an unreachable address.
    /// Production never sets this.
    /// </summary>
    internal static Func<IReadOnlyList<string>>? LocalAddressesOverride { get; set; }

    public static IReadOnlyList<string> LocalAddresses() =>
        LocalAddressesOverride?.Invoke() ?? EnumerateLocalAddresses();

    private static IReadOnlyList<string> EnumerateLocalAddresses()
    {
        var candidates = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        candidates.Add(unicast.Address);
                    }
                }
                catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
                {
                    // Skip adapters whose IP properties cannot be queried on this platform.
                }
            }
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        return OrderLocalAddresses(candidates);
    }

    /// <summary>
    /// Orders usable local direct addresses with Tailscale IPv4 first, followed by RFC1918 LAN IPv4
    /// (excluding link-local APIPA <c>169.254.0.0/16</c> and loopback), then Tailscale IPv6.
    /// </summary>
    internal static IReadOnlyList<string> OrderLocalAddresses(IEnumerable<IPAddress> addresses)
    {
        var tailIpv4 = new List<string>();
        var lanIpv4 = new List<string>();
        var tailIpv6 = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in addresses)
        {
            var address = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (IPAddress.IsLoopback(address)) continue;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (IsTailAddress(address))
                {
                    var text = address.ToString();
                    if (seen.Add(text)) tailIpv4.Add(text);
                }
                else if (IsRfc1918Bytes(address.GetAddressBytes()))
                {
                    var text = address.ToString();
                    if (seen.Add(text)) lanIpv4.Add(text);
                }
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6 && IsTailAddress(address))
            {
                var text = address.ToString();
                if (seen.Add(text)) tailIpv6.Add(text);
            }
        }

        var ordered = new List<string>(tailIpv4.Count + lanIpv4.Count + tailIpv6.Count);
        ordered.AddRange(tailIpv4);
        ordered.AddRange(lanIpv4);
        ordered.AddRange(tailIpv6);
        return ordered;
    }

    public static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." ||
            name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("文件名含不支持的字符，或超过 180 个字符。");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            throw new ArgumentException("文件名是 Windows 保留名称。");
        return name;
    }

    public static string DeviceId(string name)
    {
        if (name.Length is < 1 or > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("设备收件箱名称请使用 1–64 位英文字母、数字、短横线或下划线。");
        return name;
    }

    public static string Commit(string temporary, string directory, string name)
    {
        FileName(name);
        for (var n = 0; ; n++)
        {
            var candidate = Path.Combine(directory, n == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            try { File.Move(temporary, candidate); return candidate; }
            catch (IOException) when (File.Exists(candidate)) { }
        }
    }

    /// <summary>A partial name in the tool's own namespace; never collides with a user file.</summary>
    public static string PartialPath(string directory) =>
        Path.Combine(directory, $"{PartialPrefix}{Guid.NewGuid():N}{PartialSuffix}");

    /// <summary>Removes partial files left behind by a crash or power loss. Only touches the tool's own names.</summary>
    public static int SweepPartials(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var removed = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, PartialPrefix + "*" + PartialSuffix))
            {
                try { File.Delete(path); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    public static Task CopyAsync(Stream source, Stream destination, long size,
        Action<long, long>? progress, CancellationToken token) =>
        CopyWithIdleTimeoutAsync(source, destination, size, progress, TimeSpan.FromSeconds(60), token);

    internal static async Task CopyWithIdleTimeoutAsync(Stream source, Stream destination, long size,
        Action<long, long>? progress, TimeSpan idleTimeout, CancellationToken token)
    {
        if (size < 0) throw new ArgumentException("文件长度不可为负数。");
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        var buffer = new byte[128 * 1024];
        long done = 0;
        var lastUpdate = Environment.TickCount64;
        while (done < size)
        {
            int read;
            try
            {
                // Bound inactivity, not file size or total duration. A half-open socket
                // after Doze must yield to the durable retry queue instead of hanging forever.
                idle.CancelAfter(idleTimeout);
                read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - done)), idle.Token);
                idle.CancelAfter(idleTimeout);
                if (read > 0) await destination.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("传输长时间没有进展，请检查网络连接。");
            }
            if (read == 0) throw new EndOfStreamException("传输中断，文件尚未接收完整。");
            done += read;
            if (Environment.TickCount64 - lastUpdate >= 250)
            {
                progress?.Invoke(done, size);
                lastUpdate = Environment.TickCount64;
            }
        }
        progress?.Invoke(done, size);
    }
}
