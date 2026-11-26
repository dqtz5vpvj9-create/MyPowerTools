using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class PackageRegistry
{
    private readonly PackageReader _reader;
    private readonly TransportSelector _transportSelector;
    private readonly object _writeLock = new();
    private volatile RuntimeModuleRecord[] _modules = [];

    public PackageRegistry(PackageReader reader, PlatformId platform)
    {
        _reader = reader;
        _transportSelector = new TransportSelector(platform);
    }

    public IReadOnlyList<RuntimeModuleRecord> Modules => _modules;

    public void Load(string packageRoot, IEnumerable<string>? developmentToolRoots = null)
    {
        var loaded = new List<RuntimeModuleRecord>();
        var packages = _reader.DiscoverPackages(packageRoot)
            .Concat(_reader.DiscoverDevelopmentTools(developmentToolRoots ?? []));

        foreach (var package in packages)
        {
            foreach (var module in package.Modules)
            {
                try
                {
                    loaded.Add(BuildModuleRecord(loaded, package, module));
                }
                catch (Exception exception) when (package.IsDevelopmentTool)
                {
                    // One broken development module (e.g. duplicate id) must not abort
                    // the whole catalog; it becomes an error-state module card.
                    // Installed/signed packages keep the strict throw.
                    loaded.Add(BuildErrorModuleRecord(
                        package, module, exception.GetBaseException().Message));
                }
            }
        }

        lock (_writeLock)
        {
            _modules = loaded.ToArray();
        }
    }

    private RuntimeModuleRecord BuildModuleRecord(
        IReadOnlyList<RuntimeModuleRecord> existingModules,
        MptPackageDefinition package,
        MptModuleDefinition module)
    {
        if (existingModules.Any(existing => string.Equals(
                existing.Module.Manifest.Id,
                module.Manifest.Id,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Duplicate module id '{module.Manifest.Id}' discovered at {module.ManifestPath}.");
        }

        if (module.LoadError is not null)
        {
            return BuildErrorModuleRecord(package, module, module.LoadError);
        }

        var selection = _transportSelector.Select(package, module);
        var entrypoint = selection.Entrypoint;
        var state = package.IsDevelopmentTool && entrypoint is null
            ? "ready"
            : entrypoint is null
            ? "unsupported"
            : entrypoint.Kind == "inproc-dotnet"
                ? "indexed"
                : "stopped";
        var summary = package.IsDevelopmentTool && entrypoint is null
            ? $"Development tool surface discovered from {package.Directory}."
            : entrypoint is null
            ? "No compatible runnable entrypoint for this platform."
            : $"Indexed via {entrypoint.Kind}. {entrypoint.SelectionReason}";

        return new RuntimeModuleRecord(
            package,
            module,
            entrypoint,
            selection.Diagnostics,
            new MyPowerTools.Abstractions.ModuleStatusSnapshot(
                module.Manifest.Id,
                state,
                summary,
                DateTimeOffset.UtcNow,
                [
                    new MyPowerTools.Abstractions.HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                    new MyPowerTools.Abstractions.HealthCheckSnapshot("transport", "Transport", entrypoint is not null, entrypoint?.SelectionReason ?? "No compatible entrypoint")
                ],
                0));
    }

    private static RuntimeModuleRecord BuildErrorModuleRecord(
        MptPackageDefinition package,
        MptModuleDefinition module,
        string message)
    {
        var failed = module.LoadError is not null ? module : module with { LoadError = message };
        return new RuntimeModuleRecord(
            package,
            failed,
            null,
            [],
            new MyPowerTools.Abstractions.ModuleStatusSnapshot(
                module.Manifest.Id,
                "error",
                message,
                DateTimeOffset.UtcNow,
                [
                    new MyPowerTools.Abstractions.HealthCheckSnapshot("manifest", "Manifest", false, message)
                ],
                0));
    }

    public RuntimeModuleRecord? FindModule(string moduleId)
    {
        return _modules.FirstOrDefault(module => string.Equals(module.Module.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    public TransportSelectionResult SelectCommandEntrypoint(
        RuntimeModuleRecord module,
        Sdk.MptCommandDescriptor command,
        Sdk.CommandRequest request,
        IReadOnlySet<string> availableTransportKinds)
    {
        return _transportSelector.SelectForCommand(module.Package, module.Module, command, request, availableTransportKinds);
    }

    public void UpdateStatus(string moduleId, MyPowerTools.Abstractions.ModuleStatusSnapshot status)
    {
        lock (_writeLock)
        {
            var current = _modules;
            var index = Array.FindIndex(
                current,
                module => string.Equals(module.Module.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var updated = new RuntimeModuleRecord[current.Length];
            Array.Copy(current, updated, current.Length);
            updated[index] = current[index] with { Status = status };
            _modules = updated;
        }
    }
}
