using System.Runtime.InteropServices;

namespace MyPowerTools.WebToolHost;

internal static class Win32Native
{
    public const uint WsChild = 0x40000000;
    public const uint WsClipSiblings = 0x04000000;
    public const uint WsClipChildren = 0x02000000;
    public const uint WsOverlapped = 0x00000000;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetParent(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int virtualKey);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(nint window, nint region, bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(nint parent, EnumWindowProcedure callback, nint parameter);

    public delegate bool EnumWindowProcedure(nint window, nint parameter);
}
