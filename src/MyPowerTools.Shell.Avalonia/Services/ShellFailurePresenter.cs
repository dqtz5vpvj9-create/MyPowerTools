using System.ComponentModel;
using System.Net.Sockets;
using System.Text.Json;
using Grpc.Core;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Shell.Avalonia.Services;

internal enum ShellFailureSource
{
    Runner,
    ServiceManager
}

internal sealed record ShellUserFacingFailure(
    string Code,
    string Message,
    string StatusMessage);

internal static class ShellFailurePresenter
{
    public static bool IsRpcFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ExpandExceptionChain(exception).Any(item => item is RpcException);
    }

    public static ShellUserFacingFailure Present(
        Exception exception,
        ShellFailureSource source = ShellFailureSource.Runner)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var exceptions = ExpandExceptionChain(exception);
        var rpc = exceptions.OfType<RpcException>().FirstOrDefault();
        var service = DescribeService(source);

        if (rpc is not null &&
            rpc.StatusCode is StatusCode.Internal or StatusCode.Unknown or StatusCode.Unavailable &&
            ContainsAccessDenied(exceptions, rpc))
        {
            var paragraphs = new List<string>
            {
                $"Windows blocked this Shell from opening the shared {service.DisplayName} connection.",
                $"Connection: {service.TransportLabel} {service.Endpoint}{Environment.NewLine}Result: Access denied",
                $"Common trigger during parallel development: the active {service.ProcessName} was started by a session using another Windows account or elevation level. Close that process or run every MPT session with the same Windows permissions, then select Try again."
            };

            if (service.Transport == IpcTransport.NamedPipe)
            {
                paragraphs.Add("localhost:80 is gRPC's placeholder address for this named-pipe connection; MPT did not contact TCP port 80.");
            }

            return new ShellUserFacingFailure(
                $"{service.CodePrefix}-access-denied",
                JoinParagraphs(paragraphs),
                $"Windows denied this Shell access to the {service.DisplayName} connection.");
        }

        if (rpc?.StatusCode == StatusCode.Unauthenticated)
        {
            return new ShellUserFacingFailure(
                $"{service.CodePrefix}-credential-mismatch",
                JoinParagraphs(
                    $"The Shell reached the {service.DisplayName}, and {service.ProcessName} rejected its local credential.",
                    "The Shell and local service are using different MPT data roots or token files. Start both with the same MPT_DATA_ROOT or --data-root value, then select Try again."),
                $"The {service.DisplayName} rejected this Shell's local credential.");
        }

        if (rpc?.StatusCode == StatusCode.PermissionDenied)
        {
            return new ShellUserFacingFailure(
                $"{service.CodePrefix}-permission-denied",
                JoinParagraphs(
                    $"The {service.DisplayName} rejected this page request because the current MPT session lacks the required permission.",
                    "Review the affected tool or module permission policy, then select Try again."),
                $"The {service.DisplayName} denied this page request.");
        }

        if (ContainsTimeout(exceptions, rpc))
        {
            return new ShellUserFacingFailure(
                $"{service.CodePrefix}-timeout",
                JoinParagraphs(
                    $"The {service.DisplayName} did not respond within the allowed time.",
                    $"Connection: {service.TransportLabel} {service.Endpoint}{Environment.NewLine}The process may still be starting or rebuilding. Wait for the active development task to finish, then select Try again."),
                $"The {service.DisplayName} connection timed out.");
        }

        if (rpc?.StatusCode is StatusCode.Unavailable or StatusCode.Cancelled ||
            exceptions.Any(IsConnectionFailure))
        {
            // ServiceManager can be launched directly from this page's Try-again button
            // (ShellServiceManagerBootstrapper); the Runner cannot, so the copy differs.
            var recoveryLine = source == ShellFailureSource.ServiceManager
                ? "Select Try again to start it now and reload this page."
                : $"The process is stopped, starting, or restarting during another development session. Wait for startup to finish or restart {service.ProcessName}, then select Try again.";

            return new ShellUserFacingFailure(
                $"{service.CodePrefix}-unavailable",
                JoinParagraphs(
                    $"The Shell could not reach the {service.DisplayName}.",
                    $"Connection: {service.TransportLabel} {service.Endpoint}{Environment.NewLine}{recoveryLine}"),
                $"The {service.DisplayName} is unavailable.");
        }

        if (exceptions.Any(item => item is FileNotFoundException or DirectoryNotFoundException))
        {
            return new ShellUserFacingFailure(
                "local-file-missing",
                JoinParagraphs(
                    "A local file required by this page is missing.",
                    "Rebuild the affected project or restore its package files, then select Try again."),
                "A required local file is missing.");
        }

        if (exceptions.Any(item => item is JsonException or InvalidDataException))
        {
            return new ShellUserFacingFailure(
                "local-data-invalid",
                JoinParagraphs(
                    "MPT found invalid tool, package, or settings data while loading this page.",
                    "Check the latest manifest or settings change, correct the invalid data, then select Try again."),
                "MPT found invalid local data.");
        }

        return new ShellUserFacingFailure(
            "unexpected-page-failure",
            JoinParagraphs(
                "MPT hit an unexpected local failure while loading this page.",
                "Technical details were saved to the Shell fault log. Select Try again; if the failure continues, inspect the latest shell-faults.log entry."),
            "MPT hit an unexpected page-loading failure.");
    }

    private static IReadOnlyList<Exception> ExpandExceptionChain(Exception exception)
    {
        var result = new List<Exception>();
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            result.Add(current);
            if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }

            if (current is RpcException rpc && rpc.Status.DebugException is not null)
            {
                pending.Push(rpc.Status.DebugException);
            }
        }

        return result;
    }

    private static bool ContainsAccessDenied(
        IReadOnlyList<Exception> exceptions,
        RpcException rpc)
    {
        if (exceptions.Any(item =>
                item is UnauthorizedAccessException ||
                item is Win32Exception { NativeErrorCode: 5 } ||
                item is SocketException { SocketErrorCode: SocketError.AccessDenied } ||
                item.HResult == unchecked((int)0x80070005)))
        {
            return true;
        }

        var details = string.Join(' ', exceptions.Select(item => item.Message).Append(rpc.Status.Detail));
        return ContainsAny(
            details,
            "access to the path is denied",
            "access is denied",
            "permission denied",
            "拒绝访问");
    }

    private static bool ContainsTimeout(
        IReadOnlyList<Exception> exceptions,
        RpcException? rpc)
    {
        return rpc?.StatusCode == StatusCode.DeadlineExceeded ||
               exceptions.Any(item => item is TimeoutException or OperationCanceledException);
    }

    private static bool IsConnectionFailure(Exception exception)
    {
        return exception is SocketException socket && socket.SocketErrorCode is
            SocketError.ConnectionAborted or
            SocketError.ConnectionRefused or
            SocketError.ConnectionReset or
            SocketError.HostDown or
            SocketError.HostNotFound or
            SocketError.NetworkDown or
            SocketError.NetworkUnreachable;
    }

    private static bool ContainsAny(string value, params string[] candidates)
    {
        return candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinParagraphs(params string[] paragraphs)
    {
        return JoinParagraphs((IEnumerable<string>)paragraphs);
    }

    private static string JoinParagraphs(IEnumerable<string> paragraphs)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            paragraphs.Where(paragraph => !string.IsNullOrWhiteSpace(paragraph)));
    }

    private static ShellServiceDescription DescribeService(ShellFailureSource source)
    {
        var endpoint = source == ShellFailureSource.ServiceManager
            ? IpcEndpoint.ServiceManagerDefault(PlatformId.Current())
            : IpcEndpoint.RunnerDefault(PlatformId.Current());
        var transportLabel = endpoint.Transport switch
        {
            IpcTransport.NamedPipe => "Windows named pipe",
            IpcTransport.UnixDomainSocket => "Unix domain socket",
            _ => "local endpoint"
        };

        return source == ShellFailureSource.ServiceManager
            ? new ShellServiceDescription(
                "MPT Service Manager",
                "ServiceManager",
                "service-manager",
                endpoint.Transport,
                endpoint.Address,
                transportLabel)
            : new ShellServiceDescription(
                "local MPT Runner",
                "Runner",
                "runner",
                endpoint.Transport,
                endpoint.Address,
                transportLabel);
    }

    private sealed record ShellServiceDescription(
        string DisplayName,
        string ProcessName,
        string CodePrefix,
        IpcTransport Transport,
        string Endpoint,
        string TransportLabel);
}
