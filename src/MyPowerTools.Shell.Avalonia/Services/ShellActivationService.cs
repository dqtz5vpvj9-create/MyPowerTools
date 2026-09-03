using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;

namespace MyPowerTools.Shell.Avalonia.Services;

internal sealed record ShellActivationRequest(
    ToolActivationRequest? ToolActivation,
    bool ShowShell = true,
    bool ShutdownShell = false)
{
    public static ShellActivationRequest FocusShell { get; } = new((ToolActivationRequest?)null);
    public static ShellActivationRequest PrewarmShell { get; } = new(
        (ToolActivationRequest?)null,
        ShowShell: false);
    public static ShellActivationRequest Shutdown { get; } = new(
        (ToolActivationRequest?)null,
        ShowShell: false,
        ShutdownShell: true);

    public static ShellActivationRequest ForTool(ToolActivationRequest activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        return new ShellActivationRequest(
            activation,
            ShowShell: !activation.SuppressShellWindow);
    }
}

internal sealed class ShellInstanceLock : IDisposable
{
    internal const string MutexName = @"Local\MyPowerTools.Shell";

    private readonly Mutex? _mutex;
    private readonly bool _ownsMutex;

    private ShellInstanceLock(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool Acquired => _ownsMutex;

    public static ShellInstanceLock Acquire(string? mutexName = null)
    {
        var effectiveName = mutexName ?? (OperatingSystem.IsWindows() ? MutexName : "MyPowerTools.Shell");
        var mutex = new Mutex(initiallyOwned: false, effectiveName);
        try
        {
            return new ShellInstanceLock(mutex, mutex.WaitOne(0));
        }
        catch (AbandonedMutexException)
        {
            return new ShellInstanceLock(mutex, ownsMutex: true);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsMutex && _mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Application shutdown can complete on a different managed thread.
            }
        }

        _mutex?.Dispose();
    }
}

internal sealed class ShellActivationPipe : IAsyncDisposable
{
    internal const string PipeName = "MyPowerTools.ShellActivation";
    private const int MaximumPayloadLength = 64 * 1024;
    private const byte ActivationAcknowledged = 0x06;
    private static readonly byte[] AcknowledgementFrame = [ActivationAcknowledged];
    private static readonly TimeSpan RequestReceiveTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ListenerRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Windows stamps the Low integrity label on the first pipe instance, and the access right
    /// that asks for it overlaps FILE_FLAG_FIRST_PIPE_INSTANCE, so only one server instance can
    /// exist there at a time. Unix tears the listening socket down together with its last
    /// instance and drops whatever the kernel had queued on it, so there the listener has to
    /// overlap instances across requests or a second launcher racing the first loses its
    /// connection and starts a duplicate Shell.
    /// </summary>
    private static readonly int ServerInstances =
        OperatingSystem.IsWindows() ? 1 : NamedPipeServerStream.MaxAllowedServerInstances;

    private static bool ServesConnectionsConcurrently => !OperatingSystem.IsWindows();

    private readonly Func<ShellActivationRequest, Task> _handler;
    private readonly Action<ShellActivationRequest>? _afterAcknowledged;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public ShellActivationPipe(
        Func<ShellActivationRequest, Task> handler,
        string? pipeName = null,
        Action<ShellActivationRequest>? afterAcknowledged = null)
    {
        _handler = handler;
        _afterAcknowledged = afterAcknowledged;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName;
    }

    public void Start()
    {
        if (_serverTask is not null)
        {
            return;
        }

        _serverTask = RunServerAsync(_cancellation.Token);
    }

    public static async Task<bool> TryForwardAsync(
        ShellActivationRequest request,
        TimeSpan? totalTimeout = null,
        CancellationToken cancellationToken = default,
        string? pipeName = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        if (payload.Length is 0 or > MaximumPayloadLength)
        {
            return false;
        }

        var deadline = DateTimeOffset.UtcNow.Add(totalTimeout ?? TimeSpan.FromSeconds(10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var client = new NamedPipeClientStream(
                    ".",
                    string.IsNullOrWhiteSpace(pipeName) ? PipeName : pipeName,
                    PipeDirection.InOut,
                    MptNamedPipePolicy.ClientOptions);
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
                await client.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
                TransferForegroundPermission(client);

                var header = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
                await client.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await client.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await client.FlushAsync(cancellationToken).ConfigureAwait(false);

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseTimeout.CancelAfter(remaining);
                var response = new byte[1];
                await ReadExactlyAsync(client, response, responseTimeout.Token).ConfigureAwait(false);
                return response[0] == ActivationAcknowledged;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static void TransferForegroundPermission(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var shellProcessId) &&
            shellProcessId != 0)
        {
            _ = AllowSetForegroundWindow(shellProcessId);
        }
    }

    private async Task RunServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? accepted = null;
            try
            {
                accepted = MptNamedPipePolicy.CreateServer(
                    _pipeName,
                    PipeDirection.InOut,
                    ServerInstances);
                await accepted.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietlyAsync(accepted).ConfigureAwait(false);
                break;
            }
            catch
            {
                // Another host may hold the endpoint for a moment during its own shutdown.
                // Back off rather than spinning the CPU while it goes away.
                await DisposeQuietlyAsync(accepted).ConfigureAwait(false);
                try
                {
                    await Task.Delay(ListenerRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            if (ServesConnectionsConcurrently)
            {
                // The next instance is created right away, which keeps the Unix listening socket
                // bound while this request is served.
                _ = ServeConnectionAsync(accepted, cancellationToken);
            }
            else
            {
                await ServeConnectionAsync(accepted, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Acknowledges the request as soon as it has been received and only then presents the
    /// window. A launcher waits milliseconds for the acknowledgement while presentation waits on
    /// the workspace, so the two cannot share a deadline without a slow UI making the launcher
    /// start a second Shell.
    /// </summary>
    private async Task ServeConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        ShellActivationRequest? request;
        try
        {
            await using (server.ConfigureAwait(false))
            {
                request = await ReceiveRequestAsync(server, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                await server.WriteAsync(AcknowledgementFrame, cancellationToken).ConfigureAwait(false);
                await server.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or EndOfStreamException or JsonException or OperationCanceledException)
        {
            // A malformed or disconnected client leaves the endpoint ready for the next launcher.
            return;
        }

        // The acknowledged request is now owned by this process, so the endpoint is free for the
        // next launcher while the window is still coming up.
        _ = PresentAsync(request);
    }

    private static async Task<ShellActivationRequest?> ReceiveRequestAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        // A peer that connects and then stops sending would otherwise hold the endpoint open
        // indefinitely, and on Windows that single instance is the whole activation surface.
        using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        receiveTimeout.CancelAfter(RequestReceiveTimeout);

        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(server, header, receiveTimeout.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumPayloadLength)
        {
            return null;
        }

        var payload = new byte[length];
        await ReadExactlyAsync(server, payload, receiveTimeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ShellActivationRequest>(payload);
    }

    private async Task PresentAsync(ShellActivationRequest request)
    {
        try
        {
            await _handler(request).ConfigureAwait(false);
        }
        catch
        {
            // A failed presentation cannot take the activation endpoint down with it.
        }

        try
        {
            _afterAcknowledged?.Invoke(request);
        }
        catch
        {
            // A post-acknowledgement lifecycle action cannot invalidate delivery.
        }
    }

    private static async ValueTask DisposeQuietlyAsync(NamedPipeServerStream? server)
    {
        if (server is null)
        {
            return;
        }

        try
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellation.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
