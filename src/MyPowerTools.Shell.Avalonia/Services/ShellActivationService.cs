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
            try
            {
                await using var server = MptNamedPipePolicy.CreateServer(
                    _pipeName,
                    PipeDirection.InOut,
                    maxInstances: 1);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                var header = new byte[sizeof(int)];
                await ReadExactlyAsync(server, header, cancellationToken).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaximumPayloadLength)
                {
                    continue;
                }

                var payload = new byte[length];
                await ReadExactlyAsync(server, payload, cancellationToken).ConfigureAwait(false);
                var request = JsonSerializer.Deserialize<ShellActivationRequest>(payload);
                if (request is not null)
                {
                    await _handler(request).ConfigureAwait(false);
                    await server.WriteAsync(
                        new byte[] { ActivationAcknowledged },
                        cancellationToken).ConfigureAwait(false);
                    await server.FlushAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        _afterAcknowledged?.Invoke(request);
                    }
                    catch
                    {
                        // A post-acknowledgement lifecycle action cannot invalidate delivery.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or EndOfStreamException or JsonException)
            {
                // Recreate the one-shot activation endpoint after malformed or disconnected clients.
            }
            catch
            {
                // Keep the activation endpoint alive after a handler or platform failure.
            }
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
