using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using Grpc.Net.Client;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Protocol.Module.V1;
using MyPowerTools.Runtime;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandParameterDescriptor = MyPowerTools.Abstractions.CommandParameterDescriptor;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using EventCursor = MyPowerTools.Abstractions.EventCursor;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptModuleEvent = MyPowerTools.Abstractions.MptModuleEvent;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;
using SettingsValidationResult = MyPowerTools.Abstractions.SettingsValidationResult;

namespace MyPowerTools.ModuleHost.GrpcIpc;

public sealed record GrpcIpcHostDiagnostics(
    string PoolKey,
    string State,
    int ProcessId,
    string Endpoint,
    int StartCount,
    int RestartLimit,
    DateTimeOffset? LastStartedAt,
    IReadOnlyList<string> ModuleIds,
    int StdoutLineCount,
    int StderrLineCount,
    string LastStdout,
    string LastStderr);

public sealed record GrpcIpcRestartPolicy(string State, string Reason, DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt);

public sealed class GrpcIpcModuleHost : IAsyncDisposable
{
    private const int MaxCapturedProcessLines = 20;

    private readonly List<Process> _ownedProcesses = [];
    private readonly object _stdioLock = new();
    private readonly Queue<string> _stdoutTail = [];
    private readonly Queue<string> _stderrTail = [];
    private GrpcChannel? _channel;
    private ModuleControl.ModuleControlClient? _client;
    private DateTimeOffset? _startedAt;
    private string _endpoint = "";
    private int _stdoutLineCount;
    private int _stderrLineCount;
    private bool _killProcessTree = true;

    public async Task InitializeAsync(SelectedEntrypoint entrypoint, ModuleContext context, CancellationToken cancellationToken)
    {
        _killProcessTree = entrypoint.SidecarKillProcessTree ?? true;
        if (!string.IsNullOrWhiteSpace(entrypoint.Command))
        {
            StartSidecar(entrypoint);
        }

        _channel = CreateChannel(entrypoint);
        _client = new ModuleControl.ModuleControlClient(_channel);

        using var readyTimeout = entrypoint.SidecarReadyTimeoutMs is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        readyTimeout?.CancelAfter(TimeSpan.FromMilliseconds(entrypoint.SidecarReadyTimeoutMs!.Value));

        await InitializeModuleAsync(context, readyTimeout?.Token ?? cancellationToken);
    }

    public async Task InitializeModuleAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var initializeRequest = new InitializeRequest
        {
            HostVersion = context.HostVersion,
            ProtocolVersion = context.ProtocolVersion,
            PackageId = context.PackageId,
            ModuleId = context.ModuleId,
            DataDir = context.DataDirectory,
            CacheDir = context.CacheDirectory,
            LogDir = context.LogDirectory,
            Platform = context.Platform
        };
        initializeRequest.GrantedCapabilities.AddRange(context.GrantedCapabilities);

        var response = await client.InitializeAsync(initializeRequest, cancellationToken: cancellationToken);

        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error?.Message ?? $"Module {context.ModuleId} rejected initialization.");
        }
    }

    public async Task<ModuleStatusSnapshot> GetStatusAsync(string moduleId, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var status = await client.GetStatusAsync(new GetStatusRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
        return new ModuleStatusSnapshot(
            status.ModuleId,
            status.State.ToString().Replace("MODULE_STATE_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
            status.Summary,
            DateTimeOffset.TryParse(status.UpdatedAt, out var updatedAt) ? updatedAt : DateTimeOffset.UtcNow,
            status.Checks.Select(check => new HealthCheckSnapshot(check.Id, check.Label, check.Ok, check.Message)).ToArray(),
            status.EventSeq);
    }

    public async Task<SettingsSchemaDocument> GetSettingsSchemaAsync(string moduleId, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var schema = await client.GetSettingsSchemaAsync(new GetSettingsSchemaRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
        return new SettingsSchemaDocument(schema.ModuleId, schema.SchemaJson);
    }

    public async Task<SettingsValidationResult> ValidateSettingsAsync(string moduleId, SettingsPatch patch, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var result = await client.ValidateSettingsAsync(new ValidateSettingsRequest
        {
            ModuleId = moduleId,
            ExpectedRevision = patch.ExpectedRevision,
            PatchJson = patch.Patch.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
        }, cancellationToken: cancellationToken);

        return new SettingsValidationResult(
            result.Ok,
            result.Messages.ToArray(),
            result.Error is null ? null : new MptRuntimeError(result.Error.Code, result.Error.Message, result.Error.Retryable));
    }

    public async Task<SettingsSnapshotDocument> ApplySettingsAsync(string moduleId, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var result = await client.ApplySettingsAsync(new ApplySettingsRequest
        {
            ModuleId = moduleId,
            ExpectedRevision = snapshot.Revision,
            PatchJson = snapshot.Values.ToJsonString(new JsonSerializerOptions { WriteIndented = false })
        }, cancellationToken: cancellationToken);

        return new SettingsSnapshotDocument(
            result.ModuleId,
            result.Revision,
            ParseJsonObject(result.ValuesJson),
            DateTimeOffset.TryParse(result.UpdatedAt, out var updatedAt) ? updatedAt : DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(string moduleId, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var response = await client.ListCommandsAsync(new ListCommandsRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
        return response.Commands.Select(command => new MptCommandDescriptor(
            command.Id,
            moduleId,
            command.Title,
            command.Subtitle,
            command.Kind,
            command.RequiresElevation,
            command.Icon,
            Parameters: command.Parameters.Select(parameter => new CommandParameterDescriptor(
                parameter.Id,
                parameter.Label,
                parameter.Type,
                parameter.Required,
                parameter.DefaultValue)).ToArray())).ToArray();
    }

    public async Task<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var moduleId = request.CommandId.Split('.').FirstOrDefault() ?? "";
        return await ExecuteCommandAsync(moduleId, request, cancellationToken);
    }

    public async Task<CommandExecutionResult> ExecuteCommandAsync(string moduleId, CommandRequest request, CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var grpcRequest = new ExecuteCommandRequest
        {
            ModuleId = moduleId,
            CommandId = request.CommandId,
            InvocationId = request.InvocationId
        };
        foreach (var argument in ToGrpcArgs(request.Args))
        {
            grpcRequest.Args[argument.Key] = argument.Value;
        }

        var response = await client.ExecuteCommandAsync(grpcRequest, cancellationToken: cancellationToken);

        return ToCommandExecutionResult(response);
    }

    public async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamAsync(
        string moduleId,
        CommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        var grpcRequest = new ExecuteCommandRequest
        {
            ModuleId = moduleId,
            CommandId = request.CommandId,
            InvocationId = request.InvocationId
        };
        foreach (var argument in ToGrpcArgs(request.Args))
        {
            grpcRequest.Args[argument.Key] = argument.Value;
        }

        using var call = client.ExecuteCommandStream(grpcRequest, cancellationToken: cancellationToken);
        while (true)
        {
            MyPowerTools.Protocol.Module.V1.CommandExecutionEvent? current = null;
            CommandExecutionResult? fallback = null;
            try
            {
                if (!await call.ResponseStream.MoveNext(cancellationToken))
                {
                    break;
                }

                current = call.ResponseStream.Current;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                fallback = await ExecuteCommandAsync(moduleId, request, cancellationToken);
            }

            if (fallback is not null)
            {
                yield return new CommandProgressEvent(
                    fallback.InvocationId,
                    fallback.CommandId,
                    fallback.State,
                    fallback.Success ? fallback.Output : fallback.Error?.Message ?? "Command failed.",
                    0,
                    true,
                    fallback);
                yield break;
            }

            if (current is null)
            {
                break;
            }

            yield return new CommandProgressEvent(
                current.InvocationId,
                current.CommandId,
                current.State,
                current.Message,
                (int)current.Sequence,
                current.Terminal,
                current.FinalResult is null ? null : ToCommandExecutionResult(current.FinalResult));
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        string moduleId,
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = EnsureClient();
        using var call = client.SubscribeEvents(new SubscribeEventsRequest
        {
            ModuleId = moduleId,
            LastEventSeq = cursor.LastEventSeq
        }, cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var evt = call.ResponseStream.Current;
            yield return new MptModuleEvent(
                evt.ModuleId,
                evt.Seq,
                evt.Type,
                DateTimeOffset.TryParse(evt.Time, out var time) ? time : DateTimeOffset.UtcNow,
                ParseJsonObject(evt.PayloadJson));
        }
    }

    private static CommandExecutionResult ToCommandExecutionResult(CommandExecution response)
    {
        return new CommandExecutionResult(
            response.InvocationId,
            response.CommandId,
            response.State.ToString().Replace("COMMAND_STATE_", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
            response.Success,
            response.Output,
            response.Error is null
                ? null
                : new MptRuntimeError(
                    response.Error.Code,
                    response.Error.Message,
                    response.Error.Retryable,
                    string.IsNullOrWhiteSpace(response.Error.DetailsJson) ? null : ParseJsonObject(response.Error.DetailsJson)));
    }

    private static IEnumerable<KeyValuePair<string, string>> ToGrpcArgs(JsonObject args)
    {
        foreach (var argument in args)
        {
            if (argument.Value is null)
            {
                continue;
            }

            yield return new KeyValuePair<string, string>(argument.Key, JsonValueToString(argument.Value));
        }
    }

    private static JsonObject ParseJsonObject(string json)
    {
        return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
    }

    private static string JsonValueToString(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }

            if (value.TryGetValue<long>(out var longValue))
            {
                return longValue.ToString(CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    public string Describe(SelectedEntrypoint entrypoint)
    {
        var endpoint = string.IsNullOrWhiteSpace(entrypoint.EndpointAddress) ? "endpoint pending" : $"{entrypoint.EndpointTransport}:{entrypoint.EndpointAddress}";
        return $"gRPC IPC sidecar selected for {entrypoint.Service ?? entrypoint.Command ?? "runtime"} at {endpoint}.";
    }

    public bool IsProcessHealthy()
    {
        return _ownedProcesses.Count == 0 || _ownedProcesses.Any(process => !process.HasExited);
    }

    public GrpcIpcHostDiagnostics GetDiagnostics(string poolKey, IReadOnlyList<string> moduleIds, int startCount, int restartLimit)
    {
        var process = _ownedProcesses.LastOrDefault();
        var state = process is null
            ? "external"
            : process.HasExited ? "exited" : "running";
        var processId = process is null
            ? 0
            : process.HasExited ? 0 : process.Id;
        lock (_stdioLock)
        {
            return new GrpcIpcHostDiagnostics(
                poolKey,
                state,
                processId,
                _endpoint,
                startCount,
                restartLimit,
                _startedAt,
                moduleIds,
                _stdoutLineCount,
                _stderrLineCount,
                _stdoutTail.LastOrDefault() ?? "",
                _stderrTail.LastOrDefault() ?? "");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel?.Dispose();
        foreach (var process in _ownedProcesses)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: _killProcessTree);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }

        _ownedProcesses.Clear();
    }

    private ModuleControl.ModuleControlClient EnsureClient()
    {
        return _client ?? throw new InvalidOperationException("gRPC IPC module host has not been initialized.");
    }

    private void StartSidecar(SelectedEntrypoint entrypoint)
    {
        var psi = new ProcessStartInfo
        {
            FileName = entrypoint.Command!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in entrypoint.Args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start sidecar '{entrypoint.Command}'.");
        process.OutputDataReceived += (_, args) => CaptureStdout(args.Data);
        process.ErrorDataReceived += (_, args) => CaptureStderr(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ownedProcesses.Add(process);
        _startedAt = DateTimeOffset.UtcNow;
    }

    private void CaptureStdout(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_stdioLock)
        {
            _stdoutLineCount++;
            _stdoutTail.Enqueue(LogRouter.Redact(line));
            while (_stdoutTail.Count > MaxCapturedProcessLines)
            {
                _stdoutTail.Dequeue();
            }
        }
    }

    private void CaptureStderr(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_stdioLock)
        {
            _stderrLineCount++;
            _stderrTail.Enqueue(LogRouter.Redact(line));
            while (_stderrTail.Count > MaxCapturedProcessLines)
            {
                _stderrTail.Dequeue();
            }
        }
    }

    private GrpcChannel CreateChannel(SelectedEntrypoint entrypoint)
    {
        var endpoint = ToEndpoint(entrypoint);
        _endpoint = $"{endpoint.Transport}:{endpoint.Address}";
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (endpoint.Transport == IpcTransport.NamedPipe)
                {
                    var stream = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await stream.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    return stream;
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
    }

    private static IpcEndpoint ToEndpoint(SelectedEntrypoint entrypoint)
    {
        var transport = entrypoint.EndpointTransport switch
        {
            "named-pipe" => IpcTransport.NamedPipe,
            "unix-domain-socket" => IpcTransport.UnixDomainSocket,
            _ => OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket
        };

        var address = entrypoint.EndpointAddress ?? throw new InvalidOperationException("gRPC IPC endpoint address is missing.");
        return new IpcEndpoint(transport, address);
    }
}

public sealed class GrpcIpcModuleRuntime : IModuleTransportRuntime, IModuleTransportDiagnosticsProvider, IAsyncDisposable
{
    private static readonly GrpcIpcPoolRuntimePolicy DefaultPoolRuntimePolicy = new(4, TimeSpan.FromSeconds(30));

    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly Dictionary<string, GrpcIpcModuleHost> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _initializedModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _knownPoolModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTimeOffset>> _startHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GrpcIpcRestartPolicy> _restartPolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GrpcIpcPoolRuntimePolicy> _poolRuntimePolicies = new(StringComparer.OrdinalIgnoreCase);

    public string Kind => "grpc-ipc";

    public string GetProcessPoolKey(RuntimeModuleRecord module)
    {
        return GetPoolKey(module);
    }

    public void RegisterProcessPool(string poolKey, string moduleId)
    {
        _poolLock.Wait();
        try
        {
            MarkKnown(poolKey, moduleId);
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public void ApplyRestartPolicy(string poolKey, string restartPolicy, string reason, DateTimeOffset updatedAt, DateTimeOffset? expiresAt)
    {
        _poolLock.Wait();
        try
        {
            if (string.Equals(restartPolicy, "paused", StringComparison.OrdinalIgnoreCase))
            {
                _restartPolicies[poolKey] = new GrpcIpcRestartPolicy("paused", reason, updatedAt, expiresAt);
                return;
            }

            _restartPolicies.Remove(poolKey);
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public async ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.GetStatusAsync(module.Module.Manifest.Id, cancellationToken), cancellationToken);
    }

    public async ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.GetSettingsSchemaAsync(module.Module.Manifest.Id, cancellationToken), cancellationToken);
    }

    public async ValueTask<SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsPatch patch, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.ValidateSettingsAsync(module.Module.Manifest.Id, patch, cancellationToken), cancellationToken);
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.ApplySettingsAsync(module.Module.Manifest.Id, snapshot, cancellationToken), cancellationToken);
    }

    public async ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.ListCommandsAsync(module.Module.Manifest.Id, cancellationToken), cancellationToken);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteWithRestartAsync(module, context, host => host.ExecuteCommandAsync(module.Module.Manifest.Id, request, cancellationToken), cancellationToken);
    }

    public async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamAsync(
        RuntimeModuleRecord module,
        ModuleContext context,
        CommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var host = await GetHostAsync(module, context, cancellationToken);
        await foreach (var evt in host.ExecuteCommandStreamAsync(module.Module.Manifest.Id, request, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        RuntimeModuleRecord module,
        ModuleContext context,
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var host = await GetHostAsync(module, context, cancellationToken);
        await foreach (var evt in host.SubscribeEventsAsync(module.Module.Manifest.Id, cursor, cancellationToken).WithCancellation(cancellationToken))
        {
            yield return evt;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _poolLock.WaitAsync();
        try
        {
            foreach (var host in _hosts.Values)
            {
                await host.DisposeAsync();
            }

            _hosts.Clear();
            _initializedModules.Clear();
            _knownPoolModules.Clear();
            _startHistory.Clear();
            _restartPolicies.Clear();
            _poolRuntimePolicies.Clear();
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics()
    {
        _poolLock.Wait();
        try
        {
            return _hosts
                .Select(pair =>
                {
                    _initializedModules.TryGetValue(pair.Key, out var modules);
                    _startHistory.TryGetValue(pair.Key, out var starts);
                    var runtimePolicy = RuntimePolicyForPool(pair.Key);
                    var host = pair.Value.GetDiagnostics(
                        pair.Key,
                        ModuleIdsForPool(pair.Key, modules),
                        starts?.Count ?? 0,
                        runtimePolicy.RestartLimit);
                    var policy = PolicyForPool(pair.Key);
                    return new RuntimeProcessDiagnostics(
                        Kind,
                        host.PoolKey,
                        host.State,
                        host.ProcessId,
                        host.Endpoint,
                        host.StartCount,
                        host.RestartLimit,
                        policy.State,
                        policy.Reason,
                        host.LastStartedAt,
                        host.ModuleIds,
                        policy.ExpiresAt,
                        host.StdoutLineCount,
                        host.StderrLineCount,
                        host.LastStdout,
                        host.LastStderr);
                })
                .Concat(_restartPolicies
                    .Where(pair => string.Equals(pair.Value.State, "paused", StringComparison.OrdinalIgnoreCase) && !_hosts.ContainsKey(pair.Key))
                    .Select(pair =>
                    {
                        _startHistory.TryGetValue(pair.Key, out var starts);
                        return new RuntimeProcessDiagnostics(
                            Kind,
                            pair.Key,
                            "paused",
                            0,
                            "",
                            starts?.Count ?? 0,
                            RuntimePolicyForPool(pair.Key).RestartLimit,
                            pair.Value.State,
                            pair.Value.Reason,
                            null,
                            ModuleIdsForPool(pair.Key, null),
                            pair.Value.ExpiresAt);
                    }))
                .ToArray();
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public async ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken)
    {
        await _poolLock.WaitAsync(cancellationToken);
        try
        {
            if (!_hosts.TryGetValue(poolKey, out var host))
            {
                return new RuntimeProcessRestartResult(false, Kind, poolKey, "missing", $"gRPC IPC runtime pool '{poolKey}' is not active.", []);
            }

            var modules = _initializedModules.TryGetValue(poolKey, out var initialized)
                ? initialized.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
                : ModuleIdsForPool(poolKey, null);
            await RemoveHostCoreAsync(poolKey, host);
            var policy = PolicyForPool(poolKey);
            var state = policy.State == "paused" ? "paused" : "restarting";
            var message = policy.State == "paused"
                ? $"gRPC IPC runtime pool '{poolKey}' was stopped; restart policy is paused."
                : $"gRPC IPC runtime pool '{poolKey}' was stopped and will restart on the next module operation.";
            return new RuntimeProcessRestartResult(true, Kind, poolKey, state, message, modules);
        }
        finally
        {
            _poolLock.Release();
        }
    }

    public async ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(string poolKey, bool paused, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await _poolLock.WaitAsync(cancellationToken);
        try
        {
            var hasPool = _hosts.ContainsKey(poolKey) || _knownPoolModules.ContainsKey(poolKey) || _startHistory.ContainsKey(poolKey);
            if (!hasPool)
            {
                return new RuntimeProcessPolicyResult(false, Kind, poolKey, "missing", "unknown", $"gRPC IPC runtime pool '{poolKey}' is not known.", []);
            }

            if (paused)
            {
                reason = string.IsNullOrWhiteSpace(reason) ? "Paused by user." : reason.Trim();
                _restartPolicies[poolKey] = new GrpcIpcRestartPolicy("paused", reason, DateTimeOffset.UtcNow, expiresAt);
                var message = expiresAt is null
                    ? $"Automatic restart is paused for gRPC IPC runtime pool '{poolKey}'."
                    : $"Automatic restart is paused for gRPC IPC runtime pool '{poolKey}' until {expiresAt.Value:O}.";
                return new RuntimeProcessPolicyResult(true, Kind, poolKey, "paused", "paused", message, ModuleIdsForPool(poolKey, null), expiresAt);
            }

            _restartPolicies.Remove(poolKey);
            return new RuntimeProcessPolicyResult(true, Kind, poolKey, "running", "automatic", $"Automatic restart is enabled for gRPC IPC runtime pool '{poolKey}'.", ModuleIdsForPool(poolKey, null));
        }
        finally
        {
            _poolLock.Release();
        }
    }

    private async Task<GrpcIpcModuleHost> GetHostAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
    {
        var poolKey = GetPoolKey(module);

        await _poolLock.WaitAsync(cancellationToken);
        try
        {
            if (_hosts.TryGetValue(poolKey, out var existing) && existing.IsProcessHealthy())
            {
                await EnsureModuleInitializedAsync(poolKey, existing, module.Module.Manifest.Id, context, cancellationToken);
                return existing;
            }

            if (existing is not null)
            {
                await RemoveHostCoreAsync(poolKey, existing);
            }

            var policy = PolicyForPool(poolKey);
            if (policy.State == "paused")
            {
                throw new InvalidOperationException($"gRPC IPC runtime '{poolKey}' restart policy is paused. Resume the pool before starting a new sidecar. {policy.Reason}");
            }

            if (module.Entrypoint is null)
            {
                throw new InvalidOperationException($"Module {module.Module.Manifest.Id} has no selected gRPC IPC entrypoint.");
            }

            var runtimePolicy = RuntimePolicyFor(module);
            _poolRuntimePolicies[poolKey] = runtimePolicy;
            RecordStartOrThrow(poolKey, runtimePolicy);
            var host = new GrpcIpcModuleHost();
            await host.InitializeAsync(module.Entrypoint, context, cancellationToken);
            _hosts[poolKey] = host;
            MarkInitialized(poolKey, module.Module.Manifest.Id);
            return host;
        }
        finally
        {
            _poolLock.Release();
        }
    }

    private async Task<T> ExecuteWithRestartAsync<T>(
        RuntimeModuleRecord module,
        ModuleContext context,
        Func<GrpcIpcModuleHost, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var host = await GetHostAsync(module, context, cancellationToken);
        try
        {
            return await action(host);
        }
        catch (Exception ex) when (ShouldRestartAfter(ex, cancellationToken))
        {
            await RemoveHostAsync(GetPoolKey(module));
            host = await GetHostAsync(module, context, cancellationToken);
            return await action(host);
        }
    }

    private async Task RemoveHostAsync(string poolKey)
    {
        await _poolLock.WaitAsync();
        try
        {
            if (_hosts.TryGetValue(poolKey, out var removed))
            {
                await RemoveHostCoreAsync(poolKey, removed);
            }
        }
        finally
        {
            _poolLock.Release();
        }
    }

    private async Task RemoveHostCoreAsync(string poolKey, GrpcIpcModuleHost host)
    {
        _hosts.Remove(poolKey);
        _initializedModules.Remove(poolKey);
        await host.DisposeAsync();
    }

    private async Task EnsureModuleInitializedAsync(
        string poolKey,
        GrpcIpcModuleHost host,
        string moduleId,
        ModuleContext context,
        CancellationToken cancellationToken)
    {
        if (_initializedModules.TryGetValue(poolKey, out var modules) && modules.Contains(moduleId))
        {
            return;
        }

        await host.InitializeModuleAsync(context, cancellationToken);
        MarkInitialized(poolKey, moduleId);
    }

    private void MarkInitialized(string poolKey, string moduleId)
    {
        if (!_initializedModules.TryGetValue(poolKey, out var modules))
        {
            modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _initializedModules[poolKey] = modules;
        }

        modules.Add(moduleId);
        MarkKnown(poolKey, moduleId);
    }

    private void MarkKnown(string poolKey, string moduleId)
    {
        if (!_knownPoolModules.TryGetValue(poolKey, out var knownModules))
        {
            knownModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _knownPoolModules[poolKey] = knownModules;
        }

        knownModules.Add(moduleId);
    }

    private IReadOnlyList<string> ModuleIdsForPool(string poolKey, HashSet<string>? currentModules)
    {
        if (currentModules is not null && currentModules.Count > 0)
        {
            return currentModules.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return _knownPoolModules.TryGetValue(poolKey, out var knownModules)
            ? knownModules.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private GrpcIpcRestartPolicy PolicyForPool(string poolKey)
    {
        return _restartPolicies.TryGetValue(poolKey, out var policy)
            ? policy
            : new GrpcIpcRestartPolicy("automatic", "", DateTimeOffset.MinValue, null);
    }

    private GrpcIpcPoolRuntimePolicy RuntimePolicyForPool(string poolKey)
    {
        return _poolRuntimePolicies.TryGetValue(poolKey, out var policy)
            ? policy
            : DefaultPoolRuntimePolicy;
    }

    private static GrpcIpcPoolRuntimePolicy RuntimePolicyFor(RuntimeModuleRecord module)
    {
        var restartLimit = module.Entrypoint?.SidecarRestartLimit is > 0
            ? module.Entrypoint.SidecarRestartLimit.Value
            : DefaultPoolRuntimePolicy.RestartLimit;
        var restartWindow = module.Entrypoint?.SidecarRestartWindowSeconds is > 0
            ? TimeSpan.FromSeconds(module.Entrypoint.SidecarRestartWindowSeconds.Value)
            : DefaultPoolRuntimePolicy.RestartWindow;
        return new GrpcIpcPoolRuntimePolicy(restartLimit, restartWindow);
    }

    private void RecordStartOrThrow(string poolKey, GrpcIpcPoolRuntimePolicy runtimePolicy)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_startHistory.TryGetValue(poolKey, out var starts))
        {
            starts = [];
            _startHistory[poolKey] = starts;
        }

        starts.RemoveAll(start => now - start > runtimePolicy.RestartWindow);
        if (starts.Count >= runtimePolicy.RestartLimit)
        {
            throw new InvalidOperationException($"gRPC IPC runtime '{poolKey}' restart limit reached.");
        }

        starts.Add(now);
    }

    private static string GetPoolKey(RuntimeModuleRecord module)
    {
        var runtimeId = module.Entrypoint?.RuntimeId;
        return string.IsNullOrWhiteSpace(runtimeId)
            ? $"module:{module.Module.Manifest.Id}"
            : $"package:{module.Package.Package.Id}:runtime:{runtimeId}";
    }

    private static bool ShouldRestartAfter(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is RpcException rpc && rpc.StatusCode is StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown ||
               ex is IOException ||
               ex is SocketException ||
               ex is ObjectDisposedException ||
               ex is InvalidOperationException;
    }
}

internal sealed record GrpcIpcPoolRuntimePolicy(int RestartLimit, TimeSpan RestartWindow);
