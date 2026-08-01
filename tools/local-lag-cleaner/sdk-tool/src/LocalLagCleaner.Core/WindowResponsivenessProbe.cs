using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LocalLagCleaner.MyPowerTools;

public sealed record WindowResponsivenessSnapshot(
    int ProcessId,
    string ProcessName,
    int VisibleWindowCount,
    int HungWindowCount);

[SupportedOSPlatform("windows")]
internal static class WindowResponsivenessProbe
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int DwmwaCloaked = 14;

    public static IReadOnlyList<WindowResponsivenessSnapshot> Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var counts = new Dictionary<int, (int Visible, int Hung)>();
        _ = EnumWindows(
            (window, parameter) =>
            {
                if (!IsWindowVisible(window) || !IsUserFacingWindow(window))
                {
                    return true;
                }

                _ = parameter;
                _ = GetWindowThreadProcessId(window, out var processId);
                if (processId == 0)
                {
                    return true;
                }

                var current = counts.GetValueOrDefault(unchecked((int)processId));
                counts[unchecked((int)processId)] = (
                    current.Visible + 1,
                    current.Hung + (IsHungAppWindow(window) ? 1 : 0));
                return true;
            },
            IntPtr.Zero);

        return counts
            .Select(item => new WindowResponsivenessSnapshot(
                item.Key,
                TryReadProcessName(item.Key),
                item.Value.Visible,
                item.Value.Hung))
            .OrderByDescending(item => item.HungWindowCount)
            .ThenByDescending(item => item.VisibleWindowCount)
            .ThenBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsUserFacingWindow(IntPtr window)
    {
        var extendedStyle = ReadWindowLongPtr(window, GwlExStyle).ToInt64();
        if ((extendedStyle & (WsExToolWindow | WsExNoActivate)) != 0)
        {
            return false;
        }

        var cloaked = 0;
        var result = DwmGetWindowAttribute(
            window,
            DwmwaCloaked,
            out cloaked,
            Marshal.SizeOf<int>());
        return result != 0 || cloaked == 0;
    }

    private static IntPtr ReadWindowLongPtr(IntPtr window, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(window, index)
            : new IntPtr(GetWindowLong32(window, index));
    }

    private static string TryReadProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return "unknown";
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        int attribute,
        out int value,
        int valueSize);
}
