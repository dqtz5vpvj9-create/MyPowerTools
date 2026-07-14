using MyPowerTools.Ipc;

namespace MyPowerTools.HostControl;

/// <summary>
/// HostControl auth options. Carries the historical header name plus the token, and is accepted by
/// <see cref="HostControlAuthServerInterceptor"/> so existing DI registration
/// (<c>AddSingleton(new HostControlAuthOptions(token))</c>) keeps working.
/// </summary>
public sealed record HostControlAuthOptions(string Token) : IpcAuthOptions(HostControlAuthTokenStore.HeaderName, Token)
{
}

/// <summary>
/// HostControl-specific auth facade. Delegates to the shared <see cref="AuthTokenStore"/>
/// while preserving the historical static API used across Runner/Shell/CLI/Tests.
/// The token file path (<c>state/hostcontrol.token</c>) and env var (<c>MPT_DATA_ROOT</c>)
/// are unchanged so existing data roots keep working.
/// </summary>
public static class HostControlAuthTokenStore
{
    public const string HeaderName = "x-mpt-hostcontrol-token";
    public const string DataRootEnvironmentVariable = "MPT_DATA_ROOT";

    private static readonly AuthTokenStore _store = new(DataRootEnvironmentVariable, Path.Combine("state", "hostcontrol.token"));

    public static string DefaultDataRoot() => _store.DefaultDataRoot();

    public static string TokenPath(string? dataRoot = null) => _store.TokenPath(dataRoot);

    public static string GetOrCreateToken(string? dataRoot = null) => _store.GetOrCreateToken(dataRoot);

    public static string? TryReadToken(string? dataRoot = null) => _store.TryReadToken(dataRoot);

    internal static AuthTokenStore Store => _store;
}

/// <summary>
/// Server interceptor validating the HostControl bearer token. Delegates to
/// <see cref="BearerTokenAuthServerInterceptor"/> to keep the auth logic in one place.
/// </summary>
public sealed class HostControlAuthServerInterceptor : BearerTokenAuthServerInterceptor
{
    public HostControlAuthServerInterceptor(HostControlAuthOptions options) : base(options)
    {
    }
}

/// <summary>
/// Client interceptor injecting the HostControl bearer token header. Delegates to
/// <see cref="BearerTokenAuthClientInterceptor"/> to keep the auth logic in one place.
/// </summary>
public sealed class HostControlAuthClientInterceptor : BearerTokenAuthClientInterceptor
{
    public HostControlAuthClientInterceptor(string? token) : base(HostControlAuthTokenStore.HeaderName, token)
    {
    }
}
