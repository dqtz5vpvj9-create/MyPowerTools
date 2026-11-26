namespace MyPowerTools.MobileToolControl;

/// <summary>
/// The module contract this page speaks. The values mirror
/// <c>MobileToolControl.Android.MobileToolControlOptions</c> and its error codes; those types are
/// internal to the module assembly, so the page repeats the literals instead of taking a compile-time
/// dependency on module internals. The module's own tests assert the literals still agree with
/// <c>package/module.json</c>, <c>package/ui/tool.json</c> and <c>package/commands.index.json</c>.
/// </summary>
internal static class MobileToolControlContract
{
    public const string ToolId = "mobile-tool-control";
    public const string ModuleId = "mobile-tool-control";

    // ------------------------------------------------------------------ command ids
    public const string CommandStatus = "mobile-tool-control.status";
    public const string CommandImportPreview = "mobile-tool-control.import.preview";
    public const string CommandImportConfirm = "mobile-tool-control.import.confirm";
    public const string CommandDevicesList = "mobile-tool-control.devices.list";
    public const string CommandDevicesCheck = "mobile-tool-control.devices.check";
    public const string CommandDevicesRemove = "mobile-tool-control.devices.remove";
    public const string CommandCatalog = "mobile-tool-control.catalog";
    public const string CommandInvoke = "mobile-tool-control.invoke";
    public const string CommandInvocationStatus = "mobile-tool-control.invocation.status";
    public const string CommandInvokeCancel = "mobile-tool-control.invoke.cancel";

    // ------------------------------------------------------------------ argument names
    public const string ArgumentCode = "code";
    public const string ArgumentAccepted = "accepted";
    public const string ArgumentDeviceId = "deviceId";
    public const string ArgumentCommandId = "commandId";
    public const string ArgumentInvocationId = "invocationId";
    public const string ArgumentArgs = "args";
    public const string ArgumentRefresh = "refresh";

    // ------------------------------------------------------------------ error codes
    public const string ErrorValidationFailed = "MPT_CONTROL_VALIDATION_FAILED";
    public const string ErrorEndpointRefused = "MPT_CONTROL_ENDPOINT_REFUSED";
    public const string ErrorUnknownDevice = "MPT_CONTROL_UNKNOWN_DEVICE";
    public const string ErrorUnknownCommand = "MPT_CONTROL_UNKNOWN_COMMAND";
    public const string ErrorCommandNotAllowed = "MPT_CONTROL_COMMAND_NOT_ALLOWED";
    public const string ErrorUnauthorized = "MPT_CONTROL_UNAUTHORIZED";
    public const string ErrorNotFound = "MPT_CONTROL_NOT_FOUND";
    public const string ErrorRejected = "MPT_CONTROL_REJECTED";
    public const string ErrorRedirectRefused = "MPT_CONTROL_REDIRECT_REFUSED";
    public const string ErrorUnreachable = "MPT_CONTROL_UNREACHABLE";
    public const string ErrorTimeout = "MPT_CONTROL_TIMEOUT";
    public const string ErrorProtocol = "MPT_CONTROL_PROTOCOL";
    public const string ErrorUnavailable = "MPT_CONTROL_UNAVAILABLE";
    public const string ErrorCancelled = "MPT_CONTROL_CANCELLED";

    // ------------------------------------------------------------------ invocation states
    /// <summary>
    /// Wire states this page has a Chinese label for. An unknown state is displayed verbatim; the
    /// label table is presentation only and never changes what the computer reported.
    /// </summary>
    public const string StateQueued = "queued";
    public const string StatePending = "pending";
    public const string StateAccepted = "accepted";
    public const string StateRunning = "running";

    /// <summary>The gateway's actual state while the desktop user has to confirm a call.</summary>
    public const string StateAwaitingConfirmation = "awaiting-confirmation";

    /// <summary>Older spelling kept for compatibility; it means the same thing.</summary>
    public const string StatePendingConfirmation = "pending-confirmation";

    /// <summary>The desktop page took the pending request and is executing it locally.</summary>
    public const string StateClaimed = "claimed";

    /// <summary>A cancel was accepted by the runtime; the real outcome is still pending.</summary>
    public const string StateCancelling = "cancelling";

    public const string StateSucceeded = "succeeded";
    public const string StateFailed = "failed";
    public const string StateCancelled = "cancelled";
    public const string StateRejected = "rejected";
    public const string StatePermissionRequired = "permission-required";

    // ------------------------------------------------------------------ activation
    public const string ActivationPrefix = "mypowertools://device-tool";
    public const string ActivationDevice = "device";
    public const string ActivationTool = "tool";
    public const string ActivationCommand = "command";
    public const string ActivationRun = "run";

    /// <summary>
    /// The desktop connection-code link. An activation with this prefix carries the full
    /// <c>mpt://control/…</c> code and opens the import confirmation sheet; nothing is stored and no
    /// command runs before the user confirms.
    /// </summary>
    public const string ControlCodePrefix = "mpt://control/";

    /// <summary>Minimum interval between two visible progress reads of a running invocation.</summary>
    public static readonly TimeSpan ProgressRefreshInterval = TimeSpan.FromMilliseconds(1200);
}
