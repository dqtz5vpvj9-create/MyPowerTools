using System.Security.Cryptography;
using System.Text.Json;

namespace MyPowerTools.Packaging;

public sealed class PackageIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PackageHashManifest CreateHashManifest(string packageDirectory) =>
        CreateHashManifest(packageDirectory, []);

    private static PackageHashManifest CreateHashManifest(string packageDirectory, IEnumerable<string> excludedPaths)
    {
        var excluded = excludedPaths.ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var excludedIgnoringCase = excluded.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excluded.Count != excludedIgnoringCase.Count)
        {
            throw new InvalidOperationException(
                "Integrity metadata output paths differ only by case. Use distinct canonical metadata paths.");
        }

        var files = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !IsIntegrityMetadata(path))
            .Where(path =>
            {
                var fullPath = Path.GetFullPath(path);
                if (excluded.Contains(fullPath))
                {
                    return false;
                }

                // Non-Windows volumes may be case-sensitive or case-insensitive.
                // Reject ambiguous custom metadata casing before writing anything:
                // ignoring it could silently omit a distinct payload on Linux, while
                // hashing it could include the output manifest itself on macOS.
                if (excludedIgnoringCase.Contains(fullPath))
                {
                    throw new InvalidOperationException(
                        $"Integrity metadata path casing conflicts with '{Normalize(Path.GetRelativePath(packageDirectory, fullPath))}'. " +
                        "Use the exact canonical metadata casing and remove case-only path collisions.");
                }

                return true;
            })
            .OrderBy(path => Path.GetRelativePath(packageDirectory, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new PackageHashEntry(
                Normalize(Path.GetRelativePath(packageDirectory, path)),
                ComputeSha256(path)))
            .ToArray();

        return new PackageHashManifest("sha256", files);
    }

    public string WriteHashManifest(string packageDirectory, string? relativePath = null) =>
        WriteHashManifest(packageDirectory, relativePath, []);

    internal string WriteHashManifest(string packageDirectory, string? relativePath, IEnumerable<string> metadataPaths)
    {
        var path = PackagePath.Resolve(packageDirectory, relativePath ?? Path.Combine("shared", "package.hashes.json"));
        var excluded = metadataPaths.Select(metadata => PackagePath.Resolve(packageDirectory, metadata)).Append(path);
        var manifest = CreateHashManifest(packageDirectory, excluded);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    public IReadOnlyList<ValidationIssue> VerifyHashManifest(string packageDirectory, string relativePath)
    {
        string path;
        PackageHashManifest? manifest;
        try
        {
            path = PackagePath.Resolve(packageDirectory, relativePath);
            if (!File.Exists(path))
            {
                return [new ValidationIssue(path, "error", "Hash manifest is missing.")];
            }

            manifest = JsonSerializer.Deserialize<PackageHashManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return [new ValidationIssue(relativePath, "error", $"Hash manifest could not be read: {exception.Message}")];
        }

        if (manifest?.Files is null)
        {
            return [new ValidationIssue(path, "error", "Hash manifest must contain a files array.")];
        }

        var issues = new List<ValidationIssue>();
        if (!string.Equals(manifest.Algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(path, "error", "Hash manifest algorithm must be sha256."));
        }

        foreach (var entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            {
                issues.Add(new ValidationIssue(path, "error", "Hash manifest entry must contain a relative file path inside the package directory."));
                continue;
            }

            try
            {
                var filePath = PackagePath.Resolve(packageDirectory, entry.Path);
                if (!File.Exists(filePath))
                {
                    issues.Add(new ValidationIssue(filePath, "error", "Hashed file is missing."));
                    continue;
                }

                var actual = ComputeSha256(filePath);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue(filePath, "error", "sha256 mismatch."));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                issues.Add(new ValidationIssue(entry.Path, "error", exception.Message));
            }
        }

        return issues;
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsIntegrityMetadata(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("package.hashes.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("package.signature.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

public sealed record PackageHashManifest(string Algorithm, IReadOnlyList<PackageHashEntry> Files);
public sealed record PackageHashEntry(string Path, string Sha256);
