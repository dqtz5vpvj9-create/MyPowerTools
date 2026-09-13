using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace MyPowerTools.Shell.Avalonia;

/// <summary>
/// Process-local, synchronous diagnostics. No timer, background upload, or user message content.
/// A pending marker records an unclean exit; it does not by itself prove that a crash occurred.
/// </summary>
internal sealed class DiagnosticSession : IDisposable
{
    internal const string DirectoryVariable = "MPT_DIAGNOSTICS_DIRECTORY";
    internal const string LogVariable = "MPT_SHELL_DIAGNOSTIC_LOG";
    private const long MaximumLogBytes = 8 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly Func<string, string> _redact;
    private FileStream? _nativeStream;
    private int _savedStderr = -1;
    private int _fatal;
    private bool _disposed;
    private string? _pendingPath;

    private DiagnosticSession(string directory, Func<string, string> redact)
    {
        DirectoryPath = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
        else Directory.CreateDirectory(DirectoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var name = $"shell-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        LogPath = Path.Combine(DirectoryPath, name + ".jsonl");
        StderrPath = Path.Combine(DirectoryPath, name + ".stderr.log");
        _redact = redact;
        _stream = CreatePrivateFile(LogPath);
    }

    public string DirectoryPath { get; }
    public string LogPath { get; }
    public string StderrPath { get; }
    public IReadOnlyList<PreviousExit> PreviousExits { get; private set; } = [];

    public static DiagnosticSession? TryStart(Func<string, string> redact, bool captureNativeStderr)
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        var normal = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "MyPowerTools")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", "diagnostics");
        var fallback = Path.Combine(Path.GetTempPath(), "MyPowerTools-diagnostics-" + Environment.UserName);
        string? storageFailure = null;
        foreach (var directory in new[] { string.IsNullOrWhiteSpace(configured) ? normal : configured, fallback }.Distinct())
        {
            DiagnosticSession? session = null;
            try
            {
                session = new DiagnosticSession(directory, redact);
                Environment.SetEnvironmentVariable(LogVariable, session.LogPath);
                session.Write("process.start", $"os={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}; runtime={RuntimeInformation.FrameworkDescription}; base={AppContext.BaseDirectory}");
                if (storageFailure is not null) session.Write("storage.fallback", storageFailure);
                TryStderr($"MyPowerTools diagnostics: {session.LogPath}");
                if (captureNativeStderr) session.CaptureNativeStderr();
                return session;
            }
            catch (Exception ex)
            {
                session?.Dispose();
                storageFailure = ex.ToString();
                TryStderr($"MyPowerTools could not open diagnostics at {directory}: {ex}");
            }
        }
        return null;
    }

    public void BeginSession()
    {
        try
        {
            var previous = new List<PreviousExit>();
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "shell-*.pending.json"))
            {
                try
                {
                    var marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(path));
                    if (marker is not null && !IsStillRunning(marker))
                        previous.Add(new PreviousExit(path, marker.LogPath, marker.StderrPath));
                }
                catch (Exception ex) { Write("marker.read.failed", path, ex); }
            }
            PreviousExits = previous;
            using var process = Process.GetCurrentProcess();
            var current = new SessionMarker(Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks, LogPath, StderrPath);
            _pendingPath = Path.ChangeExtension(LogPath, ".pending.json");
            using var file = CreatePrivateFile(_pendingPath);
            file.Write(JsonSerializer.SerializeToUtf8Bytes(current));
            file.Flush(true);
            Write("session.begin", $"previousUncleanSessions={previous.Count}");
        }
        catch (Exception ex) { Write("marker.create.failed", "Unclean-exit detection is unavailable.", ex); }
    }

    private static bool IsStillRunning(SessionMarker marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            return !process.HasExited &&
                Math.Abs(process.StartTime.ToUniversalTime().Ticks - marker.ProcessStartTicks) < TimeSpan.TicksPerSecond;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; } // An inaccessible live process must not be reported as crashed.
    }

    public void AcknowledgePreviousExits()
    {
        foreach (var previous in PreviousExits)
        {
            try { File.Move(previous.MarkerPath, previous.MarkerPath + ".reported", overwrite: true); }
            catch (Exception ex) { Write("marker.acknowledge.failed", previous.MarkerPath, ex); }
        }
    }

    public void Write(string eventName, string? detail = null, Exception? exception = null, bool fatal = false)
    {
        if (_disposed) return;
        if (fatal) Interlocked.Exchange(ref _fatal, 1);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                timeUtc = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId,
                managedThreadId = Environment.CurrentManagedThreadId,
                eventName,
                detail = _redact(detail ?? ""),
                exception = exception is null ? null : _redact(exception.ToString())
            }) + "\n");
            // Write fatal evidence independently before taking the normal log lock. A fault
            // on another thread must not depend on a lock held by the failing thread.
            if (fatal)
            {
                try
                {
                    using var emergency = CreatePrivateFile(LogPath + $".{Guid.NewGuid():N}.fatal.json");
                    emergency.Write(bytes);
                    emergency.Flush(true);
                }
                catch (Exception ex) { TryStderr($"Emergency diagnostic write failed: {ex}"); }
            }
            var acquired = false;
            try
            {
                Monitor.TryEnter(_gate, fatal ? 250 : 1000, ref acquired);
                if (!acquired) { TryStderr(Encoding.UTF8.GetString(bytes)); return; }
                if (_disposed) return;
                if (_stream.Length > MaximumLogBytes)
                {
                    _stream.Flush();
                    File.Copy(LogPath, LogPath + ".previous", overwrite: true);
                    _stream.SetLength(0);
                    _stream.Position = 0;
                }
                _stream.Write(bytes);
                _stream.Flush(fatal);
            }
            finally { if (acquired) Monitor.Exit(_gate); }
        }
        catch (Exception ex) { TryStderr($"Diagnostic write failed: {ex}"); }
    }

    public void RecordAssembly(Assembly assembly)
    {
        try
        {
            var name = assembly.GetName();
            if (name.Name is not ("RemoteNotifications.Surface" or "Avalonia.Controls.WebView" or
                "MyPowerTools.WebSurface.Avalonia" or "MyPowerTools.Shell.Avalonia")) return;
            var context = AssemblyLoadContext.GetLoadContext(assembly);
            Write("assembly.loaded", $"name={name.Name}; version={name.Version}; informationalVersion={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; mvid={assembly.ManifestModule.ModuleVersionId}; loadContext={context?.Name}; collectible={context?.IsCollectible}; location={assembly.Location}");
        }
        catch (Exception ex) { Write("assembly.metadata.failed", null, ex); }
    }

    public void Complete(int exitCode)
    {
        Write("process.exit", $"exitCode={exitCode}");
        if (_pendingPath is not null && Volatile.Read(ref _fatal) == 0)
        {
            try { File.Delete(_pendingPath); }
            catch (Exception ex) { Write("marker.delete.failed", _pendingPath, ex); }
        }
    }

    private void CaptureNativeStderr()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            _nativeStream = CreatePrivateFile(StderrPath);
            _savedStderr = NativeDup(2);
            if (_savedStderr < 0) throw new IOException($"dup(stderr) failed: {Marshal.GetLastPInvokeError()}");
            var fd = checked((int)_nativeStream.SafeFileHandle.DangerousGetHandle());
            if (NativeDup2(fd, 2) < 0) throw new IOException($"dup2(stderr) failed: {Marshal.GetLastPInvokeError()}");
            Write("stderr.captured", StderrPath);
        }
        catch (Exception ex)
        {
            RestoreNativeStderr();
            Write("stderr.capture.failed", "Managed exception logging remains enabled.", ex);
        }
    }

    private void RestoreNativeStderr()
    {
        if (_savedStderr >= 0)
        {
            _ = NativeDup2(_savedStderr, 2);
            _ = NativeClose(_savedStderr);
            _savedStderr = -1;
        }
        _nativeStream?.Dispose();
        _nativeStream = null;
    }

    private static FileStream CreatePrivateFile(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.ReadWrite | FileShare.Delete,
        BufferSize = 1,
        UnixCreateMode = OperatingSystem.IsWindows() ? null : UnixFileMode.UserRead | UnixFileMode.UserWrite
    });

    private static void TryStderr(string message)
    {
        try { Console.Error.WriteLine(message); } catch { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { RestoreNativeStderr(); } catch { }
            _stream.Dispose();
        }
    }

    private static int NativeDup(int fd) => OperatingSystem.IsMacOS() ? MacDup(fd) : LinuxDup(fd);
    private static int NativeDup2(int fd, int target) => OperatingSystem.IsMacOS() ? MacDup2(fd, target) : LinuxDup2(fd, target);
    private static int NativeClose(int fd) => OperatingSystem.IsMacOS() ? MacClose(fd) : LinuxClose(fd);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dup", SetLastError = true)] private static extern int MacDup(int fd);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dup2", SetLastError = true)] private static extern int MacDup2(int fd, int target);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close", SetLastError = true)] private static extern int MacClose(int fd);
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)] private static extern int LinuxDup(int fd);
    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)] private static extern int LinuxDup2(int fd, int target);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int LinuxClose(int fd);

    internal sealed record PreviousExit(string MarkerPath, string LogPath, string StderrPath);
    private sealed record SessionMarker(int ProcessId, long ProcessStartTicks, string LogPath, string StderrPath);
}
