using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace MyPowerTools.SampleModules.DotNet;

public sealed class SampleDotNetModule : IMptModule
{
    public string Id => "sample.dotnet";
    public string PackageId => "sample-dotnet";
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
            "InProc sample module is loaded.",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("inproc", "InProc host", true, "Loaded in Runner process")],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("sample.dotnet.ping", Id, "Ping .NET sample", "InProc trusted module", "action")
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
            "pong from SampleDotNetModule"));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (cursor.LastEventSeq < 1)
        {
            yield return new MptModuleEvent(
                Id,
                1,
                "sample.heartbeat",
                DateTimeOffset.UtcNow,
                new JsonObject { ["message"] = "sample module event stream is active" });
        }
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{"enabled":{"type":"boolean"}}}"""));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject { ["enabled"] = true }, DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("sample.dotnet.dashboard", "dashboard-card", "Sample .NET", new JsonObject { ["state"] = "ready" })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class LeakyDotNetModule : IMptModule
{
    public string Id => "sample.dotnet.leaky";
    public string PackageId => "sample-dotnet-leaky";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        SoftIsolationFixtureFiles.Increment(context.DataDirectory, "instances.txt");
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            "Leaky InProc sample module is loaded.",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("inproc", "InProc host", true, "Loaded in Runner process")],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("sample.dotnet.leaky.ping", Id, "Ping leaky .NET sample", "InProc unload failure fixture", "action"),
            new("sample.dotnet.leaky.throw", Id, "Throw from leaky .NET sample", "InProc unload failure fixture", "action")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.CommandId == "sample.dotnet.leaky.throw")
        {
            throw new InvalidOperationException("synthetic leaky inproc fault");
        }

        return ValueTask.FromResult(new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            "pong from LeakyDotNetModule"));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{}}"""));
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
        return ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
    }
}

/// <summary>
/// Deterministic fixture used to prove the Runner's in-process soft isolation.
/// It is packaged only by tests and is never part of the production module set.
/// </summary>
public sealed class FaultInjectionDotNetModule : IMptModule
{
    private string? _dataDirectory;

    public string Id => "sample.dotnet.fault-injection";
    public string PackageId => "sample-dotnet-fault-injection";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            "Fault-injection fixture is ready.",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("soft-isolation", "Soft isolation fixture", true, "Ready")],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("sample.dotnet.fault.ok", Id, "Return success", "Soft isolation fixture", "action"),
            new("sample.dotnet.fault.throw", Id, "Throw a managed exception", "Soft isolation fixture", "action"),
            new("sample.dotnet.fault.timeout", Id, "Wait until cancelled", "Soft isolation fixture", "action"),
            new("sample.dotnet.fault.ignore-timeout", Id, "Ignore cancellation", "Soft isolation fixture", "action"),
            new("sample.dotnet.fault.slow-success", Id, "Return success after a test release signal", "Soft isolation fixture", "action")
        ];
        return ValueTask.FromResult(commands);
    }

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        switch (request.CommandId)
        {
            case "sample.dotnet.fault.throw":
                throw new InvalidOperationException("synthetic inproc fault");
            case "sample.dotnet.fault.timeout":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                break;
            case "sample.dotnet.fault.ignore-timeout":
                await Task.Delay(TimeSpan.FromSeconds(8), CancellationToken.None);
                break;
            case "sample.dotnet.fault.slow-success":
                var dataDirectory = _dataDirectory ?? throw new InvalidOperationException("Fixture was not initialized.");
                SoftIsolationFixtureFiles.Touch(dataDirectory, "slow-success.started");
                SoftIsolationFixtureFiles.WaitForFile(dataDirectory, "slow-success.release", TimeSpan.FromSeconds(15));
                break;
        }

        return new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            "fault fixture recovered");
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{}}"""));
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
        return ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Fails initialization after recording enough external evidence for the host
/// to prove that every provisional instance was disposed.
/// </summary>
public sealed class InitializeFailureDotNetModule : IMptModule
{
    private string? _dataDirectory;

    public string Id => "sample.dotnet.initialize-failure";
    public string PackageId => "sample-dotnet-initialize-failure";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        SoftIsolationFixtureFiles.Increment(context.DataDirectory, "initialize-attempts.txt");
        var loadContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(GetType().Assembly);
        if (loadContext is not null)
        {
            var dataDirectory = context.DataDirectory;
            loadContext.Unloading += _ => SoftIsolationFixtureFiles.Increment(dataDirectory, "contexts-unloading.txt");
        }

        throw new InvalidOperationException("synthetic initialization failure");
    }

    ~InitializeFailureDotNetModule()
    {
        if (_dataDirectory is not null)
        {
            SoftIsolationFixtureFiles.Increment(_dataDirectory, "finalized-instances.txt");
        }
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<ModuleStatusSnapshot>(new InvalidOperationException("Initialization must fail."));

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IReadOnlyList<MptCommandDescriptor>>(new InvalidOperationException("Initialization must fail."));

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromException<CommandExecutionResult>(new InvalidOperationException("Initialization must fail."));

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<SettingsSchemaDocument>(new InvalidOperationException("Initialization must fail."));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<SettingsSnapshotDocument>(new InvalidOperationException("Initialization must fail."));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken) =>
        ValueTask.FromException<SettingsValidationResult>(new InvalidOperationException("Initialization must fail."));

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<IReadOnlyList<UiSurfaceDescriptor>>(new InvalidOperationException("Initialization must fail."));

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_dataDirectory is not null)
        {
            SoftIsolationFixtureFiles.Increment(_dataDirectory, "disposed-instances.txt");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Supplies custom event enumerators whose setup or Current accessor can ignore
/// cancellation until the test writes an explicit release marker.
/// </summary>
public sealed class EventFaultInjectionDotNetModule : IMptModule
{
    private string? _dataDirectory;

    public string Id => "sample.dotnet.event-fault";
    public string PackageId => "sample-dotnet-event-fault";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        SoftIsolationFixtureFiles.Increment(context.DataDirectory, "instances.txt");
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "events"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            "Event fault fixture is ready.",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("soft-isolation", "Event fault fixture", true, "Ready")],
            1));

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "unused"));

    public IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, CancellationToken cancellationToken)
    {
        var dataDirectory = _dataDirectory ?? throw new InvalidOperationException("Fixture was not initialized.");
        // The host's event relay consumes this stream with its own cursor, so the
        // blocking phase is selected by a control file instead of the cursor value.
        var mode = ReadBlockingMode(dataDirectory);
        return new BlockingEventEnumerable(Id, dataDirectory, mode);
    }

    private static BlockingEventMode ReadBlockingMode(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "blocking-mode.txt");
        if (!File.Exists(path))
        {
            return BlockingEventMode.None;
        }

        return File.ReadAllText(path).Trim() switch
        {
            "open" => BlockingEventMode.Open,
            "current" => BlockingEventMode.Current,
            _ => BlockingEventMode.None
        };
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{}}"""));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsValidationResult(true, []));

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_dataDirectory is not null)
        {
            SoftIsolationFixtureFiles.Increment(_dataDirectory, "disposed-instances.txt");
        }

        return ValueTask.CompletedTask;
    }

    private enum BlockingEventMode
    {
        None,
        Open,
        Current
    }

    private sealed class BlockingEventEnumerable(string moduleId, string dataDirectory, BlockingEventMode mode)
        : IAsyncEnumerable<MptModuleEvent>
    {
        public IAsyncEnumerator<MptModuleEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (mode == BlockingEventMode.Open)
            {
                SoftIsolationFixtureFiles.Touch(dataDirectory, "event-open.started");
                SoftIsolationFixtureFiles.WaitForFile(dataDirectory, "event-open.release", TimeSpan.FromSeconds(15));
            }

            return new BlockingEventEnumerator(moduleId, dataDirectory, mode);
        }
    }

    private sealed class BlockingEventEnumerator(string moduleId, string dataDirectory, BlockingEventMode mode)
        : IAsyncEnumerator<MptModuleEvent>
    {
        private bool _moved;

        public MptModuleEvent Current
        {
            get
            {
                if (mode == BlockingEventMode.Current)
                {
                    SoftIsolationFixtureFiles.Touch(dataDirectory, "event-current.started");
                    SoftIsolationFixtureFiles.WaitForFile(dataDirectory, "event-current.release", TimeSpan.FromSeconds(15));
                }

                return new MptModuleEvent(
                    moduleId,
                    1,
                    "fixture.event",
                    DateTimeOffset.UtcNow,
                    new JsonObject { ["state"] = "ready" });
            }
        }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_moved)
            {
                return ValueTask.FromResult(false);
            }

            _moved = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class DisposeTrackingDotNetModule : IMptModule
{
    private string? _dataDirectory;

    public string Id => "sample.dotnet.dispose-tracking";
    public string PackageId => "sample-dotnet-dispose-tracking";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleStatusSnapshot(Id, "running", "Dispose fixture", DateTimeOffset.UtcNow, [], 1));

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "unused"));

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{}}"""));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsValidationResult(true, []));

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        if (_dataDirectory is not null)
        {
            SoftIsolationFixtureFiles.Increment(_dataDirectory, "disposed-instances.txt");
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class BlockingFinalizerDotNetModule : IMptModule
{
    private string? _dataDirectory;
    private FinalizerSentinel? _sentinel;

    public string Id => "sample.dotnet.blocking-finalizer";
    public string PackageId => "sample-dotnet-blocking-finalizer";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        _sentinel = new FinalizerSentinel(context.DataDirectory);
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ModuleStatusSnapshot(Id, "running", "Finalizer fixture", DateTimeOffset.UtcNow, [], 1));

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "unused"));

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSchemaDocument(Id, """{"type":"object","properties":{}}"""));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsValidationResult(true, []));

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _sentinel = null;
        if (_dataDirectory is not null)
        {
            SoftIsolationFixtureFiles.Increment(_dataDirectory, "disposed-instances.txt");
        }

        return ValueTask.CompletedTask;
    }

    private sealed class FinalizerSentinel(string dataDirectory)
    {
        ~FinalizerSentinel()
        {
            try
            {
                SoftIsolationFixtureFiles.Touch(dataDirectory, "finalizer.started");
                SoftIsolationFixtureFiles.WaitForFile(dataDirectory, "finalizer.release", TimeSpan.FromSeconds(15));
            }
            catch
            {
                // Finalizers must remain non-throwing even when a test directory is removed.
            }
        }
    }
}

internal static class SoftIsolationFixtureFiles
{
    private static readonly object CounterGate = new();

    public static void Increment(string directory, string fileName)
    {
        lock (CounterGate)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            var current = File.Exists(path) && int.TryParse(File.ReadAllText(path), out var parsed) ? parsed : 0;
            File.WriteAllText(path, (current + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    public static void Touch(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "ready");
    }

    public static void WaitForFile(string directory, string fileName, TimeSpan safetyTimeout)
    {
        var path = Path.Combine(directory, fileName);
        var deadline = DateTime.UtcNow + safetyTimeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }
}

