using System.Runtime.InteropServices;
using System.Text.Json;

namespace MyPowerTools.Broker;

/// <summary>
/// Maps Win+Space to a left-Shift tap for the current user. Windows reserves Win+Space,
/// so this is implemented at the low-level keyboard hook boundary instead of through
/// the keyboard-layout registry values.
/// </summary>
public sealed class WindowsWinSpaceShiftRemapper : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint VkSpace = 0x20;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkLeftShift = 0xA0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint InputKeyboard = 1;
    private const uint LlkhfInjected = 0x00000010;
    private const nint InjectedMarker = 0x4D505457;

    private readonly string _configPath;
    private readonly ManualResetEventSlim _started = new(false);
    private readonly LowLevelKeyboardProc _keyboardProc;
    private Thread? _thread;
    private Timer? _configurationTimer;
    private uint _threadId;
    private nint _hook;
    private bool _enabled;
    private bool _winDown;
    private bool _winForwarded;
    private bool _mapped;
    private bool _shiftEmitted;
    private uint _winVirtualKey;
    private bool _disposed;

    public WindowsWinSpaceShiftRemapper(string dataRoot)
    {
        _configPath = Path.Combine(
            Path.GetFullPath(dataRoot),
            "state",
            "tools",
            "ime-manager",
            "win-space-shift.json");
        _keyboardProc = KeyboardHookCallback;
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null)
        {
            return;
        }

        _thread = new Thread(RunHookThread)
        {
            IsBackground = true,
            Name = "MyPowerTools InputRemapHost Win+Space remapper"
        };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configurationTimer?.Dispose();
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, nint.Zero, nint.Zero);
        }

        if (_thread is { IsAlive: true } thread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        _started.Dispose();
    }

    private void RunHookThread()
    {
        _threadId = GetCurrentThreadId();
        ReloadConfiguration();
        _hook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        _started.Set();
        if (_hook == nint.Zero)
        {
            Console.WriteLine($"MyPowerTools.InputRemapHost Win+Space remapper unavailable: SetWindowsHookEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
            return;
        }

        Console.WriteLine($"MyPowerTools.InputRemapHost Win+Space remapper ready; enabled={Volatile.Read(ref _enabled)}.");

        _configurationTimer = new Timer(
            static state => ((WindowsWinSpaceShiftRemapper)state!).ReloadConfiguration(),
            this,
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250));

        try
        {
            while (GetMessage(out _, nint.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            _configurationTimer?.Dispose();
            _configurationTimer = null;
            UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }
    }

    private void ReloadConfiguration()
    {
        var enabled = false;
        try
        {
            if (File.Exists(_configPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
                enabled = document.RootElement.ValueKind == JsonValueKind.Object &&
                          document.RootElement.TryGetProperty("enabled", out var property) &&
                          property.ValueKind == JsonValueKind.True;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            enabled = false;
        }

        Volatile.Write(ref _enabled, enabled);
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<Kbdllhookstruct>(lParam);
        if ((info.Flags & LlkhfInjected) != 0 || info.DwExtraInfo == InjectedMarker)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var isDown = wParam == (nint)WmKeyDown || wParam == (nint)WmSysKeyDown;
        var isUp = wParam == (nint)WmKeyUp || wParam == (nint)WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (!Volatile.Read(ref _enabled))
        {
            if (_winDown)
            {
                SendKey(_winVirtualKey, down: true);
                _winDown = false;
                _winForwarded = false;
                _mapped = false;
                _shiftEmitted = false;
            }

            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var virtualKey = info.VkCode;
        if (virtualKey is VkLWin or VkRWin)
        {
            if (isDown && !_winDown)
            {
                _winDown = true;
                _winVirtualKey = virtualKey;
                _winForwarded = false;
                _mapped = false;
                _shiftEmitted = false;
                return (nint)1;
            }

            if (isUp && _winDown && virtualKey == _winVirtualKey)
            {
                var forwarded = _winForwarded;
                var mapped = _mapped;
                if (mapped && !_shiftEmitted)
                {
                    EmitShiftTap();
                }
                _winDown = false;
                _winForwarded = false;
                _mapped = false;
                if (!forwarded && !mapped)
                {
                    SendKey(_winVirtualKey, down: true);
                    SendKey(_winVirtualKey, down: false);
                }

                return forwarded ? CallNextHookEx(_hook, code, wParam, lParam) : (nint)1;
            }
        }

        if (_winDown && virtualKey == VkSpace)
        {
            if (isDown && !_mapped)
            {
                _mapped = true;
            }
            else if (isUp && _mapped && !_shiftEmitted)
            {
                EmitShiftTap();
            }

            return (nint)1;
        }

        if (_winDown && isDown && !_winForwarded && !_mapped)
        {
            SendKey(_winVirtualKey, down: true);
            _winForwarded = true;
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void EmitShiftTap()
    {
        SendKey(VkLeftShift, down: true);
        SendKey(VkLeftShift, down: false);
        _shiftEmitted = true;
    }

    private static void SendKey(uint virtualKey, bool down)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = down ? 0u : KeyeventfKeyup,
                    ExtraInfo = InjectedMarker
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            Console.WriteLine($"MyPowerTools.InputRemapHost Win+Space remapper could not send {(down ? "down" : "up")} for VK 0x{virtualKey:X2}; Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, nint moduleHandle, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Kbdllhookstruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint MessageId;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }
}
