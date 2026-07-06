using System.Text.RegularExpressions;

namespace MyPowerTools.Platform.Abstractions;

public interface IPlatformPathService
{
    string ExpandRuntimePath(string value);
}

public sealed partial class PlatformPathService : IPlatformPathService
{
    public string ExpandRuntimePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var expanded = ExpandTilde(value.Trim());
        expanded = WindowsVariableRegex().Replace(expanded, match => ResolveVariable(match.Groups[1].Value) ?? match.Value);
        expanded = UnixVariableRegex().Replace(expanded, match => ResolveVariable(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) ?? match.Value);
        return expanded;
    }

    private static string ExpandTilde(string value)
    {
        if (value == "~")
        {
            return UserProfile();
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(UserProfile(), value[2..]);
        }

        return value;
    }

    private static string? ResolveVariable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var environment = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return environment;
        }

        return name.ToUpperInvariant() switch
        {
            "XDG_RUNTIME_DIR" => Path.Combine(Path.GetTempPath(), "mypowertools-runtime"),
            "TMPDIR" => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "APPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "USERPROFILE" => UserProfile(),
            _ => null
        };
    }

    private static string UserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Personal)
            : profile;
    }

    [GeneratedRegex("%([^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsVariableRegex();

    [GeneratedRegex("\\$\\{([A-Za-z_][A-Za-z0-9_]*)\\}|\\$([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex UnixVariableRegex();
}
