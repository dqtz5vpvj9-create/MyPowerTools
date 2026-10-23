using System.Runtime.InteropServices;

namespace LocalLagCleaner.MyPowerTools;

public sealed record ProcessHardFaultCounter(int ProcessId, long CreateTime, uint Count);
public sealed record ProcessHardFaultRate(int ProcessId, string Name, double FaultsPerSecond, uint Faults);
public sealed record ProcessHardFaultCapture(DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<int, ProcessHardFaultCounter> Counters, string Error);

public static class ProcessHardFaultProbe
{
    // SYSTEM_PROCESS_INFORMATION (Windows 7+, x64), as published by PHNT.
    // Read only the stable header prefix; no pointer to a name or thread array is dereferenced.
    public static ProcessHardFaultCapture Capture()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return new(DateTimeOffset.UtcNow, new Dictionary<int, ProcessHardFaultCounter>(), "当前平台不支持进程硬缺页计数。");
        try
        {
            var size = 1024 * 1024;
            while (size <= 64 * 1024 * 1024)
            {
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQuerySystemInformation(5, buffer, size, out var used);
                    if (status == unchecked((int)0xC0000004))
                    {
                        size = checked(Math.Max(size * 2, used + 65536));
                        continue;
                    }
                    if (status < 0 || used < 96 || used > size)
                        throw new InvalidDataException($"进程计数读取失败：0x{status:x8}");
                    var counters = new Dictionary<int, ProcessHardFaultCounter>();
                    var offset = 0;
                    while (offset <= used - 96)
                    {
                        var row = IntPtr.Add(buffer, offset);
                        var next = Marshal.ReadInt32(row);
                        var pid = Marshal.ReadInt64(row, 80);
                        if (pid is > 0 and <= int.MaxValue)
                            counters[(int)pid] = new((int)pid, Marshal.ReadInt64(row, 32), unchecked((uint)Marshal.ReadInt32(row, 16)));
                        if (next == 0)
                            return new(DateTimeOffset.UtcNow, counters, "");
                        if (next < 96 || next > used - offset - 96)
                            throw new InvalidDataException("进程计数表边界无效。");
                        offset += next;
                    }
                    throw new InvalidDataException("进程计数表不完整。");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            throw new InvalidDataException("进程计数表超过采集上限。");
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or EntryPointNotFoundException)
        {
            return new(DateTimeOffset.UtcNow, new Dictionary<int, ProcessHardFaultCounter>(), exception.Message);
        }
    }

    public static IReadOnlyList<ProcessHardFaultRate> Compare(ProcessHardFaultCapture before,
        ProcessHardFaultCapture after, IReadOnlyDictionary<int, string> names)
    {
        var seconds = (after.CapturedAtUtc - before.CapturedAtUtc).TotalSeconds;
        if (seconds <= 0 || before.Error.Length > 0 || after.Error.Length > 0)
            return [];
        return after.Counters.Values
            .Where(current => before.Counters.TryGetValue(current.ProcessId, out var previous) &&
                              current.CreateTime > 0 && current.CreateTime == previous.CreateTime &&
                              current.Count >= previous.Count && current.ProcessId != Environment.ProcessId)
            .Select(current => new ProcessHardFaultRate(current.ProcessId,
                names.GetValueOrDefault(current.ProcessId) ?? "已退出的程序",
                (current.Count - before.Counters[current.ProcessId].Count) / seconds,
                current.Count - before.Counters[current.ProcessId].Count))
            .Where(row => row.Faults > 0)
            .OrderByDescending(row => row.Faults).Take(12).ToArray();
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int length, out int returnLength);
}
