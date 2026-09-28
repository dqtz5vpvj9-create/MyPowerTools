namespace FileTransfer.Core.Discovery;

/// <summary>
/// Where Tailscale keeps its CLI and its LocalAPI socket on each supported platform. The paths come
/// from the Tailscale source of truth (<c>paths.DefaultTailscaledSocket</c> and the platform install
/// layouts), not from guesses: Windows <c>tailscale.exe</c> under Program Files and the
/// <c>\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled</c> pipe, macOS
/// <c>/Applications/Tailscale.app/Contents/MacOS/Tailscale</c> and <c>/var/run/tailscaled.socket</c>,
/// Linux <c>/usr/bin/tailscale</c> and <c>/var/run/tailscale/tailscaled.sock</c>.
/// <para>
/// Android and iOS are deliberately <see cref="Supported"/>=false: the mobile Tailscale clients embed
/// the daemon inside their own app sandbox, so an MPT process cannot reach a CLI or a LocalAPI socket
/// there. Reporting that honestly is required; a mobile install must not claim a Tailnet peer source
/// it does not have.
/// </para>
/// </summary>
public sealed record TailscaleEnvironment(
    string Platform,
    bool Supported,
    IReadOnlyList<string> CliPaths,
    IReadOnlyList<string> LocalApiPaths)
{
    /// <summary>LocalAPI host header value the daemon requires; the socket name doubles as the HTTP host.</summary>
    public const string LocalApiHost = "local-tailscaled.sock";

    /// <summary>Probes the current machine: which platform this is and every path worth trying.</summary>
    public static TailscaleEnvironment Detect()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pathLookup = OnPath("tailscale");
        if (OperatingSystem.IsWindows())
            return new TailscaleEnvironment("windows", true,
                Compact([
                    Combine(programFiles, "Tailscale", "tailscale.exe"),
                    Combine(programFilesX86, "Tailscale", "tailscale.exe"),
                    pathLookup,
                ]),
                [@"\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled"]);
        if (OperatingSystem.IsMacOS())
            return new TailscaleEnvironment("macos", true,
                Compact([
                    "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
                    "/usr/local/bin/tailscale",
                    "/opt/homebrew/bin/tailscale",
                    pathLookup,
                ]),
                [
                    "/var/run/tailscaled.socket",
                    "/var/run/tailscale/tailscaled.sock",
                    // App Store build: the sandboxed daemon's socket seen from outside the container.
                    "/Library/Containers/io.tailscale.ipn.macsys/Data/tailscaled.sock",
                ]);
        if (OperatingSystem.IsLinux())
            return new TailscaleEnvironment("linux", true,
                Compact([
                    "/usr/bin/tailscale",
                    "/usr/local/bin/tailscale",
                    "/snap/bin/tailscale",
                    "/var/packages/Tailscale/target/bin/tailscale",
                    pathLookup,
                ]),
                [
                    "/var/run/tailscale/tailscaled.sock",
                    "/var/run/tailscaled.socket",
                    "/var/packages/Tailscale/var/tailscaled.sock",
                ]);
        if (OperatingSystem.IsAndroid()) return Unsupported("android");
        if (OperatingSystem.IsIOS()) return Unsupported("ios");
        return Unsupported(OperatingSystem.IsBrowser() ? "browser" : "unknown");
    }

    /// <summary>Environment of another platform; used by tests to prove the unsupported diagnostics.</summary>
    public static TailscaleEnvironment ForPlatform(string platform) => platform switch
    {
        "windows" => new("windows", true, [@"C:\Program Files\Tailscale\tailscale.exe"],
            [@"\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled"]),
        "macos" => new("macos", true, ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale"],
            ["/var/run/tailscaled.socket"]),
        "linux" => new("linux", true, ["/usr/bin/tailscale", "/usr/local/bin/tailscale"],
            ["/var/run/tailscale/tailscaled.sock"]),
        _ => Unsupported(platform),
    };

    public static TailscaleEnvironment Unsupported(string platform) => new(platform, false, [], []);

    /// <summary>First <c>tailscale</c> executable on PATH, if any; covers Homebrew, Nix and custom installs.</summary>
    private static string? OnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Combine(directory, name);
            if (candidate.Length > 0 && File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows())
            {
                var exe = Combine(directory, name + ".exe");
                if (exe.Length > 0 && File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    private static string Combine(string directory, params string[] parts)
    {
        if (string.IsNullOrEmpty(directory)) return "";
        try { return Path.GetFullPath(Path.Combine([directory, .. parts])); }
        catch (ArgumentException) { return ""; }
    }

    private static IReadOnlyList<string> Compact(IEnumerable<string?> values) =>
        values.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).Distinct(StringComparer.Ordinal).ToArray();
}
