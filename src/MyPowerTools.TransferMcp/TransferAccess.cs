using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using MyPowerTools.Cli;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using ModelContextProtocol.Protocol;

namespace MyPowerTools.TransferMcp;

// Requests own their IPC channels and subscriptions; no subprocess or chat registry.
public sealed class TransferAccess
{
    private int activeCalls;
    private int activeSubscriptions;
    public int ActiveSubscriptions => Volatile.Read(ref activeSubscriptions);
    public int ActiveCalls => Volatile.Read(ref activeCalls);
    public async Task<CallToolResult> Call(string[] args, CancellationToken token)
    {
        Interlocked.Increment(ref activeCalls);
        try
        {
            var auth = HostControlAuthTokenStore.TryReadToken();
            if (string.IsNullOrEmpty(auth)) throw new InvalidOperationException("Runner authentication token is missing.");
            var address = Environment.GetEnvironmentVariable("MPT_ENDPOINT_ADDRESS");
            var endpoint = string.IsNullOrEmpty(address) ? IpcEndpoint.RunnerDefault(PlatformId.Current())
                : new IpcEndpoint(OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket, address);
            using var client = HostControlClient.ForEndpoint(endpoint, auth);
            using var output = new StringWriter();
            using var diagnostics = new StringWriter();
            var code = await TransferCli.RunAsync(args, output, diagnostics, new RequestInvoker(client, this), token);
            token.ThrowIfCancellationRequested();
            var json = output.ToString();
            return new CallToolResult { IsError = code != 0, Content = [new TextContentBlock { Text = json }],
                StructuredContent = JsonSerializer.Deserialize<JsonElement>(json) };
        }
        finally { Interlocked.Decrement(ref activeCalls); }
    }

    private sealed class RequestInvoker(HostControlClient client, TransferAccess owner) : ITransferCommandInvoker
    {
        private readonly TransferCli.HostInvoker inner = new(client);
        public Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Once dispatched, a short Runner command owns its completion. Aborting
            // its transport can quarantine a module still unwinding discovery or
            // interrupt an accepted send. Finish that RPC before disposing its channel;
            // the cancelled request cannot issue another command afterwards.
            return inner.InvokeAsync(command, args, CancellationToken.None);
        }
        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref owner.activeSubscriptions);
            try
            {
                await foreach (var changed in inner.EventsAsync(token)) yield return changed;
            }
            finally { Interlocked.Decrement(ref owner.activeSubscriptions); }
        }
    }

}
