namespace MyPowerTools.Android.Files;

/// <summary>
/// The names that tie the read-only file provider, its manifest registration and the paths resource
/// together. They sit in an Android-free file so the invariants (authority suffix, provider Java
/// name, resource name) can be asserted by a plain net10.0 test project without the Android
/// workload, and so the launcher, the provider and the tests cannot drift apart.
/// </summary>
public static class MptSharedFileContract
{
    /// <summary>Appended to the application id to form the provider authority.</summary>
    public const string ProviderAuthoritySuffix = ".mptfiles";

    /// <summary>
    /// Authority written into the merged manifest. The Android build substitutes
    /// <c>${applicationId}</c> with the real package id, so the provider survives an
    /// <c>ApplicationId</c> change; <see cref="AuthorityFor"/> produces the same value at runtime
    /// from <c>Context.PackageName</c>.
    /// </summary>
    public const string ProviderAuthorityTemplate = "${applicationId}" + ProviderAuthoritySuffix;

    /// <summary>
    /// Java name of the provider subclass. The provider is declared through .NET Android's
    /// <c>ContentProvider</c> attribute, and <c>Register</c> pins the generated Java callable
    /// wrapper to exactly this name so the merged manifest entry always resolves.
    /// </summary>
    public const string ProviderJavaName = "com.mypowertools.android.files.MptFileProvider";

    /// <summary>File name (without extension) of the provider paths resource under <c>Resources/xml</c>.</summary>
    public const string PathsResourceName = "mpt_shared_files";

    /// <summary>Resource reference as it appears in the provider meta-data entry.</summary>
    public const string PathsResourceReference = "@xml/" + PathsResourceName;

    /// <summary>The meta-data key AndroidX FileProvider reads its path configuration from.</summary>
    public const string PathsMetadataName = "android.support.FILE_PROVIDER_PATHS";

    /// <summary>Resource file name including the extension, for parity checks against the repo layout.</summary>
    public const string PathsResourceFileName = PathsResourceName + ".xml";

    /// <summary>
    /// Builds the runtime authority for an installed package id. The value must match
    /// <see cref="ProviderAuthorityTemplate"/> after the build substituted the application id, which
    /// is why the launcher never hard-codes a package name.
    /// </summary>
    public static string AuthorityFor(string? packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
        {
            throw new ArgumentException("包名不能为空。", nameof(packageName));
        }

        return packageName.Trim() + ProviderAuthoritySuffix;
    }
}
