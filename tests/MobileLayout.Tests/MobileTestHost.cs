using Grpc.Core;
using MyPowerTools.HostControl;
using MyPowerTools.Protocol.HostControl.V1;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MobileLayout.Tests;

/// <summary>
/// Serves tool descriptors through the real HostControl client so the phone library, the tool
/// activation path and the surface loader all run production code with only the transport stubbed.
/// </summary>
internal sealed class TestToolHost : IDisposable
{
    private readonly CallInvoker? _previous;
    private readonly Dictionary<string, HostProto.ToolDescriptor> _descriptors;
    private readonly TaskCompletionSource _listGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdInitialLoad;

    public TestToolHost(params HostProto.ToolDescriptor[] descriptors)
    {
        _descriptors = descriptors.ToDictionary(descriptor => descriptor.ToolId, StringComparer.OrdinalIgnoreCase);
        _previous = HostControlClient.EmbeddedInvoker;
        HostControlClient.EmbeddedInvoker = new Invoker(this);
    }

    public IReadOnlyCollection<string> GetToolRequests => _getToolRequests;
    private readonly List<string> _getToolRequests = [];

    public IReadOnlyCollection<string> ListToolsRequests => _listToolsRequests;
    private readonly List<string> _listToolsRequests = [];

    /// <summary>Models a Runner that answers the first catalog load slowly.</summary>
    public TestToolHost HoldInitialListTools()
    {
        _holdInitialLoad = 1;
        return this;
    }

    public void CompleteInitialLoad() => _listGate.TrySetResult();

    internal Task WaitInitialLoadAsync() => Volatile.Read(ref _holdInitialLoad) == 0
        ? Task.CompletedTask
        : _listGate.Task;

    public void Dispose() => HostControlClient.EmbeddedInvoker = _previous;

    /// <summary>A phone tool that the local runtime really registers.</summary>
    public static HostProto.ToolDescriptor PhoneTool(string toolId, string title, string description = "test tool")
    {
        var descriptor = new HostProto.ToolDescriptor
        {
            ToolId = toolId,
            OwnerModuleId = toolId,
            Title = title,
            Description = description,
            Category = "Files",
            Icon = $"tool.{toolId}",
            PrimaryRouteId = "main",
            Availability = "available",
            State = "ready",
            // native-tool keeps the surface host real without loading a dotnet assembly in the test.
            ToolType = "native-tool",
            SourceDirectory = "/nonexistent"
        };
        descriptor.Routes.Add(new ToolRoute
        {
            RouteId = "main",
            Title = title,
            SurfaceKind = "native"
        });
        return descriptor;
    }

    /// <summary>The catalog a real Android build reports for the three shipped phone tools.</summary>
    public static HostProto.ToolDescriptor[] DefaultPhoneCatalog() =>
    [
        PhoneTool("file-transfer", "文件互传", "通过 Tailscale 直传，或使用国内网盘中转。"),
        PhoneTool("remote-notifications", "Remote Notifications", "Receive notifications from paired computers."),
        PhoneTool("remote-commands", "Remote Commands", "Run saved SSH commands on a paired computer.")
    ];

    private sealed class Invoker(TestToolHost host) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? hostName,
            CallOptions options,
            TRequest request)
        {
            if (method.Name == "GetTool")
            {
                var id = (string)typeof(TRequest).GetProperty("ToolId")!.GetValue(request)!;
                lock (host._getToolRequests)
                {
                    host._getToolRequests.Add(id);
                }

                if (host._descriptors.TryGetValue(id, out var descriptor))
                {
                    return Unary((TResponse)(object)descriptor);
                }

                return Failure<TResponse>(method.Name);
            }

            if (method.Name == "ListTools")
            {
                lock (host._listToolsRequests)
                {
                    host._listToolsRequests.Add("list");
                }

                var response = new ListToolsResponse();
                response.Tools.AddRange(host._descriptors.Values);
                return Unary((TResponse)(object)response, host.WaitInitialLoadAsync());
            }

            return Failure<TResponse>(method.Name);
        }

        private static AsyncUnaryCall<T> Unary<T>(T response, Task? responseTask = null) => new(
            responseTask is null ? Task.FromResult(response) : responseTask.ContinueWith(_ => response, TaskScheduler.Default),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        private static AsyncUnaryCall<T> Failure<T>(string name) => new(
            Task.FromException<T>(new RpcException(new Status(StatusCode.Unimplemented, name))),
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.Unimplemented, name),
            () => new Metadata(),
            () => { });

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options) => throw new NotSupportedException(method.Name);

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options) => throw new NotSupportedException(method.Name);

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options, TRequest request) => throw new NotSupportedException(method.Name);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options, TRequest request) => throw new NotSupportedException(method.Name);
    }
}

/// <summary>
/// Controllable device backend. The default snapshot is the truthful "no paired device" one, so a
/// test opts in to peers, relay state and transfer history explicitly.
/// </summary>
internal sealed class FakeMobileDeviceService : IMobileDeviceService
{
    public MobileDeviceSnapshot Snapshot { get; set; } = new(
        LocalDeviceName: "测试手机",
        Receiving: false,
        Peers: [],
        RelayConfigured: false,
        RelayRunning: false,
        RelayDescription: null,
        Activities: []);

    public Exception? SnapshotFailure { get; set; }
    public Exception? CheckFailure { get; set; }
    public Exception? ImportFailure { get; set; }
    public Exception? RemoveFailure { get; set; }
    public string PairingCode { get; set; } = "MPT-PAIR-4F2A-9C31";
    public MobilePeerConnectionState CheckResult { get; set; } = MobilePeerConnectionState.Online;

    public int SnapshotCalls { get; private set; }
    public int CheckCalls { get; private set; }
    public List<string> ImportedCodes { get; } = [];
    public List<string> RemovedPeers { get; } = [];

    public Task<MobileDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        SnapshotCalls++;
        return SnapshotFailure is null
            ? Task.FromResult(Snapshot)
            : Task.FromException<MobileDeviceSnapshot>(SnapshotFailure);
    }

    public Task ImportPairingAsync(string code, CancellationToken cancellationToken = default)
    {
        if (ImportFailure is not null)
        {
            return Task.FromException(ImportFailure);
        }

        ImportedCodes.Add(code);
        return Task.CompletedTask;
    }

    public Task RemovePeerAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (RemoveFailure is not null)
        {
            return Task.FromException(RemoveFailure);
        }

        RemovedPeers.Add(deviceId);
        Snapshot = Snapshot with { Peers = Snapshot.Peers.Where(peer => peer.DeviceId != deviceId).ToArray() };
        return Task.CompletedTask;
    }

    public Task<string> GetPairingCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(PairingCode);

    public Task<MobilePeerInfo> CheckPeerAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        CheckCalls++;
        if (CheckFailure is not null)
        {
            return Task.FromException<MobilePeerInfo>(CheckFailure);
        }

        var peer = Snapshot.Peers.First(item => item.DeviceId == deviceId);
        var updated = peer with
        {
            ConnectionState = CheckResult,
            CheckedAt = DateTimeOffset.Now,
            SupportsToolControl = peer.SupportsToolControl
        };
        Snapshot = Snapshot with
        {
            Peers = Snapshot.Peers.Select(item => item.DeviceId == deviceId ? updated : item).ToArray()
        };
        return Task.FromResult(updated);
    }
}

/// <summary>Control-module fake: returns a configured device list without any host or module.</summary>
internal sealed class FakeMobileControlDeviceService : IMobileControlDeviceService
{
    public MobileControlDeviceSnapshot Snapshot { get; set; } = new([]);
    public int Calls { get; private set; }

    public Task<MobileControlDeviceSnapshot> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Snapshot);
    }

    public static MobileControlDevice Device(string id, string name, string state = "imported", string detail = "") =>
        new(id, name, "windows", state, detail, true);
}

/// <summary>Creates a phone shell service bundle whose preference files live under the test output.</summary>
internal static class MobileTestEnvironment
{
    public static MobileShellServices CreateServices(
        IMobileDeviceService? devices = null,
        IMobileControlDeviceService? controlDevices = null)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "mobile-test-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var products = new ShellToolProductService(new ShellToolPreferencesStore(Path.Combine(root, "tool-preferences.json")));
        return new MobileShellServices(
            products,
            devices ?? new FakeMobileDeviceService(),
            new ShellAppearanceService(Path.Combine(root, "shell-preferences.json")),
            new ShellPageDataService(),
            controlDevices ?? new FakeMobileControlDeviceService());
    }
}
