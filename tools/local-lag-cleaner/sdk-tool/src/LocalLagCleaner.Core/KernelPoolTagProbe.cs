using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LocalLagCleaner.MyPowerTools;

public sealed record PoolTagSnapshot(
    string Tag,
    ulong PagedBytes,
    ulong NonPagedBytes,
    ulong TotalBytes,
    uint PagedOutstandingAllocations,
    uint NonPagedOutstandingAllocations);

[SupportedOSPlatform("windows")]
internal static class KernelPoolTagProbe
{
    private const int SystemPoolTagInformation = 22;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int MaximumBufferBytes = 256 * 1024 * 1024;

    public static IReadOnlyList<PoolTagSnapshot> ReadTop(int limit = 32)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 256);
        var bufferBytes = 1024 * 1024;
        while (bufferBytes <= MaximumBufferBytes)
        {
            var buffer = Marshal.AllocHGlobal(bufferBytes);
            try
            {
                var status = NtQuerySystemInformation(
                    SystemPoolTagInformation,
                    buffer,
                    (uint)bufferBytes,
                    out var requiredBytes);
                if (status == StatusInfoLengthMismatch)
                {
                    if (bufferBytes == MaximumBufferBytes)
                    {
                        throw new InvalidOperationException(
                            $"Pool tag information exceeded the {MaximumBufferBytes / 1024 / 1024} MB safety limit.");
                    }

                    bufferBytes = checked((int)Math.Min(
                        MaximumBufferBytes,
                        Math.Max((long)bufferBytes * 2, requiredBytes + 64L * 1024)));
                    continue;
                }

                if (status < 0)
                {
                    throw new Win32Exception(
                        RtlNtStatusToDosError(status),
                        $"NtQuerySystemInformation(SystemPoolTagInformation) failed with NTSTATUS 0x{status:x8}.");
                }

                return Parse(buffer, bufferBytes)
                    .OrderByDescending(item => item.TotalBytes)
                    .ThenBy(item => item.Tag, StringComparer.Ordinal)
                    .Take(limit)
                    .ToArray();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        throw new InvalidOperationException(
            $"Pool tag information exceeded the {MaximumBufferBytes / 1024 / 1024} MB safety limit.");
    }

    private static IReadOnlyList<PoolTagSnapshot> Parse(IntPtr buffer, int bufferBytes)
    {
        var count = unchecked((uint)Marshal.ReadInt32(buffer));
        var firstEntryOffset = IntPtr.Size == 8 ? 8 : 4;
        var entryBytes = IntPtr.Size == 8 ? 40 : 28;
        var required = (long)firstEntryOffset + (long)count * entryBytes;
        if (required > bufferBytes)
        {
            throw new InvalidDataException(
                $"Pool tag buffer is truncated: count={count}, required={required}, available={bufferBytes}.");
        }

        var result = new List<PoolTagSnapshot>((int)Math.Min(count, 16_384));
        for (var index = 0U; index < count; index++)
        {
            var entry = IntPtr.Add(buffer, checked(firstEntryOffset + (int)index * entryBytes));
            var tagBytes = new byte[4];
            for (var tagIndex = 0; tagIndex < tagBytes.Length; tagIndex++)
            {
                tagBytes[tagIndex] = Marshal.ReadByte(entry, tagIndex);
            }

            var pagedAllocations = unchecked((uint)Marshal.ReadInt32(entry, 4));
            var pagedFrees = unchecked((uint)Marshal.ReadInt32(entry, 8));
            var pagedBytes = ReadNativeUInt(entry, IntPtr.Size == 8 ? 16 : 12);
            var nonPagedAllocationsOffset = IntPtr.Size == 8 ? 24 : 16;
            var nonPagedFreesOffset = IntPtr.Size == 8 ? 28 : 20;
            var nonPagedBytesOffset = IntPtr.Size == 8 ? 32 : 24;
            var nonPagedAllocations =
                unchecked((uint)Marshal.ReadInt32(entry, nonPagedAllocationsOffset));
            var nonPagedFrees =
                unchecked((uint)Marshal.ReadInt32(entry, nonPagedFreesOffset));
            var nonPagedBytes = ReadNativeUInt(entry, nonPagedBytesOffset);
            result.Add(new PoolTagSnapshot(
                FormatTag(tagBytes),
                pagedBytes,
                nonPagedBytes,
                SaturatingAdd(pagedBytes, nonPagedBytes),
                SaturatingSubtract(pagedAllocations, pagedFrees),
                SaturatingSubtract(nonPagedAllocations, nonPagedFrees)));
        }

        return result;
    }

    private static ulong ReadNativeUInt(IntPtr pointer, int offset)
    {
        return IntPtr.Size == 8
            ? unchecked((ulong)Marshal.ReadInt64(pointer, offset))
            : unchecked((uint)Marshal.ReadInt32(pointer, offset));
    }

    private static string FormatTag(byte[] bytes)
    {
        if (bytes.All(value => value is >= 0x20 and <= 0x7e))
        {
            return Encoding.ASCII.GetString(bytes);
        }

        return Convert.ToHexString(bytes);
    }

    private static uint SaturatingSubtract(uint left, uint right) =>
        left >= right ? left - right : 0;

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        uint systemInformationLength,
        out uint returnLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);
}
