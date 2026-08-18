using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

internal static class WindowsDisplays
{
    public static IReadOnlyList<ScreenBounds> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [new ScreenBounds(0, 0, 1920, 1080, true, "主屏")];
        }

        return EnumerateWindows();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ScreenBounds> EnumerateWindows()
    {
        var screens = new List<ScreenBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var width = info.Monitor.Right - info.Monitor.Left;
            var height = info.Monitor.Bottom - info.Monitor.Top;
            if (width <= 0 || height <= 0)
            {
                return true;
            }

            screens.Add(new ScreenBounds(
                info.Monitor.Left,
                info.Monitor.Top,
                width,
                height,
                (info.Flags & MonitorPrimary) != 0,
                ""));
            return true;
        }, IntPtr.Zero);

        if (screens.Count == 0)
        {
            return [new ScreenBounds(0, 0, 1920, 1080, true, "主屏")];
        }

        return screens
            .OrderByDescending(screen => screen.IsPrimary)
            .ThenBy(screen => screen.OriginX)
            .ThenBy(screen => screen.OriginY)
            .Select((screen, index) => screen with
            {
                Name = screen.IsPrimary ? "主屏" : $"显示器 {index + 1}"
            })
            .ToArray();
    }

    private const int MonitorPrimary = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
