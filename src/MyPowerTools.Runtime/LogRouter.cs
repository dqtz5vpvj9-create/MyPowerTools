using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace MyPowerTools.Runtime;

public sealed class LogRouter
{
    private static readonly Regex SensitivePattern = new("(token|secret|password|cookie|authorization|apiKey|accessKey|refreshToken)=([^\\s;,&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

        return File.ReadLines(path)
            .Reverse()
            .Take(limit)
            .Reverse()
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonSerializer.Deserialize<LogRecord>(line))
            .Where(record => record is not null)
            .Cast<LogRecord>()
            .ToArray();
    }

    public static string Redact(string value) => SensitivePattern.Replace(value, "$1=****");

    private static void AppendLine(string path, string line)
    {
        var gate = FileLocks.GetOrAdd(path, _ => new object());
        lock (gate)
        {
            for (var attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
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
