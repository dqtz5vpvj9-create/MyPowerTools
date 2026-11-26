namespace MyPowerTools.Android.Pairing;

/// <summary>
/// Turns a display name that came from another app's content provider into a single safe file name.
/// Android-free so the rules are unit tested: a shared file is written under the app cache, and a
/// provider is free to report a name containing a directory separator, a traversal segment, a
/// Windows device name or characters the file system rejects.
/// </summary>
public static class SharedFileNames
{
    private const int MaximumLength = 120;
    private const string Fallback = "shared-file";

    /// <summary>Only the leaf is kept, so a reported name can never escape its folder.</summary>
    public static string SafeLeafName(string? reported)
    {
        var value = (reported ?? "").Replace('\\', '/').Trim();
        var leaf = value[(value.LastIndexOf('/') + 1)..];
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            leaf = leaf.Replace(invalid, '_');
        }

        // ".." and "." are legal file names, not path segments, but uploading them to a peer is
        // never useful and some archives treat them specially.
        leaf = leaf.Trim().Trim('.');
        if (leaf.Length > MaximumLength)
        {
            var extension = Path.GetExtension(leaf);
            var stem = Path.GetFileNameWithoutExtension(leaf);
            var keep = Math.Max(1, MaximumLength - extension.Length);
            leaf = stem[..Math.Min(stem.Length, keep)] + extension[..Math.Min(extension.Length, MaximumLength)];
        }

        return leaf.Length == 0 ? Fallback : leaf;
    }
}
