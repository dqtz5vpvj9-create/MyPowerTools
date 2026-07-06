using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace MyPowerTools.HostControl;

public sealed record HostControlAuthOptions(string Token);

public static class HostControlAuthTokenStore
{
    public const string HeaderName = "x-mpt-hostcontrol-token";
    public const string DataRootEnvironmentVariable = "MPT_DATA_ROOT";

    public static string DefaultDataRoot()
    {
        return Environment.GetEnvironmentVariable(DataRootEnvironmentVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
    }

    public static string TokenPath(string? dataRoot = null)
    {
        return Path.Combine(dataRoot ?? DefaultDataRoot(), "state", "hostcontrol.token");
    }

    public static string GetOrCreateToken(string? dataRoot = null)
    {
        var path = TokenPath(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = TryReadToken(dataRoot);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var token = GenerateToken();
        File.WriteAllText(path, token);
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (Exception)
        {
            // File attributes are best-effort across platforms.
        }

        return token;
    }

    public static string? TryReadToken(string? dataRoot = null)
    {
        var path = TokenPath(dataRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        var token = File.ReadAllText(path).Trim();
        return token.Length >= 32 ? token : null;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed class HostControlAuthServerInterceptor : Interceptor
{
    private readonly HostControlAuthOptions _options;

    public HostControlAuthServerInterceptor(HostControlAuthOptions options)
    {
        _options = options;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorized(context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthorized(context);
        return continuation(request, responseStream, context);
    }

    private void EnsureAuthorized(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue(HostControlAuthTokenStore.HeaderName);
        if (TokenEquals(_options.Token, supplied))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.Unauthenticated, "HostControl authentication failed."));
    }

    private static bool TokenEquals(string expected, string? supplied)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

public sealed class HostControlAuthClientInterceptor : Interceptor
{
    private readonly string? _token;

    public HostControlAuthClientInterceptor(string? token)
    {
        _token = token;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, WithAuth(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, WithAuth(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> WithAuth<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            return context;
        }

        var headers = new Metadata();
        if (context.Options.Headers is not null)
        {
            foreach (var entry in context.Options.Headers)
            {
                headers.Add(entry);
            }
        }

        headers.Add(HostControlAuthTokenStore.HeaderName, _token);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithHeaders(headers));
    }
}
