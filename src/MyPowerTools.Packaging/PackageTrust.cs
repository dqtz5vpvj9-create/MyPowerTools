using System.Text.Json;

namespace MyPowerTools.Packaging;

public sealed class PackageTrustVerifier
{
    public const string SignatureFormat = "mpt-signature-v1";
    public const string DefaultHashManifestPath = "shared/package.hashes.json";
    public const string DefaultSignaturePath = "shared/package.signature.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly PackageReader _reader = new();
    private readonly PackageIntegrity _integrity = new();

    public PackageTrustReport Verify(string packageDirectory, PackageTrustPolicy? policy = null)
    {
        policy ??= PackageTrustPolicy.LocalDevelopment;
        var definition = _reader.ReadPackageDirectory(packageDirectory);
        var hashPath = ResolveHashManifestPath(definition);
        var signaturePath = ResolveSignatureManifestPath(definition);
        var issues = new List<ValidationIssue>();

        string signatureFullPath;
        try
        {
            _ = ResolvePackagePath(definition.Directory, hashPath);
            signatureFullPath = ResolvePackagePath(definition.Directory, signaturePath);
        }
        catch (Exception ex)
        {
            return new PackageTrustReport(
                definition.Directory,
                definition.Package.Id,
                definition.Package.Version,
                definition.Package.Trust?.Policy ?? "local",
                "invalid-trust-manifest",
                hashPath,
                signaturePath,
                "none",
                [new ValidationIssue(definition.Directory, "error", ex.Message)]);
        }

        issues.AddRange(_integrity.VerifyHashManifest(definition.Directory, hashPath));

        var trustPolicy = string.IsNullOrWhiteSpace(definition.Package.Trust?.Policy)
            ? "local"
            : definition.Package.Trust.Policy;
        var signatureRequired = policy.RequireSignature ||
            string.Equals(trustPolicy, "signed", StringComparison.OrdinalIgnoreCase) ||
            definition.Package.Trust?.Signature?.Required == true;
        var state = "local-trust";
        var signingHook = "none";

        if (File.Exists(signatureFullPath))
        {
            state = "signature-hook";
            signingHook = "present";
            issues.AddRange(VerifySignatureDocument(definition, hashPath, signatureFullPath));
        }
        else if (signatureRequired)
        {
            state = "missing-signature";
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook is missing."));
        }
        else if (policy.AllowUnsignedLocalPackages)
        {
            issues.Add(new ValidationIssue(signatureFullPath, "warning", "Unsigned local package allowed by local trust policy."));
        }
        else
        {
            state = "untrusted";
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Unsigned package is not trusted by the active policy."));
        }

        return new PackageTrustReport(
            definition.Directory,
            definition.Package.Id,
            definition.Package.Version,
            trustPolicy,
            state,
            hashPath,
            signaturePath,
            signingHook,
            issues);
    }

    public string WriteLocalSignatureHook(string packageDirectory, string? relativePath = null)
    {
        var definition = _reader.ReadPackageDirectory(packageDirectory);
        var hashPath = ResolveHashManifestPath(definition);
        _ = ResolvePackagePath(definition.Directory, hashPath);
        var hashFullPath = _integrity.WriteHashManifest(definition.Directory, hashPath);
        var signaturePath = string.IsNullOrWhiteSpace(relativePath)
            ? ResolveSignatureManifestPath(definition)
            : Normalize(relativePath);
        var signatureFullPath = ResolvePackagePath(definition.Directory, signaturePath);

        Directory.CreateDirectory(Path.GetDirectoryName(signatureFullPath)!);
        var document = new PackageSignatureDocument
        {
            Format = SignatureFormat,
            PackageId = definition.Package.Id,
            Version = definition.Package.Version,
            HashManifest = hashPath,
            HashManifestSha256 = PackageIntegrity.ComputeSha256(hashFullPath),
            SignatureState = "unsigned-local-trust",
            Algorithm = "sha256-manifest-local",
            KeyId = "local-development",
            Signer = Environment.UserName,
            CreatedAt = ReadExistingCreatedAt(signatureFullPath) ?? DateTimeOffset.UtcNow,
            FutureAlgorithms =
            [
                "ed25519-detached",
                "x509-rsa-sha256",
                "sigstore-bundle"
            ]
        };

        File.WriteAllText(signatureFullPath, JsonSerializer.Serialize(document, JsonOptions));
        return signatureFullPath;
    }

    private static DateTimeOffset? ReadExistingCreatedAt(string signatureFullPath)
    {
        if (!File.Exists(signatureFullPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(signatureFullPath));
            return document.RootElement.TryGetProperty(nameof(PackageSignatureDocument.CreatedAt), out var createdAt) &&
                createdAt.TryGetDateTimeOffset(out var value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveHashManifestPath(MptPackageDefinition definition)
    {
        return string.IsNullOrWhiteSpace(definition.Package.Hashes)
            ? DefaultHashManifestPath
            : Normalize(definition.Package.Hashes);
    }

    public static string ResolveSignatureManifestPath(MptPackageDefinition definition)
    {
        var path = definition.Package.Trust?.Signature?.Path;
        return string.IsNullOrWhiteSpace(path)
            ? DefaultSignaturePath
            : Normalize(path);
    }

    private static IReadOnlyList<ValidationIssue> VerifySignatureDocument(
        MptPackageDefinition definition,
        string hashPath,
        string signatureFullPath)
    {
        var issues = new List<ValidationIssue>();
        PackageSignatureDocument? signature;
        try
        {
            signature = JsonSerializer.Deserialize<PackageSignatureDocument>(
                File.ReadAllText(signatureFullPath),
                JsonOptions);
        }
        catch (Exception ex)
        {
            return [new ValidationIssue(signatureFullPath, "error", $"Package signature hook could not be parsed: {ex.Message}")];
        }

        if (signature is null)
        {
            return [new ValidationIssue(signatureFullPath, "error", "Package signature hook could not be parsed.")];
        }

        var expectedFormat = string.IsNullOrWhiteSpace(definition.Package.Trust?.Signature?.Format)
            ? SignatureFormat
            : definition.Package.Trust.Signature.Format;
        if (!string.Equals(signature.Format, expectedFormat, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(signatureFullPath, "error", $"Package signature hook format must be {expectedFormat}."));
        }

        if (!string.Equals(signature.PackageId, definition.Package.Id, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook package id does not match package.json."));
        }

        if (!string.Equals(signature.Version, definition.Package.Version, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook version does not match package.json."));
        }

        if (!string.Equals(Normalize(signature.HashManifest), hashPath, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook points at a different hash manifest."));
        }

        var hashFullPath = ResolvePackagePath(definition.Directory, hashPath);
        if (File.Exists(hashFullPath))
        {
            var actualHash = PackageIntegrity.ComputeSha256(hashFullPath);
            if (!string.Equals(actualHash, signature.HashManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook hash-manifest sha256 does not match."));
            }
        }

        if (string.IsNullOrWhiteSpace(signature.SignatureState))
        {
            issues.Add(new ValidationIssue(signatureFullPath, "error", "Package signature hook state is missing."));
        }

        return issues;
    }

    private static string ResolvePackagePath(string packageDirectory, string relativePath)
    {
        var packageRoot = Path.GetFullPath(packageDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        var packageRootWithSeparator = packageRoot.EndsWith(Path.DirectorySeparatorChar)
            ? packageRoot
            : packageRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(packageRootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, packageRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Package trust path escapes package directory.");
        }

        return fullPath;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

public sealed record PackageTrustPolicy(bool AllowUnsignedLocalPackages, bool RequireSignature)
{
    public static PackageTrustPolicy LocalDevelopment { get; } = new(true, false);
    public static PackageTrustPolicy StrictSigned { get; } = new(false, true);
}

public sealed record PackageTrustReport(
    string PackageDirectory,
    string PackageId,
    string Version,
    string Policy,
    string State,
    string HashManifestPath,
    string SignaturePath,
    string SigningHook,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsTrusted => Issues.All(issue => issue.Severity != "error");
}

public sealed class PackageSignatureDocument
{
    public string Format { get; init; } = PackageTrustVerifier.SignatureFormat;
    public string PackageId { get; init; } = "";
    public string Version { get; init; } = "";
    public string HashManifest { get; init; } = "";
    public string HashManifestSha256 { get; init; } = "";
    public string SignatureState { get; init; } = "";
    public string Algorithm { get; init; } = "";
    public string KeyId { get; init; } = "";
    public string Signer { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public IReadOnlyList<string> FutureAlgorithms { get; init; } = [];
}
