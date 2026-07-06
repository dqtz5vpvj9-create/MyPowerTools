using System.Runtime.CompilerServices;

namespace MyPowerTools.Runtime;

public interface IModuleTransportRuntime
{
    string Kind { get; }

    ValueTask EnableModuleAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask DisableModuleAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, IReadOnlySet<string> enabledModuleIds, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    ValueTask<MyPowerTools.Abstractions.ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, CancellationToken cancellationToken);
    ValueTask<MyPowerTools.Abstractions.SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, CancellationToken cancellationToken);
    ValueTask<MyPowerTools.Abstractions.SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, MyPowerTools.Abstractions.SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new MyPowerTools.Abstractions.SettingsValidationResult(true, []));
    }

    ValueTask<MyPowerTools.Abstractions.SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, MyPowerTools.Abstractions.SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(snapshot);
    }

    ValueTask<IReadOnlyList<MyPowerTools.Abstractions.MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, CancellationToken cancellationToken);
    ValueTask<MyPowerTools.Abstractions.CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, MyPowerTools.Abstractions.CommandRequest request, CancellationToken cancellationToken);
    ValueTask<CommandCancellationResult> CancelCommandAsync(RuntimeModuleRecord module, MyPowerTools.Abstractions.ModuleContext context, string invocationId, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new CommandCancellationResult(false, invocationId, "module-cancel-unsupported", $"Transport '{Kind}' does not support module-level command cancellation."));
    }

    async IAsyncEnumerable<CommandProgressEvent> ExecuteCommandStreamAsync(
        RuntimeModuleRecord module,
        MyPowerTools.Abstractions.ModuleContext context,
        MyPowerTools.Abstractions.CommandRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await ExecuteCommandAsync(module, context, request, cancellationToken);
        yield return new CommandProgressEvent(
            result.InvocationId,
            result.CommandId,
            result.State,
            result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            0,
            true,
            result);
    }

    async IAsyncEnumerable<MyPowerTools.Abstractions.MptModuleEvent> SubscribeEventsAsync(
        RuntimeModuleRecord module,
        MyPowerTools.Abstractions.ModuleContext context,
        MyPowerTools.Abstractions.EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public interface IModuleTransportDiagnosticsProvider
{
    string GetProcessPoolKey(RuntimeModuleRecord module);
    void RegisterProcessPool(string poolKey, string moduleId);
    void ApplyRestartPolicy(string poolKey, string restartPolicy, string reason, DateTimeOffset updatedAt, DateTimeOffset? expiresAt);
    IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics();
    ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken);
    ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(string poolKey, bool paused, string reason, DateTimeOffset? expiresAt, CancellationToken cancellationToken);
}
