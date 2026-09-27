namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// The module contract this phone surface speaks.
/// </summary>
/// <remarks>
/// The values mirror <c>RemoteCommands.Android.RemoteCommandsAndroidOptions</c>,
/// <c>RemoteCommandRunStates</c> and <c>RemoteCommandsAndroidErrorCodes</c>. Those types are
/// <see langword="internal"/> to the module assembly, so the surface repeats the literals here
/// instead of taking a compile-time dependency on module internals. The module's own tests assert
/// the literals still agree with <c>package/module.json</c> and <c>package/ui/tool.json</c>; the
/// integration contract in this project's hand-off notes lists the same table.
/// </remarks>
internal static class RemoteCommandsMobileContract
{
    public const string ToolId = "remote-commands-android";
    public const string ModuleId = "remote-commands-android";

    // ------------------------------------------------------------------ command ids
    public const string CommandStatus = "remote-commands-android.status";
    public const string CommandCatalog = "remote-commands-android.catalog";

    /// <summary>
    /// Writes the canonical <c>commands.yaml</c>. The module validates the content with the shipped
    /// parser and refuses anything invalid, so the phone editor cannot corrupt the catalog.
    /// </summary>
    public const string CommandCatalogSave = "remote-commands-android.catalog.save";
    public const string CommandRun = "remote-commands-android.run";
    public const string CommandCancel = "remote-commands-android.cancel";
    public const string CommandHostsList = "remote-commands-android.hosts.list";
    public const string CommandHostAdd = "remote-commands-android.host.add";
    public const string CommandHostRemove = "remote-commands-android.host.remove";
    public const string CommandHostKeyStatus = "remote-commands-android.host-key.status";
    public const string CommandHostKeyAccept = "remote-commands-android.host-key.accept";
    public const string CommandHostKeyRevoke = "remote-commands-android.host-key.revoke";
    public const string CommandHistorySummary = "remote-commands-android.history.summary";
    public const string CommandHistoryClear = "remote-commands-android.history.clear";

    // ------------------------------------------------------------------ run arguments
    public const string ArgumentCommandId = "commandId";
    public const string ArgumentHost = "host";
    public const string ArgumentInput1 = "input1";
    public const string ArgumentInput2 = "input2";
    public const string ArgumentSecondInput = "secondInput";
    public const string ArgumentInvocationId = "invocationId";

    // ------------------------------------------------------------------ host.add arguments
    public const string ArgumentAlias = "alias";
    public const string ArgumentRealHost = "hostName";
    public const string ArgumentPort = "port";
    public const string ArgumentUsername = "username";
    public const string ArgumentAuthKind = "auth";
    public const string ArgumentPassword = "password";
    public const string ArgumentPrivateKey = "privateKey";
    public const string ArgumentPassphrase = "passphrase";

    // ------------------------------------------------------------------ host-key arguments
    public const string ArgumentFingerprint = "fingerprint";
    public const string ArgumentHostKeyName = "hostKeyName";

    // ------------------------------------------------------------------ catalog.save arguments
    public const string ArgumentContent = "content";

    /// <summary>
    /// Settings write path. The module validates the five keys with the same UpdateSettingsAsync
    /// implementation the Shell uses, so the phone never writes settings.json itself.
    /// </summary>
    public const string CommandSettingsUpdate = "remote-commands-android.settings.update";

    public const string ArgumentSettingsValues = "values";

    // ------------------------------------------------------------------ values
    public const string AuthPassword = "password";
    public const string AuthPrivateKey = "privatekey";

    public const string StateSucceeded = "succeeded";
    public const string StateFailed = "failed";
    public const string StateCancelled = "cancelled";
    public const string StateHostKeyRequired = "host-key-required";

    public const string ErrorPermissionRequired = "MPT_PERMISSION_REQUIRED";
    public const string ErrorValidationFailed = "MPT_VALIDATION_FAILED";
    public const string ErrorNotFound = "MPT_NOT_FOUND";
    public const string ErrorRuntimeUnavailable = "MPT_RUNTIME_UNAVAILABLE";

    // ------------------------------------------------------------------ module events
    public const string EventRunStarted = "run.started";
    public const string EventRunStage = "run.stage";
    public const string EventRunFinished = "run.finished";
    public const string EventCommandOutput = "command.output";
    public const string EventHostKeyPending = "host-key.pending";
    public const string EventHostKeyAccepted = "host-key.accepted";
    public const string EventHostKeyRevoked = "host-key.revoked";
    public const string EventHostUpdated = "host.updated";
    public const string EventHostRemoved = "host.removed";
    public const string EventHistoryCleared = "history.cleared";

    /// <summary>Default port the module applies when <c>host.add</c> omits it.</summary>
    public const int DefaultPort = 22;
}
