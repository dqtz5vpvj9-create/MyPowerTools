using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace MyPowerTools.Ipc;

/// <summary>
/// Options describing how a bearer token is validated on a gRPC server.
/// Hosts may subclass this to carry host-specific option identity.
/// </summary>
public record IpcAuthOptions(string HeaderName, string Token);

/// <summary>
/// Persistent bearer-token store shared between a host process (which creates the token)
/// and its clients (which read it). The token file lives under a per-host subpath of the
/// data root and is regenerated if missing or too short.
/// </summary>
public sealed class AuthTokenStore
{
    private readonly string _environmentVariable;
    private readonly string _tokenRelativePath;

    /// <param name="environmentVariable">Env var used to override the data root (e.g. <c>MPT_DATA_ROOT</c>).</param>
    /// <param name="tokenRelativePath">Token file path relative to the data root (e.g. <c>state/hostcontrol.token</c>).</param>
    public AuthTokenStore(string environmentVariable, string tokenRelativePath)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
        {
            throw new ArgumentException("environment variable name is required", nameof(environmentVariable));
        }

        if (string.IsNullOrWhiteSpace(tokenRelativePath))
        {
            throw new ArgumentException("token relative path is required", nameof(tokenRelativePath));
        }

        _environmentVariable = environmentVariable;
        _tokenRelativePath = tokenRelativePath;
    }

    public string EnvironmentVariable => _environmentVariable;

    public string DefaultDataRoot()
    {
        return Environment.GetEnvironmentVariable(_environmentVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
    }

    public string TokenPath(string? dataRoot = null)
    {
        return Path.Combine(dataRoot ?? DefaultDataRoot(), _tokenRelativePath);
    }

    public string GetOrCreateToken(string? dataRoot = null)
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

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Unix file mode is best-effort; Windows uses ACLs instead.
            }
        }

        return token;
    }

    public string? TryReadToken(string? dataRoot = null)
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

    /// <summary>
    /// Constant-time comparison of two ASCII tokens. Returns false if either is blank.
    /// </summary>
    public static bool TokenEquals(string expected, string? supplied)
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

/// <summary>
/// Server interceptor that rejects calls whose <see cref="IpcAuthOptions.HeaderName"/>
/// header does not match <see cref="IpcAuthOptions.Token"/> using a constant-time compare.
/// </summary>
public class BearerTokenAuthServerInterceptor : Interceptor
{
    private readonly IpcAuthOptions _options;

    public BearerTokenAuthServerInterceptor(IpcAuthOptions options)
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
        var supplied = context.RequestHeaders.GetValue(_options.HeaderName);
        if (AuthTokenStore.TokenEquals(_options.Token, supplied))
        {
            return;
        }

        throw new RpcException(new Status(StatusCode.Unauthenticated, "IPC authentication failed."));
    }
}

/// <summary>
/// Client interceptor that injects the bearer token header on every call.
/// </summary>
public class BearerTokenAuthClientInterceptor : Interceptor
{
    private readonly string _headerName;
    private readonly string? _token;

    public BearerTokenAuthClientInterceptor(string headerName, string? token)
    {
        _headerName = headerName;
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

        headers.Add(_headerName, _token);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithHeaders(headers));
    }
}
