using System.Text.Json.Serialization;

namespace NssmManager.Contracts;

public enum NssmExitAction { Restart, Ignore, Exit, Suicide }
public enum NssmStartupType { Automatic, DelayedAutomatic, Manual, Disabled }
public enum NssmServiceState { Unknown, Stopped, StartPending, StopPending, Running, ContinuePending, PausePending, Paused }
public enum NssmSettingKind { String, ExpandString, Dword, MultiString, ExitAction, Hook, Affinity, Priority, Native }

public sealed record NssmSettingDescriptor(
    string Name,
    NssmSettingKind Kind,
    object? DefaultValue,
    bool Native = false,
    bool RequiresSubparameter = false);

/// <summary>
/// Managed equivalent of NSSM's value_t union.  The active member is selected
/// by the setting's registry type exactly as it is in settings.cpp.
/// </summary>
public sealed class NssmSettingValue
{
    public string? String { get; set; }
    public uint Numeric { get; set; }

    public static NssmSettingValue FromString(string? value) => new() { String = value };
    public static NssmSettingValue FromNumber(uint value) => new() { Numeric = value };
}

public sealed record NssmExitRule(uint? ExitCode, NssmExitAction Action);

public sealed record NssmHook(string Event, string Action, string Command);

public sealed record NssmServiceConfiguration
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public string Application { get; init; } = "";
    public string AppParameters { get; init; } = "";
    public string AppDirectory { get; init; } = "";
    public string ServiceAccount { get; init; } = "LocalSystem";
    [JsonIgnore] public char[]? ServicePassword { get; init; }
    public NssmStartupType StartupType { get; init; } = NssmStartupType.Automatic;
    public bool Interactive { get; init; }
    public string[] DependOnService { get; init; } = [];
    public string[] DependOnGroup { get; init; } = [];
    public string[] ServiceEnvironment { get; init; } = [];
    public string[] Environment { get; init; } = [];
    public string[] EnvironmentExtra { get; init; } = [];
    public string AppStdin { get; init; } = "";
    public string AppStdout { get; init; } = "";
    public string AppStderr { get; init; } = "";
    public uint AppStdinShareMode { get; init; } = 2;
    public uint AppStdoutShareMode { get; init; } = 3;
    public uint AppStderrShareMode { get; init; } = 3;
    public uint AppStdinCreationDisposition { get; init; } = 3;
    public uint AppStdoutCreationDisposition { get; init; } = 4;
    public uint AppStderrCreationDisposition { get; init; } = 4;
    public uint AppStdinFlagsAndAttributes { get; init; } = 128;
    public uint AppStdoutFlagsAndAttributes { get; init; } = 128;
    public uint AppStderrFlagsAndAttributes { get; init; } = 128;
    public bool AppStdoutCopyAndTruncate { get; init; }
    public bool AppStderrCopyAndTruncate { get; init; }
    public bool RedirectHookOutput { get; init; }
    public bool RotateFiles { get; init; }
    public bool RotateOnline { get; init; }
    public uint RotateSeconds { get; init; }
    public ulong RotateBytes { get; init; }
    public uint RotateDelayMilliseconds { get; init; } = 0;
    public bool TimestampLog { get; init; }
    public uint RestartDelayMilliseconds { get; init; }
    public uint ThrottleDelayMilliseconds { get; init; } = 1500;
    public uint StopMethodSkip { get; init; }
    public uint StopMethodConsoleMilliseconds { get; init; } = 1500;
    public uint StopMethodWindowMilliseconds { get; init; } = 1500;
    public uint StopMethodThreadsMilliseconds { get; init; } = 1500;
    public bool KillProcessTree { get; init; } = true;
    public bool NoConsole { get; init; }
    public string Priority { get; init; } = "NORMAL_PRIORITY_CLASS";
    public string Affinity { get; init; } = "All";
    public NssmExitAction DefaultExitAction { get; init; } = NssmExitAction.Restart;
    public NssmExitRule[] ExitRules { get; init; } = [];
    public NssmHook[] Hooks { get; init; } = [];
}

public sealed record NssmServiceSnapshot(
    string Name,
    string DisplayName,
    string Description,
    string Application,
    string ImagePath,
    NssmServiceState State,
    NssmStartupType StartupType,
    uint ProcessId,
    bool IsNssmCompatible,
    bool IsManagedByCSharp);

public sealed record NssmOperationResult(bool Success, string Message, int Win32Error = 0);

public sealed record NssmProcessSnapshot(uint ProcessId, int Depth, string ImagePath);

public sealed record NssmRegistryValueSnapshot(string Name, int Kind, string? StringValue, string[]? MultiStringValue, uint? DwordValue, byte[]? BinaryValue);
public sealed record NssmRegistryKeySnapshot(string RelativePath, NssmRegistryValueSnapshot[] Values);

public sealed record NssmMigrationSnapshot(
    int SchemaVersion,
    string ServiceName,
    string OriginalImagePath,
    NssmServiceConfiguration Configuration,
    DateTimeOffset CreatedAt,
    NssmServiceState State = NssmServiceState.Unknown,
    NssmRegistryKeySnapshot[]? RegistryKeys = null);
