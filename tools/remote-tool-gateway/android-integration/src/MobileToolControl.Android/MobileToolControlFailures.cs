using MyPowerTools.Abstractions;

namespace MobileToolControl.Android;

/// <summary>
/// Error codes the phone module reports. They are stable strings: the phone surface matches on them
/// to decide between "waiting for authorization", "computer unreachable" and an execution failure,
/// without parsing a message.
/// </summary>
internal static class MobileToolControlErrorCodes
{
    public const string ValidationFailed = "MPT_CONTROL_VALIDATION_FAILED";
    public const string EndpointRefused = "MPT_CONTROL_ENDPOINT_REFUSED";
    public const string UnknownDevice = "MPT_CONTROL_UNKNOWN_DEVICE";
    public const string UnknownCommand = "MPT_CONTROL_UNKNOWN_COMMAND";
    public const string CommandNotAllowed = "MPT_CONTROL_COMMAND_NOT_ALLOWED";
    public const string InvocationConflict = "MPT_CONTROL_INVOCATION_CONFLICT";
    public const string Unauthorized = "MPT_CONTROL_UNAUTHORIZED";
    public const string NotFound = "MPT_CONTROL_NOT_FOUND";
    public const string Rejected = "MPT_CONTROL_REJECTED";
    public const string RedirectRefused = "MPT_CONTROL_REDIRECT_REFUSED";
    public const string Unreachable = "MPT_CONTROL_UNREACHABLE";
    public const string Timeout = "MPT_CONTROL_TIMEOUT";
    public const string Protocol = "MPT_CONTROL_PROTOCOL";
    public const string Unavailable = "MPT_CONTROL_UNAVAILABLE";
    public const string Cancelled = "MPT_CONTROL_CANCELLED";
}

/// <summary>
/// One failed gateway call. <see cref="Code"/> is a <see cref="MobileToolControlErrorCodes"/> value,
/// <see cref="ServerCode"/> keeps the code the gateway itself returned (never a token), and
/// <see cref="Message"/> is safe to show to the user.
/// </summary>
internal sealed class MobileToolControlException(
    string code,
    string message,
    string serverCode = "",
    int statusCode = 0,
    bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;

    public string ServerCode { get; } = serverCode;

    public int StatusCode { get; } = statusCode;

    public bool Retryable { get; } = retryable;

    /// <summary>Maps the exception onto the module's <see cref="MptRuntimeError"/> contract.</summary>
    public MptRuntimeError ToRuntimeError() =>
        new(Code, Message, Retryable, ServerCode.Length == 0 ? null : new System.Text.Json.Nodes.JsonObject
        {
            ["serverCode"] = ServerCode,
            ["status"] = StatusCode
        });
}
