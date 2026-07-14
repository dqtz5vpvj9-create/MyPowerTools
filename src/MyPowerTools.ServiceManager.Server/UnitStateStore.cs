using System.Collections.Concurrent;
using System.Text;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Persists per-unit runtime state (PID + instance token) under a state directory,
/// so a restarted ServiceManager can re-adopt still-running unit processes.
/// Files are named <c>&lt;unitId&gt;.json</c> under <c>&lt;stateRoot&gt;/units/</c>.
/// </summary>
public sealed class UnitStateStore
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, object> _fileLocks = new();

    public UnitStateStore(string stateRoot)
    {
        _directory = Path.Combine(stateRoot, "units");
        Directory.CreateDirectory(_directory);
    }

    public void Save(UnitRuntimeState state)
    {
        var path = PathFor(state.UnitId);
        lock (_fileLocks.GetOrAdd(state.UnitId, _ => new object()))
        {
            File.WriteAllText(path, state.ToJson());
        }
    }

    public UnitRuntimeState? Load(string unitId)
    {
        var path = PathFor(unitId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return UnitRuntimeState.FromJson(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public void Delete(string unitId)
    {
        var path = PathFor(unitId);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private string PathFor(string unitId) => Path.Combine(_directory, $"{Sanitize(unitId)}.json");

    private static string Sanitize(string unitId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(unitId.Length);
        foreach (var c in unitId)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }
}
