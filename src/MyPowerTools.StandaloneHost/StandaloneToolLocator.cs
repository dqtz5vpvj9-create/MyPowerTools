using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;

namespace MyPowerTools.StandaloneHost;

public sealed record ResolvedStandaloneTool(
    string ToolRoot,
    string ToolManifestPath,
    MptToolManifest ToolManifest,
    ToolDescriptor Descriptor,
    ToolRoute SelectedRoute,
    string ResolvedSurfaceAssemblyPath,
    string SurfaceFactoryTypeName,
    string? ModuleManifestPath,
    MptModuleManifest? ModuleManifest,
    string? ResolvedInProcAssemblyPath,
    string? InProcModuleTypeName,
    SelectedEntrypoint? StdioEntrypoint);

/// <summary>
/// Universal manifest resolver supporting both Layout A (<c>module.json</c> + <c>ui/tool.json</c>)
/// and Layout B (SDK <c>tool.json</c> + optional <c>stdio-jsonrpc</c> runtime), with automatic
/// path fallback across flattened portable folders and multi-TFM dev trees.
/// </summary>
public static class StandaloneToolLocator
{
    public static ResolvedStandaloneTool Resolve(StandaloneCliOptions options)
    {
        var initialRoot = StandaloneCliOptions.DetermineInitialToolRoot(options);
        if (!Directory.Exists(initialRoot))
        {
            throw new DirectoryNotFoundException($"Tool directory was not found: {initialRoot}");
        }

        var reader = new PackageReader();
        var (effectiveRoot, modulePath, moduleManifest, toolPath, toolManifest) =
            DiscoverManifests(reader, initialRoot, options.ToolId);

        SharedRuntimeAssemblyResolver.RegisterToolRoot(effectiveRoot);

        var ownerModuleId = !string.IsNullOrWhiteSpace(toolManifest.OwnerModuleId)
            ? toolManifest.OwnerModuleId
            : moduleManifest?.Id ?? toolManifest.ToolId;

        var selectedRouteManifest = SelectRoute(toolManifest, options.RouteId);
        var surface = selectedRouteManifest.Surface
            ?? throw new InvalidDataException($"Route '{selectedRouteManifest.RouteId}' in '{toolPath}' does not declare a surface.");
        if (!string.Equals(surface.Kind, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Standalone Avalonia host requires a 'dotnet' surface, but '{toolManifest.ToolId}' declared '{surface.Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(surface.Assembly) || string.IsNullOrWhiteSpace(surface.Type))
        {
            throw new InvalidDataException($"Tool '{toolManifest.ToolId}' route '{selectedRouteManifest.RouteId}' must declare surface assembly and type.");
        }

        var toolDir = Path.GetDirectoryName(toolPath)!;
        var resolvedSurfaceDll = ResolveBinaryPath(effectiveRoot, toolDir, surface.Assembly);
        if (!File.Exists(resolvedSurfaceDll))
        {
            throw new FileNotFoundException(
                $"Could not resolve surface assembly '{surface.Assembly}' for tool '{toolManifest.ToolId}' under '{effectiveRoot}'.",
                resolvedSurfaceDll);
        }

        SharedRuntimeAssemblyResolver.AddProbeDirectory(Path.GetDirectoryName(resolvedSurfaceDll));

        string? resolvedInProcDll = null;
        string? inProcTypeName = null;
        SelectedEntrypoint? stdioEntrypoint = null;

        if (moduleManifest is not null && modulePath is not null)
        {
            var moduleDir = Path.GetDirectoryName(modulePath)!;
            var inProcEp = SelectInProcEntrypoint(moduleManifest);
            if (inProcEp is not null && !string.IsNullOrWhiteSpace(inProcEp.Assembly) && !string.IsNullOrWhiteSpace(inProcEp.Type))
            {
                resolvedInProcDll = ResolveBinaryPath(effectiveRoot, moduleDir, inProcEp.Assembly);
                if (!File.Exists(resolvedInProcDll))
                {
                    throw new FileNotFoundException(
                        $"Could not resolve InProc module assembly '{inProcEp.Assembly}' for module '{moduleManifest.Id}'.",
                        resolvedInProcDll);
                }

                inProcTypeName = inProcEp.Type;
                SharedRuntimeAssemblyResolver.AddProbeDirectory(Path.GetDirectoryName(resolvedInProcDll));
            }
            else
            {
                var stdioEp = moduleManifest.Entrypoints.FirstOrDefault(ep =>
                    string.Equals(ep.Kind, "jsonrpc-stdio", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ep.Kind, "stdio-jsonrpc", StringComparison.OrdinalIgnoreCase));
                if (stdioEp is not null && !string.IsNullOrWhiteSpace(stdioEp.Command))
                {
                    stdioEntrypoint = BuildStdioEntrypoint(effectiveRoot, moduleDir, stdioEp.Command, stdioEp.Args);
                }
            }
        }

        if (resolvedInProcDll is null &&
            stdioEntrypoint is null &&
            toolManifest.Runtime is { } toolRuntime &&
            (string.Equals(toolRuntime.Transport, "stdio-jsonrpc", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(toolRuntime.Transport, "jsonrpc-stdio", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(toolRuntime.Command))
        {
            stdioEntrypoint = BuildStdioEntrypoint(effectiveRoot, toolDir, toolRuntime.Command, toolRuntime.Args);
        }

        var descriptor = BuildToolDescriptor(toolManifest, ownerModuleId, toolDir, effectiveRoot, resolvedSurfaceDll);
        var selectedRoute = descriptor.Routes.First(r =>
            string.Equals(r.RouteId, selectedRouteManifest.RouteId, StringComparison.OrdinalIgnoreCase));

        return new ResolvedStandaloneTool(
            effectiveRoot,
            toolPath,
            toolManifest,
            descriptor,
            selectedRoute,
            resolvedSurfaceDll,
            surface.Type,
            modulePath,
            moduleManifest,
            resolvedInProcDll,
            inProcTypeName,
            stdioEntrypoint);
    }

    public static string ResolveBinaryPath(string toolRoot, string manifestDir, string rawRelativePath)
    {
        var candidate = StandalonePackageNormalizer.ResolveAssetPath(manifestDir, toolRoot, rawRelativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var fileName = Path.GetFileName(rawRelativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return candidate;
        }

        // Additional portable & development repository fallbacks (e.g. tools/<name>/package -> ../src/<Proj>/bin/...).
        foreach (var extraCandidate in EnumerateRepoDevBuildCandidates(toolRoot, manifestDir, fileName))
        {
            if (File.Exists(extraCandidate))
            {
                return Path.GetFullPath(extraCandidate);
            }
        }

        // Also check shared-ui / host directories if an assembly was placed in shared-ui.
        var probeDirs = SharedRuntimeAssemblyResolver.BuildOrderedProbeDirectories(toolRoot);
        foreach (var probeDir in probeDirs)
        {
            var inProbe = Path.Combine(probeDir, fileName);
            if (File.Exists(inProbe))
            {
                return Path.GetFullPath(inProbe);
            }
        }

        return candidate;
    }

    private static (
        string EffectiveRoot,
        string? ModulePath,
        MptModuleManifest? ModuleManifest,
        string ToolPath,
        MptToolManifest ToolManifest) DiscoverManifests(
            PackageReader reader,
            string initialRoot,
            string? requestedToolId)
    {
        var modulePath = FindModuleManifestCandidate(initialRoot);
        if (modulePath is not null)
        {
            var moduleManifest = reader.ReadJson<MptModuleManifest>(modulePath);
            var moduleDir = Path.GetDirectoryName(modulePath)!;
            var effectiveRoot = DetermineEffectiveToolRoot(initialRoot, moduleDir);
            var toolCandidates = FindToolManifestCandidatesForModule(effectiveRoot, moduleDir, moduleManifest);
            if (toolCandidates.Count == 0)
            {
                throw new FileNotFoundException($"Module '{moduleManifest.Id}' at '{modulePath}' did not expose any tool.json manifest.");
            }

            foreach (var candidateToolPath in toolCandidates)
            {
                var parsedTool = reader.ReadDevelopmentToolManifest(candidateToolPath, Path.GetDirectoryName(candidateToolPath)!);
                if (string.IsNullOrWhiteSpace(requestedToolId) ||
                    string.Equals(parsedTool.ToolId, requestedToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return (effectiveRoot, modulePath, moduleManifest, candidateToolPath, parsedTool);
                }
            }

            var fallbackToolPath = toolCandidates[0];
            return (
                effectiveRoot,
                modulePath,
                moduleManifest,
                fallbackToolPath,
                reader.ReadDevelopmentToolManifest(fallbackToolPath, Path.GetDirectoryName(fallbackToolPath)!));
        }

        var standaloneToolCandidates = FindStandaloneToolManifestCandidates(initialRoot);
        if (standaloneToolCandidates.Count == 0)
        {
            throw new FileNotFoundException(
                $"No module.json or tool.json manifest was found under '{initialRoot}'.");
        }

        foreach (var candidatePath in standaloneToolCandidates)
        {
            var candidateDir = Path.GetDirectoryName(candidatePath)!;
            var parsed = reader.ReadDevelopmentToolManifest(candidatePath, candidateDir);
            if (string.IsNullOrWhiteSpace(requestedToolId) ||
                string.Equals(parsed.ToolId, requestedToolId, StringComparison.OrdinalIgnoreCase))
            {
                var effectiveRoot = DetermineEffectiveToolRoot(initialRoot, candidateDir);
                return (effectiveRoot, null, null, candidatePath, parsed);
            }
        }

        var firstPath = standaloneToolCandidates[0];
        var firstDir = Path.GetDirectoryName(firstPath)!;
        return (
            DetermineEffectiveToolRoot(initialRoot, firstDir),
            null,
            null,
            firstPath,
            reader.ReadDevelopmentToolManifest(firstPath, firstDir));
    }

    private static string DetermineEffectiveToolRoot(string initialRoot, string manifestDir)
    {
        var fullInitial = Path.GetFullPath(initialRoot);
        var fullManifestDir = Path.GetFullPath(manifestDir);
        var leaf = Path.GetFileName(fullManifestDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "app", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(leaf, "ui", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(fullManifestDir)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        if (string.Equals(fullInitial, fullManifestDir, StringComparison.OrdinalIgnoreCase))
        {
            return fullInitial;
        }

        // When pointing at a repo tool root (e.g. tools/ime-manager -> tools/ime-manager/sdk-tool),
        // use the SDK/package directory if initialRoot itself had no manifest.
        if (!StandaloneCliOptions.LooksLikeToolFolder(fullInitial))
        {
            return fullManifestDir;
        }

        return fullInitial;
    }

    private static string? FindModuleManifestCandidate(string root)
    {
        string[] directLocations =
        [
            Path.Combine(root, "module.json"),
            Path.Combine(root, "app", "module.json"),
            Path.Combine(root, "package", "module.json")
        ];

        foreach (var candidate in directLocations)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var currentIntegrationModules = Path.Combine(root, "current-integration", "modules");
        if (Directory.Exists(currentIntegrationModules))
        {
            foreach (var dir in Directory.EnumerateDirectories(currentIntegrationModules).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var nested = Path.Combine(dir, "module.json");
                if (File.Exists(nested))
                {
                    return Path.GetFullPath(nested);
                }
            }
        }

        return null;
    }

    private static List<string> FindToolManifestCandidatesForModule(
        string effectiveRoot,
        string moduleDir,
        MptModuleManifest moduleManifest)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddIfExists(string path)
        {
            if (File.Exists(path))
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                {
                    results.Add(full);
                }
            }
        }

        foreach (var rel in moduleManifest.Tools)
        {
            AddIfExists(Path.Combine(moduleDir, rel));
            AddIfExists(Path.Combine(effectiveRoot, rel));
        }

        AddIfExists(Path.Combine(effectiveRoot, "ui", "tool.json"));
        AddIfExists(Path.Combine(effectiveRoot, "tool.json"));
        AddIfExists(Path.Combine(effectiveRoot, "app", "ui", "tool.json"));
        AddIfExists(Path.Combine(effectiveRoot, "app", "tool.json"));

        return results;
    }

    private static List<string> FindStandaloneToolManifestCandidates(string root)
    {
        var results = new List<string>();
        string[] candidates =
        [
            Path.Combine(root, "tool.json"),
            Path.Combine(root, "app", "tool.json"),
            Path.Combine(root, "ui", "tool.json"),
            Path.Combine(root, "sdk-tool", "tool.json")
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                results.Add(Path.GetFullPath(path));
            }
        }

        return results;
    }

    private static MptToolRouteManifest SelectRoute(MptToolManifest manifest, string? requestedRouteId)
    {
        if (manifest.Routes.Count == 0)
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' does not declare any routes.");
        }

        if (!string.IsNullOrWhiteSpace(requestedRouteId))
        {
            var explicitMatch = manifest.Routes.FirstOrDefault(r =>
                string.Equals(r.RouteId, requestedRouteId, StringComparison.OrdinalIgnoreCase));
            if (explicitMatch is not null)
            {
                return explicitMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.PrimaryRouteId))
        {
            var primary = manifest.Routes.FirstOrDefault(r =>
                string.Equals(r.RouteId, manifest.PrimaryRouteId, StringComparison.OrdinalIgnoreCase));
            if (primary is not null)
            {
                return primary;
            }
        }

        return manifest.Routes[0];
    }

    private static MptEntrypointManifest? SelectInProcEntrypoint(MptModuleManifest manifest)
    {
        var platform = PlatformId.Current();
        var matching = manifest.Entrypoints
            .Where(ep => string.Equals(ep.Kind, "inproc-dotnet", StringComparison.OrdinalIgnoreCase))
            .Where(ep => ep.Platforms.Count == 0 || ep.Platforms.Any(platform.Matches))
            .OrderByDescending(ep => ep.Priority)
            .FirstOrDefault();

        return matching ?? manifest.Entrypoints
            .Where(ep => string.Equals(ep.Kind, "inproc-dotnet", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ep => ep.Priority)
            .FirstOrDefault();
    }

    private static SelectedEntrypoint BuildStdioEntrypoint(
        string effectiveRoot,
        string manifestDir,
        string rawCommand,
        IReadOnlyList<string> rawArgs)
    {
        var resolvedCmd = ResolveBinaryPath(effectiveRoot, manifestDir, rawCommand);
        var dllCandidate = Path.ChangeExtension(resolvedCmd, ".dll");
        if (!File.Exists(dllCandidate) && rawCommand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var rawDll = Path.ChangeExtension(rawCommand, ".dll");
            var probedDll = ResolveBinaryPath(effectiveRoot, manifestDir, rawDll);
            if (File.Exists(probedDll))
            {
                dllCandidate = probedDll;
            }
        }

        // Prefer `dotnet --roll-forward Major <Runtime>.dll` when the managed runtime DLL exists:
        // works identically across .NET 8 / 9 / 10 on bare Windows, macOS, and Linux machines.
        if (File.Exists(dllCandidate))
        {
            var muxer = StandalonePackageNormalizer.ResolveDotnetMuxerPath();
            if (!string.IsNullOrWhiteSpace(muxer))
            {
                SharedRuntimeAssemblyResolver.AddProbeDirectory(Path.GetDirectoryName(dllCandidate));
                return new SelectedEntrypoint(
                    Kind: "jsonrpc-stdio",
                    Priority: 100,
                    RuntimeId: null,
                    Command: muxer,
                    Args: ["--roll-forward", "Major", dllCandidate, ..rawArgs],
                    Assembly: null,
                    Type: null,
                    Service: null,
                    EndpointTransport: null,
                    EndpointAddress: null,
                    HealthPath: null,
                    SelectionReason: $"Managed runtime DLL launched via {muxer}.",
                    SelectionDiagnostics: Array.Empty<TransportSelectionDiagnostic>(),
                    InProcMaxCallMs: null,
                    SidecarReadyTimeoutMs: null,
                    SidecarRestartLimit: null,
                    SidecarRestartWindowSeconds: null,
                    SidecarKillProcessTree: null);
            }
        }

        if (!File.Exists(resolvedCmd))
        {
            throw new FileNotFoundException(
                $"Could not resolve stdio runtime command '{rawCommand}' under '{effectiveRoot}'.",
                resolvedCmd);
        }

        return new SelectedEntrypoint(
            Kind: "jsonrpc-stdio",
            Priority: 100,
            RuntimeId: null,
            Command: resolvedCmd,
            Args: rawArgs,
            Assembly: null,
            Type: null,
            Service: null,
            EndpointTransport: null,
            EndpointAddress: null,
            HealthPath: null,
            SelectionReason: $"Native/apphost stdio command resolved at {resolvedCmd}.",
            SelectionDiagnostics: Array.Empty<TransportSelectionDiagnostic>(),
            InProcMaxCallMs: null,
            SidecarReadyTimeoutMs: null,
            SidecarRestartLimit: null,
            SidecarRestartWindowSeconds: null,
            SidecarKillProcessTree: null);
    }

    private static ToolDescriptor BuildToolDescriptor(
        MptToolManifest manifest,
        string ownerModuleId,
        string toolDir,
        string effectiveRoot,
        string primaryResolvedSurfaceDll)
    {
        var routes = manifest.Routes
            .Select(route =>
            {
                var surf = route.Surface;
                var routeDll = string.IsNullOrWhiteSpace(surf?.Assembly)
                    ? ""
                    : ResolveBinaryPath(effectiveRoot, toolDir, surf.Assembly);
                if (string.IsNullOrWhiteSpace(routeDll) &&
                    string.Equals(route.RouteId, manifest.PrimaryRouteId, StringComparison.OrdinalIgnoreCase))
                {
                    routeDll = primaryResolvedSurfaceDll;
                }

                return new ToolRoute(
                    route.RouteId,
                    route.SurfaceId,
                    route.Title,
                    route.Icon,
                    surf?.Kind ?? "",
                    surf?.Source ?? "",
                    surf?.StaticRoot ?? "",
                    routeDll,
                    surf?.Type ?? "",
                    surf?.OpenExternal ?? false,
                    surf?.AllowedOrigins ?? []);
            })
            .ToArray();

        return new ToolDescriptor(
            manifest.ToolId,
            ownerModuleId,
            manifest.Title,
            manifest.Description,
            manifest.Icon,
            manifest.Category,
            string.IsNullOrWhiteSpace(manifest.PrimaryRouteId) ? routes[0].RouteId : manifest.PrimaryRouteId,
            routes,
            new ToolHomeCard(
                manifest.HomeCard.Summary,
                manifest.HomeCard.PrimaryActionLabel,
                manifest.HomeCard.StatusBinding,
                manifest.HomeCard.Order),
            string.IsNullOrWhiteSpace(manifest.Availability) ? "available" : manifest.Availability,
            manifest.Type,
            effectiveRoot,
            manifest.Runtime is null
                ? null
                : new ToolRuntime(
                    manifest.Runtime.Transport,
                    manifest.Runtime.Endpoint,
                    manifest.Runtime.Command,
                    manifest.Runtime.Args,
                    manifest.Runtime.HealthPath,
                    manifest.Runtime.LogsPath,
                    manifest.Runtime.TimeoutMs,
                    manifest.Runtime.Remote),
            manifest.Settings is null
                ? null
                : new ToolSettings(
                    manifest.Settings.Schema,
                    manifest.Settings.Values,
                    manifest.Settings.Secrets),
            manifest.Commands.Select(cmd => new ToolCommand(
                cmd.Id,
                cmd.Title,
                cmd.Description,
                cmd.Method,
                cmd.Path)).ToArray(),
            manifest.DataRoots,
            manifest.DataRetention);
    }

    private static IEnumerable<string> EnumerateRepoDevBuildCandidates(
        string toolRoot,
        string manifestDir,
        string fileName)
    {
        var projectStem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(projectStem))
        {
            yield break;
        }

        string[] configs = ["Release", "Debug"];
        string[] tfms = ["net10.0", "net8.0", "net9.0"];

        var searchParents = new List<string>();
        var cursor = Path.GetFullPath(manifestDir);
        for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(cursor); depth++)
        {
            if (!searchParents.Contains(cursor, StringComparer.OrdinalIgnoreCase))
            {
                searchParents.Add(cursor);
            }

            cursor = Directory.GetParent(cursor)?.FullName ?? "";
        }

        var rootFull = Path.GetFullPath(toolRoot);
        if (!searchParents.Contains(rootFull, StringComparer.OrdinalIgnoreCase))
        {
            searchParents.Insert(0, rootFull);
        }

        foreach (var ancestor in searchParents)
        {
            foreach (var srcPrefix in new[] { "src", Path.Combine("current-integration", "src"), Path.Combine("sdk-tool", "src") })
            {
                var projOutRoot = Path.Combine(ancestor, srcPrefix, projectStem, "bin");
                if (!Directory.Exists(projOutRoot))
                {
                    continue;
                }

                foreach (var cfg in configs)
                {
                    foreach (var tfm in tfms)
                    {
                        yield return Path.Combine(projOutRoot, cfg, tfm, fileName);
                    }
                }
            }
        }
    }
}
