using Android.App;
using Android.Content;
using Android.Runtime;
using AndroidX.Core.Content;
using A = global::Android;

namespace MyPowerTools.Android.Files;

/// <summary>
/// The app's only content provider: a read-only AndroidX FileProvider that serves exactly the
/// directory trees declared in <c>Resources/xml/mpt_shared_files.xml</c>. The provider is registered
/// through the .NET Android manifest attributes below, is never exported, and grants URI permissions
/// so a viewer receives a revocable read grant for the one URI it was handed.
/// <para>
/// AndroidX already refuses inserts, updates and deletes; the one override here also refuses every
/// file mode except plain read, so even a mis-issued grant cannot modify a user's file.
/// </para>
/// </summary>
[ContentProvider(
    new[] { MptSharedFileContract.ProviderAuthorityTemplate },
    Name = MptSharedFileContract.ProviderJavaName,
    Exported = false,
    GrantUriPermissions = true,
    Enabled = true)]
[MetaData(MptSharedFileContract.PathsMetadataName, Resource = MptSharedFileContract.PathsResourceReference)]
[Register(MptSharedFileContract.ProviderJavaName)]
public sealed class MptFileProvider : FileProvider
{
    public MptFileProvider()
    {
    }

    /// <summary>Read-only: the host only ever grants <c>FLAG_GRANT_READ_URI_PERMISSION</c>.</summary>
    public override A.OS.ParcelFileDescriptor? OpenFile(A.Net.Uri uri, string mode)
    {
        if (!string.Equals(mode, "r", StringComparison.Ordinal))
        {
            // No file name, no path and no content in the message: the caller only learns the mode.
            throw new Java.IO.FileNotFoundException("只读共享，仅支持 r 模式。");
        }

        return base.OpenFile(uri, mode);
    }
}
