using System.Text.Json;
using MyPowerTools.Runtime;

namespace MyPowerTools.Broker;

public sealed class AuditLog
{
    private readonly string _path;

    public AuditLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Append(BrokerAuditEntry entry)
    {
        var sanitized = entry with
        {
            Reason = LogRouter.Redact(entry.Reason),
            Scope = LogRouter.Redact(entry.Scope),
            Rollback = LogRouter.Redact(entry.Rollback)
        };
        File.AppendAllText(_path, JsonSerializer.Serialize(sanitized) + Environment.NewLine);
    }

    public IReadOnlyList<BrokerAuditEntry> ReadAll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return File.ReadLines(_path)
            .Select(line => JsonSerializer.Deserialize<BrokerAuditEntry>(line))
            .Where(entry => entry is not null)
            .Cast<BrokerAuditEntry>()
            .ToArray();
    }
}
