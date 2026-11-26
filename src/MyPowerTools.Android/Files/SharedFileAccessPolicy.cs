namespace MyPowerTools.Android.Files;

/// <summary>Outcome of checking one user-visible path against the shared-file boundary.</summary>
public enum SharedFileAccessVerdict
{
    /// <summary>The path is an existing readable file inside an allowed root.</summary>
    Allowed,

    /// <summary>No path (or only whitespace) was supplied.</summary>
    EmptyPath,

    /// <summary>The value is neither a rooted path nor a usable file URI.</summary>
    MalformedPath,

    /// <summary>The value is a URI of a scheme the host does not open (for example <c>content://</c>).</summary>
    UnsupportedScheme,

    /// <summary>The path resolves outside every allowed root, including through a symbolic link.</summary>
    OutsideAllowedRoots,

    /// <summary>Nothing exists at the path.</summary>
    Missing,

    /// <summary>The path is a directory, not a file.</summary>
    NotAFile,

    /// <summary>The file exists but this process cannot read it.</summary>
    Unreadable,
}

/// <summary>
/// The one place that decides whether a phone host may hand a path to a viewer. It is deliberately
/// Android-free so it can be unit tested on the desktop build, and deliberately fail-closed: with no
/// roots configured it allows nothing, a relative path is refused, a <c>..</c> that escapes a root
/// is refused, and a symbolic link whose final target leaves the roots is refused. The provider's
/// own path configuration enforces the same boundary a second time.
/// </summary>
public sealed class SharedFileAccessPolicy
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string[] _roots;

    public SharedFileAccessPolicy(IEnumerable<string> allowedRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedRoots);
        var roots = new List<string>();
        foreach (var candidate in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string full;
            try
            {
                // A relative root would silently depend on the process working directory, which is
                // exactly the kind of accidental widening this policy exists to prevent.
                if (!Path.IsPathRooted(candidate.Trim()))
                {
                    continue;
                }

                full = TrimTrailingSeparator(Path.GetFullPath(candidate.Trim()));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!roots.Contains(full, PathComparer))
            {
                roots.Add(full);
            }
        }

        _roots = [.. roots];
    }

    /// <summary>The canonical roots this policy accepts, in declaration order.</summary>
    public IReadOnlyList<string> AllowedRoots => _roots;

    /// <summary>
    /// Full check for one candidate. On <see cref="SharedFileAccessVerdict.Allowed"/> the
    /// <paramref name="normalizedPath"/> is the absolute path that may be shared, with a symbolic
    /// link already resolved to its final target inside an allowed root.
    /// </summary>
    public SharedFileAccessVerdict Evaluate(string? candidate, out string normalizedPath)
    {
        normalizedPath = "";
        if (!TryNormalizeInput(candidate, out var path, out var verdict))
        {
            return verdict;
        }

        if (!Contains(path))
        {
            return SharedFileAccessVerdict.OutsideAllowedRoots;
        }

        var attributes = TryGetAttributes(path);
        if (attributes is null)
        {
            return SharedFileAccessVerdict.Missing;
        }

        if ((attributes.Value & FileAttributes.Directory) != 0)
        {
            return SharedFileAccessVerdict.NotAFile;
        }

        // File.Exists is false for a broken link, which is exactly the answer the caller needs.
        if (!File.Exists(path))
        {
            return SharedFileAccessVerdict.Missing;
        }

        // FileProvider canonicalizes before it maps a file to a URI, so a link that leaves the roots
        // must be refused here as well; otherwise the provider would reject it with a Java exception.
        // A link that cannot be resolved at all stays on the lexical path and is left to the provider,
        // which fails closed the same way.
        var resolved = TryResolveFinalTarget(path, out var target) ? target : path;
        if (!Contains(resolved))
        {
            return SharedFileAccessVerdict.OutsideAllowedRoots;
        }

        // A link whose target was deleted resolves to a path that no longer exists; that is a missing
        // file, not an unreadable one.
        if (!File.Exists(resolved))
        {
            return SharedFileAccessVerdict.Missing;
        }

        // The viewer reads the file through this process, so "the user may open it" means this
        // process can read it right now. The probe opens and closes; nothing is copied or cached.
        if (!CanRead(resolved))
        {
            return SharedFileAccessVerdict.Unreadable;
        }

        normalizedPath = resolved;
        return SharedFileAccessVerdict.Allowed;
    }

    /// <summary>True when an already absolute path is one of the roots or lives below one.</summary>
    public bool Contains(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return false;
        }

        foreach (var root in _roots)
        {
            if (PathComparer.Equals(absolutePath, root))
            {
                return true;
            }

            // The separator check rejects the classic prefix bug: /a/bc is not inside /a/b.
            if (absolutePath.Length > root.Length &&
                PathComparer.Equals(absolutePath[..root.Length], root) &&
                IsSeparator(absolutePath[root.Length]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Accepts an absolute filesystem path or a <c>file://</c> URI (the host's activation path is a
    /// URI string, so both spellings reach this method). Percent-encoded names, including spaces and
    /// Chinese characters, are decoded here; every other scheme is refused rather than guessed at.
    /// </summary>
    public static bool TryNormalizeInput(string? input, out string path, out SharedFileAccessVerdict verdict)
    {
        path = "";
        verdict = SharedFileAccessVerdict.Allowed;
        if (string.IsNullOrWhiteSpace(input))
        {
            verdict = SharedFileAccessVerdict.EmptyPath;
            return false;
        }

        var text = input.Trim();
        if (text.IndexOf('\0') >= 0 || text.StartsWith("\\\\", StringComparison.Ordinal))
        {
            verdict = SharedFileAccessVerdict.MalformedPath;
            return false;
        }

        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile || string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                verdict = SharedFileAccessVerdict.MalformedPath;
                return false;
            }

            text = uri.LocalPath;
        }
        else if (text.Contains("://", StringComparison.Ordinal))
        {
            verdict = SharedFileAccessVerdict.UnsupportedScheme;
            return false;
        }

        if (!Path.IsPathRooted(text))
        {
            verdict = SharedFileAccessVerdict.MalformedPath;
            return false;
        }

        try
        {
            path = TrimTrailingSeparator(Path.GetFullPath(text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            verdict = SharedFileAccessVerdict.MalformedPath;
            return false;
        }

        return true;
    }

    private static bool TryResolveFinalTarget(string path, out string resolved)
    {
        resolved = path;
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is null)
            {
                return true;
            }

            resolved = TrimTrailingSeparator(Path.GetFullPath(target.FullName));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool CanRead(string path)
    {
        try
        {
            // Share read and delete so the probe cannot disturb a module that is still writing, and
            // close immediately: the probe proves permission, not content.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.CanRead;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsSeparator(char value) => value == '/' || value == '\\';

    private static string TrimTrailingSeparator(string path)
    {
        if (path.Length <= 1)
        {
            return path;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
