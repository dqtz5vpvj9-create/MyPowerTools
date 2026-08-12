using System.Globalization;
using System.Text.Json;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed record LocalLogFileSnapshot(
    IReadOnlyList<string> ModuleIds,
    IReadOnlyList<LogLineViewModel> Lines,
    string StatusText);

/// <summary>
/// Reads the persistent JSONL/text logs under %LOCALAPPDATA%\MyPowerTools\logs.
/// Used as a fallback when the Runner/HostControl endpoint is not reachable so
/// the Logs page still shows real history instead of an empty decoration.
/// </summary>
public static class LocalLogFileReader
{
    private const int MaxLinesPerSource = 500;
    private const int MaxTotalLines = 2000;

    public static LocalLogFileSnapshot Read(string dataRoot)
    {
        var logsDir = Path.Combine(dataRoot, "logs");
        var moduleIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(DateTimeOffset Timestamp, string Level, string Message, string ModuleId)>();

        if (Directory.Exists(logsDir))
        {
            foreach (var file in Directory.GetFiles(logsDir, "*.jsonl"))
            {
                var moduleId = Path.GetFileNameWithoutExtension(file);
                moduleIds.Add(moduleId);
                ReadJsonl(file, moduleId, entries);
            }

            foreach (var file in Directory.GetFiles(logsDir, "*.log"))
            {
                var moduleId = Path.GetFileNameWithoutExtension(file);
                moduleIds.Add(moduleId);
                ReadTextLog(file, moduleId, entries);
            }
        }

        var ordered = entries
            .OrderByDescending(entry => entry.Timestamp)
            .Take(MaxTotalLines)
            .Select(entry => new LogLineViewModel(
                entry.Timestamp.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Level,
                entry.Message,
                entry.Timestamp,
                entry.ModuleId))
            .ToArray();

        var statusText = moduleIds.Count == 0
            ? $"日志目录不存在或无日志文件：{logsDir}"
            : $"运行时未连接，已显示本地日志文件（{moduleIds.Count} 个来源，最近 {ordered.Length} 行）。";

        return new LocalLogFileSnapshot(moduleIds.ToArray(), ordered, statusText);
    }

    private static void ReadJsonl(
        string file,
        string fallbackModuleId,
        List<(DateTimeOffset Timestamp, string Level, string Message, string ModuleId)> entries)
    {
        var lines = ReadTail(file, MaxLinesPerSource);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var timestamp = TryParseTimestamp(root.TryGetProperty("Time", out var timeElement) ? timeElement.GetString() : null)
                    ?? File.GetLastWriteTimeUtc(file);
                var level = NormalizeLevel(root.TryGetProperty("Level", out var levelElement) ? levelElement.GetString() : null);
                var message = root.TryGetProperty("Message", out var messageElement) ? messageElement.GetString() : line;
                var moduleId = root.TryGetProperty("ModuleId", out var moduleElement) && !string.IsNullOrWhiteSpace(moduleElement.GetString())
                    ? moduleElement.GetString()!
                    : fallbackModuleId;
                entries.Add((timestamp, level, message ?? line, moduleId));
            }
            catch (JsonException)
            {
                // Malformed or partial tail line; skip rather than failing the page.
            }
        }
    }

    private static void ReadTextLog(
        string file,
        string fallbackModuleId,
        List<(DateTimeOffset Timestamp, string Level, string Message, string ModuleId)> entries)
    {
        var lines = ReadTail(file, MaxLinesPerSource);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var timestamp = TryParseTimestamp(ExtractLeadingTimestamp(line)) ?? File.GetLastWriteTimeUtc(file);
            var message = TrimLeadingTimestamp(line);
            var level = DetectLevel(message);
            entries.Add((timestamp, level, message, fallbackModuleId));
        }
    }

    private static string[] ReadTail(string file, int maxLines)
    {
        try
        {
            return File.ReadLines(file)
                .TakeLast(maxLines)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string? ExtractLeadingTimestamp(string line)
    {
        if (line.Length < 10 || line[0] is < '0' or > '9')
        {
            return null;
        }

        var end = line.IndexOf('\t');
        if (end < 0)
        {
            end = line.IndexOf(' ');
        }

        return end > 0 ? line[..end] : line;
    }

    private static string TrimLeadingTimestamp(string line)
    {
        var end = line.IndexOf('\t');
        if (end >= 0)
        {
            return line[(end + 1)..];
        }

        if (line.Length >= 10 && line[0] is >= '0' and <= '9')
        {
            var space = line.IndexOf(' ');
            if (space > 0)
            {
                return line[(space + 1)..];
            }
        }

        return line;
    }

    private static string DetectLevel(string message)
    {
        var sample = message.Length <= 160 ? message : message[..160];
        if (sample.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "error";
        }

        if (sample.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("warn", StringComparison.OrdinalIgnoreCase))
        {
            return "warning";
        }

        if (sample.Contains("debug", StringComparison.OrdinalIgnoreCase))
        {
            return "debug";
        }

        return "info";
    }

    private static string NormalizeLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return "info";
        }

        var normalized = level.Trim().ToLowerInvariant();
        return normalized is "info" or "warning" or "warn" or "error" or "debug" or "fatal" or "critical"
            ? normalized
            : "info";
    }
}
