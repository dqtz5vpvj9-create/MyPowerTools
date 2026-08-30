using Microsoft.Win32;
using NssmManager.Contracts;

namespace NssmManager.Compatibility;

/// <summary>Direct managed translation of registry.cpp.</summary>
public static class NssmRegistry
{
    public const uint KeyRead = 0x20019;
    public const uint KeyWrite = 0x20006;
    public const uint KeySetValue = 0x0002;
    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorNoMoreItems = 259;

    private const int KeyLength = 255;
    private const uint NormalPriorityClass = 0x20;
    private static readonly IReadOnlyDictionary<string, uint> PriorityByName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["REALTIME_PRIORITY_CLASS"] = 0x100,
        ["HIGH_PRIORITY_CLASS"] = 0x80,
        ["ABOVE_NORMAL_PRIORITY_CLASS"] = 0x8000,
        ["NORMAL_PRIORITY_CLASS"] = NormalPriorityClass,
        ["BELOW_NORMAL_PRIORITY_CLASS"] = 0x4000,
        ["IDLE_PRIORITY_CLASS"] = 0x40
    };
    private static readonly IReadOnlyDictionary<uint, string> PriorityByValue = PriorityByName.ToDictionary(pair => pair.Value, pair => pair.Key);

    [NssmUpstreamFunction("src/registry.cpp", 5, "static int service_registry_path(const TCHAR *service_name, bool parameters, const TCHAR *sub, TCHAR *buffer, unsigned long buflen)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static int service_registry_path(string serviceName, bool parameters, string? sub, uint bufferLength, out string buffer)
    {
        buffer = $@"SYSTEM\CurrentControlSet\Services\{serviceName}";
        if (parameters) buffer += @"\Parameters";
        if (parameters && sub is not null) buffer += @"\" + sub;
        if (buffer.Length >= bufferLength)
        {
            buffer = bufferLength == 0 ? string.Empty : buffer[..checked((int)bufferLength - 1)];
            return -1;
        }
        return buffer.Length;
    }

    [NssmUpstreamFunction("src/registry.cpp", 17, "static long open_registry_key(const TCHAR *registry, REGSAM sam, HKEY *key, bool must_exist)", "NssmRegistryTranslationTests.open_registry_key_honours_must_exist")]
    public static int open_registry_key(string registry, uint sam, out RegistryKey? key, bool mustExist)
    {
        key = null;
        try
        {
            key = (sam & KeySetValue) != 0
                ? Registry.LocalMachine.CreateSubKey(registry, writable: true)
                : Registry.LocalMachine.OpenSubKey(registry, writable: false);
            return key is null ? ErrorFileNotFound : ErrorSuccess;
        }
        catch (UnauthorizedAccessException)
        {
            return 5;
        }
        catch (System.Security.SecurityException)
        {
            return 5;
        }
        catch (IOException exception)
        {
            return exception.HResult & 0xFFFF;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 39, "static HKEY open_registry_key(const TCHAR *registry, REGSAM sam, bool must_exist)", "NssmRegistryTranslationTests.open_registry_key_honours_must_exist")]
    public static RegistryKey? open_registry_key(string registry, uint sam, bool mustExist)
    {
        _ = open_registry_key(registry, sam, out var key, mustExist);
        return key;
    }

    [NssmUpstreamFunction("src/registry.cpp", 45, "int create_messages()", "NssmRegistryMutationTests.create_messages_registers_event_source")]
    public static int create_messages() => create_messages(Environment.ProcessPath ?? AppContext.BaseDirectory);

    public static int create_messages(string executablePath)
    {
        const string path = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\NSSM";
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return 2;
            key.SetValue("EventMessageFile", executablePath, RegistryValueKind.String);
            key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        catch (System.Security.SecurityException)
        {
            return 2;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 70, "long enumerate_registry_values(HKEY key, unsigned long *index, TCHAR *name, unsigned long namelen)", "NssmRegistryTranslationTests.enumerate_registry_values_advances_only_on_success")]
    public static int enumerate_registry_values(RegistryKey key, ref uint index, uint nameLength, out string name)
    {
        var names = key.GetValueNames();
        if (index >= names.Length)
        {
            name = string.Empty;
            return ErrorNoMoreItems;
        }
        name = names[index];
        if (name.Length >= nameLength)
        {
            name = nameLength == 0 ? string.Empty : name[..checked((int)nameLength - 1)];
            return 234;
        }
        index++;
        return ErrorSuccess;
    }

    [NssmUpstreamFunction("src/registry.cpp", 78, "int create_parameters(nssm_service_t *service, bool editing)", "NssmRegistryTranslationTests.create_parameters_writes_upstream_types")]
    public static int create_parameters(NssmServiceConfiguration service, bool editing)
    {
        using var key = open_registry(service.Name, KeyWrite);
        if (key is null) return 1;
        if (set_expand_string(key, "Application", service.Application) != 0) return 2;
        if (set_expand_string(key, "AppParameters", service.AppParameters) != 0) return 3;
        if (set_expand_string(key, "AppDirectory", service.AppDirectory) != 0) return 4;

        SetOrDeleteNumber(key, "AppPriority", Priority(service.Priority), NormalPriorityClass, editing);
        SetOrDeleteString(key, "AppAffinity", service.Affinity, "All", RegistryValueKind.String, editing);
        SetOrDeleteNumber(key, "AppStopMethodSkip", service.StopMethodSkip, 0, editing);
        _ = create_exit_action(service.Name, Action(service.DefaultExitAction), editing);
        SetOrDeleteNumber(key, "AppRestartDelay", service.RestartDelayMilliseconds, 0, editing);
        SetOrDeleteNumber(key, "AppThrottle", service.ThrottleDelayMilliseconds, 1500, editing);
        SetOrDeleteNumber(key, "AppStopMethodConsole", service.StopMethodConsoleMilliseconds, 1500, editing);
        SetOrDeleteNumber(key, "AppStopMethodWindow", service.StopMethodWindowMilliseconds, 1500, editing);
        SetOrDeleteNumber(key, "AppStopMethodThreads", service.StopMethodThreadsMilliseconds, 1500, editing);
        SetOrDeleteNumber(key, "AppKillProcessTree", service.KillProcessTree ? 1u : 0u, 1, editing);

        WriteIo(key, "AppStdin", service.AppStdin, service.AppStdinShareMode, 2, service.AppStdinCreationDisposition, 3, service.AppStdinFlagsAndAttributes, 128, false, editing);
        WriteIo(key, "AppStdout", service.AppStdout, service.AppStdoutShareMode, 3, service.AppStdoutCreationDisposition, 4, service.AppStdoutFlagsAndAttributes, 128, service.AppStdoutCopyAndTruncate, editing);
        WriteIo(key, "AppStderr", service.AppStderr, service.AppStderrShareMode, 3, service.AppStderrCreationDisposition, 4, service.AppStderrFlagsAndAttributes, 128, service.AppStderrCopyAndTruncate, editing);

        SetOrDeleteNumber(key, "AppTimestampLog", service.TimestampLog ? 1u : 0u, 0, editing);
        SetOrDeleteNumber(key, "AppRedirectHook", service.RedirectHookOutput ? 1u : 0u, 0, editing);
        SetOrDeleteNumber(key, "AppRotateFiles", service.RotateFiles ? 1u : 0u, 0, editing);
        SetOrDeleteNumber(key, "AppRotateOnline", service.RotateOnline ? 1u : 0u, 0, editing);
        SetOrDeleteNumber(key, "AppRotateSeconds", service.RotateSeconds, 0, editing);
        SetOrDeleteNumber(key, "AppRotateBytes", unchecked((uint)service.RotateBytes), 0, editing);
        SetOrDeleteNumber(key, "AppRotateBytesHigh", unchecked((uint)(service.RotateBytes >> 32)), 0, editing);
        SetOrDeleteNumber(key, "AppRotateDelay", service.RotateDelayMilliseconds, 0, editing);
        SetOrDeleteNumber(key, "AppNoConsole", service.NoConsole ? 1u : 0u, 0, editing);
        SetOrDeleteMulti(key, "AppEnvironment", service.Environment, editing);
        SetOrDeleteMulti(key, "AppEnvironmentExtra", service.EnvironmentExtra, editing);
        return 0;
    }

    [NssmUpstreamFunction("src/registry.cpp", 208, "int create_exit_action(TCHAR *service_name, const TCHAR *action_string, bool editing)", "NssmRegistryTranslationTests.create_exit_action_uses_unnamed_value")]
    public static int create_exit_action(string serviceName, string action, bool editing)
    {
        var pathStatus = service_registry_path(serviceName, true, "AppExit", KeyLength, out var path);
        if (pathStatus < 0) return 1;
        try
        {
            using var existing = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (existing is not null && !editing) return 0;
            using var key = Registry.LocalMachine.CreateSubKey(path, writable: true);
            if (key is null) return 2;
            key.SetValue("", action, RegistryValueKind.String);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        catch (System.Security.SecurityException)
        {
            return 2;
        }
        catch (IOException)
        {
            return 3;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 243, "int get_environment(TCHAR *service_name, HKEY key, TCHAR *value, TCHAR **env, unsigned long *envlen)", "NssmRegistryTranslationTests.get_environment_requires_multi_string")]
    public static int get_environment(string serviceName, RegistryKey key, string value, out string? environment, out uint environmentLength)
    {
        environment = null;
        environmentLength = 0;
        var kind = ValueKind(key, value);
        if (kind is null) return 0;
        if (kind != RegistryValueKind.MultiString) return 2;
        if (key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string[] entries) return 2;
        environment = NssmDoubleNull.FromStrings(entries);
        if (environment.Length < 4)
        {
            environment = null;
            return 3;
        }
        environmentLength = checked((uint)NssmEnvironment.environment_length(environment));
        return 0;
    }

    [NssmUpstreamFunction("src/registry.cpp", 296, "int get_string(HKEY key, TCHAR *value, TCHAR *data, unsigned long datalen, bool expand, bool sanitise, bool must_exist)", "NssmRegistryTranslationTests.get_string_honours_expand_sanitise_and_missing")]
    public static int get_string(RegistryKey key, string value, uint dataLength, bool expand, bool sanitise, bool mustExist, out string data)
    {
        data = string.Empty;
        var kind = ValueKind(key, value);
        if (kind is null) return mustExist ? 2 : 0;
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) return 2;
        var options = expand ? RegistryValueOptions.None : RegistryValueOptions.DoNotExpandEnvironmentNames;
        if (key.GetValue(value, null, options) is not string result) return 2;
        if (sanitise) result = Unquote(result);
        var maxCharacters = checked((int)(dataLength / sizeof(char)));
        if (result.Length + 1 > maxCharacters) return expand && kind == RegistryValueKind.ExpandString ? 3 : 2;
        data = result;
        return 0;
    }

    [NssmUpstreamFunction("src/registry.cpp", 346, "int get_string(HKEY key, TCHAR *value, TCHAR *data, unsigned long datalen, bool sanitise)", "NssmRegistryTranslationTests.get_string_honours_expand_sanitise_and_missing")]
    public static int get_string(RegistryKey key, string value, uint dataLength, bool sanitise, out string data) =>
        get_string(key, value, dataLength, false, sanitise, true, out data);

    [NssmUpstreamFunction("src/registry.cpp", 350, "int expand_parameter(HKEY key, TCHAR *value, TCHAR *data, unsigned long datalen, bool sanitise, bool must_exist)", "NssmRegistryTranslationTests.get_string_honours_expand_sanitise_and_missing")]
    public static int expand_parameter(RegistryKey key, string value, uint dataLength, bool sanitise, bool mustExist, out string data) =>
        get_string(key, value, dataLength, true, sanitise, mustExist, out data);

    [NssmUpstreamFunction("src/registry.cpp", 354, "int expand_parameter(HKEY key, TCHAR *value, TCHAR *data, unsigned long datalen, bool sanitise)", "NssmRegistryTranslationTests.get_string_honours_expand_sanitise_and_missing")]
    public static int expand_parameter(RegistryKey key, string value, uint dataLength, bool sanitise, out string data) =>
        expand_parameter(key, value, dataLength, sanitise, true, out data);

    [NssmUpstreamFunction("src/registry.cpp", 363, "int set_string(HKEY key, TCHAR *value, TCHAR *string, bool expand)", "NssmRegistryTranslationTests.set_string_preserves_registry_kind")]
    public static int set_string(RegistryKey key, string value, string data, bool expand)
    {
        try
        {
            key.SetValue(value, data, expand ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            return 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 370, "int set_string(HKEY key, TCHAR *value, TCHAR *string)", "NssmRegistryTranslationTests.set_string_preserves_registry_kind")]
    public static int set_string(RegistryKey key, string value, string data) => set_string(key, value, data, false);

    [NssmUpstreamFunction("src/registry.cpp", 374, "int set_expand_string(HKEY key, TCHAR *value, TCHAR *string)", "NssmRegistryTranslationTests.set_string_preserves_registry_kind")]
    public static int set_expand_string(RegistryKey key, string value, string data) => set_string(key, value, data, true);

    [NssmUpstreamFunction("src/registry.cpp", 383, "int set_number(HKEY key, TCHAR *value, unsigned long number)", "NssmRegistryTranslationTests.get_and_set_number_match_dword_contract")]
    public static int set_number(RegistryKey key, string value, uint number)
    {
        try
        {
            key.SetValue(value, unchecked((int)number), RegistryValueKind.DWord);
            return 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 396, "int get_number(HKEY key, TCHAR *value, unsigned long *number, bool must_exist)", "NssmRegistryTranslationTests.get_and_set_number_match_dword_contract")]
    public static int get_number(RegistryKey key, string value, out uint number, bool mustExist)
    {
        number = 0;
        var kind = ValueKind(key, value);
        if (kind is null) return mustExist ? -1 : 0;
        if (kind != RegistryValueKind.DWord || key.GetValue(value) is not int stored) return -2;
        number = unchecked((uint)stored);
        return 1;
    }

    [NssmUpstreamFunction("src/registry.cpp", 413, "int get_number(HKEY key, TCHAR *value, unsigned long *number)", "NssmRegistryTranslationTests.get_and_set_number_match_dword_contract")]
    public static int get_number(RegistryKey key, string value, out uint number) => get_number(key, value, out number, true);

    [NssmUpstreamFunction("src/registry.cpp", 652, "void override_milliseconds(TCHAR *service_name, HKEY key, TCHAR *value, unsigned long *buffer, unsigned long default_value, unsigned long event)", "NssmRegistryTranslationTests.override_milliseconds_uses_default_for_invalid_value")]
    public static void override_milliseconds(string serviceName, RegistryKey key, string value, ref uint buffer, uint defaultValue, uint eventId)
    {
        if (get_number(key, value, out var result, false) == 1) buffer = result;
        else buffer = defaultValue;
    }

    [NssmUpstreamFunction("src/registry.cpp", 673, "HKEY open_service_registry(const TCHAR *service_name, REGSAM sam, bool must_exist)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static RegistryKey? open_service_registry(string serviceName, uint sam, bool mustExist)
    {
        if (service_registry_path(serviceName, false, null, KeyLength, out var path) < 0) return null;
        return open_registry_key(path, sam, mustExist);
    }

    [NssmUpstreamFunction("src/registry.cpp", 685, "long open_registry(const TCHAR *service_name, const TCHAR *sub, REGSAM sam, HKEY *key, bool must_exist)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static int open_registry(string serviceName, string? sub, uint sam, out RegistryKey? key, bool mustExist)
    {
        key = null;
        if (service_registry_path(serviceName, true, sub, KeyLength, out var path) < 0) return 0;
        return open_registry_key(path, sam, out key, mustExist);
    }

    [NssmUpstreamFunction("src/registry.cpp", 696, "HKEY open_registry(const TCHAR *service_name, const TCHAR *sub, REGSAM sam, bool must_exist)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static RegistryKey? open_registry(string serviceName, string? sub, uint sam, bool mustExist)
    {
        _ = open_registry(serviceName, sub, sam, out var key, mustExist);
        return key;
    }

    [NssmUpstreamFunction("src/registry.cpp", 702, "HKEY open_registry(const TCHAR *service_name, const TCHAR *sub, REGSAM sam)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static RegistryKey? open_registry(string serviceName, string? sub, uint sam) => open_registry(serviceName, sub, sam, true);

    [NssmUpstreamFunction("src/registry.cpp", 706, "HKEY open_registry(const TCHAR *service_name, REGSAM sam)", "NssmRegistryTranslationTests.service_registry_path_matches_upstream_shape")]
    public static RegistryKey? open_registry(string serviceName, uint sam) => open_registry(serviceName, null, sam, true);

    [NssmUpstreamFunction("src/registry.cpp", 710, "int get_io_parameters(nssm_service_t *service, HKEY key)", "NssmRegistryTranslationTests.get_io_parameters_applies_nssm_defaults")]
    public static int get_io_parameters(ref NssmServiceConfiguration service, RegistryKey key)
    {
        try
        {
            service = service with
            {
                AppStdin = ReadExpandable(key, "AppStdin"),
                AppStdinShareMode = ReadNumber(key, "AppStdinShareMode", 2),
                AppStdinCreationDisposition = ReadNumber(key, "AppStdinCreationDisposition", 3),
                AppStdinFlagsAndAttributes = ReadNumber(key, "AppStdinFlagsAndAttributes", 128),
                AppStdout = ReadExpandable(key, "AppStdout"),
                AppStdoutShareMode = ReadNumber(key, "AppStdoutShareMode", 3),
                AppStdoutCreationDisposition = ReadNumber(key, "AppStdoutCreationDisposition", 4),
                AppStdoutFlagsAndAttributes = ReadNumber(key, "AppStdoutFlagsAndAttributes", 128),
                AppStdoutCopyAndTruncate = ReadNumber(key, "AppStdoutCopyAndTruncate", 0) != 0,
                AppStderr = ReadExpandable(key, "AppStderr"),
                AppStderrShareMode = ReadNumber(key, "AppStderrShareMode", 3),
                AppStderrCreationDisposition = ReadNumber(key, "AppStderrCreationDisposition", 4),
                AppStderrFlagsAndAttributes = ReadNumber(key, "AppStderrFlagsAndAttributes", 128),
                AppStderrCopyAndTruncate = ReadNumber(key, "AppStderrCopyAndTruncate", 0) != 0
            };
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            service = service with
            {
                AppStdin = "", AppStdinShareMode = 0, AppStdinCreationDisposition = 0, AppStdinFlagsAndAttributes = 0,
                AppStdout = "", AppStdoutShareMode = 0, AppStdoutCreationDisposition = 0, AppStdoutFlagsAndAttributes = 0,
                AppStderr = "", AppStderrShareMode = 0, AppStderrCreationDisposition = 0, AppStderrFlagsAndAttributes = 0
            };
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 735, "int get_parameters(nssm_service_t *service, STARTUPINFO *si)", "NssmRegistryTranslationTests.get_parameters_reads_upstream_types")]
    public static int get_parameters(string serviceName, bool expand, out NssmServiceConfiguration service)
    {
        service = new NssmServiceConfiguration { Name = serviceName };
        using var key = open_registry(serviceName, KeyRead);
        if (key is null) return 1;
        if (get_string(key, "Application", 32767 * sizeof(char), expand, false, true, out var application) != 0) return 3;
        _ = get_string(key, "AppParameters", 16383 * sizeof(char), expand, false, false, out var parameters);
        _ = get_string(key, "AppDirectory", 32767 * sizeof(char), expand, true, false, out var directory);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Path.GetDirectoryName(application);
            if (string.IsNullOrEmpty(directory)) directory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }

        _ = get_environment(serviceName, key, "AppEnvironment", out var environmentBlock, out _);
        _ = get_environment(serviceName, key, "AppEnvironmentExtra", out var environmentExtraBlock, out _);
        var priorityValue = ReadNumber(key, "AppPriority", NormalPriorityClass);
        var rotateLow = ReadNumber(key, "AppRotateBytes", 0);
        var rotateHigh = ReadNumber(key, "AppRotateBytesHigh", 0);
        service = service with
        {
            Application = application,
            AppParameters = parameters,
            AppDirectory = directory ?? "",
            Environment = NssmDoubleNull.ToStrings(environmentBlock),
            EnvironmentExtra = NssmDoubleNull.ToStrings(environmentExtraBlock),
            Affinity = ReadRawString(key, "AppAffinity", "All"),
            Priority = PriorityByValue.GetValueOrDefault(priorityValue, "NORMAL_PRIORITY_CLASS"),
            RedirectHookOutput = ReadNumber(key, "AppRedirectHook", 0) != 0,
            RotateFiles = ReadNumber(key, "AppRotateFiles", 0) != 0,
            RotateOnline = ReadNumber(key, "AppRotateOnline", 0) != 0,
            TimestampLog = ReadNumber(key, "AppTimestampLog", 0) != 0,
            RotateSeconds = ReadNumber(key, "AppRotateSeconds", 0),
            RotateBytes = ((ulong)rotateHigh << 32) | rotateLow,
            RotateDelayMilliseconds = ReadNumber(key, "AppRotateDelay", 0),
            NoConsole = ReadNumber(key, "AppNoConsole", 0) != 0,
            RestartDelayMilliseconds = ReadNumber(key, "AppRestartDelay", 0),
            ThrottleDelayMilliseconds = ReadNumber(key, "AppThrottle", 1500),
            StopMethodSkip = ReadNumber(key, "AppStopMethodSkip", 0),
            StopMethodConsoleMilliseconds = ReadNumber(key, "AppStopMethodConsole", 1500),
            StopMethodWindowMilliseconds = ReadNumber(key, "AppStopMethodWindow", 1500),
            StopMethodThreadsMilliseconds = ReadNumber(key, "AppStopMethodThreads", 1500),
            KillProcessTree = ReadNumber(key, "AppKillProcessTree", 1) != 0
        };
        if (get_io_parameters(ref service, key) != 0) return 5;
        if (!expand)
        {
            service = service with
            {
                AppStdin = ReadRawString(key, "AppStdin", ""),
                AppStdout = ReadRawString(key, "AppStdout", ""),
                AppStderr = ReadRawString(key, "AppStderr", "")
            };
        }
        _ = get_exit_action(serviceName, null, out var exitAction, out _);
        service = service with { DefaultExitAction = ParseAction(exitAction) };
        return 0;
    }

    [NssmUpstreamFunction("src/registry.cpp", 941, "int get_exit_action(const TCHAR *service_name, unsigned long *ret, TCHAR *action, bool *default_action)", "NssmRegistryTranslationTests.get_exit_action_falls_back_to_default")]
    public static int get_exit_action(string serviceName, uint? exitCode, out string action, out bool defaultAction)
    {
        defaultAction = !exitCode.HasValue;
        action = "";
        using var key = open_registry(serviceName, "AppExit", KeyRead);
        if (key is null) return 1;
        var name = exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string configured)
        {
            action = configured;
            return 0;
        }
        if (exitCode.HasValue) return get_exit_action(serviceName, null, out action, out defaultAction);
        return 0;
    }

    [NssmUpstreamFunction("src/registry.cpp", 971, "int set_hook(const TCHAR *service_name, const TCHAR *hook_event, const TCHAR *hook_action, TCHAR *cmd)", "NssmRegistryTranslationTests.set_and_get_hook_use_event_subkey")]
    public static int set_hook(string serviceName, string hookEvent, string hookAction, string command)
    {
        var sub = $@"AppEvents\{hookEvent}";
        if (command.Length == 0)
        {
            using var existing = open_registry(serviceName, sub, KeyRead, false);
            if (existing?.GetValue(hookAction) is null) return 0;
        }
        using var key = open_registry(serviceName, sub, KeyWrite);
        if (key is null) return 1;
        if (command.Length != 0) return set_string(key, hookAction, command, true);
        try
        {
            key.DeleteValue(hookAction, throwOnMissingValue: false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 1;
        }
    }

    [NssmUpstreamFunction("src/registry.cpp", 1007, "int get_hook(const TCHAR *service_name, const TCHAR *hook_event, const TCHAR *hook_action, TCHAR *buffer, unsigned long buflen)", "NssmRegistryTranslationTests.set_and_get_hook_use_event_subkey")]
    public static int get_hook(string serviceName, string hookEvent, string hookAction, uint bufferLength, out string buffer)
    {
        var error = open_registry(serviceName, $@"AppEvents\{hookEvent}", KeyRead, out var key, false);
        using (key)
        {
            if (key is null)
            {
                buffer = "";
                return error == ErrorFileNotFound ? 0 : 1;
            }
            return expand_parameter(key, hookAction, bufferLength, true, false, out buffer);
        }
    }

    private static RegistryValueKind? ValueKind(RegistryKey key, string name)
    {
        try
        {
            return key.GetValueKind(name);
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static uint Priority(string name) => PriorityByName.GetValueOrDefault(name, NormalPriorityClass);
    private static string Action(NssmExitAction action) => action switch
    {
        NssmExitAction.Restart => "Restart",
        NssmExitAction.Ignore => "Ignore",
        NssmExitAction.Exit => "Exit",
        NssmExitAction.Suicide => "Suicide",
        _ => "Restart"
    };
    private static NssmExitAction ParseAction(string? action) => action?.ToLowerInvariant() switch
    {
        "ignore" => NssmExitAction.Ignore,
        "exit" => NssmExitAction.Exit,
        "suicide" => NssmExitAction.Suicide,
        _ => NssmExitAction.Restart
    };

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string ReadExpandable(RegistryKey key, string name) =>
        key.GetValue(name, "", RegistryValueOptions.None) as string ?? "";

    private static string ReadRawString(RegistryKey key, string name, string fallback) =>
        key.GetValue(name, fallback, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? fallback;

    private static uint ReadNumber(RegistryKey key, string name, uint fallback) =>
        key.GetValue(name) is int value ? unchecked((uint)value) : fallback;

    private static void SetOrDeleteNumber(RegistryKey key, string name, uint value, uint defaultValue, bool editing)
    {
        if (value != defaultValue) _ = set_number(key, name, value);
        else if (editing) key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void SetOrDeleteString(RegistryKey key, string name, string value, string defaultValue, RegistryValueKind kind, bool editing)
    {
        if (!string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase) && value.Length != 0) key.SetValue(name, value, kind);
        else if (editing) key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void SetOrDeleteMulti(RegistryKey key, string name, string[] values, bool editing)
    {
        if (values.Length != 0) key.SetValue(name, values, RegistryValueKind.MultiString);
        else if (editing) key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void WriteIo(RegistryKey key, string prefix, string path, uint sharing, uint defaultSharing, uint disposition, uint defaultDisposition, uint flags, uint defaultFlags, bool copyAndTruncate, bool editing)
    {
        if (path.Length != 0) _ = set_expand_string(key, prefix, path);
        else if (editing) key.DeleteValue(prefix, false);
        SetOrDeleteNumber(key, prefix + "ShareMode", sharing, defaultSharing, editing);
        SetOrDeleteNumber(key, prefix + "CreationDisposition", disposition, defaultDisposition, editing);
        SetOrDeleteNumber(key, prefix + "FlagsAndAttributes", flags, defaultFlags, editing);
        if (prefix != "AppStdin") SetOrDeleteNumber(key, prefix + "CopyAndTruncate", copyAndTruncate ? 1u : 0u, 0, editing);
    }
}
