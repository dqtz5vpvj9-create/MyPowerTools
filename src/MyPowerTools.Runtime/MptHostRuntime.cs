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
    private readonly CommandIndex _commandIndex = new();
    private readonly RuntimePaths _paths;
    private readonly PlatformId _platform;
    private readonly SettingsStore _settingsStore;
    private readonly ModuleStateStore _moduleStateStore;
    private readonly RuntimeProcessPolicyStore _processPolicyStore;
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
    private IReadOnlyList<Sdk.MptCommandDescriptor> _dynamicCommands = [];
    private readonly ConcurrentDictionary<string, Task<Sdk.CommandExecutionResult>> _executions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _executionCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _cancelledInvocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ulong> _moduleEventCursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public MptHostRuntime(
        PackageReader packageReader,
        PlatformId platform,
        RuntimePaths? paths = null,
        IEnumerable<IModuleTransportRuntime>? transportRuntimes = null,
        IReadOnlyDictionary<string, object>? capabilityProviders = null)
    {
        paths ??= RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "MyPowerTools", "runtime-tests"));
        _paths = paths;
        _platform = platform;
        _packageRegistry = new PackageRegistry(packageReader, platform);
        _settingsStore = new SettingsStore(paths.Settings);
        _moduleStateStore = new ModuleStateStore(paths.State);
        _processPolicyStore = new RuntimeProcessPolicyStore(paths.State);
        _logRouter = new LogRouter(paths.Logs);
        _transportRuntimes = (transportRuntimes ?? [])
            .GroupBy(runtime => runtime.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _capabilityProviders = capabilityProviders ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RuntimeModuleRecord> Modules => _packageRegistry.Modules;
    public ulong CurrentEventSeq => _eventBus.CurrentSeq;

    public async ValueTask DisposeAsync()
    {
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

    public void Load(string packageRoot)
    {
        _packageRoot = Path.GetFullPath(packageRoot);
        _packageRegistry.Load(packageRoot);
        ApplyPersistedModuleState();
        ObserveAllModules();
        _dynamicCommands = [];
        _commandIndex.Rebuild(EnabledModules());
        RegisterProcessPoolsAndApplyPolicies();
        _eventBus.Publish("runner", "registry.loaded", new JsonObject
        {
            ["moduleCount"] = _packageRegistry.Modules.Count,
            ["enabledModuleCount"] = EnabledModules().Count
        });
        _logRouter.Append("runner", "runner", "info", $"Loaded {_packageRegistry.Modules.Count} modules from {packageRoot}", eventSeq: _eventBus.CurrentSeq);
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
        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands);
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
                            .ToArray());
                })
                .ToArray(),
            ListHotkeyDiagnostics(),
            recentCommands);
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
        var module = _packageRegistry.FindModule(moduleId)
            ?? throw new KeyNotFoundException($"Module '{moduleId}' was not found.");

        _moduleStateStore.SetModuleEnabled(moduleId, enabled);
        var nextStatus = enabled ? InitialStatus(module) : DisabledStatus(module);
        RecordModuleStatus(module, nextStatus);
        _dynamicCommands = _dynamicCommands
            .Where(command => !string.Equals(command.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands);
        var evt = _eventBus.Publish(moduleId, enabled ? "module.enabled" : "module.disabled", new JsonObject
        {
            ["enabled"] = enabled
        });
        _logRouter.Append(module.Module.Manifest.PackageId, moduleId, "info", enabled ? "Module enabled." : "Module disabled.", eventSeq: evt.Seq);
        return GetModuleDetail(moduleId);
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
        return EnabledModules()
            .SelectMany(module =>
            {
                var moduleId = module.Module.Manifest.Id;
                return module.Module.Manifest.Hotkeys
                    .Where(hotkey =>
                        !string.IsNullOrWhiteSpace(hotkey.Id) &&
                        !string.IsNullOrWhiteSpace(hotkey.Default) &&
                        !string.IsNullOrWhiteSpace(hotkey.CommandId))
                    .Select(hotkey => new RuntimeHotkeyBinding(
                        RuntimeHotkeyId(moduleId, hotkey.Id),
                        moduleId,
                        hotkey.CommandId,
                        hotkey.Default,
                        string.IsNullOrWhiteSpace(hotkey.Scope) ? "module" : hotkey.Scope,
                        string.IsNullOrWhiteSpace(hotkey.Reason) ? $"Invoke {hotkey.CommandId}." : hotkey.Reason));
            })
            .OrderBy(binding => binding.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RuntimeHotkeyDiagnostic> ListHotkeyDiagnostics()
    {
        var bindings = ListHotkeyBindings()
            .Select(binding => new RuntimeHotkeyBinding(
                binding.Id,
                binding.ModuleId,
                binding.CommandId,
                NormalizeGesture(binding.Gesture),
                binding.Scope,
                binding.Reason))
            .Concat(
            [
                new RuntimeHotkeyBinding(
                    "command-palette",
                    "runner",
                    "shell.command-palette.open",
                    "Ctrl+Alt+Space",
                    "runner",
                    "Open the command palette.")
            ])
            .ToArray();
        var conflicts = bindings
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
                    conflict ? "conflict" : "ok",
                    conflict
                        ? $"Gesture {binding.Gesture} also maps to {peers}."
                        : "Gesture is available in the runtime binding table.",
                    true);
            })
            .ToArray();
    }

    public Sdk.CommandExecutionResult ExecuteCommand(Sdk.CommandRequest request)
    {
        return ExecuteCommandAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<Sdk.CommandExecutionResult> ExecuteCommandAsync(Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        return _executions.GetOrAdd(request.InvocationId, _ => ExecuteCommandTrackedAsync(request, cancellationToken));
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

            result = await PrepareTransportCommandStreamAsync(command, request, linkedCancellation.Token);
            if (result is null)
            {
                var record = _packageRegistry.FindModule(command.ModuleId)!;
                var runtime = _transportRuntimes[record.Entrypoint!.Kind];
                var context = CreateModuleContext(record);
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

    private async Task<Sdk.CommandExecutionResult?> PrepareTransportCommandStreamAsync(Sdk.MptCommandDescriptor command, Sdk.CommandRequest request, CancellationToken cancellationToken)
    {
        var record = _packageRegistry.FindModule(command.ModuleId);
        if (record is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"Module {command.ModuleId} was not found.");
        }

        var policy = RuntimeOperationPolicy.Evaluate(command, request, record);
        if (!policy.IsAllowed)
        {
            return Failed(request, MptErrorCodes.RuntimePolicyBlocked, policy.Message, details: policy.Details);
        }

        if (!TryGetTransportRuntime(record, out _))
        {
            var transport = record.Entrypoint?.Kind ?? "none";
            return Failed(request, MptErrorCodes.UnsupportedTransport, $"No transport runtime is registered for {command.ModuleId} via {transport}.");
        }

        await Task.CompletedTask;
        return null;
    }

    public CommandCancellationResult CancelCommand(string invocationId)
    {
        if (string.IsNullOrWhiteSpace(invocationId))
        {
            return new CommandCancellationResult(false, "", "validation-failed", "invocationId is required.");
        }

        if (_executionCancellations.TryGetValue(invocationId, out var cancellation))
        {
            _cancelledInvocations[invocationId] = 1;
            cancellation.Cancel();
            return new CommandCancellationResult(true, invocationId, "cancelling", $"Cancellation requested for {invocationId}.");
        }

        if (_executions.TryGetValue(invocationId, out var execution) && execution.IsCompleted)
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
            "open" => Succeeded(request, $"Open request recorded for {command.ModuleId}."),
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
        var record = _packageRegistry.FindModule(command.ModuleId);
        if (record is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"Module {command.ModuleId} was not found.");
        }

        var policy = RuntimeOperationPolicy.Evaluate(command, request, record);
        if (!policy.IsAllowed)
        {
            return Failed(request, MptErrorCodes.RuntimePolicyBlocked, policy.Message, details: policy.Details);
        }

        if (!TryGetTransportRuntime(record, out var runtime))
        {
            var transport = record.Entrypoint?.Kind ?? "none";
            return Failed(request, MptErrorCodes.UnsupportedTransport, $"No transport runtime is registered for {command.ModuleId} via {transport}.");
        }

        try
        {
            var context = CreateModuleContext(record);
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
            _capabilityProviders);
    }

    private static Sdk.MptCommandDescriptor NormalizeDynamicCommand(RuntimeModuleRecord module, Sdk.MptCommandDescriptor command)
    {
        var execution = command.Execution;
        if (execution is null)
        {
            execution = new JsonObject { ["type"] = "module.execute" };
        }

        return command with
        {
            ModuleId = string.IsNullOrWhiteSpace(command.ModuleId) ? module.Module.Manifest.Id : command.ModuleId,
            TimeoutMs = command.TimeoutMs <= 0 ? 30000 : command.TimeoutMs,
            Execution = execution
        };
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
        var disabled = _moduleStateStore.DisabledModules();
        foreach (var module in _packageRegistry.Modules.ToArray())
        {
            var status = disabled.Contains(module.Module.Manifest.Id)
                ? DisabledStatus(module)
                : InitialStatus(module);
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
        _commandIndex.Rebuild(EnabledModules(), _dynamicCommands);
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
        var state = module.Entrypoint is null
            ? "unsupported"
            : module.Entrypoint.Kind == "inproc-dotnet"
                ? "indexed"
                : "stopped";
        var summary = module.Entrypoint is null
            ? "No compatible runnable entrypoint for this platform."
            : $"Indexed via {module.Entrypoint.Kind}. {module.Entrypoint.SelectionReason}";

        return new Sdk.ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            state,
            summary,
            DateTimeOffset.UtcNow,
            [
                new Sdk.HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new Sdk.HealthCheckSnapshot("transport", "Transport", module.Entrypoint is not null, module.Entrypoint?.SelectionReason ?? "No compatible entrypoint")
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
        var module = _packageRegistry.FindModule(patch.ModuleId);
        IModuleTransportRuntime? runtime = null;
        Sdk.ModuleContext? context = null;
        if (module is not null && TryGetTransportRuntime(module, out var selectedRuntime))
        {
            runtime = selectedRuntime;
            context = CreateModuleContext(module);
            var validation = await runtime.ValidateSettingsAsync(module, context, patch, cancellationToken);
            if (!validation.Ok)
            {
                throw new SettingsValidationException(patch.ModuleId, validation.Messages, validation.Error);
            }
        }

        var updated = _settingsStore.Update(patch);
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

        _eventBus.Publish(patch.ModuleId, "settings.updated", new JsonObject
        {
            ["revision"] = updated.Revision,
            ["applyState"] = applyState,
            ["applyMessage"] = applyMessage
        });
        _logRouter.Append(module?.Module.Manifest.PackageId ?? patch.ModuleId, patch.ModuleId, "info", $"Settings updated to revision {updated.Revision}; apply state {applyState}");
        return new SettingsUpdateResult(updated, applyState, applyMessage);
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
            var cursor = new Sdk.EventCursor(_moduleEventCursors.GetValueOrDefault(moduleId));
            try
            {
                var context = CreateModuleContext(module);
                await foreach (var evt in runtime.SubscribeEventsAsync(module, context, cursor, timeout.Token).WithCancellation(timeout.Token))
                {
                    _moduleEventCursors[moduleId] = Math.Max(_moduleEventCursors.GetValueOrDefault(moduleId), evt.Seq);
                    var payload = (evt.Payload.DeepClone() as JsonObject) ?? new JsonObject();
                    payload["moduleEventSeq"] = evt.Seq;
                    payload["moduleEventTime"] = evt.Time.ToString("O");
                    _eventBus.Publish(evt.ModuleId, evt.Type, payload);
                    PublishNotificationForModuleEvent(evt);
                    count++;
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
        _notificationCenter.Publish(evt.ModuleId, level, title, body);
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
}

