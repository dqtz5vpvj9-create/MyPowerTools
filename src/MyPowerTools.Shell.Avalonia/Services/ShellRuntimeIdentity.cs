using System.Text.Json;

namespace MyPowerTools.Shell.Avalonia.Services;

internal enum ShellRuntimeKind
{
    Installed,
    Development
}

internal sealed record ShellRuntimeIdentity(
    ShellRuntimeKind Kind,
    string Configuration,
    string? RepositoryRoot)
{
    public bool IsDevelopment => Kind == ShellRuntimeKind.Development;

    public string ModeLabel => IsDevelopment
        ? $"DEV · {Configuration}"
        : "INSTALLED";

    public string LocationLabel => IsDevelopment
        ? RepositoryRoot ?? ""
        : "";

    public string DisplayText => string.IsNullOrWhiteSpace(LocationLabel)
        ? ModeLabel
        : $"{ModeLabel} · {LocationLabel}";

    public string WindowCaption => $"MyPowerTools — {DisplayText}";
}

internal static class ShellRuntimeIdentityResolver
{
    private const string OverlayManifestFileName = "dev-update.manifest.json";
    private const string SolutionFileName = "MyPowerTools.slnx";

    public static ShellRuntimeIdentity Resolve(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var installedRoot = FindInstalledRoot(baseDirectory);
        if (installedRoot is not null)
        {
            var manifestPath = Path.Combine(installedRoot, OverlayManifestFileName);
            if (File.Exists(manifestPath))
            {
                return ReadDevelopmentOverlay(manifestPath);
            }

            return Installed();
        }

        var repositoryRoot = FindRepositoryRoot(baseDirectory);
        return repositoryRoot is null
            ? Installed()
            : Development(CurrentBuildConfiguration, repositoryRoot);
    }

    private static ShellRuntimeIdentity ReadDevelopmentOverlay(string manifestPath)
    {
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            var repositoryRoot = ReadString(root, "repositoryRoot");
            var configuration = ReadShellConfiguration(root)
                ?? ReadString(root, "configuration")
                ?? CurrentBuildConfiguration;
            return Development(configuration, NormalizeOptionalPath(repositoryRoot));
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException or
            NotSupportedException)
        {
            return Development(CurrentBuildConfiguration, null);
        }
    }

    private static string? ReadShellConfiguration(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var components) ||
            components.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var component in components.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Object ||
                !string.Equals(
                    ReadString(component, "relativePath"),
                    "Shell",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ReadString(component, "configuration");
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    private static string? FindInstalledRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Runner")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Shell")) &&
                Directory.Exists(Path.Combine(directory.FullName, "modules")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path.Trim();
        }
    }

    private static ShellRuntimeIdentity Installed() =>
        new(ShellRuntimeKind.Installed, "", null);

    private static ShellRuntimeIdentity Development(string configuration, string? repositoryRoot) =>
        new(
            ShellRuntimeKind.Development,
            string.IsNullOrWhiteSpace(configuration) ? CurrentBuildConfiguration : configuration.Trim(),
            repositoryRoot);

    private static string CurrentBuildConfiguration
    {
        get
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }
    }
}
