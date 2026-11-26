using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using MyPowerTools.Cli;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.TransferMcp;

// Requests own their IPC channels and subscriptions; no subprocess or chat registry.
public sealed class TransferAccess
{
    private static readonly TimeSpan MutatingCommandGraceTimeout = TimeSpan.FromSeconds(60);

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
            if (string.IsNullOrEmpty(auth))
            {
                throw new InvalidOperationException("Runner authentication token is missing.");
            }

            var address = Environment.GetEnvironmentVariable("MPT_ENDPOINT_ADDRESS");
            var endpoint = string.IsNullOrEmpty(address)
                ? IpcEndpoint.RunnerDefault(PlatformId.Current())
                : new IpcEndpoint(
                    OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
                    address);

            using var client = HostControlClient.ForEndpoint(endpoint, auth);
            using var output = new StringWriter();
            using var diagnostics = new StringWriter();

            var code = await TransferCli.RunAsync(args, output, diagnostics, new RequestInvoker(client, this), token);
            token.ThrowIfCancellationRequested();

            var rawOutput = output.ToString();
            if (TryParseJson(rawOutput, out var structured))
            {
                return new CallToolResult
                {
                    IsError = code != 0,
                    Content = [new TextContentBlock { Text = rawOutput }],
                    StructuredContent = structured
                };
            }

            var diagMessage = diagnostics.ToString().Trim();
            var fallbackMessage = !string.IsNullOrEmpty(diagMessage)
                ? diagMessage
                : string.IsNullOrWhiteSpace(rawOutput)
                    ? "Transfer command produced no output."
                    : "Transfer command returned non-JSON output.";

            var fallbackPayload = new JsonObject
            {
                ["ok"] = false,
                ["error"] = new JsonObject
                {
                    ["code"] = "invalid_output",
                    ["message"] = fallbackMessage
                }
            }.ToJsonString();

            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = fallbackPayload }],
                StructuredContent = JsonSerializer.Deserialize<JsonElement>(fallbackPayload)
            };
        }
        finally
        {
            Interlocked.Decrement(ref activeCalls);
        }
    }

    private static bool TryParseJson(string rawOutput, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return false;
        }

        try
        {
            element = JsonSerializer.Deserialize<JsonElement>(rawOutput);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class RequestInvoker(HostControlClient client, TransferAccess owner) : ITransferCommandInvoker
    {
        private readonly TransferCli.HostInvoker inner = new(client);

        public async Task<JsonNode> InvokeAsync(string command, JsonObject args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (IsReadOnlyCommand(command))
            {
                return await inner.InvokeAsync(command, args, token);
            }

            // Once dispatched, a mutating Runner command should finish without abrupt transport
            // cancellation interrupting an accepted send, while still staying bounded in time.
            using var boundedCts = new CancellationTokenSource(MutatingCommandGraceTimeout);
            return await inner.InvokeAsync(command, args, boundedCts.Token);
        }

        public async IAsyncEnumerable<bool> EventsAsync([EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref owner.activeSubscriptions);
            try
            {
                await foreach (var changed in inner.EventsAsync(token))
                {
                    yield return changed;
                }
            }
            finally
            {
                Interlocked.Decrement(ref owner.activeSubscriptions);
            }
        }

        private static bool IsReadOnlyCommand(string command)
        {
            return command is
                "file-transfer.inspect"
                or "file-transfer.assistant.inspect"
                or "file-transfer.assistant.devices"
                or "file-transfer.cloud.accounts.inspect"
                or "file-transfer.drop.list";
        }
    }
}
