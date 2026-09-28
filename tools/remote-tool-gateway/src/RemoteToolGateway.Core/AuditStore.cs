using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace RemoteToolGateway.Core;

/// <summary>
/// A local audit record. It carries what happened and who asked for it — never a token, a
/// credential or a raw argument value.
/// </summary>
public sealed record GatewayAuditEntry(
    string Time,
    string Kind,
    string GrantId,
    string DeviceName,
    string CommandId,
    string InvocationId,
    string State,
    string Message);

/// <summary>
/// Bounded, append-only audit file (<c>audit.jsonl</c>). It is rewritten only when the record cap
/// is reached, and every field is redacted and bounded before it reaches the disk.
/// </summary>
public sealed class AuditStore
{
    public const int MaxEntries = 500;

    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<GatewayAuditEntry> _entries = [];
    private Task _save = Task.CompletedTask;

    public AuditStore(string dataDirectory)
    {
        _path = Path.Combine(Path.GetFullPath(dataDirectory), "audit.jsonl");
    }

    public IReadOnlyList<GatewayAuditEntry> Recent(int max)
    {
        lock (_lock)
        {
            var count = Math.Min(Math.Max(0, max), _entries.Count);
            var result = new GatewayAuditEntry[count];
            for (var index = 0; index < count; index++) result[index] = _entries[_entries.Count - 1 - index];
            return result;
        }
    }

    /// <summary>A damaged audit line is skipped instead of aborting the load; the file is not rewritten.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return;
        var entries = new List<GatewayAuditEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                if (node is null) continue;
                entries.Add(new GatewayAuditEntry(
                    Read(node, "time"), Read(node, "kind"), Read(node, "grantId"), Read(node, "deviceName"),
                    Read(node, "commandId"), Read(node, "invocationId"), Read(node, "state"), Read(node, "message")));
            }
            catch (JsonException) { }
        }

        lock (_lock) _entries.AddRange(entries.TakeLast(MaxEntries));
    }

    public void Record(string kind, string grantId, string deviceName, string commandId, string invocationId, string state, string message)
    {
        var entry = new GatewayAuditEntry(
            DateTimeOffset.UtcNow.ToString("O"),
            ControlText.Bound(kind, 40),
            ControlText.Bound(grantId, 64),
            ControlText.Bound(deviceName, GrantStore.MaxDeviceNameLength),
            ControlText.Bound(commandId, ControlWire.MaxCommandIdLength),
            ControlText.Bound(invocationId, ControlWire.MaxInvocationIdLength),
            ControlText.Bound(state, 64),
            ControlText.Bound(MptLogRedactor.Redact(message ?? ""), ControlWire.MaxMessageLength));
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }

        QueuePersist();
    }

    /// <summary>Best effort and strictly ordered; a failed audit write never fails a request.</summary>
    private void QueuePersist()
    {
        GatewayAuditEntry[] snapshot;
        lock (_lock) snapshot = _entries.ToArray();
        _save = ChainAsync(_save, snapshot);
    }

    private async Task ChainAsync(Task previous, GatewayAuditEntry[] snapshot)
    {
        try { await previous; } catch (Exception) { }
        try
        {
            var lines = snapshot.Select(entry => new JsonObject
            {
                ["time"] = entry.Time,
                ["kind"] = entry.Kind,
                ["grantId"] = entry.GrantId,
                ["deviceName"] = entry.DeviceName,
                ["commandId"] = entry.CommandId,
                ["invocationId"] = entry.InvocationId,
                ["state"] = entry.State,
                ["message"] = entry.Message
            }.ToJsonString(ControlWire.Json));
            var temporary = _path + ".tmp";
            await File.WriteAllLinesAsync(temporary, lines, CancellationToken.None);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Waits for the queued write so tests and shutdown observe a complete audit file.</summary>
    public async Task FlushAsync()
    {
        Task pending;
        lock (_lock) pending = _save;
        try { await pending; } catch (Exception) { }
    }

    private static string Read(JsonObject node, string key)
    {
        try { return node[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }
}
