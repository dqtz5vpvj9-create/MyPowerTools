using System.Runtime.InteropServices;

namespace MyPowerTools.Packaging.Ota;

/// <summary>
/// Locates the pieces the OTA client needs at run time: the updater script, the product CLI
/// and a PowerShell 7 host. Windows keeps a flat install root; macOS keeps everything inside
/// <c>MyPowerTools.app</c>, where the Shell and Runner may live either directly under
/// <c>Contents/MacOS</c> or inside nested helper <c>.app</c> bundles.
/// </summary>
public static class OtaUpdaterLocator
{
    public const string UpdaterScriptName = "ota-update.ps1";
    public const string MacApplyScriptName = "ota-apply-macos.ps1";

    /// <summary>Path of the bundled OTA scripts, relative to a macOS app bundle root.</summary>
    public const string MacScriptsRelativePath = "Contents/Resources/scripts";

    /// <summary>Path of the product payload, relative to a macOS app bundle root.</summary>
    public const string MacProductRelativePath = "Contents/MacOS";

    public static string CliFileName(bool isWindows)
    {
        return isWindows ? "MyPowerTools.Cli.exe" : "MyPowerTools.Cli";
    }

    public static string CliFileName()
    {
        return CliFileName(RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
    }

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> and returns the outermost ancestor that
    /// is an app bundle (a <c>.app</c> directory holding <c>Contents/Info.plist</c>), so a Shell
    /// running from a nested helper bundle still resolves the container it was installed as.
    /// Returns <see langword="null"/> outside a bundle, for example in a source checkout.
    /// </summary>
    public static string? FindMacBundleRoot(string startDirectory, Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        fileExists ??= File.Exists;
        string? outermost = null;
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) &&
                fileExists(Path.Combine(directory.FullName, "Contents", "Info.plist")))
            {
                outermost = directory.FullName;
            }

            directory = directory.Parent;
        }

        return outermost;
    }

    /// <summary>
    /// The directory that holds the product payload: the install root on Windows, and
    /// <c>Contents/MacOS</c> inside the app bundle on macOS.
    /// </summary>
    public static string ProductRoot(string baseDirectory, string? macBundleRoot)
    {
        return macBundleRoot is null
            ? Path.GetFullPath(Path.Combine(baseDirectory, ".."))
            : Path.Combine(macBundleRoot, "Contents", "MacOS");
    }

    /// <summary>
    /// Candidate paths for <c>ota-update.ps1</c>, most specific first.
    /// <paramref name="repositoryRoot"/> is the root the CLI already resolved from its own
    /// location: the install root for a Windows installation, the repository root for a source
    /// checkout, and <c>Contents/MacOS</c> inside a macOS bundle.
    /// </summary>
    public static IReadOnlyList<string> UpdaterScriptCandidates(string repositoryRoot, string? macBundleRoot)
    {
        var candidates = new List<string>
        {
            Path.Combine(repositoryRoot, UpdaterScriptName),
            Path.Combine(repositoryRoot, "scripts", UpdaterScriptName)
        };

        if (macBundleRoot is not null)
        {
            candidates.Add(Path.Combine(macBundleRoot, "Contents", "Resources", "scripts", UpdaterScriptName));
        }

        return candidates;
    }

    /// <summary>
    /// Candidate paths for the product CLI, given the calling process's base directory.
    /// </summary>
    public static IReadOnlyList<string> CliCandidates(string baseDirectory, string? macBundleRoot, bool isWindows)
    {
        var fileName = CliFileName(isWindows);
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "Cli", fileName))
        };

        if (macBundleRoot is not null)
        {
            candidates.Add(Path.Combine(macBundleRoot, "Contents", "MacOS", "Cli", fileName));
        }

        candidates.Add(Path.Combine(baseDirectory, fileName));
        return candidates;
    }

    /// <summary>
    /// Candidate paths for a PowerShell 7 host: every <c>PATH</c> entry first, then the two
    /// locations the macOS packages install into but which a GUI process's inherited
    /// <c>PATH</c> often does not cover.
    /// </summary>
    public static IReadOnlyList<string> PowerShellCandidates(string? pathVariable, bool isWindows, bool isMacOs)
    {
        var fileName = isWindows ? "pwsh.exe" : "pwsh";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                candidates.Add(Path.Combine(entry, fileName));
            }
        }

        if (isMacOs)
        {
            candidates.Add("/usr/local/bin/pwsh");
            candidates.Add("/opt/homebrew/bin/pwsh");
        }

        return candidates;
    }

    public static string? ResolvePowerShell(Func<string, bool>? fileExists = null, string? pathVariable = null)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isMacOs = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var candidates = PowerShellCandidates(
            pathVariable ?? Environment.GetEnvironmentVariable("PATH"),
            isWindows,
            isMacOs);
        return ResolveFirstExisting(candidates, fileExists);
    }

    public static string? ResolveFirstExisting(IEnumerable<string> candidates, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        return candidates.FirstOrDefault(fileExists);
    }

    /// <summary>
    /// The message shown when no PowerShell 7 host can be found. macOS callers get the install
    /// hint, because a Mac that never ran the installer genuinely has no <c>pwsh</c>.
    /// </summary>
    public static string PowerShellMissingMessage(bool isMacOs)
    {
        return isMacOs
            ? "OTA 更新需要 PowerShell 7（pwsh），但在 PATH、/usr/local/bin 与 /opt/homebrew/bin 中都没有找到。" +
              "请先安装：brew install --cask powershell。"
            : "PowerShell 7 (pwsh) is required for OTA updates and was not found on PATH.";
    }
}
