using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrayService : ITrayService
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_APP = 0x8000;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const int IDI_APPLICATION = 32512;
    private const int FirstMenuCommandId = 1000;
    private static readonly uint TrayCallbackMessage = WM_APP + 0x4D;

    private readonly object _gate = new();
    private readonly WndProc _wndProc;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private Func<TrayActionInvocation, CancellationToken, Task>? _handler;
    private IReadOnlyList<TrayMenuItem> _menuItems = [];
    private string _className = "";
    private IntPtr _windowHandle;
    private string _state = "idle";
    private bool _disposed;

    public WindowsTrayService()
    {
        _wndProc = WindowProcedure;
    }

    public string State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public async Task<TrayStartResult> StartAsync(
        TrayOptions options,
        Func<TrayActionInvocation, CancellationToken, Task> actionHandler,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TrayStartResult(false, "unsupported", "Windows tray requires Windows.");
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return new TrayStartResult(false, "disposed", "Tray service has been disposed.");
            }

            if (_thread is { IsAlive: true })
            {
                return Task.FromResult(new TrayStartResult(true, _state, "Windows tray is already running.")).Result;
            }

            _state = "starting";
            _handler = actionHandler;
            _menuItems = options.MenuItems;
            _className = "MyPowerToolsTrayWindow_" + Guid.NewGuid().ToString("N");
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var started = new TaskCompletionSource<TrayStartResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunMessageLoop(options, started))
        {
            IsBackground = true,
            Name = "MyPowerTools tray"
        };
        thread.SetApartmentState(ApartmentState.STA);

        lock (_gate)
        {
            _thread = thread;
        }

        thread.Start();
        return await started.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Thread? thread;
        CancellationTokenSource? cts;
        IntPtr windowHandle;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = "stopping";
            thread = _thread;
            cts = _cts;
            windowHandle = _windowHandle;
        }

        cts?.Cancel();
        if (windowHandle != IntPtr.Zero)
        {
            PostMessage(windowHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        if (thread is { IsAlive: true })
        {
            await Task.Run(() => thread.Join(TimeSpan.FromSeconds(2)));
        }

        cts?.Dispose();
        lock (_gate)
        {
            _state = "stopped";
            _thread = null;
            _windowHandle = IntPtr.Zero;
            _cts = null;
        }
    }

    private void RunMessageLoop(TrayOptions options, TaskCompletionSource<TrayStartResult> started)
    {
        ushort atom = 0;
        try
        {
            var instance = GetModuleHandle(null);
            var windowClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _wndProc,
                hInstance = instance,
                lpszClassName = _className
            };

            atom = RegisterClassEx(ref windowClass);
            if (atom == 0)
            {
                var message = $"RegisterClassEx failed with Win32 error {Marshal.GetLastWin32Error()}.";
                SetState("degraded");
                started.TrySetResult(new TrayStartResult(false, "degraded", message));
                return;
            }

            var handle = CreateWindowEx(
                0,
                _className,
                options.AppId,
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                instance,
                IntPtr.Zero);

            if (handle == IntPtr.Zero)
            {
                var message = $"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.";
                SetState("degraded");
                started.TrySetResult(new TrayStartResult(false, "degraded", message));
                return;
            }

            lock (_gate)
            {
                _windowHandle = handle;
            }

            var addResult = AddTrayIcon(handle, options);
            if (!addResult.Success)
            {
                SetState(addResult.State);
                started.TrySetResult(addResult);
                DestroyWindow(handle);
                return;
            }

            SetState("running");
            started.TrySetResult(addResult);

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            DeleteTrayIcon(handle);
            DestroyWindow(handle);
        }
        catch (Exception ex)
        {
            SetState("degraded");
            started.TrySetResult(new TrayStartResult(false, "degraded", ex.Message));
        }
        finally
        {
            if (atom != 0)
            {
                UnregisterClass(_className, GetModuleHandle(null));
            }
        }
    }

    private TrayStartResult AddTrayIcon(IntPtr handle, TrayOptions options)
    {
        var data = CreateNotifyIconData(handle, options);
        if (!Shell_NotifyIcon(NIM_ADD, ref data))
        {
            return new TrayStartResult(false, "degraded", $"Shell_NotifyIcon add failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        return new TrayStartResult(true, "running", "Windows tray icon is running.");
    }

    private static void DeleteTrayIcon(IntPtr handle)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = handle,
            uID = 1,
            szTip = "",
            szInfo = "",
            szInfoTitle = ""
        };
        Shell_NotifyIcon(NIM_DELETE, ref data);
    }

    private static NOTIFYICONDATA CreateNotifyIconData(IntPtr handle, TrayOptions options)
    {
        var icon = LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
        return new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = icon,
            szTip = Trim(options.ToolTip, 127),
            szInfo = "",
            szInfoTitle = ""
        };
    }

    private IntPtr WindowProcedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayCallbackMessage)
        {
            var eventCode = unchecked((uint)lParam.ToInt64() & 0xFFFF);
            if (eventCode is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                InvokeDefaultAction();
                return IntPtr.Zero;
            }

            if (eventCode is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                ShowContextMenu(hWnd);
                return IntPtr.Zero;
            }
        }

        if (message == WM_COMMAND)
        {
            InvokeMenuCommand(unchecked((int)(wParam.ToInt64() & 0xFFFF)));
            return IntPtr.Zero;
        }

        if (message == WM_CLOSE)
        {
            DestroyWindow(hWnd);
            return IntPtr.Zero;
        }

        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu(IntPtr hWnd)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var commandId = FirstMenuCommandId;
            foreach (var item in _menuItems)
            {
                if (item.SeparatorBefore)
                {
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }

                AppendMenu(menu, MF_STRING, new UIntPtr((uint)commandId), item.Label);
                if (item.IsDefault)
                {
                    SetMenuDefaultItem(menu, (uint)commandId, false);
                }

                commandId++;
            }

            SetForegroundWindow(hWnd);
            if (!GetCursorPos(out var point))
            {
                point = new POINT();
            }

            var selected = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, hWnd, IntPtr.Zero);
            if (selected != 0)
            {
                InvokeMenuCommand(selected);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void InvokeDefaultAction()
    {
        var item = _menuItems.FirstOrDefault(item => item.IsDefault) ?? _menuItems.FirstOrDefault();
        if (item is not null)
        {
            DispatchAction(item.Id);
        }
    }

    private void InvokeMenuCommand(int commandId)
    {
        var index = commandId - FirstMenuCommandId;
        if (index >= 0 && index < _menuItems.Count)
        {
            DispatchAction(_menuItems[index].Id);
        }
    }

    private void DispatchAction(string actionId)
    {
        Func<TrayActionInvocation, CancellationToken, Task>? handler;
        CancellationToken token;
        lock (_gate)
        {
            handler = _handler;
            token = _cts?.Token ?? CancellationToken.None;
        }

        if (handler is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await handler(new TrayActionInvocation(actionId, DateTimeOffset.UtcNow), token);
            }
            catch
            {
                SetState("degraded");
            }
        }, CancellationToken.None);
    }

    private void SetState(string state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }

    private static string Trim(string value, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "MyPowerTools" : value.Trim();
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hWnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, bool fByPosition);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
}
