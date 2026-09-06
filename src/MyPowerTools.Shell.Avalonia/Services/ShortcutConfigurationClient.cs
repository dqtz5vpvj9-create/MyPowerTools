using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.HostControl;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>Read-through cache. Only Runner writes hotkeys.json, including imports and resets.</summary>
public sealed class ShortcutConfigurationClient
{
    private readonly Func<CancellationToken, Task<ShortcutCatalogSnapshot>> _read;
    private readonly Func<ulong, IReadOnlyList<ShortcutEdit>, CancellationToken, Task<ShortcutCatalogSnapshot>> _write;
    private readonly bool _allowLocalFallback;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ShortcutConfigurationClient(
        Func<CancellationToken, Task<ShortcutCatalogSnapshot>>? read = null,
        Func<ulong, IReadOnlyList<ShortcutEdit>, CancellationToken, Task<ShortcutCatalogSnapshot>>? write = null)
    {
        _allowLocalFallback = read is null;
        _read = read ?? ReadRemoteAsync;
        _write = write ?? WriteRemoteAsync;
        Snapshot = new(new(0, []), ShortcutCatalog.ShellCommands, [], PlatformId.Current().OperatingSystem);
    }

    public ShortcutCatalogSnapshot Snapshot { get; private set; }
    public bool IsLoaded { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { Accept(await _read(cancellationToken)); }
        catch
        {
            if (!IsLoaded && _allowLocalFallback)
            {
                // No writes or new independent store: the Runner's existing state file is read
                // only for startup recovery. A corrupt file must not re-enable shipped defaults.
                var directory = Path.Combine(HostControlAuthTokenStore.DefaultDataRoot(), "state");
                var state = Directory.Exists(directory) ? new HotkeyStore(directory).ReadShortcuts() : new ShortcutConfiguration(0, []);
                Accept(Snapshot with { Configuration = state, SystemStatus = "Runner unavailable; showing saved application bindings. System registration is unknown." });
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(IReadOnlyList<ShortcutEdit> edits, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsLoaded) Accept(await _read(cancellationToken));
            // A revision conflict is surfaced, not blindly retried over another editor's changes.
            Accept(await _write(Snapshot.Configuration.Revision, edits, cancellationToken));
        }
        finally { _gate.Release(); }
    }

    private void Accept(ShortcutCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
        IsLoaded = true;
        Changed?.Invoke();
    }

    public string Hint(string commandId) => string.Join(" / ", ShortcutCatalog.Effective(Snapshot)
        .Where(item => item.Definition.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase))
        .Select(item => Display(item.Gesture, Snapshot.Platform)).Distinct());

    public IReadOnlyDictionary<string, string> Hints() => Snapshot.Commands
        .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(item => item.Id, item => Hint(item.Id), StringComparer.OrdinalIgnoreCase);

    public static string Display(string gesture, string platform) => platform == "macos"
        ? gesture.Replace("Win+", "Cmd+", StringComparison.OrdinalIgnoreCase) : gesture;

    private static async Task<ShortcutCatalogSnapshot> ReadRemoteAsync(CancellationToken token)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var result = await client.GetSettingsAsync(ShortcutCatalog.SettingsModuleId, token);
        return Decode(result.Values);
    }

    private static async Task<ShortcutCatalogSnapshot> WriteRemoteAsync(ulong revision,
        IReadOnlyList<ShortcutEdit> edits, CancellationToken token)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var patch = new JsonObject { ["edits"] = JsonSerializer.SerializeToNode(edits, ShortcutCatalog.JsonOptions) };
        var result = await client.UpdateSettingsAsync(ShortcutCatalog.SettingsModuleId, revision, JsonStructMapper.ToStruct(patch), token);
        return Decode(result.Values);
    }

    private static ShortcutCatalogSnapshot Decode(Google.Protobuf.WellKnownTypes.Struct values) =>
        JsonStructMapper.ToJsonObject(values).Deserialize<ShortcutCatalogSnapshot>(ShortcutCatalog.JsonOptions)
            ?? throw new InvalidDataException("Runner did not return a shortcut catalog.");
}
