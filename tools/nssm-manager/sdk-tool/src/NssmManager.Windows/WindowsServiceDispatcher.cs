using System.Runtime.InteropServices;
using NssmManager.Compatibility;
using NssmManager.Contracts;

namespace NssmManager.Windows;

public interface INssmServiceRuntime : IAsyncDisposable
{
    event Action<NssmRuntimeStatus>? StatusChanged;
    Task Started { get; }
    Task<int> RunAsync(CancellationToken cancellationToken);
    Task PreStopAsync();
    Task StopAsync();
    Task PauseAsync();
    Task ContinueAsync();
    Task RotateAsync();
    Task PowerAsync(bool resume);
}

public enum NssmRuntimeState { StartPending, Running, StopPending, Paused, ContinuePending }
public readonly record struct NssmRuntimeStatus(NssmRuntimeState State, uint WaitHint);

public sealed class NssmServiceSuicideException(int exitCode) : Exception
{
    public int ExitCode { get; } = exitCode;
}

public static class WindowsServiceDispatcher
{
    private static Func<string, INssmServiceRuntime>? _factory;
    private static NativeMethods.ServiceMain? _serviceMain;
    private static NativeMethods.HandlerEx? _handler;
    private static INssmServiceRuntime? _runtime;
    private static IntPtr _statusHandle;
    private static uint _checkpoint;
    private static uint _serviceType = NativeMethods.ServiceWin32OwnProcess | NativeMethods.ServiceInteractiveProcess;
    private static string _serviceName = "NSSM";
    private static readonly CancellationTokenSource StopToken = new();

    public static bool TryRun(Func<string, INssmServiceRuntime> factory, out int error)
    {
        _factory = factory;
        _serviceMain = service_main;
        var table = new[]
        {
            new NativeMethods.ServiceTableEntry { ServiceName = "nssm", ServiceMain = _serviceMain },
            new NativeMethods.ServiceTableEntry { ServiceName = null, ServiceMain = null }
        };
        if (NativeMethods.StartServiceCtrlDispatcher(table)) { error = 0; return true; }
        error = Marshal.GetLastWin32Error();
        return false;
    }

    [NssmUpstreamFunction("src/service.cpp", 1551, "void WINAPI service_main(unsigned long argc, TCHAR **argv)", "NssmServiceTranslationTests.dispatcher_text_and_control_matrix_match_upstream")]
    private static void service_main(uint argumentCount, IntPtr arguments)
    {
        var serviceName = ReadArgument(arguments, 0);
        _serviceName = serviceName;
        _serviceType = NativeMethods.ServiceWin32OwnProcess | NativeMethods.ServiceInteractiveProcess;
        _handler = service_control_handler;
        _statusHandle = NativeMethods.RegisterServiceCtrlHandlerEx("nssm", _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero) return;
        log_service_control(serviceName, 0, true);
        Report(NativeMethods.ServiceStartPending, 3500, 0);
        try
        {
            if (NssmCore.check_admin())
            {
                _ = NssmRegistry.create_exit_action(serviceName, "Restart", false);
                try { new WindowsServiceManager().set_service_recovery(serviceName); }
                catch (Exception exception)
                {
                    NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_FAILURE_ACTIONS_FAILED"), serviceName, exception.Message);
                }
            }
            try { _runtime = _factory!(serviceName); }
            catch
            {
                Report(NativeMethods.ServiceStopped, 0, 0, 1066, 2);
                return;
            }
            _runtime.StatusChanged += RuntimeStatusChanged;
            var run = _runtime.RunAsync(StopToken.Token);
            while (!_runtime.Started.IsCompleted && !run.IsCompleted)
            {
                Task.WhenAny(_runtime.Started, run, Task.Delay(1000)).GetAwaiter().GetResult();
                if (!_runtime.Started.IsCompleted && !run.IsCompleted) Report(NativeMethods.ServiceStartPending, NssmServiceTranslation.ServiceStatusDeadline, 0);
            }
            if (_runtime.Started.IsCompletedSuccessfully) Report(NativeMethods.ServiceRunning, 0, AcceptedControls());
            var exitCode = run.GetAwaiter().GetResult();
            var serviceExit = unchecked((uint)exitCode);
            Report(NativeMethods.ServiceStopped, 0, 0, serviceExit == 0 ? 0u : 1066u, serviceExit);
        }
        catch (NssmServiceSuicideException exception)
        {
            Environment.Exit(exception.ExitCode);
        }
        catch
        {
            Report(NativeMethods.ServiceStopped, 0, 0, 1064);
        }
        finally
        {
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _runtime = null;
        }
    }

    [NssmUpstreamFunction("src/service.cpp", 1674, "TCHAR *service_control_text(unsigned long control)", "NssmServiceTranslationTests.dispatcher_text_and_control_matrix_match_upstream")]
    public static string? service_control_text(uint control) => control switch
    {
        NativeMethods.ServiceControlStop => "STOP",
        NativeMethods.ServiceControlShutdown => "SHUTDOWN",
        NativeMethods.ServiceControlPause => "PAUSE",
        NativeMethods.ServiceControlContinue => "CONTINUE",
        NativeMethods.ServiceControlInterrogate => "INTERROGATE",
        NativeMethods.ServiceControlRotate => "ROTATE",
        NativeMethods.ServiceControlPowerEvent => "POWEREVENT",
        0 => "START",
        _ => null
    };

    [NssmUpstreamFunction("src/service.cpp", 1689, "TCHAR *service_status_text(unsigned long status)", "NssmServiceTranslationTests.dispatcher_text_and_control_matrix_match_upstream")]
    public static string? service_status_text(uint status) => status switch
    {
        NativeMethods.ServiceStopped => "SERVICE_STOPPED",
        NativeMethods.ServiceStartPending => "SERVICE_START_PENDING",
        NativeMethods.ServiceStopPending => "SERVICE_STOP_PENDING",
        NativeMethods.ServiceRunning => "SERVICE_RUNNING",
        NativeMethods.ServiceContinuePending => "SERVICE_CONTINUE_PENDING",
        NativeMethods.ServicePausePending => "SERVICE_PAUSE_PENDING",
        NativeMethods.ServicePaused => "SERVICE_PAUSED",
        _ => null
    };

    [NssmUpstreamFunction("src/service.cpp", 1702, "void log_service_control(TCHAR *service_name, unsigned long control, bool handled)", "NssmServiceTranslationTests.dispatcher_text_and_control_matrix_match_upstream")]
    public static void log_service_control(string serviceName, uint control, bool handled)
    {
        var text = service_control_text(control);
        var symbol = text is null
            ? "NSSM_EVENT_SERVICE_CONTROL_UNKNOWN"
            : handled ? "NSSM_EVENT_SERVICE_CONTROL_HANDLED" : "NSSM_EVENT_SERVICE_CONTROL_NOT_HANDLED";
        NssmEvent.log_event(4, NssmEvent.message_id(symbol), serviceName, text ?? $"0x{control:x8}");
    }

    [NssmUpstreamFunction("src/service.cpp", 1732, "unsigned long WINAPI service_control_handler(unsigned long control, unsigned long event, void *data, void *context)", "NssmServiceTranslationTests.dispatcher_text_and_control_matrix_match_upstream")]
    private static uint service_control_handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        try
        {
            switch (control)
            {
                case NativeMethods.ServiceControlStop:
                case NativeMethods.ServiceControlShutdown:
                    log_service_control(_serviceName, control, true);
                    Report(NativeMethods.ServiceStopPending, NssmServiceTranslation.WaitHintMargin, 0);
                    _runtime?.PreStopAsync().GetAwaiter().GetResult();
                    _ = Task.Run(async () =>
                    {
                        await (_runtime?.StopAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                        StopToken.Cancel();
                    });
                    break;
                case NativeMethods.ServiceControlPause:
                    log_service_control(_serviceName, control, false);
                    return 120;
                case NativeMethods.ServiceControlContinue:
                    log_service_control(_serviceName, control, true);
                    _runtime?.ContinueAsync().GetAwaiter().GetResult();
                    break;
                case NativeMethods.ServiceControlRotate:
                    log_service_control(_serviceName, control, true);
                    _runtime?.RotateAsync().GetAwaiter().GetResult();
                    break;
                case NativeMethods.ServiceControlPowerEvent:
                    var resume = eventType == 18;
                    if (eventType is not 18 and not 10) { log_service_control(_serviceName, control, false); return 0; }
                    log_service_control(_serviceName, control, true);
                    _runtime?.PowerAsync(resume).GetAwaiter().GetResult();
                    break;
                case NativeMethods.ServiceControlInterrogate:
                    break;
                default:
                    log_service_control(_serviceName, control, false);
                    return 120;
            }
            return 0;
        }
        catch { return 1; }
    }

    private static uint AcceptedControls(bool continueControl = false) => NativeMethods.ServiceAcceptStop |
        (continueControl ? NativeMethods.ServiceAcceptPauseContinue : 0) |
        NativeMethods.ServiceAcceptShutdown | NativeMethods.ServiceAcceptPowerEvent;

    private static void RuntimeStatusChanged(NssmRuntimeStatus status)
    {
        switch (status.State)
        {
            case NssmRuntimeState.StartPending: Report(NativeMethods.ServiceStartPending, status.WaitHint, AcceptedControls()); break;
            case NssmRuntimeState.Running: Report(NativeMethods.ServiceRunning, 0, AcceptedControls()); break;
            case NssmRuntimeState.StopPending: Report(NativeMethods.ServiceStopPending, status.WaitHint, 0); break;
            case NssmRuntimeState.Paused: Report(NativeMethods.ServicePaused, status.WaitHint, AcceptedControls(continueControl: true)); break;
            case NssmRuntimeState.ContinuePending: Report(NativeMethods.ServiceContinuePending, status.WaitHint, AcceptedControls(continueControl: true)); break;
        }
    }

    private static void Report(uint state, uint waitHint, uint accepted, uint win32ExitCode = 0, uint serviceSpecificExitCode = 0)
    {
        var status = new NativeMethods.ServiceStatus
        {
            ServiceType = _serviceType,
            CurrentState = state,
            ControlsAccepted = accepted,
            Win32ExitCode = win32ExitCode,
            ServiceSpecificExitCode = serviceSpecificExitCode,
            CheckPoint = state is NativeMethods.ServiceStartPending or NativeMethods.ServiceStopPending or NativeMethods.ServicePausePending ? ++_checkpoint : 0,
            WaitHint = waitHint
        };
        NativeMethods.SetServiceStatus(_statusHandle, ref status);
    }

    private static string ReadArgument(IntPtr arguments, int index)
    {
        if (arguments == IntPtr.Zero) return "";
        var pointer = Marshal.ReadIntPtr(arguments, index * IntPtr.Size);
        return Marshal.PtrToStringUni(pointer) ?? "";
    }

}
