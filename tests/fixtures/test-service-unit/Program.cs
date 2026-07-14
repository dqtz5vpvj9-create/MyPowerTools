using System.IO.Pipes;

// Minimal Service Unit fixture for the A3 Process Gate.
// Behaviour required by the gate:
//   - writes a heartbeat line (with PID) to stdout every second
//   - writes the same heartbeat to <heartbeatFile> if provided, so the gate can observe liveness externally
//   - when --pipe <name> is given, answers a named-pipe readiness probe by replying "pong" to any line
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
            server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync(cancellationToken);
            // Read one line, reply pong.
            using var reader = new StreamReader(server);
            using var writer = new StreamWriter(server) { AutoFlush = true };
            var line = await reader.ReadLineAsync(cancellationToken);
            if (!string.IsNullOrEmpty(line))
            {
                await writer.WriteLineAsync("pong");
            }
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
