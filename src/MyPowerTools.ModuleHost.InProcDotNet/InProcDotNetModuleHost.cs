using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using MyPowerTools.Packaging;
using MyPowerTools.Runtime;

namespace MyPowerTools.ModuleHost.InProcDotNet;

public sealed class InProcDotNetModuleHost : IModuleTransportRuntime, IModuleTransportDiagnosticsProvider, IAsyncDisposable
{
    private static readonly string[] SharedAssemblies =
    [
        "MyPowerTools.Abstractions",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions"
    ];

    private readonly SemaphoreSlim _moduleLock = new(1, 1);
    private readonly Dictionary<string, InProcModuleSession> _loadedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _knownPoolModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InProcUnloadRecord> _unloadRecords = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string Kind => "inproc-dotnet";

    public string GetProcessPoolKey(RuntimeModuleRecord module)
    {
        return PoolKeyForModule(module.Module.Manifest.Id);
    }

    public void RegisterProcessPool(string poolKey, string moduleId)
    {
        _moduleLock.Wait();
        try
        {
            MarkKnown(poolKey, moduleId);
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    public void ApplyRestartPolicy(string poolKey, string restartPolicy, string reason, DateTimeOffset updatedAt, DateTimeOffset? expiresAt)
    {
        // InProc modules share the Runner process, so persisted sidecar restart policies do not apply.
    }

    public async ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.GetStatusAsync(token);
        });
    }

    public async ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.GetSettingsSchemaAsync(token);
        });
    }

    public async ValueTask<SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsPatch patch, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.ValidateSettingsAsync(patch, token);
        });
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.ApplySettingsAsync(snapshot, token);
        });
    }

    public async ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.ListCommandsAsync(token);
        });
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteWithBudgetAsync(module, cancellationToken, async token =>
        {
            var loaded = await LoadCachedAsync(module.Module, context, token);
            return await loaded.ExecuteCommandAsync(request, token);
        });
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

        await ThrowIfPendingRunnerRestartAsync(module.Manifest.Id, cancellationToken);
        var assemblyPath = Path.GetFullPath(Path.Combine(module.Directory, entrypoint.Assembly));
        MptPluginLoadContext? loadContext = null;

        var assembly = File.Exists(assemblyPath)
            ? LoadIsolatedAssembly(module, context, entrypoint, assemblyPath, out loadContext)
            : ResolveAlreadyLoadedIfAllowed(module, entrypoint.Assembly);

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
            await _moduleLock.WaitAsync(cancellationToken);
            try
            {
                var poolKey = PoolKeyForModule(module.Manifest.Id);
                MarkKnown(poolKey, module.Manifest.Id);
                _loadedModules[module.Manifest.Id] = new InProcModuleSession(instance, loadContext, module.Manifest.Id, poolKey, DateTimeOffset.UtcNow);
                _unloadRecords.Remove(poolKey);
            }
            finally
            {
                _moduleLock.Release();
            }
        }

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _moduleLock.WaitAsync();
        try
        {
            foreach (var session in _loadedModules.Values.ToArray())
            {
                var result = await session.DisposeAndUnloadAsync(CancellationToken.None);
                RecordUnloadResult(result);
            }

            _loadedModules.Clear();
        }
        finally
        {
            _moduleLock.Release();
            _moduleLock.Dispose();
        }
    }

    public IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics()
    {
        _moduleLock.Wait();
        try
        {
            var active = _loadedModules.Values
                .Select(session => new RuntimeProcessDiagnostics(
                    Kind,
                    session.PoolKey,
                    "loaded",
                    Environment.ProcessId,
                    session.LoadContextName,
                    1,
                    0,
                    "runner-owned",
                    "InProc module is loaded inside the Runner process.",
                    session.LoadedAt,
                    [session.ModuleId]))
                .ToArray();
            var pending = _unloadRecords.Values
                .Where(record => !_loadedModules.Values.Any(session => string.Equals(session.PoolKey, record.PoolKey, StringComparison.OrdinalIgnoreCase)))
                .Select(record => new RuntimeProcessDiagnostics(
                    Kind,
                    record.PoolKey,
                    record.State,
                    Environment.ProcessId,
                    record.LoadContextName,
                    1,
                    0,
                    record.RestartPolicy,
                    record.Message,
                    record.UpdatedAt,
                    [record.ModuleId]))
                .ToArray();

            return active.Concat(pending)
                .OrderBy(process => process.PoolKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    public async ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken)
    {
        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            var session = _loadedModules.Values.FirstOrDefault(value => string.Equals(value.PoolKey, poolKey, StringComparison.OrdinalIgnoreCase));
            if (session is null)
            {
                if (_unloadRecords.TryGetValue(poolKey, out var pending))
                {
                    return new RuntimeProcessRestartResult(
                        false,
                        Kind,
                        poolKey,
                        pending.State,
                        pending.Message,
                        [pending.ModuleId]);
                }

                return new RuntimeProcessRestartResult(false, Kind, poolKey, "missing", $"InProc module pool '{poolKey}' is not loaded.", ModuleIdsForPool(poolKey));
            }

            var result = await session.DisposeAndUnloadAsync(cancellationToken);
            _loadedModules.Remove(session.ModuleId);
            RecordUnloadResult(result);
            return new RuntimeProcessRestartResult(
                result.Unloaded,
                Kind,
                poolKey,
                result.State,
                result.Message,
                [session.ModuleId]);
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    public ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(string poolKey, bool paused, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        var modules = ModuleIdsForPool(poolKey);
        return ValueTask.FromResult(new RuntimeProcessPolicyResult(
            false,
            Kind,
            poolKey,
            "unsupported",
            "runner-owned",
            "InProc modules share the Runner process; restart policy is controlled by Runner lifecycle.",
            modules,
            null));
    }

    private async ValueTask<IMptModule> LoadCachedAsync(MptModuleDefinition module, ModuleContext context, CancellationToken cancellationToken)
    {
        await ThrowIfPendingRunnerRestartAsync(module.Manifest.Id, cancellationToken);
        if (_loadedModules.TryGetValue(module.Manifest.Id, out var loaded))
        {
            return loaded.Module;
        }

        return await LoadAsync(module, context, cancellationToken);
    }

    private static async ValueTask<T> ExecuteWithBudgetAsync<T>(
        RuntimeModuleRecord module,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<T>> action)
    {
        var maxCallMs = module.Entrypoint?.InProcMaxCallMs;
        if (maxCallMs is not > 0)
        {
            return await action(cancellationToken);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(maxCallMs.Value));
        try
        {
            return await action(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"InProc module '{module.Module.Manifest.Id}' exceeded runtimePolicy.inProcRules.maxCallMs={maxCallMs.Value}.");
        }
    }

    private async ValueTask ThrowIfPendingRunnerRestartAsync(string moduleId, CancellationToken cancellationToken)
    {
        await _moduleLock.WaitAsync(cancellationToken);
        try
        {
            if (_unloadRecords.TryGetValue(PoolKeyForModule(moduleId), out var pending) &&
                string.Equals(pending.State, "pending-runner-restart", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(pending.Message);
            }
        }
        finally
        {
            _moduleLock.Release();
        }
    }

    private void MarkKnown(string poolKey, string moduleId)
    {
        if (!_knownPoolModules.TryGetValue(poolKey, out var modules))
        {
            modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _knownPoolModules[poolKey] = modules;
        }

        modules.Add(moduleId);
    }

    private IReadOnlyList<string> ModuleIdsForPool(string poolKey)
    {
        return _knownPoolModules.TryGetValue(poolKey, out var modules)
            ? modules.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private void RecordUnloadResult(InProcUnloadResult result)
    {
        if (result.Unloaded)
        {
            _unloadRecords.Remove(result.PoolKey);
            return;
        }

        _unloadRecords[result.PoolKey] = new InProcUnloadRecord(
            result.ModuleId,
            result.PoolKey,
            result.State,
            "manual-runner-restart",
            result.Message,
            result.LoadContextName,
            DateTimeOffset.UtcNow);
    }

    private static string PoolKeyForModule(string moduleId) => $"module:{moduleId}";

    private static Assembly ResolveAlreadyLoadedIfAllowed(MptModuleDefinition module, string assemblyNameOrPath)
    {
        if (module.Manifest.Development?.AllowAlreadyLoadedFallback != true)
        {
            throw new FileNotFoundException($"Assembly '{assemblyNameOrPath}' was not found on disk, and development.allowAlreadyLoadedFallback is not enabled.");
        }

        return ResolveAlreadyLoaded(assemblyNameOrPath);
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
    private MptPluginLoadContext? _loadContext;
    private readonly WeakReference _loadContextReference;
    private IMptModule? _module;

    public InProcModuleSession(IMptModule module, MptPluginLoadContext loadContext, string moduleId, string poolKey, DateTimeOffset loadedAt)
    {
        _module = module;
        _loadContext = loadContext;
        _loadContextReference = new WeakReference(loadContext, trackResurrection: false);
        ModuleId = moduleId;
        PoolKey = poolKey;
        LoadedAt = loadedAt;
        LoadContextName = loadContext.Name ?? moduleId;
    }

    public string ModuleId { get; }
    public string PoolKey { get; }
    public DateTimeOffset LoadedAt { get; }
    public string LoadContextName { get; }
    public IMptModule Module => _module ?? throw new ObjectDisposedException(ModuleId);

    public async ValueTask<InProcUnloadResult> DisposeAndUnloadAsync(CancellationToken cancellationToken)
    {
        if (_module is null || _loadContext is null)
        {
            return new InProcUnloadResult(ModuleId, PoolKey, true, "unloaded", $"InProc module '{ModuleId}' was already unloaded.", LoadContextName);
        }

        await DisposeModuleAsync(cancellationToken);
        RequestUnloadAndClear();
        var unloaded = await WaitForUnloadAsync(cancellationToken);
        return unloaded
            ? new InProcUnloadResult(ModuleId, PoolKey, true, "unloaded", $"InProc module '{ModuleId}' unloaded cleanly.", LoadContextName)
            : new InProcUnloadResult(ModuleId, PoolKey, false, "pending-runner-restart", $"InProc module '{ModuleId}' did not release its collectible AssemblyLoadContext; restart Runner before updating this package.", LoadContextName);
    }

    private static void RequestUnload(MptPluginLoadContext loadContext)
    {
        loadContext.Unload();
    }

    private async ValueTask DisposeModuleAsync(CancellationToken cancellationToken)
    {
        var module = _module;
        _module = null;
        if (module is not null)
        {
            await module.DisposeAsync(cancellationToken);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RequestUnloadAndClear()
    {
        var loadContext = _loadContext;
        _loadContext = null;
        if (loadContext is not null)
        {
            RequestUnload(loadContext);
        }
    }

    private async ValueTask<bool> WaitForUnloadAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10 && _loadContextReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100, cancellationToken);
        }

        return !_loadContextReference.IsAlive;
    }
}

internal sealed record InProcUnloadResult(
    string ModuleId,
    string PoolKey,
    bool Unloaded,
    string State,
    string Message,
    string LoadContextName);

internal sealed record InProcUnloadRecord(
    string ModuleId,
    string PoolKey,
    string State,
    string RestartPolicy,
    string Message,
    string LoadContextName,
    DateTimeOffset UpdatedAt);
