using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>
/// Loads a dotnet-surface tool assembly into a collectible, shadow-copied <see cref="AssemblyLoadContext"/>
/// and instantiates its <see cref="IMptAvaloniaSurfaceFactory"/>. The context is tracked so that on
/// refresh or tool removal the surface, event subscriptions and the assembly context can be unloaded,
/// keeping the Shell process clean across dynamic catalog changes.
///
/// Shared assemblies (Abstractions, Platform.Abstractions, AvaloniaSdk, logging/DI abstractions) are
/// resolved against <see cref="AssemblyLoadContext.Default"/> so the Surface receives the same Shell-side
/// contract instances rather than duplicate copies.
/// </summary>
internal sealed class DotnetSurfaceLoader
{
    private static readonly string[] SharedAssemblies =
    [
        "MyPowerTools.Abstractions",
        "MyPowerTools.Platform.Abstractions",
        "MyPowerTools.AvaloniaSdk",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    ];

    private readonly string _shadowRoot;
    private readonly Dictionary<string, LoadedSurface> _loaded = new(StringComparer.Ordinal);

    public DotnetSurfaceLoader(string shadowRoot)
    {
        _shadowRoot = shadowRoot;
        Directory.CreateDirectory(_shadowRoot);
    }

    /// <summary>
    /// Loads (or reuses) the surface Control for the given tool/route. Returns the created control
    /// plus a lease that can unload it.
    /// </summary>
    public LoadedSurface Load(HostProto.ToolDescriptor descriptor, HostProto.ToolRoute route, MptAvaloniaSurfaceContext context)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.VerifyAccess();

        if (string.IsNullOrWhiteSpace(route.Assembly) || !File.Exists(route.Assembly))
        {
            throw new FileNotFoundException("Dotnet surface assembly was not found.", route.Assembly);
        }

        if (string.IsNullOrWhiteSpace(route.Type))
        {
            throw new InvalidDataException("Dotnet surface type is required.");
        }

        var cacheKey = BuildCacheKey(descriptor.ToolId, route.Assembly!);
        var shadowDir = Path.Combine(_shadowRoot, cacheKey);
        var shadowAssemblyPath = ShadowCopy(route.Assembly!, shadowDir, cacheKey);

        var loadContext = new SurfaceLoadContext(
            shadowAssemblyPath,
            SharedAssemblies,
            Path.GetDirectoryName(route.Assembly!));
        var assembly = loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
        var factoryType = assembly.GetType(route.Type, throwOnError: true)!;
        if (Activator.CreateInstance(factoryType) is not IMptAvaloniaSurfaceFactory factory)
        {
            throw new InvalidDataException($"{route.Type} does not implement IMptAvaloniaSurfaceFactory.");
        }

        var control = factory.CreateSurface(context);
        if (_loaded.Remove(cacheKey, out var previous))
        {
            previous.UnloadCore();
        }
        var loaded = new LoadedSurface(
            descriptor.ToolId,
            route.RouteId,
            control,
            loadContext,
            surface => Release(cacheKey, surface));
        _loaded[cacheKey] = loaded;
        return loaded;
    }

    /// <summary>Unloads the surface for a tool id, releasing its assembly context.</summary>
    public void Unload(string toolId)
    {
        var keys = _loaded.Where(kv => string.Equals(kv.Value.ToolId, toolId, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToArray();
        foreach (var key in keys)
        {
            if (_loaded.Remove(key, out var surface))
            {
                surface.UnloadCore();
            }
        }
    }

    /// <summary>Unloads every loaded surface.</summary>
    public void UnloadAll()
    {
        foreach (var surface in _loaded.Values)
        {
            surface.UnloadCore();
        }

        _loaded.Clear();
    }

    private void Release(string cacheKey, LoadedSurface surface)
    {
        if (_loaded.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, surface))
        {
            _loaded.Remove(cacheKey);
        }
        surface.UnloadCore();
    }

    private static string BuildCacheKey(string toolId, string assemblyPath)
    {
        var info = new FileInfo(assemblyPath);
        var raw = $"{toolId}|{assemblyPath}|{info.Length}|{info.LastWriteTimeUtc:O}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ShadowCopy(string sourceAssembly, string targetDir, string cacheKey)
    {
        Directory.CreateDirectory(targetDir);
        var sourceDir = Path.GetDirectoryName(sourceAssembly)!;
        var assemblyName = Path.GetFileName(sourceAssembly);
        var target = Path.Combine(targetDir, assemblyName);

        // If already shadow-copied with the same fingerprint, reuse it.
        if (File.Exists(target))
        {
            return target;
        }

        // Clean stale shadow for a previous fingerprint of the same tool, then copy fresh.
        if (Directory.Exists(targetDir))
        {
            try { Directory.Delete(targetDir, recursive: true); } catch { }
            Directory.CreateDirectory(targetDir);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            try { File.Copy(file, dest, overwrite: true); } catch { }
        }

        return target;
    }

    /// <summary>A loaded surface Control plus the ability to unload its assembly context.</summary>
    internal sealed class LoadedSurface : IDisposable
    {
        private readonly SurfaceLoadContext _loadContext;
        private readonly WeakReference _weakRef;
        private readonly Action<LoadedSurface> _release;
        private int _unloaded;

        public LoadedSurface(
            string toolId,
            string routeId,
            Control control,
            SurfaceLoadContext loadContext,
            Action<LoadedSurface> release)
        {
            ToolId = toolId;
            RouteId = routeId;
            Control = control;
            _loadContext = loadContext;
            _weakRef = new WeakReference(loadContext);
            _release = release;
        }

        public string ToolId { get; }
        public string RouteId { get; }
        public Control Control { get; }

        internal void UnloadCore()
        {
            if (Interlocked.Exchange(ref _unloaded, 1) != 0)
            {
                return;
            }

            var dataContext = Control.DataContext;
            Control.DataContext = null;
            if (dataContext is IDisposable disposableDataContext)
            {
                try { disposableDataContext.Dispose(); } catch { }
            }
            if (Control is IDisposable disposableControl && !ReferenceEquals(disposableControl, dataContext))
            {
                try { disposableControl.Dispose(); } catch { }
            }
            try { _loadContext.Unload(); } catch { }
        }

        public void Dispose() => _release(this);
    }

    /// <summary>
    /// Collectible AssemblyLoadContext for a dotnet surface. Shared contract assemblies resolve to
    /// the default context; everything else resolves from the surface's shadow-copied directory.
    /// </summary>
    internal sealed class SurfaceLoadContext : AssemblyLoadContext
    {
        private const int MaxProbeDepth = 2;

        private readonly AssemblyDependencyResolver? _resolver;
        private readonly string _assemblyDirectory;
        private readonly string[] _probeDirectories;
        private readonly HashSet<string> _sharedAssemblies;

        public SurfaceLoadContext(string mainAssemblyPath, IEnumerable<string> sharedAssemblies, string? probeRoot = null)
            : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
        {
            _assemblyDirectory = Path.GetDirectoryName(mainAssemblyPath)!;
            // Android does not provide the hostpolicy-based dependency resolver, so probing the
            // shadow directory plus the packaged module root replaces it.
            if (!OperatingSystem.IsAndroid()) _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _probeDirectories = BuildProbeDirectories(_assemblyDirectory, probeRoot);
            _sharedAssemblies = sharedAssemblies.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not null && _sharedAssemblies.Contains(assemblyName.Name))
            {
                return ResolveShared(assemblyName.Name);
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName.Name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        /// <summary>
        /// A shared contract must be the Shell's own copy. Returning a duplicate loaded from the
        /// shadow directory would make the surface fail its <c>is IMptAvaloniaSurfaceFactory</c>
        /// check, so a not-yet-loaded shared assembly is loaded into the default context by name
        /// instead of being skipped.
        /// </summary>
        private static Assembly? ResolveShared(string name)
        {
            var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
            if (loaded is not null)
            {
                return loaded;
            }

            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
            }
            catch
            {
                return null;
            }
        }

        private string? Probe(string? assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName))
            {
                return null;
            }

            foreach (var directory in _probeDirectories)
            {
                var candidate = Path.Combine(directory, assemblyName + ".dll");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string[] BuildProbeDirectories(string assemblyDirectory, string? probeRoot)
        {
            var directories = new List<string> { assemblyDirectory };
            if (!OperatingSystem.IsAndroid())
            {
                return directories.ToArray();
            }

            // The shadow copy only carries the surface's own directory, so the packaged module root
            // (and its directory above) is where sibling dependencies such as the module assembly
            // still live on Android.
            foreach (var root in new[] { probeRoot, assemblyDirectory })
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var current = new DirectoryInfo(root!);
                for (var depth = 0; current is not null && depth <= MaxProbeDepth; depth++)
                {
                    if (!directories.Contains(current.FullName, StringComparer.Ordinal))
                    {
                        directories.Add(current.FullName);
                    }

                    current = current.Parent;
                }
            }

            return directories.ToArray();
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
