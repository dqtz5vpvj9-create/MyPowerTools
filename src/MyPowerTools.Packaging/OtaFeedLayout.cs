using System.Runtime.InteropServices;

namespace MyPowerTools.Packaging.Ota;

/// <summary>
/// How a MyPowerTools installation receives its core update. The Web distribution exists
/// only on Windows; it ships the product without the private .NET runtime and keeps the
/// runtime components selected at install time.
/// </summary>
public enum OtaDistributionMode
{
    Full,
    Web
}

/// <summary>
/// Names of the OTA release assets for one platform.
/// </summary>
/// <remarks>
/// The Windows Full and Web names are the ones every released client already asks for, so
/// <c>win-x64</c> keeps them unsuffixed. Every other platform appends its runtime identifier,
/// which lets a macOS release add its own signed channel file without touching the bytes of
/// <c>channel-&lt;channel&gt;.json</c> that Windows clients verify.
/// </remarks>
public static class OtaFeedLayout
{
    public const string WindowsX64 = "win-x64";
    public const string OsxArm64 = "osx-arm64";
    public const string OsxX64 = "osx-x64";

    /// <summary>
    /// The runtime identifier the running process publishes and consumes OTA assets for.
    /// </summary>
    public static string CurrentRuntimeIdentifier()
    {
        return RuntimeIdentifierFor(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.ProcessArchitecture);
    }

    /// <summary>Maps an OS/architecture pair to the .NET runtime identifier spelling.</summary>
    public static string RuntimeIdentifierFor(bool isWindows, bool isMacOs, Architecture architecture)
    {
        var os = isWindows ? "win" : isMacOs ? "osx" : "linux";
        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => architecture.ToString().ToLowerInvariant()
        };

        return $"{os}-{arch}";
    }

    /// <summary>The full package archive, for example <c>MyPowerTools-osx-arm64.zip</c>.</summary>
    public static string FullPackageAsset(
        string runtimeIdentifier,
        OtaDistributionMode mode = OtaDistributionMode.Full)
    {
        return $"MyPowerTools-{ProductInfix(runtimeIdentifier, mode)}.zip";
    }

    /// <summary>The file manifest that the full package archive was generated from.</summary>
    public static string FileManifestAsset(
        string runtimeIdentifier,
        OtaDistributionMode mode = OtaDistributionMode.Full)
    {
        return $"MyPowerTools-{ProductInfix(runtimeIdentifier, mode)}.manifest.json";
    }

    /// <summary>
    /// The signed channel feed asset. <c>win-x64</c> keeps the historical unsuffixed names
    /// (<c>channel-stable.json</c> / <c>channel-stable-web.json</c>); other platforms get
    /// <c>channel-&lt;channel&gt;-&lt;rid&gt;.json</c>.
    /// </summary>
    public static string ChannelFeedAsset(
        string channel,
        string runtimeIdentifier,
        OtaDistributionMode mode = OtaDistributionMode.Full)
    {
        RequireChannel(channel);
        var rid = Normalize(runtimeIdentifier);
        if (rid == WindowsX64)
        {
            return mode == OtaDistributionMode.Web
                ? $"channel-{channel}-web.json"
                : $"channel-{channel}.json";
        }

        RequireFullMode(rid, mode);
        return $"channel-{channel}-{rid}.json";
    }

    /// <summary>The detached Ed25519 signature that accompanies a channel feed asset.</summary>
    public static string ChannelSignatureAsset(
        string channel,
        string runtimeIdentifier,
        OtaDistributionMode mode = OtaDistributionMode.Full)
    {
        return ChannelFeedAsset(channel, runtimeIdentifier, mode) + ".sig";
    }

    /// <summary>
    /// Whether file-level delta packages are published for a platform. macOS ships full
    /// bundles only: the <c>.app</c> is code signed as a unit and a per-file replacement
    /// neither preserves the executable bit nor keeps <c>_CodeSignature</c> consistent.
    /// </summary>
    public static bool SupportsDeltaPackages(string runtimeIdentifier)
    {
        return !Normalize(runtimeIdentifier).StartsWith("osx-", StringComparison.Ordinal);
    }

    private static string ProductInfix(string runtimeIdentifier, OtaDistributionMode mode)
    {
        var rid = Normalize(runtimeIdentifier);
        if (mode == OtaDistributionMode.Web)
        {
            RequireFullMode(rid, mode);
            return $"core-{rid}";
        }

        return rid;
    }

    private static void RequireFullMode(string runtimeIdentifier, OtaDistributionMode mode)
    {
        if (mode == OtaDistributionMode.Web && runtimeIdentifier != WindowsX64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                $"The Web distribution is published for {WindowsX64} only, not {runtimeIdentifier}.");
        }
    }

    private static string Normalize(string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            throw new ArgumentException("A runtime identifier is required.", nameof(runtimeIdentifier));
        }

        return runtimeIdentifier.Trim().ToLowerInvariant();
    }

    private static void RequireChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new ArgumentException("A channel name is required.", nameof(channel));
        }
    }
}
