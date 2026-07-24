using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DoubaoAgent.Surface.Services;
using MyPowerTools.Abstractions;
using RemoteNotifications.Surface.Services;

namespace MyPowerTools.Tests;

public sealed class ServiceUnitSurfaceClientTests
{
    [Fact]
    public async Task Remote_notifications_uses_the_readiness_pipe_from_the_scoped_snapshot()
    {
        var pipeName = $"mpt-rn-surface-{Guid.NewGuid():N}";
        var snapshot = Unit(
            RemoteNotificationsServiceClient.UnitId,
            "remote-notifications",
            pipeName);
        var server = ServeOnceAsync(pipeName, new
        {
            connectionState = "idle",
            lastPoll = "2026-07-16T00:00:00Z",
            lastError = "none",
            latest = "never",
            fetched = 0,
            shown = 0,
            pollIntervalSeconds = 30
        });

        var state = await new RemoteNotificationsServiceClient(new FakeServiceUnitClient(snapshot))
            .GetStateAsync();
        await server;

        Assert.Equal("idle", state.ConnectionState);
        Assert.Equal(30, state.PollIntervalSeconds);
    }

    [Fact]
    public async Task Remote_notifications_uses_the_readiness_unix_socket_from_the_scoped_snapshot()
    {
        Assert.True(Socket.OSSupportsUnixDomainSockets);
        var socketPath = Path.Combine(Path.GetTempPath(), $"mpt-rn-{Guid.NewGuid():N}.sock");
        var snapshot = Unit(
            RemoteNotificationsServiceClient.UnitId,
            "remote-notifications",
            socketPath,
            "unix-socket");
        var server = ServeUnixSocketOnceAsync(socketPath, new
        {
            connectionState = "connected",
            lastPoll = "2026-07-18T00:00:00Z",
            lastError = "none",
            latest = "2026-07-18T00:00:00Z",
            fetched = 1,
            shown = 1,
            pollIntervalSeconds = 45
        });

        var state = await new RemoteNotificationsServiceClient(new FakeServiceUnitClient(snapshot))
            .GetStateAsync();
        await server;

        Assert.Equal("connected", state.ConnectionState);
        Assert.Equal(45, state.PollIntervalSeconds);
    }

    [Fact]
    public async Task Doubao_surface_client_uses_the_readiness_pipe_from_the_scoped_snapshot()
    {
        var pipeName = $"mpt-doubao-surface-{Guid.NewGuid():N}";
        var snapshot = Unit(
            DoubaoServiceUnitRuntimeController.UnitId,
            "doubao-agent",
            pipeName);
        var server = ServeOnceAsync(pipeName, new
        {
            securitySafe = true,
            securityDetail = "ready",
            listeners = Array.Empty<object>(),
            ownedProcesses = Array.Empty<object>(),
            toolOnline = true,
            plannerOnline = true,
            mcpOnline = true,
            runtimeRoot = "C:\\runtime",
            checkedAt = "2026-07-16T00:00:00Z"
        });

        var state = await new DoubaoServiceUnitRuntimeController(new FakeServiceUnitClient(snapshot))
            .GetCachedSnapshotAsync();
        await server;

        Assert.True(state.SecuritySafe);
        Assert.True(state.ToolOnline);
        Assert.Equal("C:\\runtime", state.RuntimeRoot);
    }

    private static ServiceUnitSnapshot Unit(
        string unitId,
        string toolId,
        string address,
        string readinessKind = "pipe") => new(
        unitId,
        toolId,
        unitId,
        ServiceUnitState.Active,
        Environment.ProcessId,
        DateTimeOffset.UtcNow,
        TimeSpan.FromSeconds(1),
        "test",
        true,
        ServiceUnitRestartPolicy.Default,
        0,
        null,
        new ServiceUnitReadiness(readinessKind, address, TimeSpan.FromSeconds(5)),
        null,
        1);

    private static async Task ServeOnceAsync(string pipeName, object data)
    {
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await ServeControlExchangeAsync(server, data);
    }

    private static async Task ServeUnixSocketOnceAsync(string socketPath, object data)
    {
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            using var client = await listener.AcceptAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var stream = new NetworkStream(client, ownsSocket: false);
            await ServeControlExchangeAsync(stream, data);
        }
        finally
        {
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
        }
    }

    private static async Task ServeControlExchangeAsync(Stream server, object data)
    {
        var header = new byte[4];
        await ReadExactlyAsync(server, header);
        var requestLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(requestLength, 1, 1024 * 1024);
        var request = new byte[requestLength];
        await ReadExactlyAsync(server, request);
        using var requestJson = JsonDocument.Parse(request);
        Assert.Equal("state", requestJson.RootElement.GetProperty("command").GetString());

        var response = JsonSerializer.SerializeToUtf8Bytes(new { ok = true, data });
        BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
        await server.WriteAsync(header);
        await server.WriteAsync(response);
        await server.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    private sealed class FakeServiceUnitClient(ServiceUnitSnapshot snapshot) : IServiceUnitClient
    {
        public string ToolId => snapshot.ToolId;

        public ValueTask<IReadOnlyList<ServiceUnitSnapshot>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ServiceUnitSnapshot>>([snapshot]);

        public ValueTask<ServiceUnitSnapshot> GetSnapshotAsync(string unitId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<ServiceUnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<ServiceUnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);

        public ValueTask<ServiceUnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(snapshot);

        public ValueTask ReloadAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ServiceUnitEvent> SubscribeEventsAsync(
            EventCursor cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<MptToolLogEntry> TailLogsAsync(
            string unitId,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
