namespace NssmManager.Contracts;

public static class NssmSettings
{
    public const string ParametersKey = "Parameters";
    public const string ExitKey = "AppExit";
    public const string HooksKey = "AppEvents";

    public static IReadOnlyList<NssmSettingDescriptor> All { get; } =
    [
        new("Application", NssmSettingKind.ExpandString, ""),
        new("AppParameters", NssmSettingKind.ExpandString, ""),
        new("AppDirectory", NssmSettingKind.ExpandString, ""),
        new("AppExit", NssmSettingKind.ExitAction, "Restart", RequiresSubparameter: true),
        new("AppEvents", NssmSettingKind.Hook, "", RequiresSubparameter: true),
        new("AppAffinity", NssmSettingKind.Affinity, "All"),
        new("AppEnvironment", NssmSettingKind.MultiString, Array.Empty<string>()),
        new("AppEnvironmentExtra", NssmSettingKind.MultiString, Array.Empty<string>()),
        new("AppNoConsole", NssmSettingKind.Dword, 0u),
        new("AppPriority", NssmSettingKind.Priority, "NORMAL_PRIORITY_CLASS"),
        new("AppRestartDelay", NssmSettingKind.Dword, 0u),
        new("AppStdin", NssmSettingKind.ExpandString, ""),
        new("AppStdinShareMode", NssmSettingKind.Dword, 2u),
        new("AppStdinCreationDisposition", NssmSettingKind.Dword, 3u),
        new("AppStdinFlagsAndAttributes", NssmSettingKind.Dword, 128u),
        new("AppStdout", NssmSettingKind.ExpandString, ""),
        new("AppStdoutShareMode", NssmSettingKind.Dword, 3u),
        new("AppStdoutCreationDisposition", NssmSettingKind.Dword, 4u),
        new("AppStdoutFlagsAndAttributes", NssmSettingKind.Dword, 128u),
        new("AppStdoutCopyAndTruncate", NssmSettingKind.Dword, 0u),
        new("AppStderr", NssmSettingKind.ExpandString, ""),
        new("AppStderrShareMode", NssmSettingKind.Dword, 3u),
        new("AppStderrCreationDisposition", NssmSettingKind.Dword, 4u),
        new("AppStderrFlagsAndAttributes", NssmSettingKind.Dword, 128u),
        new("AppStderrCopyAndTruncate", NssmSettingKind.Dword, 0u),
        new("AppStopMethodSkip", NssmSettingKind.Dword, 0u),
        new("AppStopMethodConsole", NssmSettingKind.Dword, 1500u),
        new("AppStopMethodWindow", NssmSettingKind.Dword, 1500u),
        new("AppStopMethodThreads", NssmSettingKind.Dword, 1500u),
        new("AppKillProcessTree", NssmSettingKind.Dword, 1u),
        new("AppThrottle", NssmSettingKind.Dword, 1500u),
        new("AppRedirectHook", NssmSettingKind.Dword, 0u),
        new("AppRotateFiles", NssmSettingKind.Dword, 0u),
        new("AppRotateOnline", NssmSettingKind.Dword, 0u),
        new("AppRotateSeconds", NssmSettingKind.Dword, 0u),
        new("AppRotateBytes", NssmSettingKind.Dword, 0u),
        new("AppRotateBytesHigh", NssmSettingKind.Dword, 0u),
        new("AppRotateDelay", NssmSettingKind.Dword, 0u),
        new("AppTimestampLog", NssmSettingKind.Dword, 0u),
        new("DependOnGroup", NssmSettingKind.Native, Array.Empty<string>(), true),
        new("DependOnService", NssmSettingKind.Native, Array.Empty<string>(), true),
        new("Description", NssmSettingKind.Native, "", true),
        new("DisplayName", NssmSettingKind.Native, "", true),
        new("Environment", NssmSettingKind.Native, Array.Empty<string>(), true),
        new("ImagePath", NssmSettingKind.Native, "", true),
        new("ObjectName", NssmSettingKind.Native, "LocalSystem", true),
        new("Name", NssmSettingKind.Native, "", true),
        new("Start", NssmSettingKind.Native, "SERVICE_AUTO_START", true),
        new("Type", NssmSettingKind.Native, "SERVICE_WIN32_OWN_PROCESS", true)
    ];

    public static NssmSettingDescriptor Find(string name) =>
        All.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
        throw new ArgumentException($"Unknown parameter '{name}'.", nameof(name));
}
