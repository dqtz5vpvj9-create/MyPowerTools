namespace MyPowerTools.Platform.Abstractions;

public enum IpcTransport
{
    NamedPipe,
    UnixDomainSocket,
    Tcp
}

public sealed record IpcEndpoint(IpcTransport Transport, string Address)
{
    public static IpcEndpoint RunnerDefault(PlatformId platform)
    {
        return platform.OperatingSystem == "windows"
            ? new IpcEndpoint(IpcTransport.NamedPipe, "mypowertools.runner.hostcontrol")
            : new IpcEndpoint(IpcTransport.UnixDomainSocket, Path.Combine(Path.GetTempPath(), "mypowertools.runner.hostcontrol.sock"));
    }

    /// <summary>
    /// Default endpoint for the independent ServiceManager process. Uses a distinct pipe/socket
    /// from Runner so that a Runner restart never affects Service Units or the Services page.
    /// </summary>
    public static IpcEndpoint ServiceManagerDefault(PlatformId platform)
    {
        return platform.OperatingSystem == "windows"
            ? new IpcEndpoint(IpcTransport.NamedPipe, "mypewertools.servicemanager.v1")
            : new IpcEndpoint(IpcTransport.UnixDomainSocket, Path.Combine(Path.GetTempPath(), "mypewertools.servicemanager.v1.sock"));
    }
}
