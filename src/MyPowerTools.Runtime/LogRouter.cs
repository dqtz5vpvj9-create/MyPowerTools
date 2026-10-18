using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class LogRouter
{
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _logDirectory;

    public LogRouter(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public LogRecord Append(string packageId, string moduleId, string level, string message, string? invocationId = null, ulong? eventSeq = null, string? requestId = null)
    {
        var record = new LogRecord(
            DateTimeOffset.UtcNow,
            packageId,
            moduleId,
            invocationId ?? "",
            eventSeq ?? 0,
            requestId ?? "",
            level,
            Redact(message));

        var path = Path.Combine(_logDirectory, $"{Sanitize(moduleId)}.jsonl");
        AppendLine(path, JsonSerializer.Serialize(record));
        return record;
    }

    public IReadOnlyList<LogRecord> Tail(string moduleId, int limit = 200)
    {
        var path = Path.Combine(_logDirectory, $"{Sanitize(moduleId)}.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        if (limit <= 0)
        {
            return [];
        }

        // Keep one handle throughout the read so rotation cannot mix two files.
        // The scanner and StreamReader provide buffering; avoid stale FileStream
        // read-ahead when an observed truncation requires restarting from zero.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        return ReadTailLines(stream, limit)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<LogRecord>(line))
            .Where(record => record is not null)
            .Cast<LogRecord>()
            .ToArray();
    }

    // Exposed internally so the regression harness can measure bytes actually read.
    // The caller owns the seekable stream. This is a live read, not an atomic snapshot.
    internal static string[] ReadTailLines(Stream stream, int limit)
    {
        if (limit <= 0)
        {
            return [];
        }

        var length = stream.Length;
        var offset = FindTailOffset(stream, length, limit);
        // A shrinking file invalidates the offsets found by the reverse scan.
        if (stream.Length < length)
        {
            offset = 0;
        }

        var lines = ReadFrom(offset);
        // Also handle truncation between locating the boundary and reading the tail.
        // A sequential fallback retains the previous live-reader behavior.
        return offset > 0 && stream.Length < length ? ReadFrom(0) : lines;

        string[] ReadFrom(long start)
        {
            stream.Position = start;
            // A BOM belongs only at the beginning of the file. In particular, do not
            // strip U+FEFF from a selected line in the middle of a UTF-8 file.
            using var reader = new StreamReader(stream, start == 0 ? Encoding.UTF8 : new UTF8Encoding(false),
                detectEncodingFromByteOrderMarks: start == 0, bufferSize: 4096, leaveOpen: true);
            return Lines().TakeLast(limit).ToArray();

            IEnumerable<string> Lines()
            {
                while (reader.ReadLine() is { } line)
                {
                    yield return line;
                }
            }
        }
    }

    private static long FindTailOffset(Stream stream, long length, int limit)
    {
        // StreamReader recognizes UTF-16/32 BOMs too. Keep its exact decoding and
        // line semantics for those uncommon files by using the sequential path.
        Span<byte> prefix = stackalloc byte[4];
        stream.Position = 0;
        if (length < prefix.Length || !ReadBlock(stream, prefix)
            || (prefix[0] == 0xff && prefix[1] == 0xfe)
            || (prefix[0] == 0xfe && prefix[1] == 0xff)
            || (prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff))
        {
            return 0;
        }

        var buffer = new byte[8192];
        var position = length;
        var delimiters = 0;
        byte next = 0;
        while (position > 0)
        {
            var count = (int)Math.Min(position, buffer.Length);
            position -= count;
            stream.Position = position;
            if (!ReadBlock(stream, buffer.AsSpan(0, count)))
            {
                return 0; // Truncated while scanning; do not inspect stale bytes.
            }

            for (var index = count - 1; index >= 0; index--)
            {
                var current = buffer[index];
                // ReadLine treats CRLF as one delimiter, and a final delimiter does
                // not create an additional line. CRLF may straddle two blocks.
                if ((current == '\n' || (current == '\r' && next != '\n'))
                    && position + index != length - 1 && ++delimiters == limit)
                {
                    return position + index + 1;
                }
                next = current;
            }
        }
        return 0;
    }

    private static bool ReadBlock(Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var count = stream.Read(buffer);
            if (count == 0)
            {
                return false;
            }
            buffer = buffer[count..];
        }
        return true;
    }

    public static string Redact(string value) => MptLogRedactor.Redact(value);

    private static void AppendLine(string path, string line)
    {
        var gate = FileLocks.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream);
                    writer.WriteLine(line);
                    return;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(20 * (attempt + 1)));
                }
            }
        }
    }

    private static string Sanitize(string value)
    {
        return string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' ? ch : '_'));
    }
}

public sealed record LogRecord(
    DateTimeOffset Time,
    string PackageId,
    string ModuleId,
    string InvocationId,
    ulong EventSeq,
    string RequestId,
    string Level,
    string Message);
