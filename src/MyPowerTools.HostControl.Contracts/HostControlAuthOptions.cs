using MyPowerTools.Ipc;

namespace MyPowerTools.HostControl;

/// <summary>HostControl auth options, part of the Contracts assembly.</summary>
public sealed record HostControlAuthOptions(string Token) : IpcAuthOptions(HostControlAuthTokenStore.HeaderName, Token)
{
}

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

public sealed class HostControlAuthServerInterceptor : BearerTokenAuthServerInterceptor
{
    public HostControlAuthServerInterceptor(HostControlAuthOptions options) : base(options) { }
}

public sealed class HostControlAuthClientInterceptor : BearerTokenAuthClientInterceptor
{
    public HostControlAuthClientInterceptor(string? token) : base(HostControlAuthTokenStore.HeaderName, token) { }
}
