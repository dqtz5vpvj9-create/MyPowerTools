using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MyPowerTools.Ipc;

/// <summary>
/// Makes Runner and ServiceManager windowless Windows processes: the PE subsystem
/// is WINDOWS (WinExe), stdout goes to JSONL under the data-root logs directory,
/// and a parent console is attached only for explicit CLI modes.
/// </summary>
public sealed class DaemonProcessConsole : IDisposable
{
    private const int AttachParentProcess = -1;
    private readonly JsonlLogWriter _jsonl;
    private readonly TextWriter _previousOut;
    private readonly TextWriter _previousError;
    private readonly TextWriter? _humanWriter;
    private bool _disposed;

    private DaemonProcessConsole(
        JsonlLogWriter jsonl,
        TextWriter previousOut,
        TextWriter previousError,
        TextWriter? humanWriter)
    {
        _jsonl = jsonl;
        _previousOut = previousOut;
        _previousError = previousError;
        _humanWriter = humanWriter;
    }

    public static DaemonProcessConsole Initialize(
        string moduleId,
        string logsDirectory,
        IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        ArgumentNullException.ThrowIfNull(args);

        Directory.CreateDirectory(logsDirectory);
        var jsonl = new JsonlLogWriter(Path.Combine(logsDirectory, moduleId + ".jsonl"), moduleId);
        var jsonlText = new JsonlLogTextWriter(jsonl);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var redirected = Console.IsOutputRedirected;
        var wantsCli = HasFlag(args, "--once")
            || HasFlag(args, "--console")
            || HasFlag(args, "--register-autostart")
            || HasFlag(args, "--unregister-autostart");

        TextWriter? humanOut = null;
        TextWriter? humanError = null;
        if (redirected)
        {
            humanOut = previousOut;
            humanError = previousError;
        }
        else if (wantsCli && OperatingSystem.IsWindows())
        {
            if (TryAttachWindowsConsole(HasFlag(args, "--console"), out var attachedOut, out var attachedError))
            {
                humanOut = attachedOut;
                humanError = attachedError;
            }
        }
        else if (wantsCli)
        {
            humanOut = previousOut;
            humanError = previousError;
        }

        if (humanOut is null)
        {
            Console.SetOut(jsonlText);
            Console.SetError(jsonlText);
        }
        else
        {
            Console.SetOut(new TeeTextWriter(humanOut, jsonlText));
            Console.SetError(new TeeTextWriter(humanError ?? humanOut, jsonlText));
        }

        return new DaemonProcessConsole(jsonl, previousOut, previousError, humanOut);
    }

    public void ConfigureHostLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        logging.AddProvider(new JsonlLoggerProvider(_jsonl));
        if (_humanWriter is not null)
        {
            logging.AddProvider(new TextWriterLoggerProvider(_humanWriter));
        }

        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        logging.AddFilter("System", LogLevel.Warning);
        logging.AddFilter("Grpc", LogLevel.Warning);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (ObjectDisposedException)
        {
        }

        Console.SetOut(_previousOut);
        Console.SetError(_previousError);
        _jsonl.Dispose();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryAttachWindowsConsole(bool allowAlloc, out TextWriter? stdout, out TextWriter? stderr)
    {
        stdout = null;
        stderr = null;
        var attached = AttachConsole(AttachParentProcess);
        if (!attached && allowAlloc)
        {
            attached = AllocConsole();
        }

        if (!attached)
        {
            return false;
        }

        stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
        stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
        return true;
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}

public sealed class JsonlLogWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _moduleId;
    private bool _disposed;

    public JsonlLogWriter(string path, string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _path = path;
        _moduleId = moduleId;
    }

    public void Append(string level, string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = JsonSerializer.Serialize(
            new JsonlHostLogRecord(DateTimeOffset.UtcNow, _moduleId, NormalizeLevel(level), message.TrimEnd()),
            JsonOptions);
        lock (_gate)
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.WriteLine(line);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(20 * (attempt + 1));
                }
            }
        }
    }

    public void Dispose() => _disposed = true;

    internal static string NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return "info";
        }

        var normalized = level.Trim().ToLowerInvariant();
        return normalized switch
        {
            "warn" or "warning" => "warning",
            "err" or "error" or "fail" or "critical" or "fatal" or "crit" => "error",
            "trace" or "trce" or "debug" or "dbug" => "debug",
            "info" or "information" => "info",
            _ => "info"
        };
    }

    public static string DetectLevel(string message)
    {
        var sample = message.Length <= 160 ? message : message[..160];
        if (ContainsToken(sample, "fail:") ||
            ContainsToken(sample, "crit:") ||
            ContainsToken(sample, "error") ||
            ContainsToken(sample, "failed"))
        {
            return "error";
        }

        if (ContainsToken(sample, "warn:") || ContainsToken(sample, "warning"))
        {
            return "warning";
        }

        if (ContainsToken(sample, "dbug:") ||
            ContainsToken(sample, "trce:") ||
            ContainsToken(sample, "debug"))
        {
            return "debug";
        }

        return "info";
    }

    private static bool ContainsToken(string sample, string token) =>
        sample.Contains(token, StringComparison.OrdinalIgnoreCase);

    private sealed record JsonlHostLogRecord(DateTimeOffset Time, string ModuleId, string Level, string Message);
}

internal sealed class JsonlLogTextWriter : TextWriter
{
    private readonly JsonlLogWriter _jsonl;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();

    public JsonlLogTextWriter(JsonlLogWriter jsonl)
    {
        _jsonl = jsonl;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                FlushBuffer();
                return;
            }

            if (value != '\r')
            {
                _buffer.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            foreach (var ch in value)
            {
                if (ch == '\n')
                {
                    FlushBuffer();
                    continue;
                }

                if (ch != '\r')
                {
                    _buffer.Append(ch);
                }
            }
        }
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    public override void Flush()
    {
        lock (_gate)
        {
            FlushBuffer();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void FlushBuffer()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        var message = _buffer.ToString();
        _buffer.Clear();
        _jsonl.Append(JsonlLogWriter.DetectLevel(message), message);
    }
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _left;
    private readonly TextWriter _right;

    public TeeTextWriter(TextWriter left, TextWriter right)
    {
        _left = left;
        _right = right;
    }

    public override Encoding Encoding => _left.Encoding;

    public override void Write(char value)
    {
        _left.Write(value);
        _right.Write(value);
    }

    public override void Write(string? value)
    {
        _left.Write(value);
        _right.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _left.WriteLine(value);
        _right.WriteLine(value);
    }

    public override void Flush()
    {
        _left.Flush();
        _right.Flush();
    }
}

internal sealed class JsonlLoggerProvider : ILoggerProvider
{
    private readonly JsonlLogWriter _jsonl;

    public JsonlLoggerProvider(JsonlLogWriter jsonl)
    {
        _jsonl = jsonl;
    }

    public ILogger CreateLogger(string categoryName) => new JsonlLogger(_jsonl, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class JsonlLogger : ILogger
{
    private readonly JsonlLogWriter _jsonl;
    private readonly string _categoryName;

    public JsonlLogger(JsonlLogWriter jsonl, string categoryName)
    {
        _jsonl = jsonl;
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        if (exception is not null)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? exception.ToString()
                : message + " " + exception;
        }

        _jsonl.Append(MapLevel(logLevel), _categoryName + ": " + message);
    }

    private static string MapLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace or LogLevel.Debug => "debug",
        LogLevel.Warning => "warning",
        LogLevel.Error or LogLevel.Critical => "error",
        _ => "info"
    };
}

internal sealed class TextWriterLoggerProvider : ILoggerProvider
{
    private readonly TextWriter _writer;

    public TextWriterLoggerProvider(TextWriter writer)
    {
        _writer = writer;
    }

    public ILogger CreateLogger(string categoryName) => new TextWriterLogger(_writer, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class TextWriterLogger : ILogger
{
    private readonly TextWriter _writer;
    private readonly string _categoryName;

    public TextWriterLogger(TextWriter writer, string categoryName)
    {
        _writer = writer;
        _categoryName = categoryName;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message) && exception is null)
        {
            return;
        }

        _writer.WriteLine($"{ShortLevel(logLevel)}: {_categoryName}: {message}");
        if (exception is not null)
        {
            _writer.WriteLine(exception.ToString());
        }
    }

    private static string ShortLevel(LogLevel logLevel) => logLevel switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "info"
    };
}

internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    public void Dispose()
    {
    }
}
