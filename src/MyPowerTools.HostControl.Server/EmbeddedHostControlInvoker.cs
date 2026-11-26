using System.Threading.Channels;
using Grpc.Core;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.HostControl;

/// <summary>In-process transport for the existing HostControl service, including its validation and audit.</summary>
public sealed class EmbeddedHostControlInvoker : CallInvoker
{
    private readonly Binder _binder = new();
    public EmbeddedHostControlInvoker(HostControlGrpcService service) => HostProto.HostControl.BindService(_binder, service);

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
        string? host, CallOptions options, TRequest request)
    {
        var context = new Context(options);
        var handler = (UnaryServerMethod<TRequest, TResponse>)_binder.Handlers[method.FullName];
        async Task<TResponse> Run()
        {
            try { return await handler(request, context).WaitAsync(context.CancellationToken); }
            catch (RpcException) { throw; }
            catch (Exception ex) { throw TranslateUnary(ex, context); }
            finally { context.Dispose(); }
        }
        return new AsyncUnaryCall<TResponse>(Run(), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), context.Cancel);
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        => AsyncUnaryCall(method, host, options, request).ResponseAsync.GetAwaiter().GetResult();

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
        string? host, CallOptions options, TRequest request)
    {
        var context = new Context(options);
        var queue = Channel.CreateBounded<TResponse>(32);
        var handler = (ServerStreamingServerMethod<TRequest, TResponse>)_binder.Handlers[method.FullName];
        _ = Run();
        async Task Run()
        {
            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await handler(request, new Writer<TResponse>(queue.Writer, context.CancellationToken), context);
                context.CancellationToken.ThrowIfCancellationRequested();
                queue.Writer.TryComplete();
            }
            catch (RpcException ex) { queue.Writer.TryComplete(ex); }
            catch (OperationCanceledException ex) { queue.Writer.TryComplete(context.DeadlineElapsed ? TranslateUnary(ex, context) : ex); }
            catch (Exception ex) { queue.Writer.TryComplete(TranslateUnary(ex, context)); }
            finally { context.Dispose(); }
        }
        return new AsyncServerStreamingCall<TResponse>(new Reader<TResponse>(queue.Reader), Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), context.Cancel);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw new NotSupportedException("HostControl does not define client streaming methods.");
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
        => throw new NotSupportedException("HostControl does not define duplex methods.");

    /// <summary>
    /// Matches the status a Kestrel-hosted HostControl call would report for the same failure,
    /// so desktop and embedded (Android) clients observe identical <see cref="RpcException"/> shapes.
    /// </summary>
    private static RpcException TranslateUnary(Exception exception, Context context)
    {
        if (exception is RpcException rpc)
        {
            return rpc;
        }

        if (exception is OperationCanceledException)
        {
            return context.DeadlineElapsed
                ? new RpcException(new Status(StatusCode.DeadlineExceeded, "Deadline Exceeded"))
                : new RpcException(new Status(StatusCode.Cancelled, "Call canceled by the client."));
        }

        return new RpcException(new Status(StatusCode.Unknown, exception.Message));
    }

    private sealed class Binder : ServiceBinderBase
    {
        public Dictionary<string, Delegate> Handlers { get; } = new();
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, UnaryServerMethod<TRequest, TResponse>? handler) => Handlers.Add(method.FullName, handler!);
        public override void AddMethod<TRequest, TResponse>(Method<TRequest, TResponse> method, ServerStreamingServerMethod<TRequest, TResponse>? handler) => Handlers.Add(method.FullName, handler!);
    }
    private sealed class Writer<T>(ChannelWriter<T> writer, CancellationToken token) : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(T message) => writer.WriteAsync(message, token).AsTask();
    }
    private sealed class Reader<T>(ChannelReader<T> reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;
        public async Task<bool> MoveNext(CancellationToken token)
        {
            if (!await reader.WaitToReadAsync(token)) return false;
            Current = await reader.ReadAsync(token); return true;
        }
    }
    private sealed class Context : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly CallOptions _options;
        private readonly CancellationToken _token;
        public Context(CallOptions options)
        {
            _options = options;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
            _token = _cancellation.Token;
            if (options.Deadline is { } deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) _cancellation.Cancel();
                else _cancellation.CancelAfter(remaining);
            }
        }
        public void Cancel() { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() => _cancellation.Dispose();
        public bool DeadlineElapsed => _options.Deadline is { } deadline && deadline <= DateTime.UtcNow;
        protected override string MethodCore => "embedded";
        protected override string HostCore => "application";
        protected override string PeerCore => "in-process";
        protected override DateTime DeadlineCore => _options.Deadline ?? DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => _options.Headers ?? new();
        protected override CancellationToken CancellationTokenCore => _token;
        protected override Metadata ResponseTrailersCore { get; } = new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new("in-process", new Dictionary<string, List<AuthProperty>>());
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
