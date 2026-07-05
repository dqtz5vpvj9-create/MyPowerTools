using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace Sample.DotNetInProc;

public sealed class ModuleEntry : IMptModule
{
    public string Id => "sample.dotnet-inproc";
    public string PackageId => "sample-dotnet-inproc";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "dashboardCard"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            "Sample .NET InProc module is running.",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("inproc", ".NET InProc", true, "Loaded in the Runner process.")],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new MptCommandDescriptor(
                $"{Id}.hello",
                Id,
                "Hello from .NET InProc",
                "Executes inside the trusted InProc module host.",
                "action",
                Category: "Templates")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            "Hello from the .NET InProc template."));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        const string schema = """{"sections":[{"id":"general","title":"General","settings":[]}]}""";
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, schema));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new UiSurfaceDescriptor(
                $"{Id}.dashboard",
                "dashboard-card",
                "Sample .NET InProc",
                new JsonObject
                {
                    ["summary"] = "Trusted .NET module template",
                    ["state"] = "running"
                })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}

