using System.Text.Json;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class TransportSelector
{
    private readonly PlatformId _platform;
    private readonly IPlatformPathService _pathService;

    public TransportSelector(PlatformId platform, IPlatformPathService? pathService = null)
    {
        _platform = platform;
        _pathService = pathService ?? new PlatformPathService();
    }

    public TransportSelectionResult Select(MptPackageDefinition package, MptModuleDefinition module)
    {
        var diagnostics = new List<TransportSelectionDiagnostic>();
        var candidates = module.Manifest.Entrypoints
            .Select(entrypoint => CreateCandidate(package, entrypoint))
            .ToArray();
        var viable = new List<EntrypointCandidate>();

        foreach (var candidate in candidates)
        {
            var label = DescribeCandidate(candidate);
            if (candidate.Resolved is null)
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, "package runtime could not be resolved."));
                continue;
            }

            if (!IsPlatformMatch(candidate.Resolved))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, $"platform '{_platform.Rid}' is not supported by this entrypoint."));
                continue;
            }

            if (!IsAllowedByRuntimePolicy(module, candidate, out var policyReason))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, policyReason));
                continue;
            }

            if (!IsViable(package, module, candidate.Resolved, out var viabilityReason))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, viabilityReason));
                continue;
            }

            diagnostics.Add(new TransportSelectionDiagnostic(label, candidate.RuntimeId ?? "", "eligible", EligibilityReason(module.Manifest.RuntimePolicy, candidate)));
            viable.Add(candidate);
        }

        var selected = viable
            .OrderBy(candidate => PolicyRank(module.Manifest.RuntimePolicy, candidate))
            .ThenByDescending(candidate => candidate.Resolved!.Priority)
            .ThenBy(candidate => candidate.Resolved!.StartupCost ?? 0)
            .FirstOrDefault();

        if (selected is null)
        {
            return new TransportSelectionResult(null, diagnostics);
        }

        var selectionReason = SelectionReason(module.Manifest.RuntimePolicy, selected);
        diagnostics.Add(new TransportSelectionDiagnostic(DescribeCandidate(selected), selected.RuntimeId ?? "", "selected", selectionReason));
        return new TransportSelectionResult(ToSelected(package, module, selected, selectionReason, diagnostics), diagnostics);
    }

    public TransportSelectionResult SelectForCommand(
        MptPackageDefinition package,
        MptModuleDefinition module,
        Sdk.MptCommandDescriptor command,
        Sdk.CommandRequest request,
        IReadOnlySet<string> availableTransportKinds)
    {
        var constraints = RuntimeOperationPolicy.GetConstraints(command, request);
        if (constraints.Count == 0)
        {
            return Select(package, module);
        }

        var diagnostics = new List<TransportSelectionDiagnostic>();
        var candidates = module.Manifest.Entrypoints
            .Select(entrypoint => CreateCandidate(package, entrypoint))
            .ToArray();
        var viable = new List<EntrypointCandidate>();

        foreach (var candidate in candidates)
        {
            var label = DescribeCandidate(candidate);
            if (candidate.Resolved is null)
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, "package runtime could not be resolved."));
                continue;
            }

            if (!IsPlatformMatch(candidate.Resolved))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, $"platform '{_platform.Rid}' is not supported by this entrypoint."));
                continue;
            }

            if (!IsAllowedByRuntimePolicy(module, candidate, out var policyReason))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, policyReason));
                continue;
            }

            if (!IsViable(package, module, candidate.Resolved, out var viabilityReason))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, viabilityReason));
                continue;
            }

            if (!availableTransportKinds.Contains(candidate.Resolved.Kind))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, $"transport runtime '{candidate.Resolved.Kind}' is not registered in this host."));
                continue;
            }

            if (!IsAllowedForCommandRoute(module, command, constraints, candidate.Resolved, out var routeReason))
            {
                diagnostics.Add(Skipped(label, candidate.RuntimeId, routeReason));
                continue;
            }

            diagnostics.Add(new TransportSelectionDiagnostic(label, candidate.RuntimeId ?? "", "eligible", routeReason));
            viable.Add(candidate);
        }

        var selected = viable
            .OrderBy(candidate => PolicyRank(module.Manifest.RuntimePolicy, candidate))
            .ThenByDescending(candidate => candidate.Resolved!.Priority)
            .ThenBy(candidate => candidate.Resolved!.StartupCost ?? 0)
            .FirstOrDefault();

        if (selected is null)
        {
            return new TransportSelectionResult(null, diagnostics);
        }

        var selectionReason = $"command '{command.Id}' route satisfies constraints [{string.Join(", ", constraints)}] via {DescribeCandidate(selected)}.";
        diagnostics.Add(new TransportSelectionDiagnostic(DescribeCandidate(selected), selected.RuntimeId ?? "", "selected", selectionReason));
        return new TransportSelectionResult(ToSelected(package, module, selected, selectionReason, diagnostics), diagnostics);
    }

    private EntrypointCandidate CreateCandidate(MptPackageDefinition package, MptEntrypointManifest entrypoint)
    {
        var resolved = ResolvePackageRuntime(package, entrypoint) ?? (entrypoint.Kind == "package-runtime" ? null : entrypoint);
        return new EntrypointCandidate(entrypoint, resolved, entrypoint.Kind == "package-runtime" ? entrypoint.RuntimeId : entrypoint.RuntimeId);
    }

    private MptEntrypointManifest? ResolvePackageRuntime(MptPackageDefinition package, MptEntrypointManifest entrypoint)
    {
        if (entrypoint.Kind != "package-runtime" || string.IsNullOrWhiteSpace(entrypoint.RuntimeId))
        {
            return null;
        }

        return package.Package.Shared?.Runtimes
            .FirstOrDefault(runtime => runtime.Id == entrypoint.RuntimeId)
            ?.Entrypoints
            .Where(IsPlatformMatch)
            .OrderByDescending(candidate => candidate.Priority)
            .FirstOrDefault();
    }

    private bool IsPlatformMatch(MptEntrypointManifest entrypoint)
    {
        return entrypoint.Platforms.Count == 0 || entrypoint.Platforms.Any(_platform.Matches);
    }

    private static bool IsAllowedByRuntimePolicy(MptModuleDefinition module, EntrypointCandidate candidate, out string reason)
    {
        var policy = module.Manifest.RuntimePolicy;
        if (policy is null)
        {
            reason = "no runtimePolicy declared; legacy priority selection applies.";
            return true;
        }

        var resolved = candidate.Resolved!;
        if (IsInProc(resolved))
        {
            if (!policy.AllowInProc)
            {
                reason = "runtimePolicy.allowInProc=false.";
                return false;
            }

            var rules = policy.InProcRules;
            if (rules?.LoadContext is not null && !string.Equals(rules.LoadContext, "collectible", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"runtimePolicy.inProcRules.loadContext='{rules.LoadContext}' is unsupported.";
                return false;
            }

            if (rules?.ShadowCopy == false)
            {
                reason = "runtimePolicy.inProcRules.shadowCopy=false conflicts with the collectible soft-isolation InProc host.";
                return false;
            }

            if (rules?.AllowNativeDll == false && RequiresNativeDll(resolved))
            {
                reason = "runtimePolicy.inProcRules.allowNativeDll=false blocks this InProc entrypoint.";
                return false;
            }
        }

        reason = "allowed by runtimePolicy.";
        return true;
    }

    private static bool IsViable(MptPackageDefinition package, MptModuleDefinition module, MptEntrypointManifest entrypoint, out string reason)
    {
        if (!IsInProc(entrypoint))
        {
            return IsCommandResolvable(package.Directory, module.Directory, entrypoint, out reason);
        }

        if (string.IsNullOrWhiteSpace(entrypoint.Assembly) || string.IsNullOrWhiteSpace(entrypoint.Type))
        {
            reason = "inproc-dotnet entrypoint requires assembly and type.";
            return false;
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(module.Directory, entrypoint.Assembly));
        if (File.Exists(assemblyPath))
        {
            reason = $"assembly found at {assemblyPath}.";
            return true;
        }

        if (module.Manifest.Development?.AllowAlreadyLoadedFallback == true && IsAlreadyLoaded(entrypoint.Assembly))
        {
            reason = "assembly resolved from current AppDomain because development.allowAlreadyLoadedFallback=true.";
            return true;
        }

        reason = $"assembly '{entrypoint.Assembly}' was not found on disk; AppDomain fallback is disabled.";
        return false;
    }

    private static bool IsCommandResolvable(string packageDirectory, string moduleDirectory, MptEntrypointManifest entrypoint, out string reason)
    {
        if (string.IsNullOrWhiteSpace(entrypoint.Command))
        {
            reason = "entrypoint has no launch command; host will connect to the configured endpoint.";
            return true;
        }

        if (!CommandExists(packageDirectory, moduleDirectory, entrypoint.Command))
        {
            reason = $"command '{entrypoint.Command}' could not be resolved.";
            return false;
        }

        if (entrypoint.Kind == "jsonrpc-stdio")
        {
            foreach (var fileArg in entrypoint.Args.Where(arg => arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(Path.GetFullPath(Path.Combine(moduleDirectory, fileArg))) &&
                    !File.Exists(Path.GetFullPath(Path.Combine(packageDirectory, fileArg))))
                {
                    reason = $"script argument '{fileArg}' could not be resolved.";
                    return false;
                }
            }
        }

        reason = $"command '{entrypoint.Command}' is resolvable.";
        return true;
    }

    private static bool CommandExists(string packageDirectory, string moduleDirectory, string command)
    {
        if (Path.IsPathRooted(command))
        {
            return File.Exists(command);
        }

        if (command.Contains('/') || command.Contains('\\'))
        {
            return File.Exists(Path.GetFullPath(Path.Combine(packageDirectory, command))) ||
                   File.Exists(Path.GetFullPath(Path.Combine(moduleDirectory, command)));
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        return paths.Any(path => extensions.Any(ext => File.Exists(Path.Combine(path, command.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? command : command + ext))));
    }

    private SelectedEntrypoint ToSelected(
        MptPackageDefinition package,
        MptModuleDefinition module,
        EntrypointCandidate candidate,
        string selectionReason,
        IReadOnlyList<TransportSelectionDiagnostic> diagnostics)
    {
        var entrypoint = candidate.Resolved!;
        var endpoint = _platform.OperatingSystem switch
        {
            "windows" => entrypoint.Windows,
            "macos" => entrypoint.Macos,
            _ => entrypoint.Linux
        };
        var policy = module.Manifest.RuntimePolicy;

        return new SelectedEntrypoint(
            entrypoint.Kind,
            entrypoint.Priority,
            candidate.RuntimeId,
            ResolveCommand(package.Directory, module.Directory, entrypoint.Command),
            entrypoint.Args,
            entrypoint.Assembly,
            entrypoint.Type,
            entrypoint.Service,
            endpoint?.Transport,
            ExpandEndpointAddress(endpoint?.Name ?? endpoint?.Path ?? entrypoint.BaseUrl),
            TryGetHealthPath(entrypoint),
            selectionReason,
            diagnostics.ToArray(),
            IsInProc(entrypoint) ? policy?.InProcRules?.MaxCallMs : null,
            IsSidecar(candidate) ? policy?.SidecarRules?.ReadyTimeoutMs : null,
            IsSidecar(candidate) ? policy?.SidecarRules?.RestartLimit : null,
            IsSidecar(candidate) ? policy?.SidecarRules?.RestartWindowSeconds : null,
            IsSidecar(candidate) ? policy?.SidecarRules?.KillProcessTree : null);
    }

    private string? ExpandEndpointAddress(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? value : _pathService.ExpandRuntimePath(value);
    }

    private static string? ResolveCommand(string packageDirectory, string moduleDirectory, string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command))
        {
            return command;
        }

        if (command.Contains('/') || command.Contains('\\'))
        {
            var packageRelative = Path.GetFullPath(Path.Combine(packageDirectory, command));
            if (File.Exists(packageRelative))
            {
                return packageRelative;
            }

            var moduleRelative = Path.GetFullPath(Path.Combine(moduleDirectory, command));
            if (File.Exists(moduleRelative))
            {
                return moduleRelative;
            }
        }

        return command;
    }

    private static string? TryGetHealthPath(MptEntrypointManifest entrypoint)
    {
        if (entrypoint.Health is null)
        {
            return null;
        }

        return entrypoint.Health.Value.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
    }

    private static int PolicyRank(MptRuntimePolicyManifest? policy, EntrypointCandidate candidate)
    {
        if (policy is null || string.IsNullOrWhiteSpace(policy.Preferred))
        {
            return candidate.Resolved!.Compat ? 9 : 0;
        }

        var category = PolicyCategory(candidate);
        var preferred = NormalizePreferred(policy.Preferred);
        if (string.Equals(category, preferred, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return preferred switch
        {
            "sidecar" => category switch
            {
                "inproc" => policy.AllowInProc ? 1 : 9,
                "service" => 2,
                "compat" => 9,
                _ => 5
            },
            "service" => category switch
            {
                "sidecar" => 1,
                "inproc" => policy.AllowInProc ? 2 : 9,
                "compat" => 9,
                _ => 5
            },
            "inproc" => category switch
            {
                "sidecar" => 2,
                "service" => 3,
                "compat" => 9,
                _ => 5
            },
            "compat" => category == "compat" ? 0 : 5,
            _ => category == "compat" ? 9 : 5
        };
    }

    private static string EligibilityReason(MptRuntimePolicyManifest? policy, EntrypointCandidate candidate)
    {
        if (policy is null || string.IsNullOrWhiteSpace(policy.Preferred))
        {
            return "eligible by platform, command, and legacy priority rules.";
        }

        var category = PolicyCategory(candidate);
        return string.Equals(category, NormalizePreferred(policy.Preferred), StringComparison.OrdinalIgnoreCase)
            ? $"matches runtimePolicy.preferred='{policy.Preferred}'."
            : $"eligible fallback for runtimePolicy.preferred='{policy.Preferred}'.";
    }

    private static string SelectionReason(MptRuntimePolicyManifest? policy, EntrypointCandidate candidate)
    {
        if (policy is null || string.IsNullOrWhiteSpace(policy.Preferred))
        {
            return "selected by legacy priority and startup cost.";
        }

        var category = PolicyCategory(candidate);
        return string.Equals(category, NormalizePreferred(policy.Preferred), StringComparison.OrdinalIgnoreCase)
            ? $"runtimePolicy.preferred='{policy.Preferred}' matched."
            : $"runtimePolicy.preferred='{policy.Preferred}' was unavailable; selected {category} fallback.";
    }

    private static string PolicyCategory(EntrypointCandidate candidate)
    {
        if (candidate.Resolved is null)
        {
            return "unknown";
        }

        if (IsInProc(candidate.Resolved))
        {
            return "inproc";
        }

        if (candidate.Resolved.Compat || string.Equals(candidate.Resolved.Kind, "jsonrpc-stdio", StringComparison.OrdinalIgnoreCase))
        {
            return "compat";
        }

        if (IsSidecar(candidate))
        {
            return "sidecar";
        }

        return string.Equals(candidate.Resolved.Kind, "http", StringComparison.OrdinalIgnoreCase)
            ? "service"
            : candidate.Resolved.Kind;
    }

    private static string NormalizePreferred(string preferred)
    {
        return preferred switch
        {
            "inproc" => "inproc",
            "sidecar" => "sidecar",
            "service" => "service",
            "compat" => "compat",
            _ => preferred
        };
    }

    private static bool IsSidecar(EntrypointCandidate candidate)
    {
        return string.Equals(candidate.Original.Kind, "package-runtime", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate.Resolved?.Kind, "grpc-ipc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInProc(MptEntrypointManifest entrypoint)
    {
        return string.Equals(entrypoint.Kind, "inproc-dotnet", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresNativeDll(MptEntrypointManifest entrypoint)
    {
        return TryReadBool(entrypoint.ExtensionData, "requiresNativeDll") ||
               TryReadBool(entrypoint.ExtensionData, "usesNativeDll");
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        return values.TryGetValue(key, out var value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static bool IsAlreadyLoaded(string assemblyNameOrPath)
    {
        var simpleName = Path.GetFileNameWithoutExtension(assemblyNameOrPath);
        return AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
    }

    private static string DescribeCandidate(EntrypointCandidate candidate)
    {
        if (candidate.Resolved is null)
        {
            return candidate.Original.Kind;
        }

        return string.Equals(candidate.Original.Kind, "package-runtime", StringComparison.OrdinalIgnoreCase)
            ? $"package-runtime:{candidate.RuntimeId}->{candidate.Resolved.Kind}"
            : candidate.Resolved.Kind;
    }

    private static TransportSelectionDiagnostic Skipped(string transportKind, string? runtimeId, string reason)
    {
        return new TransportSelectionDiagnostic(transportKind, runtimeId ?? "", "skipped", reason);
    }

    private static bool IsAllowedForCommandRoute(
        MptModuleDefinition module,
        Sdk.MptCommandDescriptor command,
        IReadOnlyList<string> constraints,
        MptEntrypointManifest entrypoint,
        out string reason)
    {
        foreach (var constraint in constraints)
        {
            var rule = RuntimeOperationPolicy.ResolveRule(module.Manifest.RuntimePolicy?.OperationRules, constraint);
            if (RuntimeOperationPolicy.IsAllowedForTransport(rule, entrypoint.Kind, command))
            {
                continue;
            }

            var requiredRoute = RuntimeOperationPolicy.RequiredRouteForRule(rule);
            reason = $"command '{command.Id}' requires constraint '{constraint}' via {requiredRoute}; transport '{entrypoint.Kind}' is unavailable for that route.";
            return false;
        }

        reason = $"command '{command.Id}' constraints [{string.Join(", ", constraints)}] are satisfied by transport '{entrypoint.Kind}'.";
        return true;
    }

    private sealed record EntrypointCandidate(MptEntrypointManifest Original, MptEntrypointManifest? Resolved, string? RuntimeId);
}

public sealed record TransportSelectionResult(
    SelectedEntrypoint? Entrypoint,
    IReadOnlyList<TransportSelectionDiagnostic> Diagnostics);
