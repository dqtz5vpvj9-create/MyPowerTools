using System.Text.Json;

namespace MyPowerTools.Runtime;

public sealed class RuntimeProcessPolicyStore
{
    private const int MaxHistoryEntries = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private RuntimeProcessPolicySnapshot? _snapshot;

    public RuntimeProcessPolicyStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "runtime.process-policies.json");
    }

    public RuntimeProcessPolicySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public IReadOnlyList<RuntimeProcessPolicyRecord> CurrentPolicies()
    {
        lock (_gate)
        {
            return Load().Policies;
        }
    }

    public IReadOnlyList<RuntimeProcessPolicyHistoryEntry> History(int limit)
    {
        lock (_gate)
        {
            return Load().History.Take(Math.Max(1, limit)).ToArray();
        }
    }

    public RuntimeProcessPolicySnapshot Record(RuntimeProcessPolicyResult result, string reason, string source, DateTimeOffset? expiresAt)
    {
        if (!result.Success)
        {
            return GetSnapshot();
        }

        lock (_gate)
        {
            var current = Load();
            var nextRevision = current.Revision + 1;
            var now = DateTimeOffset.UtcNow;
            var policyReason = string.IsNullOrWhiteSpace(reason) ? result.Message : reason.Trim();
            var policySource = string.IsNullOrWhiteSpace(source) ? "runtime" : source.Trim();
            var policies = current.Policies
                .Where(policy =>
                    !string.Equals(policy.TransportKind, result.TransportKind, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(policy.PoolKey, result.PoolKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!string.Equals(result.RestartPolicy, "automatic", StringComparison.OrdinalIgnoreCase))
            {
                policies.Add(new RuntimeProcessPolicyRecord(
                    result.TransportKind,
                    result.PoolKey,
                    result.RestartPolicy,
                    policyReason,
                    policySource,
                    result.ModuleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    now,
                    Normalize(expiresAt)));
            }

            var historyEntry = new RuntimeProcessPolicyHistoryEntry(
                nextRevision,
                now,
                result.TransportKind,
                result.PoolKey,
                result.RestartPolicy,
                policyReason,
                policySource,
                result.ModuleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                string.Equals(result.RestartPolicy, "automatic", StringComparison.OrdinalIgnoreCase) ? null : Normalize(expiresAt));

            var history = new[] { historyEntry }
                .Concat(current.History)
                .Take(MaxHistoryEntries)
                .ToArray();

            var next = new RuntimeProcessPolicySnapshot(
                nextRevision,
                policies
                    .OrderBy(policy => policy.TransportKind, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(policy => policy.PoolKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                history,
                now);
            _snapshot = next;
            Save(next);
            return next;
        }
    }

    public IReadOnlyList<RuntimeProcessPolicyRecord> Expire(DateTimeOffset now, string source)
    {
        lock (_gate)
        {
            var current = Load();
            var expired = current.Policies
                .Where(policy => policy.ExpiresAt is not null && policy.ExpiresAt.Value <= now)
                .ToArray();
            if (expired.Length == 0)
            {
                return [];
            }

            var policies = current.Policies
                .Except(expired)
                .ToArray();
            var history = current.History.ToList();
            var revision = current.Revision;
            foreach (var policy in expired.OrderBy(policy => policy.ExpiresAt))
            {
                revision++;
                history.Insert(0, new RuntimeProcessPolicyHistoryEntry(
                    revision,
                    now,
                    policy.TransportKind,
                    policy.PoolKey,
                    "automatic",
                    $"Restart policy expired at {policy.ExpiresAt!.Value:O}.",
                    string.IsNullOrWhiteSpace(source) ? "runtime.expiry" : source,
                    policy.ModuleIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    null));
            }

            var next = new RuntimeProcessPolicySnapshot(
                revision,
                policies,
                history.Take(MaxHistoryEntries).ToArray(),
                now);
            _snapshot = next;
            Save(next);
            return expired;
        }
    }

    private RuntimeProcessPolicySnapshot Load()
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        if (!File.Exists(_path))
        {
            _snapshot = new RuntimeProcessPolicySnapshot(1, [], [], DateTimeOffset.UtcNow);
            return _snapshot;
        }

        var snapshot = JsonSerializer.Deserialize<RuntimeProcessPolicySnapshot>(File.ReadAllText(_path), JsonOptions);
        _snapshot = snapshot ?? new RuntimeProcessPolicySnapshot(1, [], [], DateTimeOffset.UtcNow);
        return _snapshot;
    }

    private void Save(RuntimeProcessPolicySnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }

    private static DateTimeOffset? Normalize(DateTimeOffset? value)
    {
        return value?.ToUniversalTime();
    }
}
