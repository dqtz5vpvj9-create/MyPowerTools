using System.Reflection;
using System.Runtime.Loader;
using MyPowerTools.Packaging;
using MyPowerTools.Runtime;

namespace MyPowerTools.ModuleHost.InProcDotNet;

public sealed class InProcDotNetModuleHost : IModuleTransportRuntime, IAsyncDisposable
{
    private static readonly string[] SharedAssemblies =
    [
        "MyPowerTools.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    ];

    private readonly Dictionary<string, InProcModuleSession> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    public string Kind => "inproc-dotnet";

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
        MptPluginLoadContext? loadContext = null;

        var assembly = File.Exists(assemblyPath)
            ? LoadIsolatedAssembly(module, context, entrypoint, assemblyPath, out loadContext)
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

        if (loadContext is not null)
        {
            _loadedModules[module.Manifest.Id] = new InProcModuleSession(instance, loadContext);
        }

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _loadedModules.Values)
        {
            await session.DisposeAndUnloadAsync(CancellationToken.None);
        }

        _loadedModules.Clear();
    }

    private async ValueTask<IMptModule> LoadCachedAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        if (_loadedModules.TryGetValue(module.Manifest.Id, out var loaded))
        {
            return loaded.Module;
        }

        return await LoadAsync(module, context, cancellationToken);
    }

    private static Assembly ResolveAlreadyLoaded(string assemblyNameOrPath)
    {
        var simpleName = Path.GetFileNameWithoutExtension(assemblyNameOrPath);
        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Assembly '{assemblyNameOrPath}' was not found on disk or in the current load context.");
    }

    private static Assembly LoadIsolatedAssembly(
        MptModuleDefinition module,
        ModuleContext context,
        MptEntrypointManifest entrypoint,
        string assemblyPath,
        out MptPluginLoadContext loadContext)
    {
        var sourceRoot = Path.GetDirectoryName(assemblyPath)
            ?? throw new InvalidOperationException($"Module '{module.Manifest.Id}' assembly path has no directory.");
        var shadowRoot = ShadowCopyModule(module, context, entrypoint, assemblyPath, sourceRoot);
        var shadowAssemblyPath = Path.Combine(shadowRoot, Path.GetFileName(assemblyPath));
        loadContext = new MptPluginLoadContext(shadowAssemblyPath, SharedAssemblies);
        return loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
    }

    private static string ShadowCopyModule(
        MptModuleDefinition module,
        ModuleContext context,
        MptEntrypointManifest entrypoint,
        string assemblyPath,
        string sourceRoot)
    {
        var fingerprint = HashForPath(module, entrypoint, assemblyPath);
        var safePackage = SanitizePathSegment(module.Manifest.PackageId);
        var safeModule = SanitizePathSegment(module.Manifest.Id);
        var shadowRoot = Path.Combine(context.CacheDirectory, "inproc-shadow", safePackage, safeModule, fingerprint);
        var marker = Path.Combine(shadowRoot, ".complete");
        if (File.Exists(marker))
        {
            return shadowRoot;
        }

        Directory.CreateDirectory(shadowRoot);
        CopyDirectory(sourceRoot, shadowRoot);
        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        return shadowRoot;
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, directory);
            if (IsIgnoredSegment(relative))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationRoot, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            if (IsIgnoredSegment(relative))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static bool IsIgnoredSegment(string relativePath)
    {
        return relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                            segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static string HashForPath(MptModuleDefinition module, MptEntrypointManifest entrypoint, string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        var input = string.Join(
            "|",
            module.Manifest.PackageId,
            module.Manifest.Id,
            entrypoint.Assembly,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)))[..16].ToLowerInvariant();
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }
}

internal sealed class MptPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _sharedAssemblies;

    public MptPluginLoadContext(string mainAssemblyPath, IEnumerable<string> sharedAssemblies)
        : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sharedAssemblies = sharedAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && _sharedAssemblies.Contains(assemblyName.Name))
        {
            return AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

internal sealed class InProcModuleSession
{
    private readonly MptPluginLoadContext _loadContext;
    private readonly WeakReference _loadContextReference;

    public InProcModuleSession(IMptModule module, MptPluginLoadContext loadContext)
    {
        Module = module;
        _loadContext = loadContext;
        _loadContextReference = new WeakReference(loadContext, trackResurrection: false);
    }

    public IMptModule Module { get; private set; }

    public async ValueTask<bool> DisposeAndUnloadAsync(CancellationToken cancellationToken)
    {
        await Module.DisposeAsync(cancellationToken);
        Module = null!;
        _loadContext.Unload();
        return await WaitForUnloadAsync();
    }

    private async ValueTask<bool> WaitForUnloadAsync()
    {
        for (var attempt = 0; attempt < 10 && _loadContextReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        return !_loadContextReference.IsAlive;
    }
}
