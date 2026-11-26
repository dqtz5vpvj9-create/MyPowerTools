using System.Net;
using System.Net.NetworkInformation;

namespace FileTransfer.Core;

public static class TransferFiles
{
    public const int Port = 47165;
    public const string PartialPrefix = ".mpt-";
    public const string PartialSuffix = ".part";

    public static bool IsTailAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 ? b[0] == 100 && b[1] is >= 64 and <= 127
            : address.ToString().StartsWith("fd7a:115c:a1e0:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Test seam: a machine with no usable Tailnet interface is a real state (phones without Tailscale,
    /// CI hosts), so tests replace the enumeration instead of inventing an unreachable address.
    /// Production never sets this.
    /// </summary>
    internal static Func<IReadOnlyList<string>>? LocalAddressesOverride { get; set; }

    public static IReadOnlyList<string> LocalAddresses() =>
        LocalAddressesOverride?.Invoke() ?? EnumerateLocalAddresses();

    private static IReadOnlyList<string> EnumerateLocalAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address).Where(IsTailAddress).Select(a => a.ToString()).Distinct().ToArray();

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
