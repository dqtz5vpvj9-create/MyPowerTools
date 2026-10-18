namespace MyPowerTools.Packaging;

/// <summary>Resolves portable package-relative paths without following package links.</summary>
internal static class PackagePath
{
    public static string Resolve(string packageDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("A path inside the package directory is required.");
        }

        // Package metadata is portable: backslashes must not become ordinary
        // filename characters on Unix, and Windows drive/stream paths are invalid.
        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            throw new InvalidOperationException("A relative path inside the package directory is required.");
        }

        var root = Path.GetFullPath(packageDirectory);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root), comparison) ||
            !path.StartsWith(prefix, comparison))
        {
            throw new InvalidOperationException("Path escapes package directory.");
        }

        // A lexical prefix check alone permits a link below the package root to
        // redirect a later read/write outside the package. The supplied root itself
        // may be an intentional link; all descendants must be ordinary entries.
        var current = root;
        foreach (var component in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, component);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Symbolic links are not allowed inside a package directory path.");
            }
        }

        return path;
    }
}
