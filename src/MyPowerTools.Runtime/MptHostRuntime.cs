using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;

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
    private readonly IReadOnlyDictionary<string, IModuleTransportRuntime> _transportRuntimes;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private string _packageRoot = "";
    private IReadOnlyList<MptCommandDescriptor> _dynamicCommands = [];
    private readonly ConcurrentDictionary<string, Task<CommandExecutionResult>> _executions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public MptHostRuntime(PackageReader packageReader, PlatformId platform, RuntimePaths? paths = null, IEnumerable<IModuleTransportRuntime>? transportRuntimes = null)
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
        var cards = EnabledModules()
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

        return new DashboardSnapshot(cards, [], _eventBus.CurrentSeq);
    }

    public async Task RefreshHealthAsync(CancellationToken cancellationToken)
    {
        ExpireProcessPolicies();
        foreach (var module in EnabledModules().ToArray())
        {
            var status = await CheckTransportHealthAsync(module, cancellationToken)
                ?? await _healthMonitor.CheckAsync(module, cancellationToken);
            if (!ReferenceEquals(status, module.Status))
            {
                _packageRegistry.UpdateStatus(module.Module.Manifest.Id, status);
            }
        }
    }

    public async Task<int> RefreshDynamicCommandsAsync(CancellationToken cancellationToken)
    {
        ExpireProcessPolicies();
        var commands = new List<MptCommandDescriptor>();
        foreach (var module in EnabledModules().ToArray())
        {
            if (!TryGetTransportRuntime(module, out var runtime))
            {
                continue;
            }

            var context = CreateModuleContext(module);
            try
            {
                var status = await runtime.GetStatusAsync(module, context, cancellationToken);
                if (status is not null)
                {
                    _packageRegistry.UpdateStatus(module.Module.Manifest.Id, status);
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
                _packageRegistry.UpdateStatus(module.Module.Manifest.Id, Degraded(module, message));
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
                return new PackageSummarySnapshot(
                    first.Package.Id,
                    first.Package.DisplayName,
                    first.Package.Version,
                    first.Package.Publisher ?? "",
                    first.Directory,
                    first.Package.Hashes ?? "",
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
                .Select(module => new RuntimeModuleDiagnostics(
                    module.Module.Manifest.Id,
                    module.Module.Manifest.PackageId,
                    module.Module.Manifest.DisplayName,
                    module.Status.State,
                    module.Status.State != "disabled",
                    module.Entrypoint?.Kind ?? "none",
                    module.Status.UpdatedAt,
                    module.Status.Checks.Count))
                .ToArray(),
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

        var result = await diagnosticsProvider.RestartProcessAsync(poolKey, cancellationToken);
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
        _packageRegistry.UpdateStatus(moduleId, nextStatus);
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

    public IReadOnlyList<MptCommandDescriptor> ListCommands(string? query)
    {
        return _commandIndex.Search(query);
    }

    public CommandExecutionResult ExecuteCommand(CommandRequest request)
    {
        return ExecuteCommandAsync(request, CancellationToken.None).GetAwaiter().GetResult();
    }

    public Task<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return _executions.GetOrAdd(request.InvocationId, _ => ExecuteCommandInternalAsync(request, cancellationToken));
    }

    private async Task<CommandExecutionResult> ExecuteCommandInternalAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var command = _commandIndex.Find(request.CommandId);
        _commandHistory.Add(request, command, "accepted");
        if (command is null)
        {
            var failed = new CommandExecutionResult(
                request.InvocationId,
                request.CommandId,
                "failed",
                false,
                "",
                new MptRuntimeError(MptErrorCodes.NotFound, $"Command '{request.CommandId}' was not found."));
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

        var result = await ExecuteCommandCoreAsync(command, request, cancellationToken);
        _commandHistory.Complete(result);

        var record = _packageRegistry.FindModule(command.ModuleId);
        _logRouter.Append(record?.Module.Manifest.PackageId ?? command.ModuleId, command.ModuleId, result.Success ? "info" : "error", result.Success ? result.Output : result.Error?.Message ?? "Command failed.", request.InvocationId, evt.Seq);
        return result;
    }

    private async Task<CommandExecutionResult> ExecuteCommandCoreAsync(MptCommandDescriptor command, CommandRequest request, CancellationToken cancellationToken)
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

    private async Task<CommandExecutionResult> RefreshCommandAsync(MptCommandDescriptor command, CommandRequest request, CancellationToken cancellationToken)
    {
        await RefreshHealthAsync(cancellationToken);
        return Succeeded(request, $"Status refreshed for {command.ModuleId}.");
    }

    private CommandExecutionResult TailLogsCommand(MptCommandDescriptor command, CommandRequest request)
    {
        var logs = TailLogs(command.ModuleId);
        return Succeeded(request, $"{logs.Count} log records available for {command.ModuleId}.");
    }

    private CommandExecutionResult SettingsReadCommand(MptCommandDescriptor command, CommandRequest request)
    {
        var settings = GetSettings(command.ModuleId);
        return Succeeded(request, $"Settings revision {settings.Revision} loaded for {command.ModuleId}.");
    }

    private CommandExecutionResult NotificationTestCommand(MptCommandDescriptor command, CommandRequest request)
    {
        var notification = PublishNotification(command.ModuleId, "info", command.Title, command.Subtitle);
        return Succeeded(request, $"Notification {notification.Id} created.");
    }

    private async Task<CommandExecutionResult> HttpRequestCommandAsync(MptCommandDescriptor command, CommandRequest request, CancellationToken cancellationToken)
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
            return Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, LogRouter.Redact(ex.Message), retryable: true);
        }
    }

    private CommandExecutionResult BrokerRequestCommand(MptCommandDescriptor command, CommandRequest request)
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
        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "permission-required",
            false,
            "",
            new MptRuntimeError(MptErrorCodes.PermissionRequired, $"Broker approval required for {actionId}.", false, details));
    }

    private async Task<CommandExecutionResult> TransportCommandAsync(MptCommandDescriptor command, CommandRequest request, CancellationToken cancellationToken)
    {
        var record = _packageRegistry.FindModule(command.ModuleId);
        if (record is null)
        {
            return Failed(request, MptErrorCodes.NotFound, $"Module {command.ModuleId} was not found.");
        }

        if (!TryGetTransportRuntime(record, out var runtime))
        {
            var transport = record.Entrypoint?.Kind ?? "none";
            return Failed(request, MptErrorCodes.UnsupportedTransport, $"No transport runtime is registered for {command.ModuleId} via {transport}.");
        }

        try
        {
            return await runtime.ExecuteCommandAsync(record, CreateModuleContext(record), request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failed(request, MptErrorCodes.CommandTimeout, $"Command {request.CommandId} timed out.", retryable: true);
        }
        catch (Exception ex)
        {
            return Failed(request, MptErrorCodes.RuntimeUnavailable, LogRouter.Redact(ex.Message), retryable: true);
        }
    }

    private static CommandExecutionResult Succeeded(CommandRequest request, string output)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output);
    }

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message, bool retryable = false)
    {
        return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message, retryable));
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

    private async Task<ModuleStatusSnapshot?> CheckTransportHealthAsync(RuntimeModuleRecord module, CancellationToken cancellationToken)
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

    private ModuleContext CreateModuleContext(RuntimeModuleRecord module)
    {
        var moduleStateRoot = Path.Combine(_paths.State, "modules", module.Module.Manifest.Id);
        var dataDir = Path.Combine(moduleStateRoot, "data");
        var cacheDir = Path.Combine(moduleStateRoot, "cache");
        var logDir = Path.Combine(_paths.Logs, module.Module.Manifest.Id);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(logDir);

        return new ModuleContext(
            ProtocolConstants.HostVersion,
            ProtocolConstants.ModuleProtocolVersion,
            module.Module.Manifest.PackageId,
            module.Module.Manifest.Id,
            dataDir,
            cacheDir,
            logDir,
            PlatformId.Current().Rid,
            module.Module.Manifest.Capabilities);
    }

    private static MptCommandDescriptor NormalizeDynamicCommand(RuntimeModuleRecord module, MptCommandDescriptor command)
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

    private static ModuleStatusSnapshot Degraded(RuntimeModuleRecord module, string message)
    {
        return new ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            "degraded",
            message,
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new HealthCheckSnapshot("transport", "Transport runtime", false, message)
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

    private static ModuleStatusSnapshot InitialStatus(RuntimeModuleRecord module)
    {
        var state = module.Entrypoint is null
            ? "unsupported"
            : module.Entrypoint.Kind == "inproc-dotnet"
                ? "indexed"
                : "stopped";
        var summary = module.Entrypoint is null
            ? "No compatible runnable entrypoint for this platform."
            : $"Indexed via {module.Entrypoint.Kind}.";

        return new ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            state,
            summary,
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new HealthCheckSnapshot("transport", "Transport", module.Entrypoint is not null, module.Entrypoint?.Kind ?? "No compatible entrypoint")
            ],
            module.Status.EventSeq);
    }

    private static ModuleStatusSnapshot DisabledStatus(RuntimeModuleRecord module)
    {
        return new ModuleStatusSnapshot(
            module.Module.Manifest.Id,
            "disabled",
            "Module is disabled by user configuration.",
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("manifest", "Manifest", true, "Loaded"),
                new HealthCheckSnapshot("module-state", "Module state", false, "Disabled by user configuration")
            ],
            module.Status.EventSeq);
    }

    private static string Trim(string value)
    {
        return value.Length <= 500 ? value : value[..500] + "...";
    }

    public SettingsSnapshotDocument GetSettings(string moduleId)
    {
        EnsureModule(moduleId);
        return _settingsStore.Get(moduleId);
    }

    public SettingsSnapshotDocument UpdateSettings(SettingsPatch patch)
    {
        EnsureModule(patch.ModuleId);
        var updated = _settingsStore.Update(patch);
        _eventBus.Publish(patch.ModuleId, "settings.updated", new JsonObject
        {
            ["revision"] = updated.Revision
        });
        var module = _packageRegistry.FindModule(patch.ModuleId);
        _logRouter.Append(module?.Module.Manifest.PackageId ?? patch.ModuleId, patch.ModuleId, "info", $"Settings updated to revision {updated.Revision}");
        return updated;
    }

    public IReadOnlyList<LogRecord> TailLogs(string moduleId)
    {
        EnsureModule(moduleId);
        return _logRouter.Tail(moduleId);
    }

    public IReadOnlyList<MptModuleEvent> HostEventsSince(ulong lastEventSeq)
    {
        return _eventBus.Since(lastEventSeq);
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

    private void EnsureModule(string moduleId)
    {
        if (_packageRegistry.FindModule(moduleId) is null)
        {
            throw new KeyNotFoundException($"Module '{moduleId}' was not found.");
        }
    }
}
