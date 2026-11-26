using System.Text;

namespace MyPowerTools.Platform.Android;

/// <summary>
/// Naming and MIME rules for publishing a completed file into the user's Downloads collection.
/// The MIME lookup itself is platform-owned (<c>MimeTypeMap</c>); the pure rules live here so
/// extension handling, collision naming and name sanitising stay testable off-device.
/// </summary>
public static class DownloadPublication
{
    /// <summary>MediaStore relative path; MediaStore appends the trailing slash itself.</summary>
    public const string RelativeDirectory = "Download/MPT";

    public const string FallbackMimeType = "application/octet-stream";

    private const int MaxDisplayNameLength = 180;

    /// <summary>Strips path components and characters MediaStore rejects, never returning an empty name.</summary>
    public static string SanitizeDisplayName(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var leaf = Path.GetFileName(path.Replace('\\', '/')).Trim();
        var cleaned = new StringBuilder(leaf.Length);
        foreach (var character in leaf)
        {
            cleaned.Append(char.IsControl(character) || character is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'
                ? '_'
                : character);
        }

        var name = cleaned.ToString().Trim(' ', '.');
        if (name.Length == 0)
        {
            return "download";
        }

        if (name.Length <= MaxDisplayNameLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        if (extension.Length >= MaxDisplayNameLength)
        {
            return name[..MaxDisplayNameLength];
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return string.Concat(stem.AsSpan(0, MaxDisplayNameLength - extension.Length), extension);
    }

    /// <summary>Lower-case extension without the dot; empty when the name has none.</summary>
    public static string ExtensionOf(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var extension = Path.GetExtension(displayName);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : "";
    }

    /// <summary>
    /// Resolves the MIME type from the file extension (never from the full name), falling back to
    /// <see cref="FallbackMimeType"/> when the platform map has no entry.
    /// </summary>
    public static string MimeTypeFor(string displayName, Func<string, string?>? mimeTypeMap = null)
    {
        var extension = ExtensionOf(displayName);
        var mime = extension.Length == 0 ? null : mimeTypeMap?.Invoke(extension);
        return string.IsNullOrWhiteSpace(mime) ? FallbackMimeType : mime;
    }

    /// <summary>
    /// Picks a display name that does not overwrite an existing download: "report.pdf" becomes
    /// "report (1).pdf" and so on. MediaStore would also rename collisions, but resolving them
    /// here keeps the published name deterministic and testable.
    /// </summary>
    public static string UniqueDisplayName(string displayName, Func<string, bool> isTaken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(isTaken);
        if (!isTaken(displayName))
        {
            return displayName;
        }

        var extension = Path.GetExtension(displayName);
        var stem = Path.GetFileNameWithoutExtension(displayName);
        for (var attempt = 1; attempt <= 999; attempt++)
        {
            var candidate = $"{stem} ({attempt}){extension}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为下载文件生成唯一的文件名。");
    }
}
