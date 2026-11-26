using MyPowerTools.Packaging;

namespace MyPowerTools.Runtime;

/// <summary>
/// Enables zero-configuration Hub aggregation when a standalone or composable tool folder
/// (containing <c>module.json</c> + <c>ui/tool.json</c>, or <c>app/tool.json</c>, with binaries
/// placed at the root or inside <c>app/</c>, <c>surface/</c>, or <c>module/</c>) is dropped into
/// <c>tools/</c>, <c>custom-tools/</c>, or a sibling <c>Mpt*</c> directory.
/// </summary>
public static class StandalonePackageNormalizer
{
    public static IReadOnlyList<MptPackageDefinition> DiscoverStandalonePackages(
        PackageReader reader,
        IEnumerable<string> roots,
        IReadOnlySet<string> installedModuleIds)
    {
        var results = new List<MptPackageDefinition>();
        var seenManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawRoot in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawRoot));
            if (!Directory.Exists(root))
            {
                continue;
            }

            var candidateDirs = new List<string> { root };
            try
            {
                candidateDirs.AddRange(
                    Directory.EnumerateDirectories(root)
                        .Where(dir => !Path.GetFileName(dir).EndsWith(".rollback", StringComparison.OrdinalIgnoreCase))
                        .Where(dir => !string.Equals(Path.GetFileName(dir), "shared-ui", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                continue;
            }

            foreach (var dir in candidateDirs)
            {
                var moduleManifestPath = FindModuleManifest(dir);
                if (moduleManifestPath is not null)
                {
                    if (!seenManifests.Add(moduleManifestPath))
                    {
                        continue;
                    }

                    var package = TryReadStandaloneModulePackage(reader, dir, moduleManifestPath, installedModuleIds);
                    if (package is not null)
                    {
                        results.Add(package);
                    }

                    continue;
                }

                // Layout B standalone folder where tool.json lives inside app/tool.json rather than <dir>/tool.json.
                if (!File.Exists(Path.Combine(dir, "tool.json")))
                {
                    var appToolPath = Path.Combine(dir, "app", "tool.json");
                    if (File.Exists(appToolPath) && seenManifests.Add(Path.GetFullPath(appToolPath)))
                    {
                        var appToolPackage = TryReadNestedToolPackage(reader, dir, Path.Combine(dir, "app"));
                        if (appToolPackage is not null &&
                            !appToolPackage.Modules.All(m => installedModuleIds.Contains(m.Manifest.Id)))
                        {
                            results.Add(appToolPackage);
                        }
                    }
                }
            }
        }

        return results;
    }

    public static MptPackageDefinition NormalizeDevelopmentPackage(MptPackageDefinition package)
    {
        if (!package.IsDevelopmentTool)
        {
            return package;
        }

        var normalizedModules = package.Modules
            .Select(module => NormalizeModuleDefinition(package.Directory, module))
            .ToArray();

        return package with { Modules = normalizedModules };
    }

    public static string ResolveAssetPath(string primaryDirectory, string packageDirectory, string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return rawPath;
        }

        if (Uri.TryCreate(rawPath, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return rawPath;
        }

        var directPrimary = Path.GetFullPath(Path.Combine(primaryDirectory, rawPath));
        if (File.Exists(directPrimary))
        {
            return directPrimary;
        }

        var directPackage = Path.GetFullPath(Path.Combine(packageDirectory, rawPath));
        if (File.Exists(directPackage))
        {
            return directPackage;
        }

        foreach (var variant in EnumerateFrameworkAndConfigVariants(rawPath))
        {
            var primaryVariant = Path.GetFullPath(Path.Combine(primaryDirectory, variant));
            if (File.Exists(primaryVariant))
            {
                return primaryVariant;
            }

            var packageVariant = Path.GetFullPath(Path.Combine(packageDirectory, variant));
            if (File.Exists(packageVariant))
            {
                return packageVariant;
            }
        }

        var fileName = Path.GetFileName(rawPath.Replace('\\', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var probeDir in EnumerateProbeDirectories(primaryDirectory, packageDirectory))
            {
                var candidate = Path.Combine(probeDir, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return directPrimary;
    }

    private static string? FindModuleManifest(string dir)
    {
        var rootModule = Path.Combine(dir, "module.json");
        if (File.Exists(rootModule))
        {
            return Path.GetFullPath(rootModule);
        }

        var appModule = Path.Combine(dir, "app", "module.json");
        if (File.Exists(appModule))
        {
            return Path.GetFullPath(appModule);
        }

        return null;
    }

    private static MptPackageDefinition? TryReadStandaloneModulePackage(
        PackageReader reader,
        string containerDirectory,
        string moduleManifestPath,
        IReadOnlySet<string> installedModuleIds)
    {
        try
        {
            var manifestDir = Path.GetDirectoryName(moduleManifestPath)!;
            var rawPackage = reader.ReadPackageDirectory(manifestDir);

            var remainingModules = rawPackage.Modules
                .Where(module => !installedModuleIds.Contains(module.Manifest.Id))
                .Select(module => NormalizeModuleDefinition(containerDirectory, module))
                .ToArray();

            if (remainingModules.Length == 0)
            {
                return null;
            }

            return new MptPackageDefinition(
                Path.GetFullPath(containerDirectory),
                rawPackage.Package,
                remainingModules,
                IsDevelopmentTool: true);
        }
        catch (Exception ex)
        {
            var folderName = Path.GetFileName(containerDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var fallbackId = $"dev.{folderName}";
            var message = ex.GetBaseException().Message;
            var failedModule = new MptModuleManifest
            {
                SchemaVersion = "1.0",
                Id = fallbackId,
                PackageId = fallbackId,
                DisplayName = folderName,
                Version = "0.1.0",
                ModuleSdk = "1.0"
            };
            var failedPackage = new MptPackageManifest
            {
                SchemaVersion = "1.0",
                Id = fallbackId,
                DisplayName = folderName,
                Version = "0.1.0",
                Publisher = "standalone",
                MinHostVersion = "0.2.0"
            };
            return new MptPackageDefinition(
                Path.GetFullPath(containerDirectory),
                failedPackage,
                [new MptModuleDefinition(Path.GetFullPath(containerDirectory), moduleManifestPath, failedModule, LoadError: message)],
                IsDevelopmentTool: true);
        }
    }

    private static MptPackageDefinition? TryReadNestedToolPackage(
        PackageReader reader,
        string containerDirectory,
        string nestedToolDirectory)
    {
        try
        {
            var raw = reader.ReadDevelopmentToolDirectory(nestedToolDirectory);
            return NormalizeDevelopmentPackage(raw with { Directory = Path.GetFullPath(containerDirectory) });
        }
        catch
        {
            return null;
        }
    }

    private static MptModuleDefinition NormalizeModuleDefinition(string containerDirectory, MptModuleDefinition module)
    {
        if (module.LoadError is not null)
        {
            return module;
        }

        var effectiveDirectory = module.Directory;
        var updatedTools = new List<string>(module.Manifest.Tools.Count);
        foreach (var toolRel in module.Manifest.Tools)
        {
            var inModuleDir = Path.GetFullPath(Path.Combine(module.Directory, toolRel));
            if (File.Exists(inModuleDir))
            {
                updatedTools.Add(toolRel);
                continue;
            }

            var inContainerDir = Path.GetFullPath(Path.Combine(containerDirectory, toolRel));
            if (File.Exists(inContainerDir))
            {
                effectiveDirectory = Path.GetFullPath(containerDirectory);
                updatedTools.Add(toolRel);
                continue;
            }

            var rootToolJson = Path.Combine(containerDirectory, "tool.json");
            if (File.Exists(rootToolJson))
            {
                effectiveDirectory = Path.GetFullPath(containerDirectory);
                updatedTools.Add("tool.json");
                continue;
            }

            updatedTools.Add(toolRel);
        }

        var updatedEntrypoints = module.Manifest.Entrypoints
            .Select(ep => NormalizeEntrypoint(containerDirectory, effectiveDirectory, ep))
            .ToList();

        var updatedManifest = new MptModuleManifest
        {
            SchemaVersion = module.Manifest.SchemaVersion,
            Id = module.Manifest.Id,
            PackageId = module.Manifest.PackageId,
            DisplayName = module.Manifest.DisplayName,
            Version = module.Manifest.Version,
            ModuleSdk = module.Manifest.ModuleSdk,
            Entrypoints = updatedEntrypoints,
            Capabilities = module.Manifest.Capabilities,
            Requires = module.Manifest.Requires,
            Permissions = module.Manifest.Permissions,
            Surfaces = module.Manifest.Surfaces,
            UiSurfaces = module.Manifest.UiSurfaces,
            Tools = updatedTools,
            Hotkeys = module.Manifest.Hotkeys,
            StaticIndexes = module.Manifest.StaticIndexes,
            RuntimePolicy = module.Manifest.RuntimePolicy,
            Development = module.Manifest.Development,
            ExtensionData = module.Manifest.ExtensionData
        };

        return module with
        {
            Directory = effectiveDirectory,
            Manifest = updatedManifest
        };
    }

    private static MptEntrypointManifest NormalizeEntrypoint(
        string containerDirectory,
        string moduleDirectory,
        MptEntrypointManifest entrypoint)
    {
        var resolvedAssembly = entrypoint.Assembly;
        if (!string.IsNullOrWhiteSpace(entrypoint.Assembly))
        {
            var candidate = ResolveAssetPath(moduleDirectory, containerDirectory, entrypoint.Assembly);
            if (File.Exists(candidate))
            {
                resolvedAssembly = candidate;
            }
        }

        var resolvedCommand = entrypoint.Command;
        var resolvedArgs = entrypoint.Args;
        if (!string.IsNullOrWhiteSpace(entrypoint.Command))
        {
            var candidateCmd = ResolveAssetPath(moduleDirectory, containerDirectory, entrypoint.Command);
            var companionDll = Path.ChangeExtension(candidateCmd, ".dll");
            if (!File.Exists(companionDll) && entrypoint.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var dllRaw = Path.ChangeExtension(entrypoint.Command, ".dll");
                var probedDll = ResolveAssetPath(moduleDirectory, containerDirectory, dllRaw);
                if (File.Exists(probedDll))
                {
                    companionDll = probedDll;
                }
            }

            if (File.Exists(companionDll) && (!OperatingSystem.IsWindows() || !File.Exists(candidateCmd)))
            {
                var dotnetHost = ResolveDotnetMuxerPath();
                if (!string.IsNullOrWhiteSpace(dotnetHost))
                {
                    resolvedCommand = dotnetHost;
                    resolvedArgs = ["--roll-forward", "Major", companionDll, ..entrypoint.Args];
                }
            }
            else if (File.Exists(candidateCmd))
            {
                resolvedCommand = candidateCmd;
            }
        }

        if (string.Equals(resolvedAssembly, entrypoint.Assembly, StringComparison.Ordinal) &&
            string.Equals(resolvedCommand, entrypoint.Command, StringComparison.Ordinal) &&
            ReferenceEquals(resolvedArgs, entrypoint.Args))
        {
            return entrypoint;
        }

        return new MptEntrypointManifest
        {
            Kind = entrypoint.Kind,
            Priority = entrypoint.Priority,
            Platforms = entrypoint.Platforms,
            Assembly = resolvedAssembly,
            Type = entrypoint.Type,
            Command = resolvedCommand,
            Args = resolvedArgs,
            RuntimeId = entrypoint.RuntimeId,
            Service = entrypoint.Service,
            BaseUrl = entrypoint.BaseUrl,
            Windows = entrypoint.Windows,
            Macos = entrypoint.Macos,
            Linux = entrypoint.Linux,
            Health = entrypoint.Health,
            Compat = entrypoint.Compat,
            StartupCost = entrypoint.StartupCost,
            ExtensionData = entrypoint.ExtensionData
        };
    }

    public static string? ResolveDotnetMuxerPath()
    {
        var envHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(envHost) && File.Exists(envHost))
        {
            return envHost;
        }

        var exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var envRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            var fromRoot = Path.Combine(envRoot, exeName);
            if (File.Exists(fromRoot))
            {
                return fromRoot;
            }
        }

        var coreLibDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(coreLibDir))
        {
            var muxerFromRuntime = Path.GetFullPath(Path.Combine(coreLibDir, "..", "..", "..", exeName));
            if (File.Exists(muxerFromRuntime))
            {
                return muxerFromRuntime;
            }
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var homeDotnet = Path.Combine(home, ".dotnet", exeName);
            if (File.Exists(homeDotnet))
            {
                return homeDotnet;
            }
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFrameworkAndConfigVariants(string rawPath)
    {
        string[] tfms = ["net8.0", "net10.0", "net9.0"];
        string[] configs = ["Release", "Debug"];

        foreach (var fromTfm in tfms)
        {
            if (!rawPath.Contains(fromTfm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var toTfm in tfms)
            {
                if (!string.Equals(fromTfm, toTfm, StringComparison.OrdinalIgnoreCase))
                {
                    var swappedTfm = ReplaceIgnoreCase(rawPath, fromTfm, toTfm);
                    yield return swappedTfm;

                    foreach (var fromCfg in configs)
                    {
                        if (swappedTfm.Contains(fromCfg, StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var toCfg in configs)
                            {
                                if (!string.Equals(fromCfg, toCfg, StringComparison.OrdinalIgnoreCase))
                                {
                                    yield return ReplaceIgnoreCase(swappedTfm, fromCfg, toCfg);
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (var fromCfg in configs)
        {
            if (rawPath.Contains(fromCfg, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var toCfg in configs)
                {
                    if (!string.Equals(fromCfg, toCfg, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return ReplaceIgnoreCase(rawPath, fromCfg, toCfg);
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateProbeDirectories(string primaryDirectory, string packageDirectory)
    {
        string[] roots = [primaryDirectory, packageDirectory];
        string[] subs = ["", "app", "surface", "module", "runtime"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var sub in subs)
            {
                var dir = string.IsNullOrEmpty(sub) ? Path.GetFullPath(root) : Path.GetFullPath(Path.Combine(root, sub));
                if (seen.Add(dir) && Directory.Exists(dir))
                {
                    yield return dir;
                }
            }
        }
    }

    private static string ReplaceIgnoreCase(string input, string oldValue, string newValue)
    {
        var idx = input.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return input;
        }

        return string.Concat(input.AsSpan(0, idx), newValue, input.AsSpan(idx + oldValue.Length));
    }
}
