using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InputMonitor.Core;

namespace InputMonitor.MyPowerTools;

[SupportedOSPlatform("windows")]
internal sealed class WindowsFrontAppTracker : IFrontAppTracker
{
    private readonly AppCategoryMap _categories;
    private readonly object _gate = new();
    private readonly StringBuilder _titleBuffer = new(1024);
    private Timer? _heartbeat;
    private TimeSpan _interval;
    private FrontAppSession? _current;
    private bool _screenLocked;
    private bool _running;

    public WindowsFrontAppTracker(AppCategoryMap categories, TimeSpan? heartbeatInterval = null)
    {
        _categories = categories;
        _interval = heartbeatInterval ?? TimeSpan.FromSeconds(30);
    }

    public event Action<FrontAppSession>? SessionCompleted;
    public event Action<FrontAppSession, DateTimeOffset>? Heartbeat;
    public bool ScreenLocked => _screenLocked;

    public FrontAppSession? CurrentSession
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _running)
        {
            return;
        }

        _running = true;
        SwitchToForeground(DateTimeOffset.Now);
        _heartbeat = new Timer(_ => Pulse(), null, _interval, _interval);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _heartbeat?.Dispose();
        _heartbeat = null;
        lock (_gate)
        {
            EndCurrentLocked(DateTimeOffset.Now);
        }
    }

    public void UpdateHeartbeatInterval(TimeSpan interval)
    {
        _interval = interval;
        if (_heartbeat is not null)
        {
            _heartbeat.Change(interval, interval);
        }
    }

    public void Dispose() => Stop();

    private void Pulse()
    {
        var locked = IsWorkstationLocked();
        lock (_gate)
        {
            if (locked && !_screenLocked)
            {
                _screenLocked = true;
                EndCurrentLocked(DateTimeOffset.Now);
                return;
            }

            if (!locked && _screenLocked)
            {
                _screenLocked = false;
            }
            else if (_screenLocked)
            {
                return;
            }
        }

        if (locked)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var probe = ProbeForeground();
        lock (_gate)
        {
            if (_current is { } session)
            {
                if (!string.Equals(session.BundleId, probe.BundleId, StringComparison.OrdinalIgnoreCase))
                {
                    EndCurrentLocked(now);
                    _current = Begin(probe, now);
                    return;
                }

                if (!string.Equals(session.WindowTitle, probe.Title, StringComparison.Ordinal) &&
                    probe.Title is not null)
                {
                    EndCurrentLocked(now);
                    _current = Begin(probe with { }, now);
                    _current.WindowTitle = probe.Title;
                    return;
                }

                Heartbeat?.Invoke(session, now);
                return;
            }
        }

        SwitchToForeground(now);
    }

    private void SwitchToForeground(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_screenLocked)
            {
                return;
            }

            EndCurrentLocked(now);
            var probe = ProbeForeground();
            _current = Begin(probe, now);
        }
    }

    private FrontAppSession Begin((string BundleId, string AppName, string? Title) probe, DateTimeOffset now) =>
        new()
        {
            BundleId = probe.BundleId,
            AppName = probe.AppName,
            WindowTitle = probe.Title,
            Start = now,
            Category = _categories.CategoryFor(probe.BundleId)
        };

    private void EndCurrentLocked(DateTimeOffset now)
    {
        if (_current is not { } session)
        {
            return;
        }

        session.End = now;
        _current = null;
        if ((session.DurationSeconds ?? 0) >= 1)
        {
            SessionCompleted?.Invoke(session);
        }
    }

    private (string BundleId, string AppName, string? Title) ProbeForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return ("unknown", "Unknown", null);
        }

        _titleBuffer.Clear();
        var length = GetWindowText(hwnd, _titleBuffer, _titleBuffer.Capacity);
        var title = length > 0 ? _titleBuffer.ToString() : null;
        GetWindowThreadProcessId(hwnd, out var processId);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            var path = process.MainModule?.FileName;
            var bundle = string.IsNullOrWhiteSpace(path) ? name : Path.GetFileName(path);
            return (bundle.ToLowerInvariant(), string.IsNullOrWhiteSpace(process.MainWindowTitle) ? name : process.ProcessName, title);
        }
        catch
        {
            return ("unknown", "Unknown", title);
        }
    }

    private static bool IsWorkstationLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0)
        {
            return true;
        }

        CloseDesktop(desktop);
        return false;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern nint OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(nint hDesktop);
}
