using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRestOverlay : IRestOverlay
{
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsVisible = 0x10000000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int LwaAlpha = 0x02;
    private const int WmDestroy = 0x0002;
    private const int WmClose = 0x0010;
    private const int WmPaint = 0x000F;
    private const int WmKeyDown = 0x0100;
    private const int VkEscape = 0x1B;
    private const uint SmCxScreen = 0;
    private const uint SmCyScreen = 1;

    private readonly List<nint> _windows = [];
    private Thread? _thread;
    private Action? _skip;
    private Action? _finished;
    private int _remaining;
    private bool _showing;
    private WndProc? _wndProc;

    public bool IsShowing => _showing;

    public void Show(int totalSeconds, Action skip, Action finished)
    {
        if (!OperatingSystem.IsWindows() || _showing)
        {
            return;
        }

        _skip = skip;
        _finished = finished;
        _remaining = Math.Max(10, totalSeconds);
        _showing = true;
        _thread = new Thread(MessageLoop)
        {
            IsBackground = true,
            Name = "InputMonitor.RestOverlay"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Dismiss()
    {
        foreach (var window in _windows.ToArray())
        {
            if (window != 0)
            {
                PostMessage(window, WmClose, 0, 0);
            }
        }
    }

    private void MessageLoop()
    {
        _wndProc = WindowProc;
        var className = "InputMonitorRestOverlay";
        var wndClass = new WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            HInstance = GetModuleHandle(null),
            LpszClassName = className
        };
        RegisterClassEx(ref wndClass);

        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        var window = CreateWindowEx(
            WsExTopmost | WsExToolwindow | WsExLayered,
            className,
            "休息一下",
            WsPopup | WsVisible,
            0,
            0,
            width,
            height,
            0,
            0,
            wndClass.HInstance,
            0);
        SetLayeredWindowAttributes(window, 0, 210, LwaAlpha);
        _windows.Add(window);

        var timer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Decrement(ref _remaining) <= 0)
            {
                _finished?.Invoke();
                Dismiss();
            }
            else
            {
                InvalidateRect(window, 0, true);
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            timer.Dispose();
            _windows.Clear();
            _showing = false;
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WmPaint:
                Paint(hwnd);
                return 0;
            case WmKeyDown when (int)wParam == VkEscape:
                _skip?.Invoke();
                DestroyWindow(hwnd);
                return 0;
            case WmClose:
                DestroyWindow(hwnd);
                return 0;
            case WmDestroy:
                PostQuitMessage(0);
                return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Paint(nint hwnd)
    {
        var dc = BeginPaint(hwnd, out var ps);
        SetBkMode(dc, 1);
        SetTextColor(dc, 0x00333333);
        var text = $"该休息了  {_remaining / 60:00}:{_remaining % 60:00}\n按 Esc 跳过";
        DrawText(dc, text, -1, ref ps.RcPaint, 0x00000001 | 0x00000004 | 0x00000010);
        EndPaint(hwnd, ref ps);
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public nint HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public nint Hdc;
        public bool Erase;
        public Rect RcPaint;
        public bool Restore;
        public bool IncUpdate;
        public unsafe fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, out PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(nint hWnd, ref PaintStruct lpPaint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, bool bErase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawText(nint hdc, string lpchText, int cchText, ref Rect lprc, uint format);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(nint hdc, uint color);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(uint nIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
