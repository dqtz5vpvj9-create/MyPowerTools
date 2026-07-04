using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class PackageRegistry
{
    private readonly PackageReader _reader;
    private readonly TransportSelector _transportSelector;
    private readonly List<RuntimeModuleRecord> _modules = [];

    public PackageRegistry(PackageReader reader, PlatformId platform)
    {
        _reader = reader;
        _transportSelector = new TransportSelector(platform);
    }

    public IReadOnlyList<RuntimeModuleRecord> Modules => _modules;

    public void Load(string packageRoot)
    {
        _modules.Clear();

        foreach (var package in _reader.DiscoverPackages(packageRoot))
        {
            foreach (var module in package.Modules)
            {
                var selection = _transportSelector.Select(package, module);
                var entrypoint = selection.Entrypoint;
                var state = entrypoint is null
                    ? "unsupported"
                    : entrypoint.Kind == "inproc-dotnet"
                        ? "indexed"
                        : "stopped";
                var summary = entrypoint is null
                    ? "No compatible runnable entrypoint for this platform."
                    : $"Indexed via {entrypoint.Kind}. {entrypoint.SelectionReason}";

                _modules.Add(new RuntimeModuleRecord(
                    package,
                    module,
                    entrypoint,
                    selection.Diagnostics,
                    new ModuleStatusSnapshot(
                        module.Manifest.Id,
                        state,
                        summary,
                        DateTimeOffset.UtcNow,
                        [
                            new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                            new HealthCheckSnapshot("transport", "Transport", entrypoint is not null, entrypoint?.SelectionReason ?? "No compatible entrypoint")
                        ],
                        0)));
            }
        }
    }

    public RuntimeModuleRecord? FindModule(string moduleId)
    {
        return _modules.FirstOrDefault(module => string.Equals(module.Module.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateStatus(string moduleId, ModuleStatusSnapshot status)
    {
        var index = _modules.FindIndex(module => string.Equals(module.Module.Manifest.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _modules[index] = _modules[index] with { Status = status };
        }
    }
}
