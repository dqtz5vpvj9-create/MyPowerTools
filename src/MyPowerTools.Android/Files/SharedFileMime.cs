namespace MyPowerTools.Android.Files;

/// <summary>Where an inferred content type came from.</summary>
public enum SharedFileMimeSource
{
    /// <summary>The deterministic table in <see cref="SharedFileMime"/> recognised the extension.</summary>
    KnownExtension,

    /// <summary>The platform's own extension map recognised it.</summary>
    SystemMap,

    /// <summary>Nothing recognised the extension.</summary>
    Unknown,
}

/// <summary>One inferred content type plus the reason it was chosen.</summary>
public readonly record struct SharedFileMimeResult(string MimeType, SharedFileMimeSource Source)
{
    /// <summary>True when the sender had to fall back to a generic binary type.</summary>
    public bool IsUnknown => Source == SharedFileMimeSource.Unknown;
}

/// <summary>
/// Content-type inference for the phone file viewer. A fixed table comes first so the common
/// document, image, media and archive types resolve identically on every device and can be unit
/// tested; the platform extension map is only consulted for extensions the table does not know, and
/// a generic binary type is the last resort. No file content is read to guess a type.
/// </summary>
public static class SharedFileMime
{
    /// <summary>Fallback for an extension nobody recognises.</summary>
    public const string BinaryMimeType = "application/octet-stream";

    /// <summary>Last-resort type for the one bounded retry when nothing handles the specific type.</summary>
    public const string WildcardMimeType = "*/*";

    /// <summary>Extensions longer than this are treated as unknown instead of looked up verbatim.</summary>
    public const int MaxExtensionLength = 12;

    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // Images
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["jpe"] = "image/jpeg",
        ["png"] = "image/png",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
        ["bmp"] = "image/bmp",
        ["heic"] = "image/heic",
        ["heif"] = "image/heif",
        ["avif"] = "image/avif",
        ["tif"] = "image/tiff",
        ["tiff"] = "image/tiff",
        ["svg"] = "image/svg+xml",
        ["ico"] = "image/x-icon",

        // Video
        ["mp4"] = "video/mp4",
        ["m4v"] = "video/mp4",
        ["mkv"] = "video/x-matroska",
        ["mov"] = "video/quicktime",
        ["avi"] = "video/x-msvideo",
        ["webm"] = "video/webm",
        ["3gp"] = "video/3gpp",
        ["flv"] = "video/x-flv",
        ["ts"] = "video/mp2t",

        // Audio
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["aac"] = "audio/aac",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["ogg"] = "audio/ogg",
        ["oga"] = "audio/ogg",
        ["opus"] = "audio/opus",
        ["amr"] = "audio/amr",
        ["mid"] = "audio/midi",
        ["midi"] = "audio/midi",

        // Documents
        ["pdf"] = "application/pdf",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["odt"] = "application/vnd.oasis.opendocument.text",
        ["ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        ["odp"] = "application/vnd.oasis.opendocument.presentation",
        ["rtf"] = "application/rtf",
        ["epub"] = "application/epub+zip",

        // Text. Plain text is deliberate for formats without a widely installed viewer: a text
        // editor is far more likely to exist than a viewer for text/x-yaml.
        ["txt"] = "text/plain",
        ["text"] = "text/plain",
        ["log"] = "text/plain",
        ["ini"] = "text/plain",
        ["cfg"] = "text/plain",
        ["conf"] = "text/plain",
        ["properties"] = "text/plain",
        ["yaml"] = "text/plain",
        ["yml"] = "text/plain",
        ["toml"] = "text/plain",
        ["md"] = "text/markdown",
        ["markdown"] = "text/markdown",
        ["csv"] = "text/csv",
        ["tsv"] = "text/tab-separated-values",
        ["json"] = "application/json",
        ["xml"] = "application/xml",
        ["html"] = "text/html",
        ["htm"] = "text/html",
        ["vcf"] = "text/vcard",
        ["ics"] = "text/calendar",

        // Archives and packages
        ["zip"] = "application/zip",
        ["rar"] = "application/vnd.rar",
        ["7z"] = "application/x-7z-compressed",
        ["tar"] = "application/x-tar",
        ["gz"] = "application/gzip",
        ["tgz"] = "application/gzip",
        ["bz2"] = "application/x-bzip2",
        ["xz"] = "application/x-xz",
        ["apk"] = "application/vnd.android.package-archive",
        ["db"] = "application/vnd.sqlite3",
        ["sqlite"] = "application/vnd.sqlite3",
    };

    /// <summary>
    /// Lower-case extension without the dot, or <see langword="null"/> when the name has none worth
    /// looking up (no dot, a leading-dot hidden file, a trailing dot, or an implausible length).
    /// </summary>
    public static string? Extension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileName(fileName.Trim().Replace('\\', '/'));
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            return null;
        }

        var extension = name[(dot + 1)..].ToLowerInvariant();
        if (extension.Length > MaxExtensionLength)
        {
            return null;
        }

        foreach (var character in extension)
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return null;
            }
        }

        return extension;
    }

    /// <summary>
    /// Infers the content type of a file from its name. <paramref name="systemLookup"/> is the
    /// platform extension map and is optional; a lookup that fails or returns something unusable is
    /// treated as "unknown" instead of blocking the open.
    /// </summary>
    public static SharedFileMimeResult Infer(string? fileName, Func<string, string?>? systemLookup = null)
    {
        var extension = Extension(fileName);
        if (extension is not null)
        {
            if (Known.TryGetValue(extension, out var known))
            {
                return new SharedFileMimeResult(known, SharedFileMimeSource.KnownExtension);
            }

            if (systemLookup is not null)
            {
                string? mapped = null;
                try
                {
                    mapped = Normalize(systemLookup(extension));
                }
                catch (Exception)
                {
                    // A platform lookup is a convenience; its failure must not fail the open.
                }

                if (mapped is not null)
                {
                    return new SharedFileMimeResult(mapped, SharedFileMimeSource.SystemMap);
                }
            }
        }

        return new SharedFileMimeResult(BinaryMimeType, SharedFileMimeSource.Unknown);
    }

    /// <summary>
    /// Accepts a candidate type only when it is a plain <c>type/subtype</c>. Parameters, whitespace
    /// and control characters are dropped or rejected, because <c>Intent.setDataAndType</c> matches
    /// on the bare type.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var parameters = text.IndexOf(';');
        if (parameters >= 0)
        {
            text = text[..parameters].Trim();
        }

        if (text.Length is 0 or > 255)
        {
            return null;
        }

        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1 || text.IndexOf('/', slash + 1) >= 0)
        {
            return null;
        }

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character) || character is '"' or '\\' or '\'')
            {
                return null;
            }
        }

        return text.ToLowerInvariant();
    }
}
