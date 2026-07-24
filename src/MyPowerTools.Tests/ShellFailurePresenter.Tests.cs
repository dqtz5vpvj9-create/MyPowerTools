using Grpc.Core;
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

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-access-denied", failure.Code);
        Assert.Contains("Windows named pipe", failure.Message, StringComparison.Ordinal);
        Assert.Contains("mypowertools.runner.hostcontrol", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Result: Access denied", failure.Message, StringComparison.Ordinal);
        Assert.Contains("parallel development", failure.Message, StringComparison.Ordinal);
        Assert.Contains("localhost:80 is gRPC's placeholder", failure.Message, StringComparison.Ordinal);
        Assert.Contains("did not contact TCP port 80", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusCode", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DebugException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unauthenticated_runner_call_explains_the_data_root_mismatch()
    {
        var exception = new RpcException(new Status(StatusCode.Unauthenticated, "IPC authentication failed."));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-credential-mismatch", failure.Code);
        Assert.Contains("rejected its local credential", failure.Message, StringComparison.Ordinal);
        Assert.Contains("MPT_DATA_ROOT", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IPC authentication failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_runner_call_gives_a_recovery_path()
    {
        var exception = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-unavailable", failure.Code);
        Assert.Contains("could not reach the local MPT Runner", failure.Message, StringComparison.Ordinal);
        Assert.Contains("restart Runner", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection refused", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Server_permission_denial_is_not_misreported_as_a_pipe_acl_failure()
    {
        var exception = new RpcException(new Status(StatusCode.PermissionDenied, "Access is denied."));

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("runner-permission-denied", failure.Code);
        Assert.Contains("permission policy", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows named pipe", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:80", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_manager_failure_names_its_own_endpoint()
    {
        var debugException = new UnauthorizedAccessException("Access is denied.");
        var exception = new RpcException(new Status(StatusCode.Internal, "Access is denied.", debugException));

        var failure = ShellFailurePresenter.Present(exception, ShellFailureSource.ServiceManager);

        Assert.Equal("service-manager-access-denied", failure.Code);
        Assert.Contains("MPT Service Manager", failure.Message, StringComparison.Ordinal);
        Assert.Contains("mypewertools.servicemanager.v1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mypowertools.runner.hostcontrol", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexpected_failure_keeps_internal_text_out_of_the_page()
    {
        var exception = new InvalidOperationException("internal implementation detail");

        var failure = ShellFailurePresenter.Present(exception);

        Assert.Equal("unexpected-page-failure", failure.Code);
        Assert.Contains("Shell fault log", failure.Message, StringComparison.Ordinal);
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
        Assert.Contains("Windows denied this Shell access", snapshot.Message, StringComparison.Ordinal);
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
