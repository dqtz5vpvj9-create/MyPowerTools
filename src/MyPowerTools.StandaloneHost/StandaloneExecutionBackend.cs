using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Platform;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.ServiceManager.Client;

using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using EventCursor = MyPowerTools.Abstractions.EventCursor;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using IMptModule = MyPowerTools.Abstractions.IMptModule;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;

namespace MyPowerTools.StandaloneHost;

public static class StandaloneDataRootResolver
{
    public static (string DataRoot, bool IsPortable) Resolve(StandaloneCliOptions options, string toolRoot)
    {
        var hasPortableFlag = File.Exists(Path.Combine(toolRoot, "portable.flag"));
        if (!string.IsNullOrWhiteSpace(options.DataRoot))
        {
            return (Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.DataRoot)), options.Portable || hasPortableFlag);
        }

        if (options.Portable || hasPortableFlag)
        {
            return (Path.GetFullPath(Path.Combine(toolRoot, "data")), true);
        }

        var envDataRoot = Environment.GetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envDataRoot))
        {
            return (Path.GetFullPath(Environment.ExpandEnvironmentVariables(envDataRoot)), false);
        }

        var standard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools");
        return (Path.GetFullPath(standard), false);
    }

    public static string GetToolSurfaceDataDirectory(string dataRoot, string toolId)
    {
        var safeId = SanitizeSegment(toolId);
        var path = Path.Combine(dataRoot, "state", "tools", safeId);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        var chars = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            chars[i] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_';
        }

        var cleaned = new string(chars).Trim('.', '_', ' ');
        return cleaned.Length > 0 ? cleaned : "tool";
    }
}

/// <summary>
/// "可分可合" execution backend:
/// - Automatically attaches to a live <c>MyPowerTools.Runner</c> (&lt;150 ms probe) when Hub is running
///   and owns an active module for this tool (unless <c>--standalone</c>, <c>--isolated</c>, or <c>portable.flag</c>).
/// - Otherwise runs 100% standalone in-process (<see cref="IMptModule"/>) or via on-demand
///   <see cref="StdioCompatModuleHost"/> with zero Runner, zero ServiceManager, and zero Shell!
/// </summary>
public abstract class StandaloneExecutionBackend : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Channel<MptSurfaceEvent>> _subscribers = new();

    protected StandaloneExecutionBackend(
        ResolvedStandaloneTool tool,
        string dataRoot,
        bool isPortable,
        StandaloneServiceUnitClientFactory serviceUnitsFactory)
    {
        Tool = tool;
        DataRoot = dataRoot;
        IsPortable = isPortable;
        ServiceUnitsFactory = serviceUnitsFactory;
        SurfaceDataDirectory = StandaloneDataRootResolver.GetToolSurfaceDataDirectory(dataRoot, tool.Descriptor.ToolId);
    }

    public ResolvedStandaloneTool Tool { get; }
    public string DataRoot { get; }
    public bool IsPortable { get; }
    public string SurfaceDataDirectory { get; }
    public StandaloneServiceUnitClientFactory ServiceUnitsFactory { get; }

    public abstract string Mode { get; }
    public abstract bool IsHubAttached { get; }
    public string ModeBadgeText => IsHubAttached ? "已连接聚合后台" : "独立运行";

    public static async Task<StandaloneExecutionBackend> CreateAsync(
        ResolvedStandaloneTool tool,
        StandaloneCliOptions options,
        CancellationToken cancellationToken)
    {
        var (dataRoot, isPortable) = StandaloneDataRootResolver.Resolve(options, tool.ToolRoot);
        var runtimePaths = RuntimePaths.Create(dataRoot);
        Environment.SetEnvironmentVariable(ServiceManagerAdminClient.DataRootEnvironmentVariable, runtimePaths.Root);
        Environment.SetEnvironmentVariable("DOTNET_ROLL_FORWARD", "Major");

        var allowHubAttach = !options.StandaloneOnly && !options.Isolated && !isPortable;
        var serviceUnits = await StandaloneServiceUnitClientFactory.CreateAsync(
            runtimePaths.Root,
            allowHubAttach,
            cancellationToken).ConfigureAwait(false);

        if (allowHubAttach)
        {
            var attached = await TryAttachToRunningHubAsync(tool, runtimePaths.Root, serviceUnits, cancellationToken)
                .ConfigureAwait(false);
            if (attached is not null)
            {
                return attached;
            }
        }

        if (!string.IsNullOrWhiteSpace(tool.ResolvedInProcAssemblyPath) &&
            !string.IsNullOrWhiteSpace(tool.InProcModuleTypeName))
        {
            return new InProcStandaloneBackend(tool, runtimePaths, isPortable, serviceUnits);
        }

        if (tool.StdioEntrypoint is not null)
        {
            return new StdioStandaloneBackend(tool, runtimePaths.Root, isPortable, serviceUnits, tool.StdioEntrypoint);
        }

        return new UiOnlyStandaloneBackend(tool, runtimePaths.Root, isPortable, serviceUnits);
    }

    public abstract Task InitializeAsync(CancellationToken cancellationToken);
    public abstract Task StartAsync(CancellationToken cancellationToken);
    public abstract Task<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken);
    public abstract Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken);

    public Task<CommandExecutionResult> ExecuteCommandAsync(
        string commandId,
        JsonObject? arguments,
        CancellationToken cancellationToken)
    {
        return ExecuteCommandWithInvocationAsync(
            commandId,
            Guid.NewGuid().ToString("N"),
            arguments,
            cancellationToken);
    }

    public abstract Task<CommandExecutionResult> ExecuteCommandWithInvocationAsync(
        string commandId,
        string invocationId,
        JsonObject? arguments,
        CancellationToken cancellationToken);

    public IDisposable SubscribeSurfaceEvents(Action<MptSurfaceEvent> handler)
    {
        return SubscribeEvents(handler, null);
    }

    public StandaloneEventSubscription SubscribeEvents(
        Action<MptSurfaceEvent> handler,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<MptSurfaceEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _subscribers[id] = channel;

        var cts = new CancellationTokenSource();
        var readerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in channel.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                {
                    handler(evt);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex);
            }
        });

        return new StandaloneEventSubscription(async () =>
        {
            if (_subscribers.TryRemove(id, out var removed))
            {
                removed.Writer.TryComplete();
            }

            cts.Cancel();
            try
            {
                await readerTask.ConfigureAwait(false);
            }
            catch
            {
            }
            finally
            {
                cts.Dispose();
            }
        });
    }

    protected void PublishEvent(MptSurfaceEvent evt)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(evt);
        }
    }

    protected void CompleteAllSubscribers()
    {
        foreach (var pair in _subscribers)
        {
            if (_subscribers.TryRemove(pair.Key, out var ch))
            {
                ch.Writer.TryComplete();
            }
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        CompleteAllSubscribers();
        ServiceUnitsFactory.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<StandaloneExecutionBackend?> TryAttachToRunningHubAsync(
        ResolvedStandaloneTool tool,
        string dataRoot,
        StandaloneServiceUnitClientFactory serviceUnits,
        CancellationToken outerToken)
    {
        var authToken = HostControlAuthTokenStore.TryReadToken(dataRoot);
        if (string.IsNullOrWhiteSpace(authToken))
        {
            return null;
        }

        var platform = PlatformId.Current();
        var endpoint = IpcEndpoint.RunnerDefault(platform);
        if (endpoint.Transport == IpcTransport.UnixDomainSocket && !File.Exists(endpoint.Address))
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(150));

        if (endpoint.Transport == IpcTransport.NamedPipe)
        {
            try
            {
                await using var pipeProbe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeProbe.ConnectAsync(budget.Token).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        HostControlClient? client = null;
        try
        {
            client = HostControlClient.ForEndpoint(endpoint);
            var detail = await client.GetModuleDetailAsync(tool.Descriptor.OwnerModuleId, budget.Token).ConfigureAwait(false);
            if (string.Equals(detail.State, "running", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(detail.State, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return new RunnerAttachedBackend(tool, dataRoot, serviceUnits, client);
            }

            client.Dispose();
            return null;
        }
        catch
        {
            client?.Dispose();
            return null;
        }
    }
}

internal sealed class RunnerAttachedBackend : StandaloneExecutionBackend
{
    private readonly HostControlClient _client;
    private CancellationTokenSource? _eventPumpCts;
    private Task? _eventPumpTask;

    public RunnerAttachedBackend(
        ResolvedStandaloneTool tool,
        string dataRoot,
        StandaloneServiceUnitClientFactory serviceUnits,
        HostControlClient client)
        : base(tool, dataRoot, isPortable: false, serviceUnits)
    {
        _client = client;
    }

    public override string Mode => "hub-attached";
    public override bool IsHubAttached => true;

    public override Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _eventPumpCts = new CancellationTokenSource();
        var ct = _eventPumpCts.Token;
        var ownerModuleId = Tool.Descriptor.OwnerModuleId;
        var toolId = Tool.Descriptor.ToolId;
        _eventPumpTask = Task.Run(async () =>
        {
            try
            {
                const ulong afterSeq = 0UL;
                await foreach (var evt in _client.SubscribeHostEventsAsync(afterSeq, ct).ConfigureAwait(false))
                {
                    if (!string.Equals(evt.SourceId, ownerModuleId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(evt.SourceId, toolId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var payload = evt.Payload is not null
                        ? JsonStructMapper.ToJsonObject(evt.Payload)
                        : new JsonObject();
                    var ts = evt.Time is not null ? evt.Time.ToDateTimeOffset() : DateTimeOffset.UtcNow;

                    PublishEvent(new MptSurfaceEvent(
                        evt.Seq,
                        evt.SourceId,
                        evt.Type,
                        ts,
                        payload));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch
            {
                // Best-effort event stream when attached to external Runner.
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public override async Task<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var detail = await _client.GetModuleDetailAsync(Tool.Descriptor.OwnerModuleId, cancellationToken).ConfigureAwait(false);
        return new ModuleStatusSnapshot(
            detail.ModuleId,
            detail.State,
            detail.Summary,
            DateTimeOffset.UtcNow,
            detail.Diagnostics.Select(d => new HealthCheckSnapshot(
                d.Id,
                d.Label,
                string.Equals(d.State, "ok", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.State, "healthy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.State, "pass", StringComparison.OrdinalIgnoreCase),
                d.Detail)).ToArray(),
            0);
    }

    public override async Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var resp = await _client.ListCommandsAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        return resp.Commands
            .Where(c => string.Equals(c.ModuleId, Tool.Descriptor.OwnerModuleId, StringComparison.OrdinalIgnoreCase))
            .Select(c => new MptCommandDescriptor(
                c.CommandId,
                c.ModuleId,
                c.Title,
                c.Subtitle,
                c.Kind,
                Category: c.Category))
            .ToArray();
    }

    public override async Task<CommandExecutionResult> ExecuteCommandWithInvocationAsync(
        string commandId,
        string invocationId,
        JsonObject? arguments,
        CancellationToken cancellationToken)
    {
        var response = await _client.ExecuteCommandAsync(
            invocationId,
            commandId,
            arguments ?? new JsonObject(),
            cancellationToken).ConfigureAwait(false);

        var ok = string.Equals(response.State, "succeeded", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(response.State, "completed", StringComparison.OrdinalIgnoreCase) ||
                 (string.IsNullOrWhiteSpace(response.ErrorCode) &&
                  !string.Equals(response.State, "failed", StringComparison.OrdinalIgnoreCase) &&
                  !string.Equals(response.State, "cancelled", StringComparison.OrdinalIgnoreCase));
        var outputJson = string.IsNullOrWhiteSpace(response.Summary)
            ? "{}"
            : (response.Summary.TrimStart().StartsWith('{') || response.Summary.TrimStart().StartsWith('[')
                ? response.Summary
                : new JsonObject { ["summary"] = response.Summary }.ToJsonString());

        return new CommandExecutionResult(
            string.IsNullOrWhiteSpace(response.InvocationId) ? invocationId : response.InvocationId,
            commandId,
            response.State,
            ok,
            outputJson,
            string.IsNullOrWhiteSpace(response.ErrorCode) && string.IsNullOrWhiteSpace(response.ErrorMessage)
                ? null
                : new MptRuntimeError(
                    string.IsNullOrWhiteSpace(response.ErrorCode) ? "command.failed" : response.ErrorCode,
                    response.ErrorMessage));
    }

    public override async ValueTask DisposeAsync()
    {
        if (_eventPumpCts is not null)
        {
            _eventPumpCts.Cancel();
            if (_eventPumpTask is not null)
            {
                try
                {
                    await _eventPumpTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _eventPumpCts.Dispose();
        }

        _client.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class InProcStandaloneBackend : StandaloneExecutionBackend
{
    private readonly RuntimePaths _paths;
    private readonly IPlatformPack _platformPack;
    private IMptModule? _module;
    private ModuleContext? _moduleContext;
    private CancellationTokenSource? _eventPumpCts;
    private Task? _eventPumpTask;

    public InProcStandaloneBackend(
        ResolvedStandaloneTool tool,
        RuntimePaths paths,
        bool isPortable,
        StandaloneServiceUnitClientFactory serviceUnits)
        : base(tool, paths.Root, isPortable, serviceUnits)
    {
        _paths = paths;
        _platformPack = PlatformPackFactory.Create();
    }

    public override string Mode => "standalone-inproc";
    public override bool IsHubAttached => false;

    public override async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dllPath = Tool.ResolvedInProcAssemblyPath!;
        var typeName = Tool.InProcModuleTypeName!;
        SharedRuntimeAssemblyResolver.AddProbeDirectory(Path.GetDirectoryName(dllPath));

        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
        var moduleType = assembly.GetType(typeName, throwOnError: true)
            ?? throw new TypeLoadException($"Module type '{typeName}' was not found in '{dllPath}'.");

        _module = Activator.CreateInstance(moduleType) as IMptModule
            ?? throw new InvalidOperationException($"Type '{typeName}' does not implement IMptModule.");

        var moduleId = string.IsNullOrWhiteSpace(_module.Id) ? Tool.Descriptor.OwnerModuleId : _module.Id;
        var safeModuleId = StandaloneDataRootResolver.SanitizeSegment(moduleId);
        var moduleDataDir = Path.Combine(_paths.State, "modules", safeModuleId);
        var moduleCacheDir = Path.Combine(_paths.State, "cache", safeModuleId);
        var moduleLogDir = Path.Combine(_paths.Logs, "modules", safeModuleId);
        Directory.CreateDirectory(moduleDataDir);
        Directory.CreateDirectory(moduleCacheDir);
        Directory.CreateDirectory(moduleLogDir);

        var capabilities = BuildCapabilityProviders(_platformPack, ServiceUnitsFactory);
        _moduleContext = new ModuleContext(
            HostVersion: ProtocolConstants.HostVersion,
            ProtocolVersion: ProtocolConstants.ModuleProtocolVersion,
            PackageId: Tool.Descriptor.ToolId,
            ModuleId: moduleId,
            DataDirectory: moduleDataDir,
            CacheDirectory: moduleCacheDir,
            LogDirectory: moduleLogDir,
            Platform: PlatformId.Current().ToString().ToLowerInvariant(),
            GrantedCapabilities: capabilities.Keys.ToArray(),
            CapabilityProviders: capabilities);

        await _module.InitializeAsync(_moduleContext, cancellationToken).ConfigureAwait(false);
        await ApplySavedModuleSettingsIfPresentAsync(moduleId, cancellationToken).ConfigureAwait(false);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_module is null || _moduleContext is null)
        {
            throw new InvalidOperationException("Module backend must be initialized before starting.");
        }

        if (_module is IMptModuleLifecycle lifecycle)
        {
            await lifecycle.EnableAsync(_moduleContext, cancellationToken).ConfigureAwait(false);
            await lifecycle.StartAsync(_moduleContext, cancellationToken).ConfigureAwait(false);
        }

        _eventPumpCts = new CancellationTokenSource();
        var ct = _eventPumpCts.Token;
        var module = _module;
        _eventPumpTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in module.SubscribeEventsAsync(new EventCursor(0), ct).ConfigureAwait(false))
                {
                    PublishEvent(new MptSurfaceEvent(
                        evt.Seq,
                        evt.ModuleId,
                        evt.Type,
                        evt.Time,
                        evt.Payload));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch
            {
                // Keep standalone session alive even if a module ends its event stream.
            }
        }, CancellationToken.None);
    }

    public override async Task<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_module is null)
        {
            throw new InvalidOperationException("Module is not initialized.");
        }

        return await _module.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        if (_module is null)
        {
            return [];
        }

        return await _module.ListCommandsAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<CommandExecutionResult> ExecuteCommandWithInvocationAsync(
        string commandId,
        string invocationId,
        JsonObject? arguments,
        CancellationToken cancellationToken)
    {
        if (_module is null)
        {
            return new CommandExecutionResult(
                invocationId,
                commandId,
                "failed",
                false,
                "",
                new MptRuntimeError("module.uninitialized", "InProc module has not been initialized."));
        }

        var request = new CommandRequest(
            invocationId,
            commandId,
            arguments ?? new JsonObject());

        return await _module.ExecuteCommandAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_eventPumpCts is not null)
        {
            _eventPumpCts.Cancel();
            if (_eventPumpTask is not null)
            {
                try
                {
                    await _eventPumpTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            _eventPumpCts.Dispose();
        }

        if (_module is IMptModuleLifecycle lifecycle && _moduleContext is not null)
        {
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await lifecycle.StopAsync(_moduleContext, stopBudget.Token).ConfigureAwait(false);
                await lifecycle.DisableAsync(_moduleContext, stopBudget.Token).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        switch (_module)
        {
            case IAsyncDisposable asyncDisposable:
                try { await asyncDisposable.DisposeAsync().ConfigureAwait(false); } catch { }
                break;
            case IDisposable disposable:
                try { disposable.Dispose(); } catch { }
                break;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ApplySavedModuleSettingsIfPresentAsync(string moduleId, CancellationToken cancellationToken)
    {
        if (_module is null)
        {
            return;
        }

        var settingsStore = new SettingsStore(_paths.Settings);
        var saved = settingsStore.Get(moduleId);
        if (saved is null)
        {
            return;
        }

        try
        {
            await _module.ApplySettingsAsync(
                new MyPowerTools.Abstractions.SettingsSnapshotDocument(moduleId, saved.Revision, saved.Values.DeepClone()?.AsObject() ?? new JsonObject(), saved.UpdatedAt),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Optional saved host settings must not abort standalone tool launch.
        }
    }

    private static IReadOnlyDictionary<string, object> BuildCapabilityProviders(
        IPlatformPack platformPack,
        StandaloneServiceUnitClientFactory serviceUnitsFactory)
    {
        var providers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["service.units"] = serviceUnitsFactory
        };

        if (platformPack.Capabilities.Resolve("display.profile").Supported)
        {
            providers["display.profile"] = platformPack.Display;
        }

        if (platformPack.Capabilities.Resolve("notification.desktop").Supported)
        {
            providers["notification.desktop"] = platformPack.Notifications;
        }

        if (platformPack.Capabilities.Resolve("clipboard.image").Supported)
        {
            providers["clipboard.image"] = platformPack.ClipboardImages;
        }

        if (platformPack.Capabilities.Resolve("keyboard.shortcut").Supported)
        {
            providers["keyboard.shortcut"] = platformPack.KeyboardShortcuts;
        }

        if (platformPack.Capabilities.Resolve("secret.store").Supported)
        {
            providers["secret.store"] = platformPack.Secrets;
        }

        return providers;
    }
}

internal sealed class StdioStandaloneBackend : StandaloneExecutionBackend
{
    private readonly StdioCompatModuleHost _stdioHost = new();
    private readonly SelectedEntrypoint _entrypoint;
    private readonly TimeSpan _defaultTimeout;

    public StdioStandaloneBackend(
        ResolvedStandaloneTool tool,
        string dataRoot,
        bool isPortable,
        StandaloneServiceUnitClientFactory serviceUnits,
        SelectedEntrypoint entrypoint)
        : base(tool, dataRoot, isPortable, serviceUnits)
    {
        _entrypoint = entrypoint;
        var timeoutMs = tool.ToolManifest.Runtime?.TimeoutMs ?? 30000;
        _defaultTimeout = timeoutMs > 0 ? TimeSpan.FromMilliseconds(timeoutMs) : TimeSpan.FromSeconds(30);
    }

    public override string Mode => "standalone-stdio";
    public override bool IsHubAttached => false;

    public override Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = new ModuleStatusSnapshot(
            Tool.Descriptor.OwnerModuleId,
            "ready",
            _stdioHost.Describe(_entrypoint),
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("stdio", "stdio-jsonrpc", true, _entrypoint.SelectionReason)],
            0);
        return Task.FromResult(snapshot);
    }

    public override Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        var commands = Tool.ToolManifest.Commands
            .Select(cmd => new MptCommandDescriptor(
                cmd.Id,
                Tool.Descriptor.OwnerModuleId,
                cmd.Title,
                cmd.Description,
                "action"))
            .ToArray();
        return Task.FromResult<IReadOnlyList<MptCommandDescriptor>>(commands);
    }

    public override Task<CommandExecutionResult> ExecuteCommandWithInvocationAsync(
        string commandId,
        string invocationId,
        JsonObject? arguments,
        CancellationToken cancellationToken)
    {
        var request = new CommandRequest(
            invocationId,
            commandId,
            arguments ?? new JsonObject());
        return _stdioHost.ExecuteAsync(_entrypoint, request, _defaultTimeout, cancellationToken);
    }
}

internal sealed class UiOnlyStandaloneBackend : StandaloneExecutionBackend
{
    public UiOnlyStandaloneBackend(
        ResolvedStandaloneTool tool,
        string dataRoot,
        bool isPortable,
        StandaloneServiceUnitClientFactory serviceUnits)
        : base(tool, dataRoot, isPortable, serviceUnits)
    {
    }

    public override string Mode => "standalone-ui";
    public override bool IsHubAttached => false;

    public override Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new ModuleStatusSnapshot(
            Tool.Descriptor.OwnerModuleId,
            "ready",
            "Pure Avalonia UI surface ready.",
            DateTimeOffset.UtcNow,
            [],
            0));
    }

    public override Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);
    }

    public override Task<CommandExecutionResult> ExecuteCommandWithInvocationAsync(
        string commandId,
        string invocationId,
        JsonObject? arguments,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new CommandExecutionResult(
            invocationId,
            commandId,
            "unsupported",
            false,
            "",
            new MptRuntimeError(
                "standalone.no_backend",
                $"Tool '{Tool.Descriptor.ToolId}' is a UI-only tool without a backend command runtime.")));
    }
}


public sealed class StandaloneEventSubscription : IDisposable, IAsyncDisposable
{
    private readonly Func<Task> _disposeAsync;
    private int _disposed;

    public StandaloneEventSubscription(Func<Task> disposeAsync)
    {
        _disposeAsync = disposeAsync;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _ = _disposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _disposeAsync().ConfigureAwait(false);
        }
    }
}
