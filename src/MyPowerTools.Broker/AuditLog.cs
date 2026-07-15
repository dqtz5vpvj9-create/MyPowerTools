using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MyPowerTools.Broker;

public sealed class AuditLog
{
    private readonly string _path;
    private readonly string? _protectedRoot;

    public AuditLog(string path, string? protectedRoot = null)
    {
        _path = Path.GetFullPath(path);
        _protectedRoot = string.IsNullOrWhiteSpace(protectedRoot) ? null : Path.GetFullPath(protectedRoot);
        if (_protectedRoot is not null && !IsInside(_protectedRoot, _path))
        {
            throw new InvalidOperationException("The protected audit path must remain under the Broker installation root.");
        }

        var directory = Path.GetDirectoryName(_path)!;
        if (_protectedRoot is not null && WindowsProtectedExecutable.ContainsReparsePoint(_protectedRoot))
        {
            throw new InvalidOperationException("The protected Broker audit root contains a reparse point.");
        }
        Directory.CreateDirectory(directory);
        if (_protectedRoot is not null && !ProtectedPathIsSafe())
        {
            throw new InvalidOperationException("The protected Broker audit path contains a reparse point.");
        }
    }

    public string LastWriteError { get; private set; } = "";

    public void Append(BrokerAuditEntry entry)
    {
        _ = TryAppend(entry);
    }

    public bool TryAppend(BrokerAuditEntry entry)
    {
        var sanitized = entry with
        {
            Reason = AuditRedactor.Redact(entry.Reason),
            Scope = AuditRedactor.Redact(entry.Scope),
            Rollback = AuditRedactor.Redact(entry.Rollback)
        };
        try
        {
            if (_protectedRoot is not null && !ProtectedPathIsSafe())
            {
                LastWriteError = "Protected audit path failed reparse validation.";
                return false;
            }

            var line = JsonSerializer.Serialize(sanitized, BrokerAuditJsonContext.Default.BrokerAuditEntry);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            LastWriteError = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastWriteError = ex.GetType().Name;
            return false;
        }
    }

    public IReadOnlyList<BrokerAuditEntry> ReadAll()
    {
        if (!File.Exists(_path) || (_protectedRoot is not null && !ProtectedPathIsSafe()))
        {
            return [];
        }

        try
        {
            return File.ReadLines(_path)
                .Select(line => JsonSerializer.Deserialize(line, BrokerAuditJsonContext.Default.BrokerAuditEntry))
                .Where(entry => entry is not null)
                .Cast<BrokerAuditEntry>()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private bool ProtectedPathIsSafe()
    {
        if (_protectedRoot is null || !IsInside(_protectedRoot, _path))
        {
            return false;
        }
        if (WindowsProtectedExecutable.ContainsReparsePoint(_protectedRoot))
        {
            return false;
        }

        var current = new DirectoryInfo(Path.GetDirectoryName(_path)!);
        var root = new DirectoryInfo(_protectedRoot);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if (string.Equals(current.FullName, root.FullName, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }

        return current is not null &&
               (!File.Exists(_path) || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) == 0);
    }

    private static bool IsInside(string root, string path)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(BrokerAuditEntry))]
internal sealed partial class BrokerAuditJsonContext : JsonSerializerContext;

internal static class AuditRedactor
{
    private static readonly Regex SensitivePattern = new(
        "(token|secret|password|cookie|authorization|apiKey|accessKey|refreshToken)=([^\\s;,&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Redact(string value) => SensitivePattern.Replace(value, "$1=****");
}
