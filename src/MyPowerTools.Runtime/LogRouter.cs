using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyPowerTools.Runtime;

public sealed class LogRouter
{
    private static readonly Regex SensitivePattern = new("(token|secret|password|cookie|authorization|apiKey|accessKey|refreshToken)=([^\\s;,&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
        File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine);
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
            .Select(line => JsonSerializer.Deserialize<LogRecord>(line))
            .Where(record => record is not null)
            .Cast<LogRecord>()
            .ToArray();
    }

    public static string Redact(string value) => SensitivePattern.Replace(value, "$1=****");

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
