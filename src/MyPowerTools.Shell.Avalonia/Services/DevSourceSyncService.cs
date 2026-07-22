using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>
/// Local "developer source" sync: copies build outputs from a configured source directory into the
/// installed module directory so a tool page refresh can hot-reload them. Persisted as a JSON file
/// alongside the shell preferences. Pure file sync — no delta packaging, no process restart —
/// because DotnetSurfaceLoader already hot-reloads shadow-copied assemblies when the
/// LastWriteTimeUtc of a source file changes.
/// </summary>
public sealed class DevSourceSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;
    private readonly object _gate = new();
    private DevSourceSettings _settings;

    public DevSourceSyncService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? DefaultSettingsPath();
        _settings = Read(_settingsPath);
    }

    public DevSourceSettings Snapshot
    {
        get
        {
            lock (_gate)
            {
                return Clone(_settings);
            }
        }
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _settings.Enabled;
            }
        }
    }

    public bool SyncOnRefresh
    {
        get
        {
            lock (_gate)
            {
                return _settings.SyncOnRefresh;
            }
        }
    }

    public void Update(Action<DevSourceSettings> mutate)
    {
        lock (_gate)
        {
            mutate(_settings);
            Persist(_settingsPath, _settings);
        }
    }

    /// <summary>
    /// Syncs every mapping whose ToolId matches (or has no ToolId). When <paramref name="enabledOnly"/>
    /// is true and the global toggle is off, this is a no-op returning a clean summary.
    /// </summary>
    public Task<DevSourceSyncOutcome> SyncForToolAsync(string? toolId, bool enabledOnly = true)
    {
        List<DevSourceMapping> mappings;
        lock (_gate)
        {
            if (enabledOnly && !_settings.Enabled)
            {
                return Task.FromResult(DevSourceSyncOutcome.Disabled);
            }

            mappings = _settings.Mappings
                .Where(mapping => string.IsNullOrWhiteSpace(toolId)
                                  || string.IsNullOrWhiteSpace(mapping.ToolId)
                                  || string.Equals(mapping.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Task.FromResult(SyncMappings(mappings));
    }

    public Task<DevSourceSyncOutcome> SyncAllAsync()
    {
        return SyncForToolAsync(toolId: null);
    }

    private static DevSourceSyncOutcome SyncMappings(IReadOnlyList<DevSourceMapping> mappings)
    {
        if (mappings.Count == 0)
        {
            return new DevSourceSyncOutcome(0, 0, 0, "No developer sources are configured.", Array.Empty<string>());
        }

        var updated = 0;
        var skipped = 0;
        var errors = 0;
        var details = new List<string>();
        foreach (var mapping in mappings)
        {
            try
            {
                var (updatedInMapping, skippedInMapping) = SyncMapping(mapping);
                updated += updatedInMapping;
                skipped += skippedInMapping;
                details.Add(updatedInMapping > 0
                    ? $"{mapping.Name}: {updatedInMapping} updated, {skippedInMapping} unchanged."
                    : $"{mapping.Name}: up to date ({skippedInMapping} file(s)).");
            }
            catch (Exception ex)
            {
                errors++;
                details.Add($"{mapping.Name}: {ex.Message}");
            }
        }

        var summary = errors > 0
            ? $"Updated {updated}, skipped {skipped}, {errors} error(s)."
            : updated > 0
                ? $"Updated {updated} file(s), {skipped} unchanged."
                : "All configured sources are up to date.";
        return new DevSourceSyncOutcome(updated, skipped, errors, summary, details);
    }

    private static (int Updated, int Skipped) SyncMapping(DevSourceMapping mapping)
    {
        var sourceDir = ExpandPath(mapping.SourceDir);
        var targetDir = ExpandPath(mapping.TargetDir);
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            throw new InvalidOperationException("Source directory is empty.");
        }

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDir}");
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new InvalidOperationException("Target directory is empty.");
        }

        Directory.CreateDirectory(targetDir);
        var targetRoot = Path.GetFullPath(targetDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var patterns = mapping.FilePatterns is { Count: > 0 }
            ? mapping.FilePatterns
            : new List<string> { "*" };

        var updated = 0;
        var skipped = 0;
        foreach (var pattern in patterns)
        {
            IEnumerable<string> sources;
            try
            {
                sources = Directory.EnumerateFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var source in sources)
            {
                var fileName = Path.GetFileName(source);
                var targetPath = Path.GetFullPath(Path.Combine(targetDir, fileName));
                if (!targetPath.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Resolved target path escapes the target directory: {fileName}");
                }

                if (ShouldCopy(source, targetPath))
                {
                    File.Copy(source, targetPath, overwrite: true);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return (updated, skipped);
    }

    private static bool ShouldCopy(string source, string target)
    {
        if (!File.Exists(target))
        {
            return true;
        }

        var src = new FileInfo(source);
        var dst = new FileInfo(target);
        if (src.Length != dst.Length)
        {
            return true;
        }

        return src.LastWriteTimeUtc > dst.LastWriteTimeUtc;
    }

    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return Environment.ExpandEnvironmentVariables(path.Trim());
    }

    private static string DefaultSettingsPath()
    {
        return Path.Combine(
            HostControlAuthTokenStore.DefaultDataRoot(),
            "state",
            "dev-source-mappings.json");
    }

    private static DevSourceSettings Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new DevSourceSettings();
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<DevSourceSettings>(stream, JsonOptions) ?? new DevSourceSettings();
        }
        catch
        {
            return new DevSourceSettings();
        }
    }

    private static void Persist(string path, DevSourceSettings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static DevSourceSettings Clone(DevSourceSettings source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions);
        return JsonSerializer.Deserialize<DevSourceSettings>(json, JsonOptions)!;
    }
}

public sealed class DevSourceSettings
{
    public bool Enabled { get; set; }
    public bool SyncOnRefresh { get; set; } = true;
    public List<DevSourceMapping> Mappings { get; set; } = new();
}

public sealed class DevSourceMapping
{
    public string Name { get; set; } = "";
    public string SourceDir { get; set; } = "";
    public string TargetDir { get; set; } = "";
    public string? ToolId { get; set; }
    public List<string> FilePatterns { get; set; } = new() { "*" };
}

[DebuggerDisplay("{Summary}")]
public sealed record DevSourceSyncOutcome(
    int UpdatedFiles,
    int SkippedFiles,
    int Errors,
    string Summary,
    IReadOnlyList<string> Details)
{
    public static DevSourceSyncOutcome Disabled { get; } = new(0, 0, 0, "Developer source sync is disabled.", Array.Empty<string>());
}
