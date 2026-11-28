using System.Text.Json;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// Persisted configuration for where completed Android downloads are written so external file
/// managers can access them directly. Pure file/string rules live here so path formatting and
/// preference persistence stay testable off-device.
/// </summary>
public sealed record AndroidExternalStorageConfig(
    string Mode,
    string TreeUri,
    string DisplayPath,
    bool SetupCompleted,
    DateTimeOffset? UpdatedAtUtc)
{
    public const string ModeUnset = "unset";
    public const string ModeSaf = "saf";
    public const string ModeMediaStore = "mediastore";

    public const string DefaultMediaStoreDisplayPath = "系统下载目录 / Download / MPT";

    public static AndroidExternalStorageConfig Default => new(
        ModeUnset,
        "",
        DefaultMediaStoreDisplayPath,
        SetupCompleted: false,
        UpdatedAtUtc: null);
}

public static class AndroidExternalStoragePreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DefaultPreferencesPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyPowerTools",
        "state",
        "android-external-storage.json");

    public static AndroidExternalStorageConfig Load(string? path = null)
    {
        var resolved = string.IsNullOrWhiteSpace(path) ? DefaultPreferencesPath() : path;
        if (!File.Exists(resolved))
        {
            return AndroidExternalStorageConfig.Default;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AndroidExternalStorageConfig>(File.ReadAllText(resolved), JsonOptions);
            if (loaded is null)
            {
                return AndroidExternalStorageConfig.Default;
            }

            var mode = NormalizeMode(loaded.Mode);
            var uri = (loaded.TreeUri ?? "").Trim();
            var display = string.IsNullOrWhiteSpace(loaded.DisplayPath)
                ? mode == AndroidExternalStorageConfig.ModeSaf && uri.Length > 0
                    ? FormatTreeUriDisplayPath(uri)
                    : AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath
                : loaded.DisplayPath.Trim();
            return loaded with { Mode = mode, TreeUri = uri, DisplayPath = display };
        }
        catch
        {
            return AndroidExternalStorageConfig.Default;
        }
    }

    public static void Save(AndroidExternalStorageConfig config, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resolved = string.IsNullOrWhiteSpace(path) ? DefaultPreferencesPath() : path;
        var directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var normalized = config with
        {
            Mode = NormalizeMode(config.Mode),
            TreeUri = (config.TreeUri ?? "").Trim(),
            DisplayPath = string.IsNullOrWhiteSpace(config.DisplayPath)
                ? AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath
                : config.DisplayPath.Trim(),
            UpdatedAtUtc = config.UpdatedAtUtc ?? DateTimeOffset.UtcNow
        };
        var temp = resolved + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(normalized, JsonOptions));
        File.Move(temp, resolved, overwrite: true);
    }

    /// <summary>
    /// Converts a Storage Access Framework tree URI into a clean user-facing directory label
    /// (for example <c>内部存储 / Download / MPT</c>) instead of raw percent-encoded URI segments.
    /// </summary>
    public static string FormatTreeUriDisplayPath(string? treeUri)
    {
        if (string.IsNullOrWhiteSpace(treeUri))
        {
            return AndroidExternalStorageConfig.DefaultMediaStoreDisplayPath;
        }

        var trimmed = treeUri.Trim();
        var treeIndex = trimmed.IndexOf("/tree/", StringComparison.OrdinalIgnoreCase);
        if (treeIndex < 0)
        {
            return "已授权外部目录";
        }

        var tail = trimmed[(treeIndex + "/tree/".Length)..];
        var docIndex = tail.IndexOf("/document/", StringComparison.OrdinalIgnoreCase);
        if (docIndex >= 0)
        {
            tail = tail[..docIndex];
        }

        var decoded = Uri.UnescapeDataString(tail).Trim('/');
        if (decoded.Length == 0)
        {
            return "已授权外部目录";
        }

        if (decoded.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
            decoded.Equals("msd:downloads", StringComparison.OrdinalIgnoreCase))
        {
            return "系统下载目录 / Download";
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0)
        {
            return decoded.Replace("/", " / ");
        }

        var volume = decoded[..colon].Trim();
        var relative = decoded[(colon + 1)..].Trim('/');
        var volumeLabel = volume.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? "内部存储"
            : volume.Equals("home", StringComparison.OrdinalIgnoreCase)
                ? "文档目录"
                : $"外部存储 ({volume})";

        if (relative.Length == 0)
        {
            return volumeLabel;
        }

        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return volumeLabel + " / " + string.Join(" / ", parts);
    }

    private static string NormalizeMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        AndroidExternalStorageConfig.ModeSaf => AndroidExternalStorageConfig.ModeSaf,
        AndroidExternalStorageConfig.ModeMediaStore => AndroidExternalStorageConfig.ModeMediaStore,
        _ => AndroidExternalStorageConfig.ModeUnset
    };
}
