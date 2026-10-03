namespace MyPowerTools.Installer.Mac;

/// <summary>
/// Command line accepted by the installer window.
/// <list type="bullet">
/// <item><c>--update</c>: start installing right after the check when an update is available
/// (used by the in-app updater).</item>
/// <item><c>--channel &lt;name&gt;</c>: OTA channel, <c>stable</c> by default.</item>
/// <item><c>--app &lt;path&gt;</c>: target MyPowerTools.app. When omitted and this installer
/// runs from inside a MyPowerTools.app (Contents/Resources/Installer), that enclosing bundle
/// is the target; otherwise the engine default (~/Applications/MyPowerTools.app).</item>
/// <item><c>--feed &lt;url&gt;</c>: feed override for testing.</item>
/// </list>
/// Unknown arguments (for example the <c>-psn_*</c> argument Finder used to pass) are ignored.
/// </summary>
internal sealed record InstallerArguments(bool AutoUpdate, string? Channel, string? AppBundlePath, string? FeedUrl)
{
    public static InstallerArguments Parse(IReadOnlyList<string> args)
    {
        var autoUpdate = false;
        string? channel = null;
        string? app = null;
        string? feed = null;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            string? NextValue() => index + 1 < args.Count ? args[++index] : null;
            switch (argument.ToLowerInvariant())
            {
                case "--update":
                    autoUpdate = true;
                    break;
                case "--channel":
                    channel = NextValue();
                    break;
                case "--app":
                    app = NextValue();
                    break;
                case "--feed":
                    feed = NextValue();
                    break;
                default:
                    if (argument.StartsWith("--channel=", StringComparison.OrdinalIgnoreCase))
                        channel = argument["--channel=".Length..];
                    else if (argument.StartsWith("--app=", StringComparison.OrdinalIgnoreCase))
                        app = argument["--app=".Length..];
                    else if (argument.StartsWith("--feed=", StringComparison.OrdinalIgnoreCase))
                        feed = argument["--feed=".Length..];
                    break;
            }
        }

        return new InstallerArguments(
            autoUpdate,
            string.IsNullOrWhiteSpace(channel) ? null : channel.Trim(),
            string.IsNullOrWhiteSpace(app) ? null : Path.GetFullPath(app.Trim()),
            string.IsNullOrWhiteSpace(feed) ? null : feed.Trim());
    }

    /// <summary>
    /// When this executable lives at
    /// <c>X.app/Contents/Resources/Installer/MyPowerTools Installer.app/Contents/MacOS/exe</c>,
    /// returns <c>X.app</c> so an update launched from inside the product targets the copy the
    /// user actually runs, wherever it was installed.
    /// </summary>
    public static string? FindEnclosingProductBundle(string baseDirectory)
    {
        try
        {
            var installerMacOs = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDirectory));
            var installerBundle = installerMacOs.Parent?.Parent;          // MyPowerTools Installer.app
            var installerFolder = installerBundle?.Parent;                // Installer
            var resources = installerFolder?.Parent;                      // Resources
            var contents = resources?.Parent;                             // Contents
            var product = contents?.Parent;                               // X.app
            if (installerBundle is null || installerFolder is null || resources is null ||
                contents is null || product is null)
                return null;
            if (!installerBundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(installerFolder.Name, "Installer", StringComparison.Ordinal) ||
                !string.Equals(resources.Name, "Resources", StringComparison.Ordinal) ||
                !string.Equals(contents.Name, "Contents", StringComparison.Ordinal) ||
                !product.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(Path.Combine(contents.FullName, "Info.plist")) ? product.FullName : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
