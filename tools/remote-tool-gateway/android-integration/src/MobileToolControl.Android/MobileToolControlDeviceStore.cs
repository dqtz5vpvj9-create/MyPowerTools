using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;

namespace MobileToolControl.Android;

/// <summary>One imported computer. The grant token is deliberately not part of this record.</summary>
internal sealed record MobileToolDevice(
    string DeviceId,
    string DeviceName,
    string Endpoint,
    string Platform,
    DateTimeOffset ImportedAt,
    string LastState,
    string LastDetail,
    string LastCheckedAt);

/// <summary>
/// <c>devices.json</c>: the imported computers, written atomically and never containing a token.
/// A corrupt or unreadable file yields an empty list plus a load error instead of throwing into the
/// module lifecycle, so a bad file cannot stop the phone page from opening.
/// </summary>
internal sealed class MobileToolControlDeviceStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path = path ?? throw new ArgumentNullException(nameof(path));
    private readonly List<MobileToolDevice> _devices = [];

    public string Path => _path;

    public string LoadError { get; private set; } = "";

    public IReadOnlyList<MobileToolDevice> Devices => _devices;

    public void Load()
    {
        _devices.Clear();
        LoadError = "";
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (document?["devices"] is not JsonArray array)
            {
                LoadError = "devices.json 结构不正确，已忽略。";
                return;
            }

            foreach (var item in array)
            {
                if (item is not JsonObject device)
                {
                    continue;
                }

                var id = Read(device, "deviceId");
                if (id.Length == 0)
                {
                    continue;
                }

                _devices.Add(new MobileToolDevice(
                    id,
                    Read(device, "deviceName"),
                    Read(device, "endpoint"),
                    Read(device, "platform"),
                    ParseTime(Read(device, "importedAt")),
                    Read(device, "lastState"),
                    Read(device, "lastDetail"),
                    Read(device, "lastCheckedAt")));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadError = $"devices.json 读取失败：{exception.Message}";
        }
    }

    public MobileToolDevice? Find(string deviceId) => _devices.FirstOrDefault(device =>
        string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));

    /// <summary>Inserts or replaces the device with the same grant id (re-import of the same code).</summary>
    public void Upsert(MobileToolDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var index = _devices.FindIndex(existing =>
            string.Equals(existing.DeviceId, device.DeviceId, StringComparison.Ordinal));
        if (index >= 0)
        {
            _devices[index] = device;
        }
        else
        {
            _devices.Add(device);
        }

        Save();
    }

    public bool Remove(string deviceId)
    {
        var index = _devices.FindIndex(device =>
            string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        _devices.RemoveAt(index);
        Save();
        return true;
    }

    public void UpdateProbe(string deviceId, string state, string detail)
    {
        var index = _devices.FindIndex(device =>
            string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal));
        if (index < 0)
        {
            return;
        }

        _devices[index] = _devices[index] with
        {
            LastState = state,
            LastDetail = detail,
            LastCheckedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        Save();
    }

    private void Save()
    {
        var document = new JsonObject
        {
            ["version"] = 1,
            ["devices"] = new JsonArray(_devices.Select(device => (JsonNode)new JsonObject
            {
                ["deviceId"] = device.DeviceId,
                ["deviceName"] = device.DeviceName,
                ["endpoint"] = device.Endpoint,
                ["platform"] = device.Platform,
                ["importedAt"] = device.ImportedAt.ToString("O"),
                ["lastState"] = device.LastState,
                ["lastDetail"] = device.LastDetail,
                ["lastCheckedAt"] = device.LastCheckedAt
            }).ToArray())
        };

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(Options), new UTF8Encoding(false));
        File.Move(temporary, _path, overwrite: true);
    }

    private static string Read(JsonObject document, string name) =>
        document.TryGetPropertyValue(name, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text)
            ? text
            : "";

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
}

/// <summary>
/// Grant-token access. The token lives only in the platform <c>secret.store</c>; the reference is
/// <c>secret://mobile-tool-control/&lt;sanitized deviceId&gt;.token</c>. Only the sanitized name ever
/// leaves this class (the store validates the name shape), and no method returns the token to a
/// caller other than the HTTP client that needs it for the Authorization header.
/// </summary>
internal sealed class MobileToolControlSecrets(ISecretStore store)
{
    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public static string SanitizeDeviceId(string deviceId)
    {
        var builder = new StringBuilder(deviceId.Length);
        foreach (var character in deviceId.Trim())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '-');
        }

        return builder.Length == 0 ? "device" : builder.ToString();
    }

    public static string SecretName(string deviceId) =>
        SanitizeDeviceId(deviceId) + MobileToolControlOptions.SecretNameSuffix;

    public Task<string?> ReadAsync(string deviceId, CancellationToken cancellationToken) =>
        _store.ReadAsync(SecretReference.Create(MobileToolControlOptions.ModuleId, SecretName(deviceId)), cancellationToken);

    public Task SaveAsync(string deviceId, string token, CancellationToken cancellationToken) =>
        _store.SaveAsync(MobileToolControlOptions.ModuleId, SecretName(deviceId), token, cancellationToken);

    public Task DeleteAsync(string deviceId, CancellationToken cancellationToken) =>
        _store.DeleteAsync(SecretReference.Create(MobileToolControlOptions.ModuleId, SecretName(deviceId)), cancellationToken);

    public async Task<bool> HasTokenAsync(string deviceId, CancellationToken cancellationToken) =>
        !string.IsNullOrEmpty(await ReadAsync(deviceId, cancellationToken).ConfigureAwait(false));
}
