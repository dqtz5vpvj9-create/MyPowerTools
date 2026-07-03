using MyPowerTools.Sdk;

namespace SampleModule;

public sealed class ModuleEntry : IMptModule
{
    public string Id => "sample.dotnet-module";
    public string PackageId => "sample-dotnet-package";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken ct) =>
        ValueTask.FromResult(new InitializeResult(true, "1.0", new[] { "status", "commands", "settings" }));

    public ValueTask<ModuleStatus> GetStatusAsync(CancellationToken ct) => ValueTask.FromResult(
        new ModuleStatus(Id, "running", "Sample .NET module is running", DateTimeOffset.UtcNow, 1));

    public ValueTask<IReadOnlyList<ToolCommand>> ListCommandsAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<ToolCommand>>(
        new[] { new ToolCommand("sample.dotnet-module.hello", "Hello from .NET", "action") });

    public ValueTask<CommandExecution> ExecuteCommandAsync(CommandRequest request, CancellationToken ct) =>
        ValueTask.FromResult(new CommandExecution(request.InvocationId, request.CommandId, "succeeded", true, "Hello from .NET module"));

    public ValueTask<CommandCancelResult> CancelCommandAsync(string invocationId, CancellationToken ct) =>
        ValueTask.FromResult(new CommandCancelResult(false));

    public ValueTask<SettingsSchema> GetSettingsSchemaAsync(CancellationToken ct) =>
        ValueTask.FromResult(new SettingsSchema(Id, "{\"sections\":[]}"));

    public ValueTask<SettingsSnapshot> GetSettingsAsync(CancellationToken ct) =>
        ValueTask.FromResult(new SettingsSnapshot(Id, 1, "{}", DateTimeOffset.UtcNow));

    public ValueTask<ValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken ct) =>
        ValueTask.FromResult(new ValidationResult(true, Array.Empty<string>()));

    public ValueTask<IReadOnlyList<UiSurface>> ListSurfacesAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<UiSurface>>(Array.Empty<UiSurface>());

    public async IAsyncEnumerable<ModuleEvent> SubscribeEventsAsync(EventCursor cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<LogEntry> TailLogsAsync(LogCursor cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask DisposeAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
