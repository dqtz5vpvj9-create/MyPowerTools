using System.Security.Cryptography;
using System.Text.Json;

namespace MyPowerTools.Packaging;

public sealed class PackageIntegrity
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PackageHashManifest CreateHashManifest(string packageDirectory)
    {
        var files = Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !IsIntegrityMetadata(path))
            .OrderBy(path => Path.GetRelativePath(packageDirectory, path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new PackageHashEntry(
                Normalize(Path.GetRelativePath(packageDirectory, path)),
                ComputeSha256(path)))
            .ToArray();

        return new PackageHashManifest("sha256", files);
    }

    public string WriteHashManifest(string packageDirectory, string? relativePath = null)
    {
        var manifest = CreateHashManifest(packageDirectory);
        var path = Path.Combine(packageDirectory, relativePath ?? Path.Combine("shared", "package.hashes.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    public IReadOnlyList<ValidationIssue> VerifyHashManifest(string packageDirectory, string relativePath)
    {
        var path = Path.Combine(packageDirectory, relativePath);
        if (!File.Exists(path))
        {
            return [new ValidationIssue(path, "error", "Hash manifest is missing.")];
        }

        var manifest = JsonSerializer.Deserialize<PackageHashManifest>(File.ReadAllText(path), JsonOptions);
        if (manifest is null)
        {
            return [new ValidationIssue(path, "error", "Hash manifest could not be parsed.")];
        }

        var issues = new List<ValidationIssue>();
        foreach (var entry in manifest.Files)
        {
            var filePath = Path.Combine(packageDirectory, entry.Path);
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
