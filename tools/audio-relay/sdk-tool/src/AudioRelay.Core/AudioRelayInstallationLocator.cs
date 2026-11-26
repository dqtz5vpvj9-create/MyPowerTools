namespace AudioRelay.MyPowerTools;

public sealed record AudioRelayProductRegistration(
    string? InstallLocation,
    string? DisplayIcon,
    string? DisplayVersion);

public sealed record AudioRelayInstallation(string ExecutablePath, string Version);

public static class AudioRelayInstallationLocator
{
    public static AudioRelayInstallation? Find(
        IEnumerable<AudioRelayProductRegistration> registrations,
        IEnumerable<string> knownExecutablePaths,
        Func<string, bool> fileExists,
        Func<string, string?> readFileVersion)
    {
        var registrationList = registrations.ToArray();
        var candidates = registrationList.SelectMany(RegistrationCandidates)
            .Concat(knownExecutablePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            var path = candidate.Trim();
            if (!fileExists(path))
            {
                continue;
            }

            var registeredVersion = registrationList
                .FirstOrDefault(registration => RegistrationCandidates(registration)
                    .Contains(path, StringComparer.OrdinalIgnoreCase))?
                .DisplayVersion;
            var version = FirstNonEmpty(readFileVersion(path), registeredVersion, "未知");
            return new AudioRelayInstallation(path, version);
        }

        return null;
    }

    public static IEnumerable<string> RegistrationCandidates(AudioRelayProductRegistration registration)
    {
        var iconPath = ParseDisplayIcon(registration.DisplayIcon);
        if (iconPath is not null)
        {
            yield return iconPath;
        }

        if (!string.IsNullOrWhiteSpace(registration.InstallLocation))
        {
            yield return Path.Combine(registration.InstallLocation.Trim(), "AudioRelay.exe");
            yield return Path.Combine(registration.InstallLocation.Trim(), "audiorelay.exe");
        }
    }

    public static string? ParseDisplayIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim();
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..], out _))
        {
            value = value[..comma].Trim();
        }

        return value.Trim('"');
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;
}
