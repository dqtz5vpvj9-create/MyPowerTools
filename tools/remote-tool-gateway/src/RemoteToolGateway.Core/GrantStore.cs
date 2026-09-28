using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;

namespace RemoteToolGateway.Core;

/// <summary>One phone's authorization on this computer. Never contains the token itself.</summary>
public sealed record GatewayGrant(
    string GrantId,
    string DeviceName,
    bool AllowElevated,
    IReadOnlyList<string> CommandIds,
    string SecretUri,
    string CreatedAt,
    string LastUsedAt)
{
    public bool Allows(string commandId) => CommandIds.Contains(commandId, StringComparer.Ordinal);
}

/// <summary>
/// Grant metadata lives in <c>grants.json</c>; the token lives only in the platform secret store
/// and is referenced by <see cref="GatewayGrant.SecretUri"/>. A token is compared by SHA-256 so
/// the comparison is constant time and no plaintext token index is kept in memory.
/// </summary>
public sealed class GrantStore
{
    public const int MaxGrants = 64;
    public const int MaxCommandsPerGrant = 400;
    public const int MaxDeviceNameLength = 120;

    private readonly string _directory;
    private readonly ISecretStore _secrets;
    private readonly string _moduleId;
    private readonly SemaphoreSlim _write = new(1, 1);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(30);

    private readonly object _stateLock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastTouch = new(StringComparer.Ordinal);
    private List<GatewayGrant> _grants = [];

    public GrantStore(string dataDirectory, ISecretStore secrets, string moduleId)
    {
        _directory = Path.GetFullPath(dataDirectory);
        _secrets = secrets;
        _moduleId = moduleId;
    }

    private string FilePath => System.IO.Path.Combine(_directory, "grants.json");

    public IReadOnlyList<GatewayGrant> Grants
    {
        get { lock (_stateLock) return _grants.ToArray(); }
    }

    /// <summary>A damaged grant file is reported with its path; it is never silently reset.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        if (!File.Exists(FilePath)) return;
        JsonArray array;
        try
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath, cancellationToken)) as JsonObject
                ?? throw new InvalidDataException("授权文件的根节点必须是对象。");
            array = root["grants"] as JsonArray ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"电脑工具授权文件已损坏，未做任何修改：{FilePath}。请修复或重命名该文件后重新加载工具。", ex);
        }

        var grants = new List<GatewayGrant>();
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var grantId = ReadString(item, "grantId");
            if (!IsSafeId(grantId)) continue;
            grants.Add(new GatewayGrant(
                grantId,
                ControlText.Bound(ReadString(item, "deviceName"), MaxDeviceNameLength),
                ReadBool(item, "allowElevated"),
                NormalizeCommands(ReadStrings(item, "commandIds")),
                ReadString(item, "secretUri"),
                ReadString(item, "createdAt"),
                ReadString(item, "lastUsedAt")));
        }

        lock (_stateLock) _grants = grants;
    }

    /// <summary>Creates a grant with a fresh 256-bit token. The caller shows the code once.</summary>
    public async Task<(GatewayGrant Grant, string Token)> CreateAsync(
        string deviceName,
        IReadOnlyList<string> commandIds,
        bool allowElevated,
        CancellationToken cancellationToken)
    {
        var name = ControlText.Bound(deviceName, MaxDeviceNameLength);
        if (name.Length == 0) throw new ArgumentException("请填写手机名称。");
        await _write.WaitAsync(cancellationToken);
        try
        {
            if (Grants.Count >= MaxGrants) throw new InvalidOperationException($"最多只能创建 {MaxGrants} 个设备授权。");
            var grantId = "g-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
            var reference = SecretReference.Create(_moduleId, "grant-" + grantId);
            await _secrets.SaveAsync(_moduleId, "grant-" + grantId, token, cancellationToken);
            var grant = new GatewayGrant(
                grantId,
                name,
                allowElevated,
                NormalizeCommands(commandIds),
                reference.Uri,
                DateTimeOffset.UtcNow.ToString("O"),
                "");
            lock (_stateLock) _grants = [.. _grants, grant];
            await WriteAsync(cancellationToken);
            return (grant, token);
        }
        finally { _write.Release(); }
    }

    /// <summary>
    /// Publishes a new authorization set synchronously (no I/O). The caller runs this inside its
    /// admission boundary, so a submission or a confirmation claim can never decide against the
    /// previous command list or the previous allowElevated flag.
    /// </summary>
    public GatewayGrant PublishUpdate(
        string grantId,
        string deviceName,
        IReadOnlyList<string> commandIds,
        bool allowElevated)
    {
        var name = ControlText.Bound(deviceName, MaxDeviceNameLength);
        if (name.Length == 0) throw new ArgumentException("请填写手机名称。");
        var existing = Find(grantId) ?? throw new ArgumentException("未找到该设备授权。");
        var updated = existing with
        {
            DeviceName = name,
            AllowElevated = allowElevated,
            CommandIds = NormalizeCommands(commandIds)
        };
        lock (_stateLock) _grants = _grants.Select(item => item.GrantId == grantId ? updated : item).ToList();
        return updated;
    }

    /// <summary>Publishes an update and persists it in one call.</summary>
    public async Task<GatewayGrant> UpdateAsync(
        string grantId,
        string deviceName,
        IReadOnlyList<string> commandIds,
        bool allowElevated,
        CancellationToken cancellationToken)
    {
        var updated = PublishUpdate(grantId, deviceName, commandIds, allowElevated);
        await SaveAsync(cancellationToken);
        return updated;
    }

    /// <summary>
    /// Revocation step 1, synchronous and immediate: the grant leaves the live authorization set,
    /// so every request that authenticates after this call fails even while the caller is still
    /// cancelling the grant's active invocations or deleting the secret.
    /// </summary>
    public void MarkRevoked(string grantId)
    {
        lock (_stateLock)
        {
            _grants = _grants.Where(item => item.GrantId != grantId).ToList();
            _lastTouch.Remove(grantId);
        }
    }

    /// <summary>
    /// Revocation step 2: removes the secret first so a copy of the file can never re-enable it,
    /// then persists the grant list without the grant.
    /// </summary>
    public async Task CompleteRevocationAsync(string grantId, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            await _secrets.DeleteAsync(SecretReference.Create(_moduleId, "grant-" + grantId), cancellationToken);
            MarkRevoked(grantId);
            await WriteAsync(cancellationToken);
        }
        finally { _write.Release(); }
    }

    /// <summary>Marks and completes a revocation in one call.</summary>
    public async Task RevokeAsync(string grantId, CancellationToken cancellationToken)
    {
        if (Find(grantId) is null) return;
        MarkRevoked(grantId);
        await CompleteRevocationAsync(grantId, cancellationToken);
    }

    public GatewayGrant? Find(string grantId) =>
        Grants.FirstOrDefault(item => string.Equals(item.GrantId, grantId, StringComparison.Ordinal));

    public async Task<string?> ReadTokenAsync(string grantId, CancellationToken cancellationToken)
    {
        var grant = Find(grantId);
        return grant is null ? null : await _secrets.ReadAsync(SecretReference.Create(_moduleId, "grant-" + grantId), cancellationToken);
    }

    /// <summary>
    /// Locates the grant owning a presented bearer token. Every stored token is compared as a
    /// SHA-256 digest; an unknown, revoked or foreign token (for example a file-transfer pairing
    /// token) simply finds no grant.
    /// </summary>
    public async Task<GatewayGrant?> FindByTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var presented = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        foreach (var grant in Grants)
        {
            var stored = await _secrets.ReadAsync(SecretReference.Create(_moduleId, "grant-" + grant.GrantId), cancellationToken);
            if (string.IsNullOrEmpty(stored)) continue;
            var candidate = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stored));
            if (CryptographicOperations.FixedTimeEquals(presented, candidate)) return grant;
        }

        return null;
    }

    /// <summary>
    /// Records the last successful use; best effort, never fails a request. Writes are throttled so
    /// a phone polling an invocation does not rewrite the grant file on every request.
    /// </summary>
    public async Task TouchAsync(string grantId, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            var last = _lastTouch.GetValueOrDefault(grantId, DateTimeOffset.MinValue);
            if (DateTimeOffset.UtcNow - last < TouchInterval) return;
            _lastTouch[grantId] = DateTimeOffset.UtcNow;
        }

        await _write.WaitAsync(cancellationToken);
        try
        {
            var existing = Find(grantId);
            if (existing is null) return;
            var updated = existing with { LastUsedAt = DateTimeOffset.UtcNow.ToString("O") };
            lock (_stateLock) _grants = _grants.Select(item => item.GrantId == grantId ? updated : item).ToList();
            await WriteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A failed timestamp write must not fail an authorized request.
        }
        finally { _write.Release(); }
    }

    public static IReadOnlyList<string> NormalizeCommands(IEnumerable<string>? commandIds)
    {
        if (commandIds is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in commandIds)
        {
            var value = ControlText.Bound(id, ControlWire.MaxCommandIdLength);
            if (value.Length == 0 || !IsSafeId(value)) continue;
            if (seen.Add(value) && result.Count < MaxCommandsPerGrant) result.Add(value);
        }

        return result;
    }

    private static bool IsSafeId(string value) =>
        value.Length is > 0 and <= ControlWire.MaxCommandIdLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>
    /// Persists the current grant list. The authorization set itself is published synchronously by
    /// the caller inside its decision boundary; this slow file/host I/O deliberately stays outside it.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try { await WriteAsync(cancellationToken); }
        finally { _write.Release(); }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        var array = new JsonArray();
        foreach (var grant in Grants)
        {
            array.Add(new JsonObject
            {
                ["grantId"] = grant.GrantId,
                ["deviceName"] = grant.DeviceName,
                ["allowElevated"] = grant.AllowElevated,
                ["commandIds"] = new JsonArray(grant.CommandIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["secretUri"] = grant.SecretUri,
                ["createdAt"] = grant.CreatedAt,
                ["lastUsedAt"] = grant.LastUsedAt
            });
        }

        var root = new JsonObject { ["version"] = 1, ["grants"] = array };
        var temporary = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporary, root.ToJsonString(ControlWire.Json), cancellationToken);
        File.Move(temporary, FilePath, overwrite: true);
    }

    private static string ReadString(JsonObject item, string key)
    {
        try { return item[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static bool ReadBool(JsonObject item, string key)
    {
        try { return item[key]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }

    private static IEnumerable<string> ReadStrings(JsonObject item, string key)
    {
        if (item[key] is not JsonArray array) yield break;
        foreach (var node in array)
        {
            string? value = null;
            try { value = node?.GetValue<string>(); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
            if (!string.IsNullOrWhiteSpace(value)) yield return value;
        }
    }
}
