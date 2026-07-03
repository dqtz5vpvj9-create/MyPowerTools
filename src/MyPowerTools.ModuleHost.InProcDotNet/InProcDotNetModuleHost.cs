using System.Reflection;
using System.Runtime.Loader;
using MyPowerTools.Packaging;
using MyPowerTools.Runtime;

namespace MyPowerTools.ModuleHost.InProcDotNet;

public sealed class InProcDotNetModuleHost : IModuleTransportRuntime, IAsyncDisposable
{
    private readonly Dictionary<string, IMptModule> _loadedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _probeDirectories = new(StringComparer.OrdinalIgnoreCase);

    public string Kind => "inproc-dotnet";

    public InProcDotNetModuleHost()
    {
        AssemblyLoadContext.Default.Resolving += ResolveFromProbeDirectories;
    }

    public async ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        var loaded = await LoadCachedAsync(module.Module, context, cancellationToken);
        return await loaded.GetStatusAsync(cancellationToken);
    }

    public async ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        var loaded = await LoadCachedAsync(module.Module, context, cancellationToken);
        return await loaded.GetSettingsSchemaAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        var loaded = await LoadCachedAsync(module.Module, context, cancellationToken);
        return await loaded.ListCommandsAsync(cancellationToken);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadCachedAsync(module.Module, context, cancellationToken);
        return await loaded.ExecuteCommandAsync(request, cancellationToken);
    }

    public ValueTask<IMptModule> LoadAsync(MptModuleDefinition module, CancellationToken cancellationToken)
    {
        var context = new ModuleContext(
            "0.2.0",
            "1.0",
            module.Manifest.PackageId,
            module.Manifest.Id,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "data", module.Manifest.Id),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "cache", module.Manifest.Id),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "logs", module.Manifest.Id),
            Environment.OSVersion.Platform.ToString(),
            module.Manifest.Capabilities);

        return LoadAsync(module, context, cancellationToken);
    }

    public async ValueTask<IMptModule> LoadAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        var entrypoint = module.Manifest.Entrypoints
            .Where(entry => entry.Kind == "inproc-dotnet")
            .OrderByDescending(entry => entry.Priority)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Module '{module.Manifest.Id}' has no inproc-dotnet entrypoint.");

        if (string.IsNullOrWhiteSpace(entrypoint.Assembly) || string.IsNullOrWhiteSpace(entrypoint.Type))
        {
            throw new InvalidOperationException($"Module '{module.Manifest.Id}' inproc entrypoint requires assembly and type.");
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(module.Directory, entrypoint.Assembly));
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            _probeDirectories.Add(assemblyDirectory);
        }

        var assembly = File.Exists(assemblyPath)
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath)
            : ResolveAlreadyLoaded(entrypoint.Assembly);

        var type = assembly.GetType(entrypoint.Type, throwOnError: true)!;
        if (Activator.CreateInstance(type) is not IMptModule instance)
        {
            throw new InvalidCastException($"{entrypoint.Type} does not implement {nameof(IMptModule)}.");
        }

        var initialized = await instance.InitializeAsync(context, cancellationToken);
        if (!initialized.Ok)
        {
            throw new InvalidOperationException(initialized.Error?.Message ?? $"Module '{module.Manifest.Id}' rejected initialization.");
        }

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var module in _loadedModules.Values)
        {
            await module.DisposeAsync(CancellationToken.None);
        }

        _loadedModules.Clear();
        AssemblyLoadContext.Default.Resolving -= ResolveFromProbeDirectories;
    }

    private async ValueTask<IMptModule> LoadCachedAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        if (_loadedModules.TryGetValue(module.Manifest.Id, out var loaded))
        {
            return loaded;
        }

        loaded = await LoadAsync(module, context, cancellationToken);
        _loadedModules[module.Manifest.Id] = loaded;
        return loaded;
    }

    private static Assembly ResolveAlreadyLoaded(string assemblyNameOrPath)
    {
        var simpleName = Path.GetFileNameWithoutExtension(assemblyNameOrPath);
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Assembly '{assemblyNameOrPath}' was not found on disk or in the current load context.");
    }

    private Assembly? ResolveFromProbeDirectories(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var fileName = assemblyName.Name + ".dll";
        foreach (var directory in _probeDirectories)
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return context.LoadFromAssemblyPath(candidate);
            }
        }

        return null;
    }
}
