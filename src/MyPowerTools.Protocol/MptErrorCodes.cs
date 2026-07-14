namespace MyPowerTools.Protocol;

public static class MptErrorCodes
{
    public const string VersionIncompatible = "MPT_VERSION_INCOMPATIBLE";
    public const string CapabilityMissing = "MPT_CAPABILITY_MISSING";
    public const string PermissionRequired = "MPT_PERMISSION_REQUIRED";
    public const string SettingsConflict = "MPT_SETTINGS_CONFLICT";
    public const string CommandTimeout = "MPT_COMMAND_TIMEOUT";
    public const string CommandCancelled = "MPT_COMMAND_CANCELLED";
    public const string RuntimeUnavailable = "MPT_RUNTIME_UNAVAILABLE";
    public const string RuntimePolicyBlocked = "MPT_RUNTIME_POLICY_BLOCKED";
    public const string UnsupportedTransport = "MPT_UNSUPPORTED_TRANSPORT";
    public const string ValidationFailed = "MPT_VALIDATION_FAILED";
    public const string NotFound = "MPT_NOT_FOUND";
    public const string ScopeDenied = "MPT_SCOPE_DENIED";
}
