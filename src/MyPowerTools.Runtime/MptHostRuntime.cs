using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class MptHostRuntime : IAsyncDisposable
{
    private readonly PackageRegistry _packageRegistry;
    private readonly ToolRegistry _toolRegistry;
    private readonly CommandIndex _commandIndex = new();
    private readonly RuntimePaths _paths;
    private readonly PlatformId _platform;
    private readonly SettingsStore _settingsStore;
    private readonly ModuleStateStore _moduleStateStore;
    private readonly HotkeyStore _hotkeyStore;
    private readonly RuntimeProcessPolicyStore _processPolicyStore;
    private readonly ModuleEventStore _moduleEventStore;
    private readonly EventBus _eventBus = new();
    private readonly LogRouter _logRouter;
    private readonly NotificationCenter _notificationCenter = new();
    private readonly CommandHistory _commandHistory = new();
    private readonly HealthMonitor _healthMonitor = new();
    private readonly ModuleSupervisor _moduleSupervisor = new();
    private readonly PackageTrustVerifier _packageTrust = new();
    private readonly IReadOnlyDictionary<string, IModuleTransportRuntime> _transportRuntimes;
    private readonly IReadOnlyDictionary<string, object> _capabilityProviders;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _packageRoot = "";
    private IReadOnlyList<string> _developmentToolRoots = [];
    private IReadOnlyList<Sdk.MptCommandDescriptor> _dynamicCommands = [];
    private readonly InvocationExecutionCache _executions = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _executionCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CommandRuntimeCancellationTarget> _executionRuntimeTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _cancelledInvocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ulong> _moduleEventCursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _moduleEventPumpTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _moduleEventPumpCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _moduleEventPumpFailures = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _moduleEventPumpCancellation;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public MptHostRuntime(
        PackageReader packageReader,
        PlatformId platform,
        RuntimePaths? paths = null,
        IEnumerable<IModuleTransportRuntime>? transportRuntimes = null,
        IReadOnlyDictionary<string, object>? capabilityProviders = null,
        IEnumerable<string>? initialEnabledModules = null)
    {
        paths ??= RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "MyPowerTools", "runtime-tests"));
        _paths = paths;
        _platform = platform;
        _packageRegistry = new PackageRegistry(packageReader, platform);
        _toolRegistry = new ToolRegistry(packageReader);
        _settingsStore = new SettingsStore(paths.Settings);
        _moduleStateStore = new ModuleStateStore(paths.State, initialEnabledModules);
        _hotkeyStore = new HotkeyStore(paths.State);
        _processPolicyStore = new RuntimeProcessPolicyStore(paths.State);
        _moduleEventStore = new ModuleEventStore(paths.State);
        _logRouter = new LogRouter(paths.Logs);
        _transportRuntimes = (transportRuntimes ?? [])
            .GroupBy(runtime => runtime.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _capabilityProviders = capabilityProviders ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var cursor in _moduleEventStore.LoadCursors())
        {
            _moduleEventCursors[cursor.Key] = cursor.Value;
        }
    }

    public IReadOnlyList<RuntimeModuleRecord> Modules => _packageRegistry.Modules;
    public ulong CurrentEventSeq => _eventBus.CurrentSeq;

    public async ValueTask DisposeAsync()
    {
        await StopModuleEventPumpAsync();
        _httpClient.Dispose();
        foreach (var runtime in _transportRuntimes.Values)
        {
            switch (runtime)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    public void Load(string packageRoot, IEnumerable<string>? developmentToolRoots = null)
    {
        _packageRoot = Path.GetFullPath(packageRoot);
        if (developmentToolRoots is not null)
        {
            _developmentToolRoots = developmentToolRoots
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        _packageRegistry.Load(packageRoot, _developmentToolRoots);
        _toolRegistry.Load(_packageRegistry.Modules);
        ResetRestartLifetimeCursors();
        ApplyPersistedModuleState();
        ObserveAllModules();
        _dynamicCommands = [];
        _commandIndex.Rebuild(EnabledModules(), tools: _toolRegistry.Tools);
        RegisterProcessPoolsAndApplyPolicies();
        _eventBus.Publish("runner", "registry.loaded", new JsonObject
        {
            ["moduleCount"] = _packageRegistry.Modules.Count,
            ["enabledModuleCount"] = EnabledModules().Count
        });
        _logRouter.Append("runner", "runner", "info", $"Loaded {_packageRegistry.Modules.Count} modules from {packageRoot}", eventSeq: _eventBus.CurrentSeq);
    }

    public async Task<IReadOnlyList<RuntimeToolSnapshot>> RefreshToolCatalogAsync(CancellationToken cancellationToken)
    {
        await StopModuleEventPumpAsync();
        cancellationToken.ThrowIfCancellationRequested();
        Load(_packageRoot, _developmentToolRoots);
        await RefreshDynamicCommandsAsync(cancellationToken);
        StartModuleEventPump();
        return ListTools(includeDisabled: true);
    }

    public IReadOnlyList<RuntimeToolSnapshot> ListTools(bool includeDisabled = false)
    {
        return _toolRegistry.Tools
            .Select(tool =>
            {
                var module = _packageRegistry.FindModule(tool.OwnerModuleId)
                    ?? throw new InvalidDataException($"Tool '{tool.ToolId}' owner module '{tool.OwnerModuleId}' is not loaded.");
                var enabled = _moduleStateStore.IsEnabled(module.Module.Manifest.Id);
                return CreateToolSnapshot(tool, module, enabled);
            })
            .Where(tool => includeDisabled || tool.Enabled)
            .ToArray();
    }

    private static RuntimeToolSnapshot CreateToolSnapshot(
        MyPowerTools.Abstractions.ToolDescriptor tool,
        RuntimeModuleRecord module,
        bool enabled)
    {
        // A tool whose manifest never loaded reports as an error regardless of its
        // module's state, so catalogs and dashboards render a failure card.
        var state = tool.LoadError is { Length: > 0 } ? "error" : module.Status.State;
        var summary = tool.LoadError is { Length: > 0 } ? tool.LoadError : module.Status.Summary;
        return new RuntimeToolSnapshot(tool, state, summary, enabled);
    }

    public Sdk.MptModuleEvent PublishToolEvent(string toolId, string topic, JsonObject payload)
    {
        var tool = GetTool(toolId);
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new InvalidDataException("Tool event topic is required.");
        }
        var published = _eventBus.Publish(tool.Descriptor.OwnerModuleId, topic, payload);
        _logRouter.Append(
            tool.Descriptor.ToolId,
            tool.Descriptor.OwnerModuleId,
            "info",
            $"Tool event '{topic}' published.",
            eventSeq: published.Seq);
        return published;
    }

    public RuntimeToolSnapshot GetTool(string toolId)
    {
        var descriptor = _toolRegistry.Find(toolId)
            ?? throw new KeyNotFoundException($"Tool '{toolId}' was not found.");
        var module = _packageRegistry.FindModule(descriptor.OwnerModuleId)
            ?? throw new InvalidDataException($"Tool '{toolId}' owner module '{descriptor.OwnerModuleId}' is not loaded.");
        return CreateToolSnapshot(
            descriptor,
            module,
            _moduleStateStore.IsEnabled(module.Module.Manifest.Id));
    }

    public DashboardSnapshot GetDashboardSnapshot()
    {
        var enabledModules = EnabledModules();
        var cards = enabledModules
            .Select(module => new DashboardCard(
                module.Module.Manifest.Id,
                module.Module.Manifest.PackageId,
                module.Module.Manifest.DisplayName,
                module.Status.State,
                module.Status.Summary,
                [
                    new DashboardMetric("Transport", module.Entrypoint?.Kind ?? "none"),
                    new DashboardMetric("SDK", module.Module.Manifest.ModuleSdk)
                ],
                [
                    new DashboardAction($"{module.Module.Manifest.Id}.open", "Open", "primary"),
                    new DashboardAction($"{module.Module.Manifest.Id}.status.refresh", "Refresh", "secondary")
                ]))
            .ToArray();
        var alerts = _moduleSupervisor.Snapshots(enabledModules)
            .Where(snapshot => snapshot.SupervisorState == "intervention-needed")
            .Select(snapshot => new HostAlert(
                $"module-supervisor-{snapshot.ModuleId}",
                "warning",
                $"{snapshot.ModuleId} needs attention",
                snapshot.NextAction))
            .ToArray();

        return new DashboardSnapshot(cards, alerts, _eventBus.CurrentSeq);
    }

    public async Task RefreshHealthAsync(CancellationToken cancellationToken)
    {
        ExpireProcessPolicies();
        foreach (var module in EnabledModules().ToArray())
        {
            if (TryGetTransportRuntime(module, out var runtime))
            {
                var context = CreateModuleContext(module);
                await ApplyPersistedSettingsIfPresentAsync(module, runtime, context, cancellationToken);
            }

            var status = await CheckTransportHealthAsync(module, cancellationToken)
                ?? await _healthMonitor.CheckAsync(module, cancellationToken);
            RecordModuleStatus(module, status);
        }
    }

    public async Task<int> RefreshDynamicCommandsAsync(CancellationToken cancellationToken)
    {
        ExpireProcessPolicies();
        var commands = new List<Sdk.MptCommandDescriptor>();
        foreach (var module in EnabledModules().ToArray())
        {
            if (!TryGetTransportRuntime(module, out var runtime))
            {
                continue;
            }

            var context = CreateModuleContext(module);
            try
            {
                await ApplyPersistedSettingsIfPresentAsync(module, runtime, context, cancellationToken);
                var status = await runtime.GetStatusAsync(module, context, cancellationToken);
                if (status is not null)
                {
                    RecordModuleStatus(module, status);
                }

                var moduleCommands = await runtime.ListCommandsAsync(module, context, cancellationToken);
                foreach (var command in moduleCommands)
                {
                    commands.Add(NormalizeDynamicCommand(module, command));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var message = LogRouter.Redact(ex.Message);
                RecordModuleStatus(module, Degraded(module, message));
                _logRouter.Append(module.Module.Manifest.PackageId, module.Module.Manifest.Id, "error", message);
            }
        }

        _dynamicCommands = commands;
        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands, _toolRegistry.Tools);
        _eventBus.Publish("runner", "commands.dynamic.refreshed", new JsonObject
        {
            ["commandCount"] = _dynamicCommands.Count
        });
        return _dynamicCommands.Count;
    }

    public IReadOnlyList<PackageSummarySnapshot> ListPackages(bool includeDisabled)
    {
        return ListModules(includeDisabled)
            .GroupBy(module => module.Package.Package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First().Package;
                var trust = _packageTrust.Verify(first.Directory, PackageTrustPolicy.LocalDevelopment);
                return new PackageSummarySnapshot(
                    first.Package.Id,
                    first.Package.DisplayName,
                    first.Package.Version,
                    first.Package.Publisher ?? "",
                    first.Directory,
                    first.Package.Hashes ?? "",
                    trust.State,
                    trust.Policy,
                    trust.SignaturePath,
                    trust.Issues.Count,
                    group.Count(),
                    first.Package.Shared?.Runtimes.Count ?? 0,
                    group.Select(module => module.Module.Manifest.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
            })
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RuntimeModuleRecord> ListModules(bool includeDisabled)
    {
        return includeDisabled
            ? _packageRegistry.Modules
            : EnabledModules();
    }

    public RuntimeDiagnosticsSnapshot GetRuntimeDiagnostics()
    {
        ExpireProcessPolicies();
        var modules = _packageRegistry.Modules.ToArray();
        var enabledModules = modules.Where(module => module.Status.State != "disabled").ToArray();
        var disabledModules = modules.Length - enabledModules.Length;
        var commandCount = _commandIndex.Search("").Count;
        var recentCommands = _commandHistory.List().Take(10).ToArray();
        var transportKinds = modules
            .Select(module => module.Entrypoint?.Kind ?? "none")
            .Concat(_transportRuntimes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase)
            .Select(kind => new RuntimeTransportDiagnostics(
                kind,
                _transportRuntimes.ContainsKey(kind),
                modules.Count(module => string.Equals(module.Entrypoint?.Kind ?? "none", kind, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        var processDiagnostics = _transportRuntimes.Values
            .OfType<IModuleTransportDiagnosticsProvider>()
            .SelectMany(provider => provider.GetProcessDiagnostics())
            .OrderBy(process => process.TransportKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.PoolKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var processPolicyHistory = _processPolicyStore.History(10);
        var supervision = _moduleSupervisor.Snapshots(modules)
            .ToDictionary(snapshot => snapshot.ModuleId, StringComparer.OrdinalIgnoreCase);

        return new RuntimeDiagnosticsSnapshot(
            ProtocolConstants.HostVersion,
            ProtocolConstants.HostControlProtocolVersion,
            ProtocolConstants.ModuleProtocolVersion,
            _platform.Rid,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            _startedAt,
            DateTimeOffset.UtcNow,
            _eventBus.CurrentSeq,
            new RuntimePathDiagnostics(
                _paths.Root,
                _paths.Settings,
                _paths.Logs,
                _paths.State,
                _paths.Packages,
                _packageRoot),
            new RuntimeCountDiagnostics(
                modules.Select(module => module.Package.Package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                modules.Length,
                enabledModules.Length,
                disabledModules,
                modules.Count(module => module.Status.State == "running"),
                modules.Count(module => module.Status.State == "degraded"),
                modules.Count(module => module.Status.State == "error"),
                commandCount,
                _dynamicCommands.Count,
                _notificationCenter.List().Count,
                _commandHistory.List().Count),
            transportKinds,
            processDiagnostics,
            processPolicyHistory,
            modules
                .OrderBy(module => module.Module.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(module =>
                {
                    var snapshot = supervision[module.Module.Manifest.Id];
                    return new RuntimeModuleDiagnostics(
                        module.Module.Manifest.Id,
                        module.Module.Manifest.PackageId,
                        module.Module.Manifest.DisplayName,
                        module.Status.State,
                        module.Status.Summary,
                        module.Status.State != "disabled",
                        module.Entrypoint?.Kind ?? "none",
                        module.Status.UpdatedAt,
                        module.Status.Checks.Count,
                        snapshot.ObservationCount,
                        snapshot.ConsecutiveFailureCount,
                        snapshot.SupervisorState,
                        snapshot.NextAction,
                        snapshot.LastObservedAt,
                        module.Entrypoint?.SelectionReason ?? "No compatible runnable entrypoint was selected.",
                        module.TransportDiagnostics
                            .Select(diagnostic => $"{diagnostic.State}:{diagnostic.TransportKind}:{diagnostic.Reason}")
                            .ToArray(),
                        DescribeModuleEnabledState(module),
                        DescribeTransportActiveState(module, processDiagnostics),
                        DescribeToolRuntimeState(module, processDiagnostics));
                })
                .ToArray(),
            ListHotkeyDiagnostics(),
            recentCommands);
    }

    private string DescribeTransportActiveState(RuntimeModuleRecord module, IReadOnlyList<RuntimeProcessDiagnostics> processes)
    {
        if (module.Status.State == "disabled")
        {
            return "inactive";
        }

        if (module.Entrypoint is null)
        {
            return "no-entrypoint";
        }

        if (!_transportRuntimes.ContainsKey(module.Entrypoint.Kind))
        {
            return "unregistered";
        }

        var process = processes.FirstOrDefault(candidate =>
            string.Equals(candidate.TransportKind, module.Entrypoint.Kind, StringComparison.OrdinalIgnoreCase) &&
            candidate.ModuleIds.Contains(module.Module.Manifest.Id, StringComparer.OrdinalIgnoreCase));
        return process?.State ?? "registered";
    }

    private static string DescribeModuleEnabledState(RuntimeModuleRecord module)
    {
        return module.Status.State == "disabled" ? "disabled" : "enabled";
    }

    private string DescribeToolRuntimeState(RuntimeModuleRecord module, IReadOnlyList<RuntimeProcessDiagnostics> processes)
    {
        if (module.Status.State == "disabled")
        {
            return "disabled";
        }

        var transportState = DescribeTransportActiveState(module, processes);
        if (transportState is "no-entrypoint" or "unregistered")
        {
            return "unavailable";
        }

        return module.Status.State switch
        {
            "running" => "running",
            "degraded" => "partial",
            "error" => "error",
            var state when string.IsNullOrWhiteSpace(state) => "unknown",
            var state => state
        };
    }

    public async Task<RuntimeProcessRestartResult> RestartRuntimeProcessAsync(string transportKind, string poolKey, CancellationToken cancellationToken)
    {
        ExpireProcessPolicies();
        if (string.IsNullOrWhiteSpace(transportKind))
        {
            return new RuntimeProcessRestartResult(false, "", poolKey, "validation-failed", "transportKind is required.", []);
        }

        if (string.IsNullOrWhiteSpace(poolKey))
        {
            return new RuntimeProcessRestartResult(false, transportKind, "", "validation-failed", "poolKey is required.", []);
        }

        if (!_transportRuntimes.TryGetValue(transportKind, out var runtime) ||
            runtime is not IModuleTransportDiagnosticsProvider diagnosticsProvider)
        {
            return new RuntimeProcessRestartResult(false, transportKind, poolKey, "unsupported", $"Transport '{transportKind}' does not expose process controls.", []);
        }

        var clearDynamicCommands = string.Equals(transportKind, "inproc-dotnet", StringComparison.OrdinalIgnoreCase);
        if (clearDynamicCommands)
        {
            var moduleIds = diagnosticsProvider.GetProcessDiagnostics()
                .Where(process =>
                    string.Equals(process.TransportKind, transportKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(process.PoolKey, poolKey, StringComparison.OrdinalIgnoreCase))
                .SelectMany(process => process.ModuleIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ClearDynamicCommandsForModules(moduleIds);
        }

        var result = await diagnosticsProvider.RestartProcessAsync(poolKey, cancellationToken);
        if (clearDynamicCommands && result.Success)
        {
            ClearDynamicCommandsForModules(result.ModuleIds);
        }

        var evt = _eventBus.Publish("runner", "runtime.process.restart", new JsonObject
        {
            ["transportKind"] = transportKind,
            ["poolKey"] = poolKey,
            ["state"] = result.State,
            ["success"] = result.Success
        });
        _logRouter.Append("runner", "runner", result.Success ? "info" : "error", result.Message, eventSeq: evt.Seq);
        return result;
    }

    public async Task<RuntimeProcessPolicyResult> SetRuntimeProcessRestartPolicyAsync(string transportKind, string poolKey, bool paused, string reason, CancellationToken cancellationToken, string source = "runtime", DateTimeOffset? expiresAt = null)
    {
        ExpireProcessPolicies();
        if (string.IsNullOrWhiteSpace(transportKind))
        {
            return new RuntimeProcessPolicyResult(false, "", poolKey, "validation-failed", "unknown", "transportKind is required.", []);
        }

        if (string.IsNullOrWhiteSpace(poolKey))
        {
            return new RuntimeProcessPolicyResult(false, transportKind, "", "validation-failed", "unknown", "poolKey is required.", []);
        }

        if (!_transportRuntimes.TryGetValue(transportKind, out var runtime) ||
            runtime is not IModuleTransportDiagnosticsProvider diagnosticsProvider)
        {
            return new RuntimeProcessPolicyResult(false, transportKind, poolKey, "unsupported", "unknown", $"Transport '{transportKind}' does not expose process controls.", []);
        }

        var normalizedExpiresAt = expiresAt?.ToUniversalTime();
        if (paused && normalizedExpiresAt is not null && normalizedExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return new RuntimeProcessPolicyResult(false, transportKind, poolKey, "validation-failed", "unknown", "expiresAt must be in the future.", []);
        }

        var result = await diagnosticsProvider.SetRestartPolicyAsync(poolKey, paused, reason, normalizedExpiresAt, cancellationToken);
        _processPolicyStore.Record(result, reason, source, result.ExpiresAt);
        var evt = _eventBus.Publish("runner", "runtime.process.policy", new JsonObject
        {
            ["transportKind"] = transportKind,
            ["poolKey"] = poolKey,
            ["state"] = result.State,
            ["restartPolicy"] = result.RestartPolicy,
            ["source"] = source,
            ["expiresAt"] = result.ExpiresAt?.ToString("O"),
            ["success"] = result.Success
        });
        _logRouter.Append("runner", "runner", result.Success ? "info" : "error", result.Message, eventSeq: evt.Seq);
        return result;
    }

    public ModuleDetailSnapshot SetModuleEnabled(string moduleId, bool enabled)
    {
        return SetModuleEnabledAsync(moduleId, enabled, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<ModuleDetailSnapshot> SetModuleEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken = default)
    {
        var module = _packageRegistry.FindModule(moduleId)
            ?? throw new KeyNotFoundException($"Module '{moduleId}' was not found.");

        if (!enabled)
        {
            await StopModuleEventStreamAsync(moduleId);
        }

        _moduleStateStore.SetModuleEnabled(moduleId, enabled);
        var nextStatus = enabled ? InitialStatus(module) : DisabledStatus(module);
        RecordModuleStatus(module, nextStatus);
        _dynamicCommands = _dynamicCommands
            .Where(command => !string.Equals(command.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (enabled)
        {
            await EnableModuleResourcesAsync(module, cancellationToken);
        }
        else
        {
            await DisableModuleResourcesAsync(module, cancellationToken);
        }

        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands, _toolRegistry.Tools);
        var evt = _eventBus.Publish(moduleId, enabled ? "module.enabled" : "module.disabled", new JsonObject
        {
            ["enabled"] = enabled
        });
        _logRouter.Append(module.Module.Manifest.PackageId, moduleId, "info", enabled ? "Module enabled." : "Module disabled.", eventSeq: evt.Seq);
        if (enabled)
        {
            StartModuleEventStream(moduleId);
        }

        return GetModuleDetail(moduleId);
    }

    public void StartModuleEventPump()
    {
        if (_moduleEventPumpCancellation is not null)
        {
            return;
        }

        _moduleEventPumpCancellation = new CancellationTokenSource();
        foreach (var module in EnabledModules())
        {
            StartModuleEventStream(module.Module.Manifest.Id);
        }

        var evt = _eventBus.Publish("runner", "module.eventPump.started", new JsonObject
        {
            ["moduleCount"] = EnabledModules().Count
        });
        _logRouter.Append("runner", "runner", "info", "Module event pump started.", eventSeq: evt.Seq);
    }

    public async ValueTask StopModuleEventPumpAsync()
    {
        var pump = _moduleEventPumpCancellation;
        if (pump is null)
        {
            return;
        }

        _moduleEventPumpCancellation = null;
        pump.Cancel();
        var cancellations = _moduleEventPumpCancellations.Values.ToArray();
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stream task may have completed and disposed its linked token first.
            }
        }

        var tasks = _moduleEventPumpTasks.Values.ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // Expected while stopping the pump.
            }
        }

        foreach (var cancellation in cancellations)
        {
            cancellation.Dispose();
        }

        _moduleEventPumpTasks.Clear();
        _moduleEventPumpCancellations.Clear();
        _moduleEventPumpFailures.Clear();
        pump.Dispose();
        var evt = _eventBus.Publish("runner", "module.eventPump.stopped", new JsonObject());
        _logRouter.Append("runner", "runner", "info", "Module event pump stopped.", eventSeq: evt.Seq);
    }

    public ModuleDetailSnapshot GetModuleDetail(string moduleId)
    {
        var module = _packageRegistry.FindModule(moduleId)
            ?? throw new KeyNotFoundException($"Module '{moduleId}' was not found.");

        return new ModuleDetailSnapshot(
            module.Module.Manifest.Id,
            module.Module.Manifest.PackageId,
            module.Module.Manifest.DisplayName,
            module.Status.State,
            module.Status.Summary,
            module.Status.Checks,
            module.Module.Manifest.Permissions
                .Select(permission => new ModulePermissionSnapshot(
                    permission.Id,
                    permission.Level,
                    permission.Capability ?? "",
                    permission.Reason))
                .ToArray(),
            module.Module.Manifest.Requires
                .Select(requirement => new ModuleRequirementSnapshot(
                    requirement.Capability,
                    requirement.Required,
                    requirement.Reason ?? ""))
                .ToArray());
    }

    public IReadOnlyList<Sdk.MptCommandDescriptor> ListCommands(string? query)
    {
        return _commandIndex.Search(query);
    }

    public IReadOnlyList<RuntimeHotkeyBinding> ListHotkeyBindings()
    {
        return EnumerateHotkeyBindings(includeDisabledOverrides: false)
            .OrderBy(binding => binding.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RuntimeHotkeyDiagnostic> ListHotkeyDiagnostics()
    {
        var moduleBindings = EnumerateHotkeyBindings(includeDisabledOverrides: true).ToArray();
        var bindings = moduleBindings
            .Select(binding => new RuntimeHotkeyBinding(
                binding.Id,
                binding.ModuleId,
                binding.CommandId,
                NormalizeGesture(binding.Gesture),
                binding.Scope,
                binding.Reason,
                binding.DefaultGesture,
                binding.IsDefault,
                binding.Disabled,
                binding.CommandArgsJson))
            .Concat(
            [
                new RuntimeHotkeyBinding(
                    "command-palette",
                    "runner",
                    "shell.command-palette.open",
                    "Ctrl+Alt+Space",
                    "runner",
                    "Open the command palette.",
                    "Ctrl+Alt+Space")
            ])
            .ToArray();
        var conflicts = bindings
            .Where(binding => !binding.Disabled)
            .GroupBy(binding => NormalizeGesture(binding.Gesture), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Id).ToArray(), StringComparer.OrdinalIgnoreCase);

        return bindings
            .OrderBy(binding => binding.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(binding => binding.Id, StringComparer.OrdinalIgnoreCase)
            .Select(binding =>
            {
                var conflict = conflicts.TryGetValue(NormalizeGesture(binding.Gesture), out var ids);
                var peers = conflict && ids is not null
                    ? string.Join(", ", ids.Where(id => !string.Equals(id, binding.Id, StringComparison.OrdinalIgnoreCase)))
                    : "";
                return new RuntimeHotkeyDiagnostic(
                    binding.Id,
                    binding.ModuleId,
                    binding.CommandId,
                    NormalizeGesture(binding.Gesture),
                    binding.Scope,
                    binding.Disabled ? "disabled" : conflict ? "conflict" : "ok",
                    binding.Disabled
                        ? "Hotkey is disabled and will not be registered."
                        : conflict
                        ? $"Gesture {binding.Gesture} also maps to {peers}."
                        : "Gesture is available in the runtime binding table.",
                    binding.IsDefault,
                    string.IsNullOrWhiteSpace(binding.DefaultGesture) ? NormalizeGesture(binding.Gesture) : NormalizeGesture(binding.DefaultGesture));
            })
            .ToArray();
    }

    private IEnumerable<RuntimeHotkeyBinding> EnumerateHotkeyBindings(bool includeDisabledOverrides)
    {
        foreach (var module in EnabledModules())
        {
            var moduleId = module.Module.Manifest.Id;
            foreach (var hotkey in module.Module.Manifest.Hotkeys)
            {
                if (string.IsNullOrWhiteSpace(hotkey.Id) ||
                    string.IsNullOrWhiteSpace(hotkey.Default) ||
                    string.IsNullOrWhiteSpace(hotkey.CommandId))
                {
                    continue;
                }

                var stored = _hotkeyStore.Get(moduleId, hotkey.Id);
                var disabled = stored?.Disabled ?? !hotkey.EnabledByDefault;
                if (disabled && !includeDisabledOverrides)
                {
                    continue;
                }

                var gesture = stored is null || string.IsNullOrWhiteSpace(stored.Gesture)
                    ? hotkey.Default
                    : stored.Gesture;
                yield return new RuntimeHotkeyBinding(
                    RuntimeHotkeyId(moduleId, hotkey.Id),
                    moduleId,
                    hotkey.CommandId,
                    gesture,
                    string.IsNullOrWhiteSpace(hotkey.Scope) ? "module" : hotkey.Scope,
                    string.IsNullOrWhiteSpace(hotkey.Reason) ? $"Invoke {hotkey.CommandId}." : hotkey.Reason,
                    hotkey.Default,
                    stored is null,
                    disabled,
                    stored?.CommandArgsJson ?? "{}");
            }
        }
    }

    public Sdk.CommandExecutionResult ExecuteCommand(Sdk.CommandRequest request)
    {
        return ExecuteCommandAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<Sdk.CommandExecutionResult> ExecuteCommandAsync(Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        return _executions.GetOrAdd(request.InvocationId, () => ExecuteCommandTrackedAsync(request, cancellationToken));
    }

    public async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamAsync(Sdk.CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sequence = 1;
        yield return new CommandProgressEvent(
            request.InvocationId,
            request.CommandId,
            "accepted",
            $"Command {request.CommandId} accepted.",
            sequence,
            false);

        sequence++;
        yield return new CommandProgressEvent(
            request.InvocationId,
            request.CommandId,
            "running",
            $"Command {request.CommandId} is running.",
            sequence,
            false);

        await foreach (var evt in ExecuteCommandStreamInternalAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            sequence++;
            yield return evt with { Sequence = sequence };
        }
    }

    private async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamInternalAsync(Sdk.CommandRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var command = _commandIndex.Find(request.CommandId);
        var executionType = command?.Execution?["type"]?.GetValue<string>() ?? command?.Kind ?? "";
        if (command is null || !IsTransportExecutionType(executionType))
        {
            var fallbackResult = await ExecuteCommandAsync(request, cancellationToken);
            yield return FinalProgress(fallbackResult);
            yield break;
        }

        _commandHistory.Add(request, command, "accepted");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executionCancellations[request.InvocationId] = linkedCancellation;
        Sdk.CommandExecutionResult? result = null;
        var terminalEmitted = false;
        try
        {
            ExpireProcessPolicies();
            var evt = _eventBus.Publish(command.ModuleId, "command.executed", new JsonObject
            {
                ["commandId"] = request.CommandId,
                ["invocationId"] = request.InvocationId,
                ["streamed"] = true
            });

            var route = ResolveTransportCommandRoute(command, request);
            if (route.Failure is not null)
            {
                result = route.Failure;
            }
            else
            {
                var record = route.Record!;
                var runtime = _transportRuntimes[record.Entrypoint!.Kind];
                var context = CreateModuleContext(record);
                _executionRuntimeTargets[request.InvocationId] = new CommandRuntimeCancellationTarget(record, runtime, context);
                await ApplyPersistedSettingsIfPresentAsync(record, runtime, context, linkedCancellation.Token);
                await using var stream = runtime.ExecuteCommandStreamAsync(record, context, request, linkedCancellation.Token).GetAsyncEnumerator(linkedCancellation.Token);
                while (true)
                {
                    CommandProgressEvent current;
                    try
                    {
                        if (!await stream.MoveNextAsync())
                        {
                            break;
                        }

                        current = stream.Current;
                    }
                    catch (OperationCanceledException)
                    {
                        result = IsCommandCancelled(request.InvocationId)
                            ? Cancelled(request)
                            : Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
                        break;
                    }
                    catch (Exception ex)
                    {
                        result = Failed(request, MptErrorCodes.RuntimeUnavailable, LogRouter.Redact(ex.Message), retryable: true);
                        break;
                    }

                    if (current.Terminal)
                    {
                        terminalEmitted = current.FinalResult is not null;
                        result = current.FinalResult;
                    }

                    yield return current;
                }

                if (result is null)
                {
                    result = Succeeded(request, $"Command stream completed for {request.CommandId}.");
                }

                _logRouter.Append(record.Module.Manifest.PackageId, command.ModuleId, result.Success ? "info" : "error", result.Success ? result.Output : result.Error?.Message ?? "Command failed.", request.InvocationId, evt.Seq);
            }
        }
        finally
        {
            _executionCancellations.TryRemove(request.InvocationId, out _);
            _executionRuntimeTargets.TryRemove(request.InvocationId, out _);
            _cancelledInvocations.TryRemove(request.InvocationId, out _);
        }

        if (result is not null)
        {
            _commandHistory.Complete(result);
            if (!terminalEmitted)
            {
                yield return FinalProgress(result);
            }
        }
    }

    public CommandCancellationResult CancelCommand(string invocationId)
    {
        return CancelCommandAsync(invocationId, CancellationToken.None).GetAwaiter().GetResult();
    }

    public async Task<CommandCancellationResult> CancelCommandAsync(string invocationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invocationId))
        {
            return new CommandCancellationResult(false, "", "validation-failed", "invocationId is required.");
        }

        if (_executionCancellations.TryGetValue(invocationId, out var cancellationSource))
        {
            _cancelledInvocations[invocationId] = 1;
            cancellationSource.Cancel();
            if (_executionRuntimeTargets.TryGetValue(invocationId, out var target))
            {
                try
                {
                    var module = await target.Runtime.CancelCommandAsync(target.Module, target.Context, invocationId, cancellationToken);
                    return module.Accepted
                        ? new CommandCancellationResult(true, invocationId, "cancelling", $"Cancellation requested for {invocationId}; module accepted cancellation.")
                        : new CommandCancellationResult(true, invocationId, "host-cancelling-module-rejected", $"Host cancellation requested for {invocationId}; module response: {module.Message}");
                }
                catch (Exception ex)
                {
                    return new CommandCancellationResult(true, invocationId, "host-cancelling-module-error", $"Host cancellation requested for {invocationId}; module cancellation failed: {LogRouter.Redact(ex.Message)}");
                }
            }

            return new CommandCancellationResult(true, invocationId, "cancelling", $"Cancellation requested for {invocationId}.");
        }

        if (_executions.IsCompleted(invocationId))
        {
            return new CommandCancellationResult(false, invocationId, "completed", $"Invocation {invocationId} has already completed.");
        }

        return new CommandCancellationResult(false, invocationId, "not-found", $"Invocation {invocationId} is not running.");
    }

    private async Task<Sdk.CommandExecutionResult> ExecuteCommandTrackedAsync(Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _executionCancellations[request.InvocationId] = linkedCancellation;
        try
        {
            return await ExecuteCommandInternalAsync(request, linkedCancellation.Token);
        }
        finally
        {
            _executionCancellations.TryRemove(request.InvocationId, out _);
            _cancelledInvocations.TryRemove(request.InvocationId, out _);
        }
    }

    private async Task<Sdk.CommandExecutionResult> ExecuteCommandInternalAsync(Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        var command = _commandIndex.Find(request.CommandId);
        _commandHistory.Add(request, command, "accepted");
        if (command is null)
        {
            var failed = new Sdk.CommandExecutionResult(
                request.InvocationId,
                request.CommandId,
                "failed",
                false,
                "",
                new Sdk.MptRuntimeError(MptErrorCodes.NotFound, $"Command '{request.CommandId}' was not found."));
            _commandHistory.Complete(failed);
            _logRouter.Append("unknown", "unknown", "error", failed.Error!.Message, request.InvocationId);
            return failed;
        }

        ExpireProcessPolicies();
        var evt = _eventBus.Publish(command.ModuleId, "command.executed", new JsonObject
        {
            ["commandId"] = request.CommandId,
            ["invocationId"] = request.InvocationId
        });

        Sdk.CommandExecutionResult result;
        try
        {
            result = await ExecuteCommandCoreAsync(command, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            result = IsCommandCancelled(request.InvocationId)
                ? Cancelled(request)
                : Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
        }

        _commandHistory.Complete(result);

        var record = _packageRegistry.FindModule(command.ModuleId);
        _logRouter.Append(record?.Module.Manifest.PackageId ?? command.ModuleId, command.ModuleId, result.Success ? "info" : "error", result.Success ? result.Output : result.Error?.Message ?? "Command failed.", request.InvocationId, evt.Seq);
        return result;
    }

    private async Task<Sdk.CommandExecutionResult> ExecuteCommandCoreAsync(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, command.TimeoutMs)));

        var executionType = command.Execution?["type"]?.GetValue<string>() ?? command.Kind;
        return executionType switch
        {
            "open" => Failed(
                request,
                MptErrorCodes.UnsupportedTransport,
                $"Command {request.CommandId} is a Shell navigation action and must be handled by MyPowerTools Shell.",
                retryable: false),
            "host.status.refresh" => await RefreshCommandAsync(command, request, timeout.Token),
            "host.logs.tail" => TailLogsCommand(command, request),
            "host.settings.read" => SettingsReadCommand(command, request),
            "host.notification.test" => NotificationTestCommand(command, request),
            "http.request" => await HttpRequestCommandAsync(command, request, timeout.Token),
            "broker.request" => BrokerRequestCommand(command, request),
            _ => await TransportCommandAsync(command, request, timeout.Token)
        };
    }

    private static bool IsTransportExecutionType(string executionType)
    {
        return executionType is not
            "open" and not
            "host.status.refresh" and not
            "host.logs.tail" and not
            "host.settings.read" and not
            "host.notification.test" and not
            "http.request" and not
            "broker.request";
    }

    private async Task<Sdk.CommandExecutionResult> RefreshCommandAsync(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        await RefreshHealthAsync(cancellationToken);
        return Succeeded(request, $"Status refreshed for {command.ModuleId}.");
    }

    private Sdk.CommandExecutionResult TailLogsCommand(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var logs = TailLogs(command.ModuleId);
        return Succeeded(request, $"{logs.Count} log records available for {command.ModuleId}.");
    }

    private Sdk.CommandExecutionResult SettingsReadCommand(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var settings = GetSettings(command.ModuleId);
        return Succeeded(request, $"Settings revision {settings.Revision} loaded for {command.ModuleId}.");
    }

    private Sdk.CommandExecutionResult NotificationTestCommand(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var notification = PublishNotification(command.ModuleId, "info", command.Title, command.Subtitle);
        return Succeeded(request, $"Notification {notification.Id} created.");
    }

    private async Task<Sdk.CommandExecutionResult> HttpRequestCommandAsync(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        var module = _packageRegistry.FindModule(command.ModuleId);
        var entrypoint = module?.Entrypoint;
        if (module is null || entrypoint?.Kind != "http" || string.IsNullOrWhiteSpace(entrypoint.EndpointAddress))
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, $"Module {command.ModuleId} has no active HTTP facade.");
        }

        var method = command.Execution?["method"]?.GetValue<string>() ?? "GET";
        var path = command.Execution?["path"]?.GetValue<string>() ?? entrypoint.HealthPath ?? "/";
        var uri = new Uri(new Uri(entrypoint.EndpointAddress.TrimEnd('/') + "/"), path.TrimStart('/'));
        using var message = new HttpRequestMessage(new HttpMethod(method), uri);
        if (command.Execution?["body"] is JsonObject requestBody)
        {
            message.Content = JsonContent.Create(requestBody);
        }

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            var responseBody = LogRouter.Redact(await response.Content.ReadAsStringAsync(cancellationToken));
            return response.IsSuccessStatusCode
                ? Succeeded(request, $"HTTP {(int)response.StatusCode}: {Trim(responseBody)}")
                : Failed(request, MptErrorCodes.RuntimeUnavailable, $"HTTP {(int)response.StatusCode}: {Trim(responseBody)}", retryable: true);
        }
        catch (OperationCanceledException)
        {
            return IsCommandCancelled(request.InvocationId)
                ? Cancelled(request)
                : Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, LogRouter.Redact(ex.Message), retryable: true);
        }
    }

    private Sdk.CommandExecutionResult BrokerRequestCommand(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var actionId = command.Execution?["actionId"]?.GetValue<string>() ?? command.Id;
        var reason = command.Execution?["reason"]?.GetValue<string>() ?? command.Subtitle;
        var scope = command.Execution?["scope"]?.GetValue<string>() ?? command.ModuleId;
        var details = new JsonObject
        {
            ["moduleId"] = command.ModuleId,
            ["actionId"] = actionId,
            ["scope"] = scope,
            ["reason"] = reason
        };
        return new Sdk.CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "permission-required",
            false,
            "",
            new Sdk.MptRuntimeError(MptErrorCodes.PermissionRequired, $"Broker approval required for {actionId}.", false, details));
    }

    private async Task<Sdk.CommandExecutionResult> TransportCommandAsync(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        var route = ResolveTransportCommandRoute(command, request);
        if (route.Failure is not null)
        {
            return route.Failure;
        }

        var record = route.Record!;
        var runtime = _transportRuntimes[record.Entrypoint!.Kind];

        try
        {
            var context = CreateModuleContext(record);
            _executionRuntimeTargets[request.InvocationId] = new CommandRuntimeCancellationTarget(record, runtime, context);
            await ApplyPersistedSettingsIfPresentAsync(record, runtime, context, cancellationToken);
            return await runtime.ExecuteCommandAsync(record, context, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return IsCommandCancelled(request.InvocationId)
                ? Cancelled(request)
                : Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, LogRouter.Redact(ex.Message), retryable: true);
        }
        finally
        {
            _executionRuntimeTargets.TryRemove(request.InvocationId, out _);
        }
    }

    private CommandRouteResolution ResolveTransportCommandRoute(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request)
    {
        var record = _packageRegistry.FindModule(command.ModuleId);
        if (record is null)
        {
            return new CommandRouteResolution(null, Failed(request, MptErrorCodes.NotFound, $"Module {command.ModuleId} was not found."));
        }

        var policy = RuntimeOperationPolicy.Evaluate(command, request, record);
        if (policy.IsAllowed && TryGetTransportRuntime(record, out _))
        {
            return new CommandRouteResolution(record, null);
        }

        var constraints = RuntimeOperationPolicy.GetConstraints(command, request);
        if (constraints.Count > 0)
        {
            var routeSelection = _packageRegistry.SelectCommandEntrypoint(
                record,
                command,
                request,
                _transportRuntimes.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

            if (routeSelection.Entrypoint is not null)
            {
                var routedRecord = record with
                {
                    Entrypoint = routeSelection.Entrypoint,
                    TransportDiagnostics = routeSelection.Diagnostics
                };
                var routedPolicy = RuntimeOperationPolicy.Evaluate(command, request, routedRecord, routeSelection.Entrypoint);
                if (routedPolicy.IsAllowed && TryGetTransportRuntime(routedRecord, out _))
                {
                    return new CommandRouteResolution(routedRecord, null);
                }
            }

            if (!policy.IsAllowed)
            {
                var routeUnavailableReason = DescribeRouteUnavailable(routeSelection);
                var blocked = RuntimeOperationPolicyDecision.Blocked(
                    command,
                    record,
                    record.Entrypoint,
                    policy.Constraints,
                    policy.Violations,
                    routeUnavailableReason,
                    routeSelection.Diagnostics);
                return new CommandRouteResolution(null, Failed(request, MptErrorCodes.RuntimePolicyBlocked, blocked.Message, details: blocked.Details));
            }
        }

        if (!policy.IsAllowed)
        {
            return new CommandRouteResolution(null, Failed(request, MptErrorCodes.RuntimePolicyBlocked, policy.Message, details: policy.Details));
        }

        var transport = record.Entrypoint?.Kind ?? "none";
        return new CommandRouteResolution(null, Failed(request, MptErrorCodes.UnsupportedTransport, $"No transport runtime is registered for {command.ModuleId} via {transport}."));
    }

    private static string DescribeRouteUnavailable(TransportSelectionResult selection)
    {
        var selected = selection.Diagnostics.LastOrDefault(diagnostic => diagnostic.State == "selected");
        if (selected is not null)
        {
            return selected.Reason;
        }

        var skipped = selection.Diagnostics.LastOrDefault(diagnostic => diagnostic.State == "skipped");
        return skipped?.Reason ?? "No alternate command route satisfied the required runtime policy.";
    }

    private async Task EnableModuleResourcesAsync(RuntimeModuleRecord module, CancellationToken cancellationToken)
    {
        if (!TryGetTransportRuntime(module, out var runtime))
        {
            return;
        }

        var context = CreateModuleContext(module);
        try
        {
            await runtime.EnableModuleAsync(module, context, cancellationToken);
            await ApplyPersistedSettingsIfPresentAsync(module, runtime, context, cancellationToken);
            var status = await runtime.GetStatusAsync(module, context, cancellationToken);
            if (status is not null)
            {
                RecordModuleStatus(module, status);
            }

            var commands = await runtime.ListCommandsAsync(module, context, cancellationToken);
            ReplaceDynamicCommands(module.Module.Manifest.Id, commands.Select(command => NormalizeDynamicCommand(module, command)).ToArray());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = LogRouter.Redact(ex.Message);
            RecordModuleStatus(module, Degraded(module, message));
            _logRouter.Append(module.Module.Manifest.PackageId, module.Module.Manifest.Id, "error", message);
        }
    }

    private async Task DisableModuleResourcesAsync(RuntimeModuleRecord module, CancellationToken cancellationToken)
    {
        if (!TryGetTransportRuntime(module, out var runtime))
        {
            return;
        }

        var context = CreateModuleContext(module);
        var enabledModuleIds = EnabledModules()
            .Select(item => item.Module.Manifest.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            await runtime.DisableModuleAsync(module, context, enabledModuleIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = LogRouter.Redact(ex.Message);
            _logRouter.Append(module.Module.Manifest.PackageId, module.Module.Manifest.Id, "error", $"Module disable cleanup failed: {message}");
            _eventBus.Publish(module.Module.Manifest.Id, "module.disable.cleanup.failed", new JsonObject
            {
                ["message"] = message
            });
        }
    }

    private void ReplaceDynamicCommands(string moduleId, IReadOnlyList<Sdk.MptCommandDescriptor> commands)
    {
        _dynamicCommands = _dynamicCommands
            .Where(command => !string.Equals(command.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
            .Concat(commands)
            .ToArray();
    }

    private void StartModuleEventStream(string moduleId)
    {
        var pump = _moduleEventPumpCancellation;
        if (pump is null || pump.IsCancellationRequested)
        {
            return;
        }

        _moduleEventPumpTasks.GetOrAdd(moduleId, id => Task.Run(() => RunModuleEventStreamAsync(id, pump.Token), CancellationToken.None));
    }

    private async ValueTask StopModuleEventStreamAsync(string moduleId)
    {
        CancellationTokenSource? cancellation = null;
        if (_moduleEventPumpCancellations.TryRemove(moduleId, out cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stream task may have completed and disposed its linked token first.
            }
        }

        if (_moduleEventPumpTasks.TryRemove(moduleId, out var task))
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Expected when disabling a module or stopping the pump.
            }
        }

        cancellation?.Dispose();
        _moduleEventPumpFailures.TryRemove(moduleId, out _);
    }

    private async Task RunModuleEventStreamAsync(string moduleId, CancellationToken pumpToken)
    {
        while (!pumpToken.IsCancellationRequested)
        {
            var module = _packageRegistry.FindModule(moduleId);
            if (module is null || string.Equals(module.Status.State, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryGetTransportRuntime(module, out var runtime))
            {
                await DelayEventPumpRetryAsync(moduleId, pumpToken);
                continue;
            }

            var moduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(pumpToken);
            _moduleEventPumpCancellations[moduleId] = moduleCancellation;
            try
            {
                var context = CreateModuleContext(module);
                var cursor = new Sdk.EventCursor(ResolveEventStreamCursor(module));
                await foreach (var evt in runtime.SubscribeEventsAsync(module, context, cursor, moduleCancellation.Token).WithCancellation(moduleCancellation.Token))
                {
                    PublishModuleEvent(evt);
                }

                _moduleEventPumpFailures[moduleId] = 0;
                await Task.Delay(TimeSpan.FromSeconds(1), pumpToken);
            }
            catch (OperationCanceledException) when (moduleCancellation.IsCancellationRequested || pumpToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var failureCount = _moduleEventPumpFailures.AddOrUpdate(moduleId, 1, (_, current) => current + 1);
                var message = LogRouter.Redact(ex.Message);
                _eventBus.Publish(moduleId, "module.eventStream.failed", new JsonObject
                {
                    ["failureCount"] = failureCount,
                    ["message"] = message
                });
                _logRouter.Append(module.Module.Manifest.PackageId, moduleId, "error", $"Module event stream failed: {message}");
                await DelayEventPumpRetryAsync(moduleId, pumpToken);
            }
            finally
            {
                _moduleEventPumpCancellations.TryRemove(moduleId, out _);
                moduleCancellation.Dispose();
            }
        }
    }

    private bool _restartLifetimeCursorsReset;

    private void ResetRestartLifetimeCursors()
    {
        // Runs once per Runner process (the first catalog load). In-proc module
        // instances share the Runner's lifetime: every Runner restart starts their
        // event sequence over at 1, so a persisted cursor from a previous
        // generation would silently drop every event until the sequence climbs
        // past the stale high-water mark — for low-frequency publishers (e.g.
        // paste-image uploads) that means never. Sidecar/external transports and
        // their persisted cursors are untouched: their modules can outlive a
        // Runner restart, and replay protection across restarts must hold. Later
        // catalog refreshes keep the in-memory cursors: the host fan-in re-stamps
        // events with a host-global sequence that advances across refreshes, and
        // the cursor dedup relies on that continuity.
        if (_restartLifetimeCursorsReset)
        {
            return;
        }

        _restartLifetimeCursorsReset = true;
        foreach (var module in _packageRegistry.Modules)
        {
            if (string.Equals(module.Entrypoint?.Kind, "inproc-dotnet", StringComparison.OrdinalIgnoreCase))
            {
                _moduleEventCursors.TryRemove(module.Module.Manifest.Id, out _);
            }
        }
    }

    private ulong ResolveEventStreamCursor(RuntimeModuleRecord module)
    {
        // Cursor high-water marks advance as events are published; stale cursors
        // from previous Runner generations are cleared in Load for every transport
        // whose modules restart with the Runner (see ResetRestartLifetimeCursors).
        return _moduleEventCursors.GetValueOrDefault(module.Module.Manifest.Id);
    }

    private async Task DelayEventPumpRetryAsync(string moduleId, CancellationToken cancellationToken)
    {
        var failureCount = _moduleEventPumpFailures.GetValueOrDefault(moduleId);
        var delaySeconds = Math.Min(30, Math.Max(1, failureCount == 0 ? 1 : 1 << Math.Min(5, failureCount - 1)));
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
    }

    private bool PublishModuleEvent(Sdk.MptModuleEvent evt)
    {
        var moduleId = evt.ModuleId;
        var lastSeen = _moduleEventCursors.GetValueOrDefault(moduleId);
        if (evt.Seq <= lastSeen)
        {
            return false;
        }

        _moduleEventCursors[moduleId] = Math.Max(lastSeen, evt.Seq);
        try
        {
            _moduleEventStore.Record(evt);
        }
        catch (Exception ex)
        {
            _logRouter.Append(evt.ModuleId, evt.ModuleId, "warning", $"Module event persistence failed: {LogRouter.Redact(ex.Message)}");
        }

        var payload = (evt.Payload.DeepClone() as JsonObject) ?? new JsonObject();
        payload["moduleEventSeq"] = evt.Seq;
        payload["moduleEventTime"] = evt.Time.ToString("O");
        _eventBus.Publish(evt.ModuleId, evt.Type, payload);
        PublishNotificationForModuleEvent(evt);
        return true;
    }

    private async ValueTask ApplyPersistedSettingsIfPresentAsync(
        RuntimeModuleRecord module,
        IModuleTransportRuntime runtime,
        Sdk.ModuleContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = _settingsStore.Get(module.Module.Manifest.Id);
        if (snapshot.Values.Count == 0)
        {
            return;
        }

        try
        {
            await runtime.ApplySettingsAsync(module, context, snapshot, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = $"Persisted settings apply failed: {LogRouter.Redact(ex.Message)}";
            RecordModuleStatus(module, Degraded(module, message));
            _logRouter.Append(module.Module.Manifest.PackageId, module.Module.Manifest.Id, "error", message);
        }
    }

    private static Sdk.CommandExecutionResult Succeeded(Sdk.CommandRequest request, string output)
    {
        return new Sdk.CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandProgressEvent FinalProgress(Sdk.CommandExecutionResult result)
    {
        return new CommandProgressEvent(
            result.InvocationId,
            result.CommandId,
            result.State,
            result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            0,
            true,
            result);
    }

    private static Sdk.CommandExecutionResult Failed(Sdk.CommandRequest request, string code, string message, bool retryable = false, JsonObject? details = null)
    {
        return new Sdk.CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new Sdk.MptRuntimeError(code, message, retryable, details));
    }

    private static Sdk.CommandExecutionResult Cancelled(Sdk.CommandRequest request)
    {
        return new Sdk.CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "cancelled",
            false,
            "",
            new Sdk.MptRuntimeError(MptErrorCodes.CommandCancelled, $"Command {request.CommandId} was cancelled."));
    }

    private bool IsCommandCancelled(string invocationId)
    {
        return _cancelledInvocations.ContainsKey(invocationId);
    }

    private bool TryGetTransportRuntime(RuntimeModuleRecord module, out IModuleTransportRuntime runtime)
    {
        if (module.Entrypoint is not null && _transportRuntimes.TryGetValue(module.Entrypoint.Kind, out runtime!))
        {
            return true;
        }

        runtime = null!;
        return false;
    }

    private async Task<Sdk.ModuleStatusSnapshot?> CheckTransportHealthAsync(RuntimeModuleRecord module, CancellationToken cancellationToken)
    {
        if (!TryGetTransportRuntime(module, out var runtime))
        {
            return null;
        }

        try
        {
            return await runtime.GetStatusAsync(module, CreateModuleContext(module), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Degraded(module, LogRouter.Redact(ex.Message));
        }
    }

    private Sdk.ModuleContext CreateModuleContext(RuntimeModuleRecord module)
    {
        var moduleStateRoot = Path.Combine(_paths.State, "modules", module.Module.Manifest.Id);
        var dataDir = Path.Combine(moduleStateRoot, "data");
        var cacheDir = Path.Combine(moduleStateRoot, "cache");
        var logDir = Path.Combine(_paths.Logs, module.Module.Manifest.Id);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(logDir);

        var providers = new Dictionary<string, object>(_capabilityProviders, StringComparer.OrdinalIgnoreCase)
        {
            ["runtime.hotkeys"] = new ModuleHotkeyConfigurationService(this, module)
        };
        return new Sdk.ModuleContext(
            ProtocolConstants.HostVersion,
            ProtocolConstants.ModuleProtocolVersion,
            module.Module.Manifest.PackageId,
            module.Module.Manifest.Id,
            dataDir,
            cacheDir,
            logDir,
            PlatformId.Current().Rid,
            module.Module.Manifest.Capabilities,
            providers);
    }

    private void ApplyModuleHotkeyConfiguration(
        RuntimeModuleRecord module,
        IReadOnlyList<ModuleHotkeyConfiguration> hotkeys)
    {
        var manifests = module.Module.Manifest.Hotkeys.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var hotkey in hotkeys)
        {
            if (!manifests.TryGetValue(hotkey.Id, out var manifest))
            {
                throw new InvalidOperationException($"Module '{module.Module.Manifest.Id}' attempted to configure undeclared hotkey '{hotkey.Id}'.");
            }

            var gesture = string.IsNullOrWhiteSpace(hotkey.Gesture) ? manifest.Default : hotkey.Gesture;
            _hotkeyStore.Set(
                module.Module.Manifest.Id,
                hotkey.Id,
                gesture,
                disabled: !hotkey.Enabled,
                commandArgsJson: hotkey.CommandArgsJson);
        }

        _eventBus.Publish(module.Module.Manifest.Id, "hotkeys.updated", new JsonObject
        {
            ["hotkeyEditCount"] = hotkeys.Count,
            ["source"] = "module-settings-import"
        });
    }

    private sealed class ModuleHotkeyConfigurationService(
        MptHostRuntime runtime,
        RuntimeModuleRecord module) : IModuleHotkeyConfigurationService
    {
        public Task ApplyAsync(IReadOnlyList<ModuleHotkeyConfiguration> hotkeys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runtime.ApplyModuleHotkeyConfiguration(module, hotkeys);
            return Task.CompletedTask;
        }
    }

    private static Sdk.MptCommandDescriptor NormalizeDynamicCommand(RuntimeModuleRecord module, Sdk.MptCommandDescriptor command)
    {
        var execution = command.Execution?.DeepClone() as JsonObject
            ?? new JsonObject { ["type"] = "module.execute" };
        var parameters = command.Parameters?
            .Select(parameter => new Sdk.CommandParameterDescriptor(
                parameter.Id,
                parameter.Label,
                parameter.Type,
                parameter.Required,
                parameter.DefaultValue))
            .ToArray();
        var constraints = command.Constraints?.Select(value => string.Concat(value)).ToArray();

        // Module records are extensible and their collection-expression backing
        // types may live in the collectible plugin ALC. Persist only host-owned
        // DTOs so command indexing cannot pin an unloaded module assembly.
        return new Sdk.MptCommandDescriptor(
            command.Id,
            string.IsNullOrWhiteSpace(command.ModuleId) ? module.Module.Manifest.Id : command.ModuleId,
            command.Title,
            command.Subtitle,
            command.Kind,
            command.RequiresElevation,
            command.Icon,
            command.DangerLevel,
            command.Category,
            command.TimeoutMs <= 0 ? 30000 : command.TimeoutMs,
            execution,
            parameters,
            constraints,
            command.SupportsProgress,
            command.SupportsCancellation);
    }

    private static Sdk.ModuleStatusSnapshot DetachModuleStatus(Sdk.ModuleStatusSnapshot status)
    {
        var checks = status.Checks
            .Select(check => new Sdk.HealthCheckSnapshot(
                check.Id,
                check.Label,
                check.Ok,
                check.Message))
            .ToArray();
        return new Sdk.ModuleStatusSnapshot(
            status.ModuleId,
            status.State,
            status.Summary,
            status.UpdatedAt,
            checks,
            status.EventSeq);
    }

    private static Sdk.ModuleStatusSnapshot Degraded(RuntimeModuleRecord module, string message)
    {
        return new Sdk.ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            "degraded",
            message,
            DateTimeOffset.UtcNow,
            [
                new Sdk.HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new Sdk.HealthCheckSnapshot("transport", "Transport runtime", false, message)
            ],
            module.Status.EventSeq);
    }

    private void ApplyPersistedModuleState()
    {
        foreach (var module in _packageRegistry.Modules.ToArray())
        {
            if (module.Module.LoadError is not null)
            {
                // The module status already reflects the load failure; an initial or
                // persisted status must not hide it (dashboards render this state).
                continue;
            }

            var status = _moduleStateStore.IsEnabled(module.Module.Manifest.Id)
                ? InitialStatus(module)
                : DisabledStatus(module);
            _packageRegistry.UpdateStatus(module.Module.Manifest.Id, status);
        }
    }

    private void ObserveAllModules()
    {
        foreach (var module in _packageRegistry.Modules)
        {
            _moduleSupervisor.Observe(module, module.Status);
        }
    }

    private void RecordModuleStatus(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleStatusSnapshot status)
    {
        status = DetachModuleStatus(status);
        var previousState = module.Status.State;
        _packageRegistry.UpdateStatus(module.Module.Manifest.Id, status);
        var updated = _packageRegistry.FindModule(module.Module.Manifest.Id) ?? module with { Status = status };
        var supervision = _moduleSupervisor.Observe(updated, status);
        if (!string.Equals(previousState, status.State, StringComparison.OrdinalIgnoreCase))
        {
            var evt = _eventBus.Publish(module.Module.Manifest.Id, "module.health.changed", new JsonObject
            {
                ["state"] = status.State,
                ["summary"] = status.Summary,
                ["supervisorState"] = supervision.SupervisorState,
                ["consecutiveFailures"] = supervision.ConsecutiveFailureCount
            });
            _logRouter.Append(
                module.Module.Manifest.PackageId,
                module.Module.Manifest.Id,
                supervision.ConsecutiveFailureCount > 0 ? "warning" : "info",
                $"{status.State}: {status.Summary}",
                eventSeq: evt.Seq);
        }
    }

    private void RegisterProcessPoolsAndApplyPolicies()
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in EnabledModules())
        {
            if (module.Entrypoint is null ||
                !_transportRuntimes.TryGetValue(module.Entrypoint.Kind, out var runtime) ||
                runtime is not IModuleTransportDiagnosticsProvider provider)
            {
                continue;
            }

            var poolKey = provider.GetProcessPoolKey(module);
            provider.RegisterProcessPool(poolKey, module.Module.Manifest.Id);
            registered.Add($"{runtime.Kind}:{poolKey}");
        }

        ExpireProcessPolicies();
        foreach (var policy in _processPolicyStore.CurrentPolicies())
        {
            if (!_transportRuntimes.TryGetValue(policy.TransportKind, out var runtime) ||
                runtime is not IModuleTransportDiagnosticsProvider provider ||
                !registered.Contains($"{policy.TransportKind}:{policy.PoolKey}"))
            {
                continue;
            }

            provider.ApplyRestartPolicy(policy.PoolKey, policy.RestartPolicy, policy.Reason, policy.UpdatedAt, policy.ExpiresAt);
        }
    }

    private void ExpireProcessPolicies()
    {
        var expired = _processPolicyStore.Expire(DateTimeOffset.UtcNow, "runtime.expiry");
        foreach (var policy in expired)
        {
            if (_transportRuntimes.TryGetValue(policy.TransportKind, out var runtime) &&
                runtime is IModuleTransportDiagnosticsProvider provider)
            {
                provider.ApplyRestartPolicy(policy.PoolKey, "automatic", $"Restart policy expired at {policy.ExpiresAt!.Value:O}.", DateTimeOffset.UtcNow, null);
            }

            var evt = _eventBus.Publish("runner", "runtime.process.policy.expired", new JsonObject
            {
                ["transportKind"] = policy.TransportKind,
                ["poolKey"] = policy.PoolKey,
                ["expiresAt"] = policy.ExpiresAt?.ToString("O")
            });
            _logRouter.Append("runner", "runner", "info", $"Restart policy expired for {policy.TransportKind} {policy.PoolKey}.", eventSeq: evt.Seq);
        }
    }

    private IReadOnlyList<RuntimeModuleRecord> EnabledModules()
    {
        return _packageRegistry.Modules
            .Where(module => module.Status.State != "disabled")
            .ToArray();
    }

    private void ClearDynamicCommandsForModules(IReadOnlyCollection<string> moduleIds)
    {
        if (moduleIds.Count == 0)
        {
            return;
        }

        var moduleIdSet = moduleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _dynamicCommands = _dynamicCommands
            .Where(command => !moduleIdSet.Contains(command.ModuleId))
            .ToArray();
        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands, _toolRegistry.Tools);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string RuntimeHotkeyId(string moduleId, string hotkeyId)
    {
        return hotkeyId.StartsWith(moduleId + ".", StringComparison.OrdinalIgnoreCase)
            ? hotkeyId
            : $"{moduleId}.{hotkeyId}";
    }

    private static string NormalizeGesture(string gesture)
    {
        return string.Join(
            "+",
            gesture
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Equals("control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : part)
                .Select(part => part.Equals("esc", StringComparison.OrdinalIgnoreCase) ? "Escape" : part));
    }

    private static Sdk.ModuleStatusSnapshot InitialStatus(RuntimeModuleRecord module)
    {
        var state = module.Package.IsDevelopmentTool
            ? "ready"
            : module.Entrypoint is null
            ? "unsupported"
            : module.Entrypoint.Kind == "inproc-dotnet"
                ? "indexed"
                : "stopped";
        var summary = module.Package.IsDevelopmentTool
            ? $"Development tool discovered from {module.Package.Directory}."
            : module.Entrypoint is null
            ? "No compatible runnable entrypoint for this platform."
            : $"Indexed via {module.Entrypoint.Kind}. {module.Entrypoint.SelectionReason}";

        return new Sdk.ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            state,
            summary,
            DateTimeOffset.UtcNow,
            [
                new Sdk.HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new Sdk.HealthCheckSnapshot(
                    "transport",
                    "Transport",
                    module.Package.IsDevelopmentTool || module.Entrypoint is not null,
                    module.Package.IsDevelopmentTool ? "Loose development tool" : module.Entrypoint?.SelectionReason ?? "No compatible entrypoint")
            ],
            module.Status.EventSeq);
    }

    private static Sdk.ModuleStatusSnapshot DisabledStatus(RuntimeModuleRecord module)
    {
        return new Sdk.ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            "disabled",
            "Module is disabled by user configuration.",
            DateTimeOffset.UtcNow,
            [
                new Sdk.HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new Sdk.HealthCheckSnapshot("module-state", "Module state", false, "Disabled by user configuration")
            ],
            module.Status.EventSeq);
    }

    private static string Trim(string value)
    {
        return value.Length <= 500 ? value : value[..500] + "...";
    }

    public Sdk.SettingsSnapshotDocument GetSettings(string moduleId)
    {
        EnsureModule(moduleId);
        return _settingsStore.Get(moduleId);
    }

    public async ValueTask<Sdk.SettingsSchemaDocument> GetSettingsSchemaAsync(string moduleId, CancellationToken cancellationToken)
    {
        var module = _packageRegistry.FindModule(moduleId)
            ?? throw new KeyNotFoundException($"Module '{moduleId}' was not found.");
        if (!TryGetTransportRuntime(module, out var runtime))
        {
            return new Sdk.SettingsSchemaDocument(moduleId, """{"type":"object","properties":{}}""");
        }

        return await runtime.GetSettingsSchemaAsync(module, CreateModuleContext(module), cancellationToken);
    }

    public Sdk.SettingsSnapshotDocument UpdateSettings(Sdk.SettingsPatch patch)
    {
        return UpdateSettingsWithApplyAsync(patch, CancellationToken.None).GetAwaiter().GetResult().Snapshot;
    }

    public async Task<SettingsUpdateResult> UpdateSettingsWithApplyAsync(Sdk.SettingsPatch patch, CancellationToken cancellationToken)
    {
        EnsureModule(patch.ModuleId);
        var hotkeyEdits = ExtractHotkeyEdits(patch.ModuleId, patch.Patch);
        var settingsPatchValues = StripRuntimeSettingsPatch(patch.Patch);
        var settingsPatch = new Sdk.SettingsPatch(patch.ModuleId, patch.ExpectedRevision, settingsPatchValues);
        var module = _packageRegistry.FindModule(patch.ModuleId);
        IModuleTransportRuntime? runtime = null;
        Sdk.ModuleContext? context = null;
        if (module is not null && TryGetTransportRuntime(module, out var selectedRuntime))
        {
            runtime = selectedRuntime;
            context = CreateModuleContext(module);
            var validation = await runtime.ValidateSettingsAsync(module, context, settingsPatch, cancellationToken);
            if (!validation.Ok)
            {
                throw new SettingsValidationException(settingsPatch.ModuleId, validation.Messages, validation.Error);
            }
        }

        var updated = _settingsStore.Update(settingsPatch);
        var applyState = runtime is null ? "stored" : "applied";
        var applyMessage = runtime is null
            ? "Settings were stored; no runtime apply hook was available."
            : $"Settings applied at revision {updated.Revision}.";

        if (runtime is not null && module is not null && context is not null)
        {
            try
            {
                var applied = await runtime.ApplySettingsAsync(module, context, updated, cancellationToken);
                applyMessage = $"Settings applied at revision {applied.Revision}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var applyError = LogRouter.Redact(ex.Message);
                try
                {
                    updated = _settingsStore.Rollback(patch.ModuleId);
                    applyState = "apply-failed-rolled-back";
                    applyMessage = $"Settings apply failed and rolled back to revision {updated.Revision}: {applyError}";
                    _logRouter.Append(module.Module.Manifest.PackageId, patch.ModuleId, "error", applyMessage);
                }
                catch (Exception rollbackEx)
                {
                    var rollbackError = LogRouter.Redact(rollbackEx.Message);
                    applyState = "apply-failed";
                    applyMessage = $"Settings apply failed and rollback failed: {applyError}; rollback: {rollbackError}";
                    _logRouter.Append(module.Module.Manifest.PackageId, patch.ModuleId, "error", applyMessage);
                }
            }
        }

        ApplyHotkeyEdits(patch.ModuleId, hotkeyEdits);
        _eventBus.Publish(patch.ModuleId, "settings.updated", new JsonObject
        {
            ["revision"] = updated.Revision,
            ["applyState"] = applyState,
            ["applyMessage"] = applyMessage,
            ["hotkeyEditCount"] = hotkeyEdits.Count
        });
        if (hotkeyEdits.Count > 0)
        {
            _eventBus.Publish(patch.ModuleId, "hotkeys.updated", new JsonObject
            {
                ["hotkeyEditCount"] = hotkeyEdits.Count
            });
        }

        _logRouter.Append(module?.Module.Manifest.PackageId ?? patch.ModuleId, patch.ModuleId, "info", $"Settings updated to revision {updated.Revision}; apply state {applyState}");
        return new SettingsUpdateResult(updated, applyState, applyMessage);
    }

    private static JsonObject StripRuntimeSettingsPatch(JsonObject patch)
    {
        var clone = (patch.DeepClone() as JsonObject) ?? new JsonObject();
        clone.Remove("$hotkeys");
        return clone;
    }

    private static IReadOnlyList<HotkeyEdit> ExtractHotkeyEdits(string moduleId, JsonObject patch)
    {
        if (patch["$hotkeys"] is not JsonArray edits)
        {
            return [];
        }

        var result = new List<HotkeyEdit>();
        foreach (var edit in edits.OfType<JsonObject>())
        {
            var id = edit["id"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var hotkeyId = id.StartsWith(moduleId + ".", StringComparison.OrdinalIgnoreCase)
                ? id[(moduleId.Length + 1)..]
                : id;
            result.Add(new HotkeyEdit(
                hotkeyId,
                edit["gesture"]?.GetValue<string>() ?? "",
                edit["reset"]?.GetValue<bool>() == true,
                edit["disabled"]?.GetValue<bool>() == true,
                edit["commandArgs"]?.ToJsonString() ?? "{}"));
        }

        return result;
    }

    private void ApplyHotkeyEdits(string moduleId, IReadOnlyList<HotkeyEdit> edits)
    {
        foreach (var edit in edits)
        {
            if (edit.Reset)
            {
                _hotkeyStore.Reset(moduleId, edit.HotkeyId);
                continue;
            }

            _hotkeyStore.Set(moduleId, edit.HotkeyId, edit.Gesture, edit.Disabled, edit.CommandArgsJson);
        }
    }

    public IReadOnlyList<LogRecord> TailLogs(string moduleId)
    {
        EnsureModule(moduleId);
        return _logRouter.Tail(moduleId);
    }

    public IReadOnlyList<Sdk.MptModuleEvent> HostEventsSince(ulong lastEventSeq)
    {
        return _eventBus.Since(lastEventSeq);
    }

    public async Task<int> CollectModuleEventsAsync(TimeSpan perModuleWindow, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var module in EnabledModules())
        {
            if (!TryGetTransportRuntime(module, out var runtime))
            {
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(perModuleWindow);
            var moduleId = module.Module.Manifest.Id;
            var cursor = new Sdk.EventCursor(ResolveEventStreamCursor(module));
            try
            {
                var context = CreateModuleContext(module);
                await foreach (var evt in runtime.SubscribeEventsAsync(module, context, cursor, timeout.Token).WithCancellation(timeout.Token))
                {
                    if (PublishModuleEvent(evt))
                    {
                        count++;
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // Bounded collection window elapsed; callers can poll again with the stored cursor.
            }
        }

        return count;
    }

    public IReadOnlyList<NotificationRecord> ListNotifications() => _notificationCenter.List();

    public NotificationReadStateUpdate? SetNotificationReadState(string notificationId, bool isRead)
    {
        var result = _notificationCenter.SetReadState(notificationId, isRead);
        if (result is null || !result.Changed)
        {
            return result;
        }

        var notification = result.Notification;
        var evt = _eventBus.Publish(notification.ModuleId, "notification.read-state-changed", new JsonObject
        {
            ["notificationId"] = notification.Id,
            ["isRead"] = notification.IsRead
        });
        var module = _packageRegistry.FindModule(notification.ModuleId);
        var state = notification.IsRead ? "read" : "unread";
        _logRouter.Append(
            module?.Module.Manifest.PackageId ?? notification.ModuleId,
            notification.ModuleId,
            "info",
            $"Notification '{notification.Id}' marked {state}.",
            eventSeq: evt.Seq);
        return result;
    }

    public NotificationReadStateUpdate? MarkNotificationRead(string notificationId)
    {
        return SetNotificationReadState(notificationId, true);
    }

    public IReadOnlyList<CommandHistoryRecord> ListCommandHistory(string? moduleId = null) => _commandHistory.List(moduleId);

    public NotificationRecord PublishNotification(string moduleId, string level, string title, string body)
    {
        var notification = _notificationCenter.Publish(moduleId, level, title, body);
        var evt = _eventBus.Publish(moduleId, "notification.created", new JsonObject
        {
            ["notificationId"] = notification.Id,
            ["level"] = level,
            ["title"] = title
        });
        var module = _packageRegistry.FindModule(moduleId);
        _logRouter.Append(module?.Module.Manifest.PackageId ?? moduleId, moduleId, "info", $"Notification '{title}' created.", eventSeq: evt.Seq);
        return notification;
    }

    private void PublishNotificationForModuleEvent(Sdk.MptModuleEvent evt)
    {
        if (!ShouldNotifyForModuleEvent(evt.Type))
        {
            return;
        }

        var level = EventNotificationLevel(evt.Type);
        var title = ReadPayloadString(evt.Payload, "title") ?? evt.Type;
        var body = ReadPayloadString(evt.Payload, "message") ??
            ReadPayloadString(evt.Payload, "summary") ??
            evt.Payload.ToJsonString();
        PublishNotification(evt.ModuleId, level, title, body);
    }

    private static bool ShouldNotifyForModuleEvent(string eventType)
    {
        return eventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               eventType.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
               eventType.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
               eventType.Contains("alert", StringComparison.OrdinalIgnoreCase) ||
               eventType.Contains("degraded", StringComparison.OrdinalIgnoreCase);
    }

    private static string EventNotificationLevel(string eventType)
    {
        return eventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               eventType.Contains("missing", StringComparison.OrdinalIgnoreCase)
            ? "warning"
            : "info";
    }

    private static string? ReadPayloadString(JsonObject payload, string key)
    {
        return payload.TryGetPropertyValue(key, out var node) && node is not null && node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : null;
    }

    private void EnsureModule(string moduleId)
    {
        if (_packageRegistry.FindModule(moduleId) is null)
        {
            throw new KeyNotFoundException($"Module '{moduleId}' was not found.");
        }
    }

    private sealed record HotkeyEdit(string HotkeyId, string Gesture, bool Reset, bool Disabled, string CommandArgsJson);

    private sealed record CommandRouteResolution(RuntimeModuleRecord? Record, Sdk.CommandExecutionResult? Failure);

    private sealed record CommandRuntimeCancellationTarget(RuntimeModuleRecord Module, IModuleTransportRuntime Runtime, MyPowerTools.Abstractions.ModuleContext Context);
}
