using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

// Minimal Service Unit fixture for the A3 Process Gate.
// Behaviour required by the gate:
//   - writes a heartbeat line (with PID) to stdout every second
//   - writes the same heartbeat to <heartbeatFile> if provided, so the gate can observe liveness externally
//   - when --pipe <name> is given, answers the standard framed { command: "ping" } readiness probe
//   - exits cleanly on CTRL_C / SIGINT (graceful stop path)
var heartbeatFile = GetOption(args, "--heartbeat-file");
var pipeName = GetOption(args, "--pipe");
var intervalMs = int.TryParse(GetOption(args, "--interval-ms"), out var iv) ? iv : 1000;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var pipeCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
if (!string.IsNullOrEmpty(pipeName))
{
    _ = Task.Run(() => ServeReadinessPipe(pipeName!, pipeCts.Token));
}

var pid = Environment.ProcessId;
try
{
    while (!cts.Token.IsCancellationRequested)
    {
        var line = $"heartbeat pid={pid} ts={DateTimeOffset.UtcNow:O}";
        Console.WriteLine(line);
        if (!string.IsNullOrEmpty(heartbeatFile))
        {
            try
            {
                await File.AppendAllTextAsync(heartbeatFile, line + Environment.NewLine, cts.Token);
            }
            catch
            {
                // heartbeat file is best-effort
            }
        }

        try
        {
            await Task.Delay(intervalMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
    // expected on stop
}

return 0;

static async Task ServeReadinessPipe(string name, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        NamedPipeServerStream? server = null;
        try
        {
            server = new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await server.WaitForConnectionAsync(cancellationToken);

            var header = new byte[4];
            await ReadExactlyAsync(server, header, cancellationToken);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > 1024 * 1024)
            {
                throw new InvalidDataException($"Invalid readiness request length {length}.");
            }

            var requestPayload = new byte[length];
            await ReadExactlyAsync(server, requestPayload, cancellationToken);
            using var request = JsonDocument.Parse(requestPayload);
            var command = request.RootElement.TryGetProperty("command", out var commandElement)
                ? commandElement.GetString()
                : null;
            object response = string.Equals(command, "ping", StringComparison.Ordinal)
                ? new { ok = true, data = new { pong = true }, error = (string?)null }
                : new { ok = false, data = (object?)null, error = $"Unknown command '{command}'." };
            var responsePayload = JsonSerializer.SerializeToUtf8Bytes(response);
            BinaryPrimitives.WriteInt32LittleEndian(header, responsePayload.Length);
            await server.WriteAsync(header, cancellationToken);
            await server.WriteAsync(responsePayload, cancellationToken);
            await server.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch
        {
            // a single failed connection must not kill the readiness server
        }
        finally
        {
            server?.Dispose();
        }
    }
}

static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
        if (read == 0)
        {
            throw new EndOfStreamException();
        }

        offset += read;
    }
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}
