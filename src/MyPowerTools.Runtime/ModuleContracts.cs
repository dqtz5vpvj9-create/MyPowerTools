namespace MyPowerTools.Runtime;

public interface IModuleTransportRuntime
{
    string Kind { get; }

    ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken);
    ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken);
    ValueTask<SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(snapshot);
    }

    ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken);
    ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken);
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
