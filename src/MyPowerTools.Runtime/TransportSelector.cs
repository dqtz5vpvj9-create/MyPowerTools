using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class TransportSelector
{
    private readonly PlatformId _platform;

    public TransportSelector(PlatformId platform)
    {
        _platform = platform;
    }

    public SelectedEntrypoint? Select(MptPackageDefinition package, MptModuleDefinition module)
    {
        var candidates = module.Manifest.Entrypoints
            .Select(entrypoint => new EntrypointCandidate(
                entrypoint,
                ResolvePackageRuntime(package, entrypoint) ?? entrypoint,
                entrypoint.Kind == "package-runtime" ? entrypoint.RuntimeId : entrypoint.RuntimeId))
            .Where(candidate => IsPlatformMatch(candidate.Resolved))
            .Where(candidate => IsViable(package, module, candidate.Resolved))
            .OrderByDescending(candidate => candidate.Resolved.Priority)
            .ThenBy(candidate => candidate.Resolved.StartupCost ?? 0)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        return selected is null ? null : ToSelected(package, module, selected);
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

    private static bool IsViable(MptPackageDefinition package, MptModuleDefinition module, MptEntrypointManifest entrypoint)
    {
        if (entrypoint.Kind != "inproc-dotnet")
        {
            return IsCommandResolvable(package.Directory, module.Directory, entrypoint);
        }

        if (string.IsNullOrWhiteSpace(entrypoint.Assembly) || string.IsNullOrWhiteSpace(entrypoint.Type))
        {
            return false;
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(module.Directory, entrypoint.Assembly));
        if (File.Exists(assemblyPath))
        {
            return true;
        }

        var simpleName = Path.GetFileNameWithoutExtension(entrypoint.Assembly);
        return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCommandResolvable(string packageDirectory, string moduleDirectory, MptEntrypointManifest entrypoint)
    {
        if (string.IsNullOrWhiteSpace(entrypoint.Command))
        {
            return true;
        }

        if (!CommandExists(packageDirectory, moduleDirectory, entrypoint.Command))
        {
            return false;
        }

        if (entrypoint.Kind == "jsonrpc-stdio")
        {
            foreach (var fileArg in entrypoint.Args.Where(arg => arg.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || arg.EndsWith(".js", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(Path.GetFullPath(Path.Combine(moduleDirectory, fileArg))) &&
                    !File.Exists(Path.GetFullPath(Path.Combine(packageDirectory, fileArg))))
                {
                    return false;
                }
            }
        }

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

    private SelectedEntrypoint ToSelected(MptPackageDefinition package, MptModuleDefinition module, EntrypointCandidate candidate)
    {
        var entrypoint = candidate.Resolved;
        var endpoint = _platform.OperatingSystem switch
        {
            "windows" => entrypoint.Windows,
            "macos" => entrypoint.Macos,
            _ => entrypoint.Linux
        };

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
            endpoint?.Name ?? endpoint?.Path ?? entrypoint.BaseUrl,
            TryGetHealthPath(entrypoint));
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

    private sealed record EntrypointCandidate(MptEntrypointManifest Original, MptEntrypointManifest Resolved, string? RuntimeId);
}
