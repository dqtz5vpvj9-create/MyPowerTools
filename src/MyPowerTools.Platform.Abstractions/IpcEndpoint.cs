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
}
