using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MyPowerTools.Packaging;

namespace MyPowerTools.Cli;

/// <summary>
/// Validates binary ABI compatibility between staged module/surface assemblies and the
/// Runner/Shell host directories using metadata inspection (PEReader) without loading assemblies.
/// </summary>
public sealed class AbiCompatibilityValidator
{
    public static readonly IReadOnlySet<string> SharedContracts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MyPowerTools.Platform.Abstractions",
        "MyPowerTools.Abstractions",
        "MyPowerTools.AvaloniaSdk",
        "MyPowerTools.Protocol",
        "MyPowerTools.Ipc.Shared"
    };

    public AbiValidationReport Validate(string hostDirectory, string modulesDirectory, string? shellDirectory = null)
    {
        var hostFullPath = Path.GetFullPath(hostDirectory);
        var modulesFullPath = Path.GetFullPath(modulesDirectory);
        var shellFullPath = string.IsNullOrWhiteSpace(shellDirectory) ? null : Path.GetFullPath(shellDirectory);

        var hostContracts = ReadHostContracts(hostFullPath);
        var shellContracts = shellFullPath is null ? null : ReadHostContracts(shellFullPath);
        var issues = new List<ValidationIssue>();
        var assembliesChecked = 0;

        foreach (var dllPath in Directory.EnumerateFiles(modulesFullPath, "*.dll", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(modulesFullPath, path), StringComparer.OrdinalIgnoreCase))
        {
            var metadata = TryReadAssemblyMetadata(dllPath);
            if (metadata is null)
            {
                continue;
            }

            assembliesChecked++;
            var relativePath = Path.GetRelativePath(modulesFullPath, dllPath).Replace('\\', '/');
            var isSurfaceAssembly = IsSurfacePath(relativePath);
            var targetContracts = isSurfaceAssembly && shellContracts is not null ? shellContracts : hostContracts;
            var targetLabel = isSurfaceAssembly && shellFullPath is not null ? "Shell" : "Runner";
            var targetDir = isSurfaceAssembly && shellFullPath is not null ? shellFullPath : hostFullPath;

            if (SharedContracts.Contains(metadata.Name))
            {
                ValidateBundledContract(
                    dllPath,
                    relativePath,
                    metadata,
                    isSurfaceAssembly,
                    shellContracts,
                    targetContracts,
                    targetLabel,
                    targetDir,
                    issues);
                continue;
            }

            foreach (var (refName, refVersion) in metadata.References)
            {
                if (!SharedContracts.Contains(refName))
                {
                    continue;
                }

                if (targetContracts.TryGetValue(refName, out var hostVersion))
                {
                    if (refVersion > hostVersion)
                    {
                        issues.Add(new ValidationIssue(
                            dllPath,
                            "error",
                            $"ABI mismatch: '{relativePath}' references {refName} {refVersion}, " +
                            $"but {targetLabel} ({targetDir}) provides {hostVersion}. " +
                            "Rebuild the host or recompile the module against the host SDK."));
                    }

                    continue;
                }

                if (isSurfaceAssembly && shellContracts is null)
                {
                    issues.Add(new ValidationIssue(
                        dllPath,
                        "warning",
                        $"Surface assembly '{relativePath}' references {refName} {refVersion}, " +
                        "which is not in Runner; pass --shell <ShellDir> to verify surface ABI."));
                    continue;
                }

                if (isSurfaceAssembly && hostContracts.TryGetValue(refName, out var runnerFallbackVersion))
                {
                    if (refVersion > runnerFallbackVersion)
                    {
                        issues.Add(new ValidationIssue(
                            dllPath,
                            "error",
                            $"ABI mismatch: '{relativePath}' references {refName} {refVersion}, " +
                            $"but Runner ({hostFullPath}) provides {runnerFallbackVersion}."));
                    }

                    continue;
                }

                issues.Add(new ValidationIssue(
                    dllPath,
                    "error",
                    $"Missing host contract: '{relativePath}' references {refName} {refVersion}, " +
                    $"which is not present in {targetLabel} ({targetDir})."));
            }
        }

        return new AbiValidationReport(
            hostFullPath,
            shellFullPath,
            modulesFullPath,
            assembliesChecked,
            hostContracts,
            shellContracts ?? new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase),
            issues);
    }

    private static void ValidateBundledContract(
        string dllPath,
        string relativePath,
        AssemblyMetadataInfo metadata,
        bool isSurfaceAssembly,
        Dictionary<string, Version>? shellContracts,
        Dictionary<string, Version> targetContracts,
        string targetLabel,
        string targetDir,
        List<ValidationIssue> issues)
    {
        if (targetContracts.TryGetValue(metadata.Name, out var hostVersion))
        {
            if (metadata.Version != hostVersion)
            {
                issues.Add(new ValidationIssue(
                    dllPath,
                    "error",
                    $"Bundled contract mismatch: '{relativePath}' ships {metadata.Name} {metadata.Version}, " +
                    $"which disagrees with {targetLabel} ({targetDir}) version {hostVersion}."));
            }
            else
            {
                issues.Add(new ValidationIssue(
                    dllPath,
                    "warning",
                    $"Duplicate bundled contract: '{relativePath}' bundles {metadata.Name} {metadata.Version} " +
                    $"which is already provided by {targetLabel}."));
            }

            return;
        }

        if (isSurfaceAssembly && shellContracts is null &&
            string.Equals(metadata.Name, "MyPowerTools.AvaloniaSdk", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue(
                dllPath,
                "warning",
                $"Bundled surface contract '{relativePath}' ({metadata.Name} {metadata.Version}) " +
                "could not be verified against Shell; pass --shell <ShellDir>."));
            return;
        }

        issues.Add(new ValidationIssue(
            dllPath,
            "warning",
            $"Bundled contract '{relativePath}' ships {metadata.Name} {metadata.Version}, " +
            $"which is not present in {targetLabel} ({targetDir})."));
    }

    private static Dictionary<string, Version> ReadHostContracts(string directory)
    {
        var contracts = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var dllPath in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var metadata = TryReadAssemblyMetadata(dllPath);
            if (metadata is not null && SharedContracts.Contains(metadata.Name))
            {
                contracts[metadata.Name] = metadata.Version;
            }
        }

        return contracts;
    }

    private static bool IsSurfacePath(string normalizedRelativePath)
    {
        var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "surface", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AssemblyMetadataInfo? TryReadAssemblyMetadata(string dllPath)
    {
        try
        {
            using var stream = File.OpenRead(dllPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return null;
            }

            var reader = peReader.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            var definition = reader.GetAssemblyDefinition();
            var name = reader.GetString(definition.Name);
            var version = definition.Version;
            var references = new List<(string Name, Version Version)>();
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                references.Add((reader.GetString(reference.Name), reference.Version));
            }

            return new AssemblyMetadataInfo(name, version, references);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record AssemblyMetadataInfo(
        string Name,
        Version Version,
        IReadOnlyList<(string Name, Version Version)> References);
}

public sealed record AbiValidationReport(
    string HostDirectory,
    string? ShellDirectory,
    string ModulesDirectory,
    int AssembliesChecked,
    IReadOnlyDictionary<string, Version> HostContracts,
    IReadOnlyDictionary<string, Version> ShellContracts,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != "error");
}
