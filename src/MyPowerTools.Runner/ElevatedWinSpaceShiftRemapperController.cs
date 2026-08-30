using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.Broker;

namespace MyPowerTools.Runner;

/// <summary>
/// Bridges the user-level IME setting to the protected input-remap task. The UAC prompt is
/// shown only when the task is first installed or when the protected host changes.
/// </summary>
internal sealed class ElevatedWinSpaceShiftRemapperController : IDisposable
{
    private readonly string _dataRoot;
    private readonly string _configPath;
    private readonly string _sourcePath;
    private readonly string _brokerPath;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private Process? _installProcess;
    private DateTimeOffset _nextInstallAttempt = DateTimeOffset.MinValue;
    private bool _taskEnabled;
    private bool _taskStarted;
    private bool _disabledOnStartup;
    private bool _disposed;

    public ElevatedWinSpaceShiftRemapperController(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
        _configPath = Path.Combine(_dataRoot, "state", "tools", "ime-manager", "win-space-shift.json");
        _sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "InputRemap",
            WindowsInputRemapTaskInstaller.HostFileName));
        _brokerPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Broker",
            "MyPowerTools.ElevatedBroker.exe"));
        _timer = new Timer(
            static state => ((ElevatedWinSpaceShiftRemapperController)state!).Reconcile(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public void Start()
    {
        Reconcile();
        _timer.Change(TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(750));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer.Dispose();
            DisposeExitedInstallProcess();
        }
    }

    private void Reconcile()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DisposeExitedInstallProcess();
            if (!IsEnabled())
            {
                if (!_disabledOnStartup)
                {
                    WindowsInputRemapTaskInstaller.DisableTask();
                    _taskEnabled = false;
                    _taskStarted = false;
                    _disabledOnStartup = true;
                }

                return;
            }

            _disabledOnStartup = false;
            if (_installProcess is not null)
            {
                return;
            }

            if (!WindowsInputRemapTaskInstaller.IsInstalledForSource(_dataRoot, _sourcePath))
            {
                StartInstallIfAllowed();
                return;
            }

            if (!_taskEnabled)
            {
                if (!WindowsInputRemapTaskInstaller.EnableTask())
                {
                    StartInstallIfAllowed(force: true);
                    return;
                }

                _taskEnabled = true;
            }

            if (!_taskStarted)
            {
                if (!WindowsInputRemapTaskInstaller.RunTask())
                {
                    _taskEnabled = false;
                    StartInstallIfAllowed(force: true);
                    return;
                }

                _taskStarted = true;
            }
        }
    }

    private bool IsEnabled()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_configPath));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("enabled", out var enabled) &&
                   enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private void StartInstallIfAllowed(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now < _nextInstallAttempt)
        {
            return;
        }

        if (!File.Exists(_sourcePath))
        {
            Console.WriteLine($"MyPowerTools.InputRemapHost is missing: {_sourcePath}");
            _nextInstallAttempt = now.AddSeconds(30);
            return;
        }

        if (!File.Exists(_brokerPath))
        {
            Console.WriteLine($"MyPowerTools elevated installer is missing: {_brokerPath}");
            _nextInstallAttempt = now.AddSeconds(30);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _brokerPath,
            Arguments = string.Join(
                " ",
                "input-remap",
                "install",
                "--data-root",
                Quote(_dataRoot),
                "--source",
                Quote(_sourcePath)),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        _nextInstallAttempt = now.AddSeconds(30);
        try
        {
            _installProcess = Process.Start(startInfo);
            Console.WriteLine("MyPowerTools requested one-time authorization for the Win+Space input task.");
        }
        catch (Win32Exception exception)
        {
            _installProcess = null;
            Console.WriteLine($"MyPowerTools Win+Space input task was not installed: {exception.Message}");
        }
    }

    private void DisposeExitedInstallProcess()
    {
        if (_installProcess is null || !_installProcess.HasExited)
        {
            return;
        }

        _installProcess.Dispose();
        _installProcess = null;
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
