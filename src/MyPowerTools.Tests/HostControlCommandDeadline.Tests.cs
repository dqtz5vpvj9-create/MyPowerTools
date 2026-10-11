using System.Text.Json.Nodes;
using Grpc.Core;
using MyPowerTools.HostControl;
using Proto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class HostControlCommandDeadlineTests
{
    [Fact]
    public async Task ExecuteUsesHostDeadlineAndPreservesCallerCancellation()
    {
        var previous = HostControlClient.EmbeddedInvoker;
        var invoker = new RecordingInvoker();
        using var cancellation = new CancellationTokenSource();
        try
        {
            HostControlClient.EmbeddedInvoker = invoker;
            using var client = HostControlClient.ForDefaultEndpoint();
            await client.ExecuteCommandAsync("download", "file-transfer.assistant.open", new JsonObject(), cancellation.Token);
            Assert.Null(invoker.Options.Deadline);
            Assert.Equal(cancellation.Token, invoker.Options.CancellationToken);
        }
        finally { HostControlClient.EmbeddedInvoker = previous; }
    }

    private sealed class RecordingInvoker : CallInvoker
    {
        public CallOptions Options { get; private set; }
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (method.Name == "ExecuteCommand") Options = options;
            var response = (TResponse)(object)new Proto.CommandExecutionResponse { State = "succeeded" };
            return new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
