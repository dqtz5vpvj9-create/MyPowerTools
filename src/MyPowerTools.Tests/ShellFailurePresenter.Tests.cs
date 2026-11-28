using Grpc.Core;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellFailurePresenterTests
{
    [Fact]
    public void Named_pipe_access_denied_explains_the_real_connection_failure()
    {
        var debugException = new HttpRequestException(
            "Access to the path is denied. (localhost:80)",
            new UnauthorizedAccessException("Access to the path is denied."));
        var exception = new RpcException(new Status(
            StatusCode.Internal,
            "Error starting gRPC call. HttpRequestException: Access to the path is denied. (localhost:80)",
            debugException));

        var failure = ShellFailurePresenter.Present(exception, platform: new PlatformId("windows", "x64"));

        Assert.Equal("runner-access-denied", failure.Code);
        Assert.Contains("Windows named pipe", failure.Details, StringComparison.Ordinal);
        Assert.Contains("mypowertools.runner.hostcontrol", failure.Details, StringComparison.Ordinal);
        Assert.Contains("Result: Access denied", failure.Details, StringComparison.Ordinal);
        Assert.Contains("parallel development", failure.Details, StringComparison.Ordinal);
        Assert.Contains("localhost:80 is gRPC's placeholder", failure.Details, StringComparison.Ordinal);
        Assert.Contains("did not contact TCP port 80", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusCode", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Windows blocked the connection", failure.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Windows blocked MyPowerTools", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("named pipe", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux", false, "mypowertools.runner.hostcontrol.sock")]
    [InlineData("macos", false, "mypowertools.runner.hostcontrol.sock")]
    [InlineData("linux", true, "mypewertools.servicemanager.v1.sock")]
    [InlineData("macos", true, "mypewertools.servicemanager.v1.sock")]
    public void Unix_socket_access_denied_explains_socket_permissions_without_windows_advice(
        string operatingSystem,
        bool serviceManager,
        string socketName)
    {
        var source = serviceManager ? ShellFailureSource.ServiceManager : ShellFailureSource.Runner;
        var exception = new RpcException(new Status(
            StatusCode.Internal,
            "Permission denied.",
            new UnauthorizedAccessException("internal transport detail")));

        var failure = ShellFailurePresenter.Present(exception, source, new PlatformId(operatingSystem, "x64"));

        Assert.Equal(source == ShellFailureSource.Runner ? "runner-access-denied" : "service-manager-access-denied", failure.Code);
        Assert.Contains("Unix domain socket", failure.Details, StringComparison.Ordinal);
        Assert.Contains(socketName, failure.Details, StringComparison.Ordinal);
        Assert.Contains("Result: Access denied", failure.Details, StringComparison.Ordinal);
        Assert.Contains("same user", failure.Details, StringComparison.Ordinal);
        Assert.Contains("socket and directory permissions", failure.Details, StringComparison.Ordinal);
        Assert.Contains("Your system blocked the connection", failure.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", failure.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("elevation", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:80", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("TCP port 80", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("internal transport detail", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal transport detail", failure.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Unauthenticated_runner_call_explains_the_data_root_mismatch()
    {
        var exception = new RpcException(new Status(StatusCode.Unauthenticated, "IPC authentication failed."));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-credential-mismatch", failure.Code);
        Assert.Contains("rejected its local credential", failure.Details, StringComparison.Ordinal);
        Assert.Contains("MPT_DATA_ROOT", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("MPT_DATA_ROOT", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IPC authentication failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_runner_call_gives_a_recovery_path()
    {
        var exception = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-unavailable", failure.Code);
        Assert.Contains("could not reach the local MPT Runner", failure.Details, StringComparison.Ordinal);
        Assert.Contains("restart Runner", failure.Details, StringComparison.Ordinal);
        Assert.Contains("isn't running", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Runner", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection refused", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_permission_denial_is_not_misreported_as_a_pipe_acl_failure()
    {
        var exception = new RpcException(new Status(StatusCode.PermissionDenied, "Access is denied."));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-permission-denied", failure.Code);
        Assert.Contains("permission policy", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows named pipe", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:80", failure.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_manager_failure_names_its_own_endpoint()
    {
        var debugException = new UnauthorizedAccessException("Access is denied.");
        var exception = new RpcException(new Status(StatusCode.Internal, "Access is denied.", debugException));

        var failure = ShellFailurePresenter.Present(exception, ShellFailureSource.ServiceManager, new PlatformId("windows", "x64"));

        Assert.Equal("service-manager-access-denied", failure.Code);
        Assert.Contains("MPT Service Manager", failure.Details, StringComparison.Ordinal);
        Assert.Contains("Windows named pipe", failure.Details, StringComparison.Ordinal);
        Assert.Contains("mypewertools.servicemanager.v1", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("mypowertools.runner.hostcontrol", failure.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_failure_keeps_internal_text_out_of_the_page()
    {
        var exception = new InvalidOperationException("internal implementation detail");

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("unexpected-page-failure", failure.Code);
        Assert.Contains("Shell fault log", failure.Details, StringComparison.Ordinal);
        Assert.Contains("Something went wrong", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal implementation detail", failure.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("internal implementation detail", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_monitor_keeps_raw_rpc_text_out_of_status_updates()
    {
        var debugException = new UnauthorizedAccessException("Access is denied.");
        var exception = new RpcException(new Status(
            StatusCode.Internal,
            "Status(StatusCode=Internal, DebugException=UnauthorizedAccessException)",
            debugException));
        await using var monitor = new HostControlConnectionMonitor(
            new FailingConnectionProbe(exception),
            pollInterval: TimeSpan.FromMinutes(1),
            attemptTimeout: TimeSpan.FromSeconds(1));

        var snapshot = await monitor.CheckOnceAsync(notify: false);

        Assert.False(snapshot.Online);
        var expectedOperatingSystem = OperatingSystem.IsWindows() ? "Windows" : "Your system";
        Assert.Contains($"{expectedOperatingSystem} blocked the connection", snapshot.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusCode", snapshot.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugException", snapshot.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", snapshot.Message, StringComparison.Ordinal);
    }

    private sealed class FailingConnectionProbe(Exception exception) : IHostControlConnectionProbe
    {
        public Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<HostControlConnectionProbeResult>(exception);
        }
    }
}
