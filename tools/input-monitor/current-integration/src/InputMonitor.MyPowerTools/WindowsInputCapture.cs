using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

[SupportedOSPlatform("windows")]
internal sealed class WindowsInputCapture : IInputCapture
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmQuit = 0x0012;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WheelDelta = 120;

    private readonly EventSampler _sampler = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly object _gate = new();
    private Thread? _thread;
    private uint _threadId;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _keyboardProc;
    private nint _mouseProc;
    private LowLevelProc? _keyboardCallback;
    private LowLevelProc? _mouseCallback;
    private bool _running;

    public event Action<InputEventRecord>? EventReceived;
    public bool IsRunning => _running;

    public void Start()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _thread = new Thread(MessageLoop)
            {
                IsBackground = true,
                Name = "InputMonitor.Hooks"
            };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            if (_threadId != 0)
            {
                PostThreadMessage(_threadId, WmQuit, 0, 0);
            }
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void UpdateTrackSampleDistance(double pixels) => _sampler.MinDistance = pixels;

    public void Dispose() => Stop();

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _keyboardCallback = KeyboardHook;
        _mouseCallback = MouseHook;
        _keyboardProc = Marshal.GetFunctionPointerForDelegate(_keyboardCallback);
        _mouseProc = Marshal.GetFunctionPointerForDelegate(_mouseCallback);
        var module = HookModuleHandle(_keyboardProc);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);

        var poller = new Timer(_ => PollCursor(), null, TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(33));
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
            poller.Dispose();
            if (_keyboardHook != 0)
            {
                UnhookWindowsHookEx(_keyboardHook);
            }

            if (_mouseHook != 0)
            {
                UnhookWindowsHookEx(_mouseHook);
            }

            _keyboardHook = 0;
            _mouseHook = 0;
            _keyboardCallback = null;
            _mouseCallback = null;
        }
    }

    private nint KeyboardHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var message = (int)wParam;
            var down = message is WmKeyDown or WmSysKeyDown;
            var up = message is WmKeyUp or WmSysKeyUp;
            if (down || up)
            {
                var vk = info.VkCode;
                var autoRepeat = false;
                lock (_keysDown)
                {
                    if (down)
                    {
                        autoRepeat = !_keysDown.Add(vk);
                    }
                    else
                    {
                        _keysDown.Remove(vk);
                    }
                }

                var characters = down && !autoRepeat ? TranslateKey(vk, info.ScanCode) : null;
                Publish(new InputEventRecord(
                    down ? InputEventKind.KeyDown : InputEventKind.KeyUp,
                    NowNs(),
                    DateTimeOffset.Now,
                    null,
                    null,
                    vk,
                    characters,
                    info.Flags,
                    0,
                    autoRepeat,
                    0));
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseHook(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            var message = (int)wParam;
            switch (message)
            {
                case WmLButtonDown:
                    Publish(Click(InputEventKind.LeftClick, info));
                    break;
                case WmRButtonDown:
                    Publish(Click(InputEventKind.RightClick, info));
                    break;
                case WmMouseWheel:
                case WmMouseHWheel:
                    var notches = (short)((info.MouseData >> 16) & 0xFFFF);
                    var lines = notches / WheelDelta;
                    if (lines == 0)
                    {
                        lines = notches >= 0 ? 1 : -1;
                    }

                    Publish(new InputEventRecord(
                        InputEventKind.Scroll,
                        NowNs(),
                        DateTimeOffset.Now,
                        info.Point.X,
                        info.Point.Y,
                        null,
                        null,
                        0,
                        lines,
                        false,
                        0));
                    break;
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void PollCursor()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var (sampled, delta) = _sampler.Feed(point.X, point.Y, NowNs());
        if (!sampled)
        {
            return;
        }

        Publish(new InputEventRecord(
            InputEventKind.MouseMoveSample,
            NowNs(),
            DateTimeOffset.Now,
            point.X,
            point.Y,
            null,
            null,
            0,
            0,
            false,
            delta));
    }

    private static InputEventRecord Click(InputEventKind kind, MsLlHookStruct info) =>
        new(kind, NowNs(), DateTimeOffset.Now, info.Point.X, info.Point.Y, null, null, 0, 0, false, 0);

    private void Publish(InputEventRecord record) => EventReceived?.Invoke(record);

    private static string? TranslateKey(int vk, int scan)
    {
        var state = new byte[256];
        if (!GetKeyboardState(state))
        {
            return null;
        }

        var buffer = new StringBuilder(8);
        var result = ToUnicode((uint)vk, (uint)scan, state, buffer, buffer.Capacity, 0);
        if (result <= 0)
        {
            return null;
        }

        var text = buffer.ToString();
        return text.All(character => char.IsControl(character)) ? null : text;
    }

    private static ulong NowNs() => (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000d / Stopwatch.Frequency));

    private delegate nint LowLevelProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, nint lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static nint HookModuleHandle(nint proc)
    {
        const int fromAddress = 0x00000004;
        const int unchangedRefCount = 0x00000002;
        if (GetModuleHandleEx(fromAddress | unchangedRefCount, proc, out var handle) && handle != 0)
        {
            return handle;
        }

        return GetModuleHandle(null);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetModuleHandleEx(int dwFlags, nint lpModuleNameOrAddress, out nint phModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
