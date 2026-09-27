using System.Text;
using MyPowerTools.Runtime;
using A = global::Android;

namespace MyPowerTools.Android;

/// <summary>
/// Startup diagnostics that survive a failed launch. Android's logcat tag is not a reliable
/// channel for a CoreCLR host (the managed logger can be attached before the Java VM is ready),
/// so every startup line is also appended to a bounded file inside the app's private files
/// directory. That file is what <c>adb shell run-as com.mypowertools.android cat ...</c> reads
/// when the Shell never appears.
/// </summary>
internal static class AndroidStartupLog
{
    private const long MaxBytes = 256 * 1024;
    private const int KeepBytes = 64 * 1024;
    private const int RingCapacity = 400;
    private const string LogcatTag = "MPT-Android";

    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new(RingCapacity);
    private static string? _path;
    private static bool _fileFailed;

    internal static string? Path
    {
        get { lock (Gate) { return _path; } }
    }

    /// <summary>Points the log at the runtime log directory (or the app files directory as a fallback).</summary>
    internal static void UseRuntimeLogDirectory(string? logDirectory)
    {
        var directory = logDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            try { directory = System.IO.Path.Combine(A.App.Application.Context.FilesDir!.AbsolutePath, "MyPowerTools", "logs"); }
            catch { directory = null; }
        }

        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            _path = System.IO.Path.Combine(directory!, "android-startup.log");
        }
    }

    internal static void Info(string stage, string message) => Write("info", stage, message);

    internal static void Error(string stage, Exception error) =>
        Write("error", stage, error.GetType().Name + ": " + error.Message + Environment.NewLine + error);

    internal static void Error(string stage, string message) => Write("error", stage, message);

    internal static void Write(string level, string stage, string message)
    {
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {stage}: {Safe(message)}");
        lock (Gate)
        {
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity)
            {
                Ring.Dequeue();
            }
        }

        TryLogcat(level, stage, message);
        TryAppendFile(line);
    }

    internal static string[] Snapshot()
    {
        lock (Gate)
        {
            return Ring.ToArray();
        }
    }

    internal static string SnapshotText() => string.Join(Environment.NewLine, Snapshot());

    private static string Safe(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }

        try
        {
            var redacted = LogRouter.Redact(message);
            return redacted.Length > 4000 ? redacted[..4000] + "…" : redacted;
        }
        catch (Exception redactionFailure)
        {
            // Diagnostics must never take the app down, and must never leak an unredacted value.
            // The failure type is safe to record and tells a reader why the line is empty.
            return "(redaction unavailable: " + redactionFailure.GetType().Name + ")";
        }
    }

    private static void TryLogcat(string level, string stage, string message)
    {
        try
        {
            var text = stage + ": " + message;
            if (level == "error")
            {
                A.Util.Log.Error(LogcatTag, text);
            }
            else
            {
                A.Util.Log.Info(LogcatTag, text);
            }
        }
        catch
        {
            // No Java VM binding on this thread: the file copy below is the durable record.
        }
    }

    private static void TryAppendFile(string line)
    {
        lock (Gate)
        {
            if (_fileFailed)
            {
                return;
            }

            try
            {
                var path = _path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                var directory = System.IO.Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                Trim(path);
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // A read-only or full filesystem must not turn a startup problem into a crash.
                _fileFailed = true;
            }
        }
    }

    private static void Trim(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        var existing = File.ReadAllBytes(path);
        var keep = existing.Length > KeepBytes ? existing[^KeepBytes..] : existing;
        // Start the retained tail at a line boundary so the file stays readable.
        var offset = Array.IndexOf(keep, (byte)'\n');
        if (offset >= 0 && offset + 1 < keep.Length)
        {
            keep = keep[(offset + 1)..];
        }

        File.WriteAllBytes(path, keep);
    }
}
