using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NssmManager.Compatibility;
using NssmManager.Contracts;

namespace NssmManager.Windows;

public delegate int NssmSettingOperation(
    string serviceName,
    object? parameter,
    string name,
    object? defaultValue,
    NssmSettingValue? value,
    string? additional);

public sealed record NssmTranslatedSetting(
    string Name,
    uint Type,
    object? DefaultValue,
    bool Native,
    int Additional,
    NssmSettingOperation? Set,
    NssmSettingOperation? Get,
    NssmSettingOperation? Dump);

/// <summary>Function-for-function managed translation of settings.cpp.</summary>
public static class NssmSettingsTranslation
{
    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;
    public const uint RegDword = 4;
    public const uint RegMultiSz = 7;
    public const int AdditionalGetting = 1;
    public const int AdditionalSetting = 2;
    public const int AdditionalResetting = 4;
    public const int AdditionalCrlf = 8;
    public const int AdditionalMandatory = AdditionalGetting | AdditionalSetting | AdditionalResetting;
    public const int DependencyServices = 1;
    public const int DependencyGroups = 2;

    private const uint ServiceNoChange = 0xffffffff;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceDisabled = 4;
    private const uint ServiceKernelDriver = 1;
    private const uint ServiceFileSystemDriver = 2;
    private const uint ServiceWin32ShareProcess = 0x20;
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidLevel = 124;
    private const int ValueLength = 16384;
    private const int ServiceNameLength = 256;
    private const int HookNameLength = 32;

    private static readonly string[] ExitActions = ["Restart", "Ignore", "Exit", "Suicide"];
    private static readonly string[] StartupStrings = ["SERVICE_AUTO_START", "SERVICE_DELAYED_AUTO_START", "SERVICE_DEMAND_START", "SERVICE_DISABLED"];
    private static readonly string[] PriorityStrings = ["REALTIME_PRIORITY_CLASS", "HIGH_PRIORITY_CLASS", "ABOVE_NORMAL_PRIORITY_CLASS", "NORMAL_PRIORITY_CLASS", "BELOW_NORMAL_PRIORITY_CLASS", "IDLE_PRIORITY_CLASS"];
    private static readonly uint[] PriorityConstants = [0x100, 0x80, 0x8000, 0x20, 0x4000, 0x40];
    private static readonly string[] HookEvents = ["Start", "Stop", "Exit", "Power", "Rotate"];
    private static readonly string[] HookActions = ["Pre", "Post", "Change", "Resume"];

    [NssmUpstreamFunction("src/settings.cpp", 16, "static inline int is_default(const TCHAR *value)", "NssmSettingsTranslationTests.defaults_and_types_match_upstream")]
    public static int is_default(string value) =>
        NssmCore.str_equiv(value, "Default") != 0 || NssmCore.str_equiv(value, "*") != 0 || value.Length == 0 ? 1 : 0;

    [NssmUpstreamFunction("src/settings.cpp", 21, "static inline bool is_string_type(const unsigned long type)", "NssmSettingsTranslationTests.defaults_and_types_match_upstream")]
    public static bool is_string_type(uint type) => type is RegMultiSz or RegExpandSz or RegSz;

    [NssmUpstreamFunction("src/settings.cpp", 24, "static inline bool is_numeric_type(const unsigned long type)", "NssmSettingsTranslationTests.defaults_and_types_match_upstream")]
    public static bool is_numeric_type(uint type) => type == RegDword;

    [NssmUpstreamFunction("src/settings.cpp", 28, "static int value_from_string(const TCHAR *name, value_t *value, const TCHAR *string)", "NssmSettingsTranslationTests.value_from_string_matches_union_contract")]
    public static int value_from_string(string name, NssmSettingValue value, string text)
    {
        value.String = text.Length == 0 ? null : new string(text.ToCharArray());
        return text.Length == 0 ? 0 : 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 51, "static int setting_set_number(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.number_and_string_settings_match_registry_contract")]
    public static int setting_set_number(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key) return -1;
        try
        {
            if (value?.String is null)
            {
                DeleteValue(key, name);
                return 0;
            }
            if (NssmCore.str_number(value.String, out var number) != 0) return -1;
            if (defaultValue is not null && number == Convert.ToUInt32(defaultValue))
            {
                DeleteValue(key, name);
                return 0;
            }
            return NssmRegistry.set_number(key, name, number) == 0 ? 1 : -1;
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return -1;
        }
    }

    [NssmUpstreamFunction("src/settings.cpp", 79, "static int setting_get_number(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.number_and_string_settings_match_registry_contract")]
    public static int setting_get_number(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        var ret = NssmRegistry.get_number(key, name, out var number, false);
        if (ret == 1) value.Numeric = number;
        return ret;
    }

    [NssmUpstreamFunction("src/settings.cpp", 84, "static int setting_set_string(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.number_and_string_settings_match_registry_contract")]
    public static int setting_set_string(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key) return -1;
        try
        {
            var text = value?.String;
            if (text is null)
            {
                if (defaultValue is string defaultText) text = defaultText;
                else
                {
                    DeleteValue(key, name);
                    return 0;
                }
            }
            if (defaultValue is string nonemptyDefault && nonemptyDefault.Length > 0 && NssmCore.str_equiv(text, nonemptyDefault) != 0)
            {
                DeleteValue(key, name);
                return 0;
            }
            return NssmRegistry.set_expand_string(key, name, text) == 0 ? 1 : -1;
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return -1;
        }
    }

    [NssmUpstreamFunction("src/settings.cpp", 112, "static int setting_get_string(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.number_and_string_settings_match_registry_contract")]
    public static int setting_get_string(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        if (NssmRegistry.get_string(key, name, ValueLength * sizeof(char), false, false, false, out var text) != 0) return -1;
        return value_from_string(name, value, text);
    }

    [NssmUpstreamFunction("src/settings.cpp", 121, "static int setting_not_dumpable(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dump_string_matches_upstream_command_shape")]
    public static int setting_not_dumpable(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) => 0;

    [NssmUpstreamFunction("src/settings.cpp", 125, "static int setting_dump_string(const TCHAR *service_name, void *param, const TCHAR *name, const value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dump_string_matches_upstream_command_shape")]
    public static int setting_dump_string(string serviceName, object? parameter, string name, NssmSettingValue value, string? additional)
    {
        if (NssmCore.quote(serviceName, ServiceNameLength * 2, out var quotedServiceName) != 0) return 1;
        var quotedAdditional = string.Empty;
        if (additional is not null)
        {
            if (additional.Length > 0)
            {
                if (NssmCore.quote(additional, ValueLength * 2, out quotedAdditional) != 0) return 3;
            }
            else quotedAdditional = "\"\"";
        }

        string quotedValue;
        var type = Convert.ToUInt32(parameter);
        if (is_string_type(type))
        {
            var text = value.String ?? string.Empty;
            if (text.Length > 0)
            {
                if (NssmCore.quote(text, ValueLength * 2, out quotedValue) != 0) return 2;
            }
            else quotedValue = "\"\"";
        }
        else if (is_numeric_type(type)) quotedValue = value.Numeric.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else return 2;

        if (NssmCore.quote(NssmCore.nssm_exe(), 65536, out var quotedNssm) != 0) return 3;
        Console.Out.WriteLine(quotedAdditional.Length > 0
            ? $"{quotedNssm} set {quotedServiceName} {name} {quotedAdditional} {quotedValue}"
            : $"{quotedNssm} set {quotedServiceName} {name} {quotedValue}");
        return 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 157, "static int setting_set_exit_action(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_set_exit_action(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        string? code = null;
        if (additional is not null && is_default(additional) == 0)
        {
            if (NssmCore.str_number(additional, out _) != 0) return -1;
            code = additional;
        }
        using var key = NssmRegistry.open_registry(serviceName, name, NssmRegistry.KeyWrite, false);
        if (key is null) return -1;
        try
        {
            string action;
            var ret = 1;
            if (value?.String is not null) action = value.String;
            else if (code is not null)
            {
                DeleteValue(key, code);
                return 0;
            }
            else
            {
                action = defaultValue as string ?? string.Empty;
                ret = 0;
            }
            var canonical = ExitActions.FirstOrDefault(item => NssmCore.str_equiv(item, action) != 0);
            if (canonical is null) return -1;
            if (defaultValue is string defaultText && NssmCore.str_equiv(action, defaultText) != 0) ret = 0;
            key.SetValue(code ?? string.Empty, canonical, RegistryValueKind.String);
            return ret;
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return -1;
        }
    }

    [NssmUpstreamFunction("src/settings.cpp", 216, "static int setting_get_exit_action(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_get_exit_action(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (value is null) return -1;
        uint? code = null;
        if (additional is not null && is_default(additional) == 0)
        {
            if (NssmCore.str_number(additional, out var parsed) != 0) return -1;
            code = parsed;
        }
        if (NssmRegistry.get_exit_action(serviceName, code, out var action, out var defaultAction) != 0) return -1;
        _ = value_from_string(name, value, action);
        return defaultAction && defaultValue is string defaultText && NssmCore.str_equiv(action, defaultText) != 0 ? 0 : 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 237, "static int setting_dump_exit_action(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_dump_exit_action(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (value is null) return -1;
        using var key = NssmRegistry.open_registry(serviceName, "AppExit", NssmRegistry.KeyRead);
        if (key is null) return -1;
        var errors = 0;
        foreach (var registryName in key.GetValueNames())
        {
            // Preserve the upstream predicate, including its permissive digit check.
            var valid = registryName.All(character => character >= '0' || character <= '9');
            if (!valid) continue;
            var subparameter = registryName.Length > 0 ? registryName : "Default";
            var ret = setting_get_exit_action(serviceName, null, name, defaultValue, value, subparameter);
            if (ret == 1)
            {
                if (setting_dump_string(serviceName, RegSz, name, value, subparameter) != 0) errors++;
            }
            else if (ret < 0) errors++;
        }
        return errors > 0 ? -1 : 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 271, "static inline bool split_hook_name(const TCHAR *hook_name, TCHAR *hook_event, TCHAR *hook_action)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static bool split_hook_name(string hookName, out string hookEvent, out string hookAction)
    {
        var separator = hookName.IndexOf('/');
        if (separator < 0)
        {
            hookEvent = hookAction = string.Empty;
            return false;
        }
        hookEvent = Truncate(hookName[..separator], HookNameLength - 1);
        hookAction = Truncate(hookName[(separator + 1)..], HookNameLength - 1);
        return ValidHookName(hookEvent, hookAction);
    }

    [NssmUpstreamFunction("src/settings.cpp", 288, "static int setting_set_hook(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_set_hook(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (additional is null || !split_hook_name(additional, out var hookEvent, out var hookAction)) return -1;
        var command = value?.String ?? string.Empty;
        if (NssmRegistry.set_hook(serviceName, hookEvent, hookAction, command) != 0) return -1;
        return command.Length == 0 ? 0 : 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 302, "static int setting_get_hook(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_get_hook(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (value is null || additional is null || !split_hook_name(additional, out var hookEvent, out var hookAction)) return -1;
        if (NssmRegistry.get_hook(serviceName, hookEvent, hookAction, ValueLength * sizeof(char), out var command) != 0) return -1;
        _ = value_from_string(name, value, command);
        return command.Length == 0 ? 0 : 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 316, "static int setting_dump_hooks(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.exit_action_and_hook_validation_match_upstream")]
    public static int setting_dump_hooks(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (value is null) return -1;
        var errors = 0;
        foreach (var hookEvent in HookEvents)
        foreach (var hookAction in HookActions)
        {
            if (!ValidHookName(hookEvent, hookAction)) continue;
            var hookName = $"{hookEvent}/{hookAction}";
            var ret = setting_get_hook(serviceName, parameter, name, defaultValue, value, hookName);
            if (ret != 1)
            {
                if (ret < 0) errors++;
                continue;
            }
            if (setting_dump_string(serviceName, RegSz, name, value, hookName) != 0) errors++;
        }
        return errors > 0 ? -1 : 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 343, "static int setting_set_affinity(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_set_affinity(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key) return -1;
        ulong mask = 0;
        var requested = value?.String;
        if (requested is not null && is_default(requested) == 0 && NssmCore.str_equiv(requested, "All") == 0)
        {
            if (AffinityStringToMask(requested, out mask) != 0) return -1;
        }
        if (mask == 0)
        {
            try { DeleteValue(key, name); return 0; }
            catch (Exception exception) when (IsRegistryException(exception)) { return -1; }
        }
        if (!GetProcessAffinityMask(GetCurrentProcess(), out _, out var systemMask)) systemMask = ulong.MaxValue;
        var effective = mask & systemMask;
        if (effective != mask && effective == 0) mask = systemMask;
        if (AffinityMaskToString(mask, out var canonical) != 0 || canonical is null) canonical = value!.String;
        try { key.SetValue(name, canonical!, RegistryValueKind.String); return 1; }
        catch (Exception exception) when (IsRegistryException(exception)) { return -1; }
    }

    [NssmUpstreamFunction("src/settings.cpp", 400, "static int setting_get_affinity(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_get_affinity(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        if (!HasValue(key, name)) return value_from_string(name, value, "All") == 1 ? 0 : -1;
        if (key.GetValueKind(name) != RegistryValueKind.String || key.GetValue(name) is not string text) return -1;
        if (AffinityStringToMask(text, out var mask) != 0 || AffinityMaskToString(mask, out var canonical) != 0 || canonical is null) return -1;
        return value_from_string(name, value, canonical);
    }

    [NssmUpstreamFunction("src/settings.cpp", 448, "static int setting_set_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_set_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key) return -1;
        var text = value?.String;
        string? unformatted = null;
        uint newLength = 0;
        var operation = 0;
        if (!string.IsNullOrEmpty(text))
        {
            if (text[0] == '+') operation = 1;
            else if (text[0] == '-') operation = -1;
            else if (text[0] == ':') text = text[1..];
        }
        if (operation != 0)
        {
            text = text![1..];
            if (NssmRegistry.get_environment(serviceName, key, name, out var environment, out var environmentLength) != 0) return -1;
            if (environment is not null)
            {
                var ret = operation > 0
                    ? NssmEnvironment.append_to_environment_block(environment, environmentLength, text, out unformatted, out newLength)
                    : NssmEnvironment.remove_from_environment_block(environment, environmentLength, text, out unformatted, out newLength);
                if (ret != 0) return -1;
                text = unformatted;
            }
            else
            {
                if (operation < 0) return 0;
                operation = 0;
            }
        }
        if (string.IsNullOrEmpty(text))
        {
            try { DeleteValue(key, name); return 0; }
            catch (Exception exception) when (IsRegistryException(exception)) { return -1; }
        }
        if (operation == 0 && NssmDoubleNull.unformat_double_null(text, checked((uint)text.Length), out unformatted, out newLength) != 0) return -1;
        if (NssmEnvironment.test_environment(unformatted) != 0) return -1;
        try
        {
            key.SetValue(name, NssmDoubleNull.ToStrings(unformatted, newLength), RegistryValueKind.MultiString);
            return 1;
        }
        catch (Exception exception) when (IsRegistryException(exception))
        {
            return -1;
        }
    }

    [NssmUpstreamFunction("src/settings.cpp", 517, "static int setting_get_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_get_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        if (NssmRegistry.get_environment(serviceName, key, name, out var environment, out var environmentLength) != 0) return -1;
        if (environmentLength == 0 || environment is null) return 0;
        if (additional is not null)
        {
            var prefix = additional + "=";
            var found = NssmDoubleNull.ToStrings(environment, environmentLength)
                .FirstOrDefault(item => item.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return found is null ? 0 : value_from_string(name, value, found[prefix.Length..]);
        }
        if (NssmDoubleNull.format_double_null(environment, environmentLength, out var formatted, out _) != 0 || formatted is null) return -1;
        return value_from_string(name, value, formatted.TrimEnd('\0'));
    }

    [NssmUpstreamFunction("src/settings.cpp", 559, "static int setting_dump_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_dump_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        if (NssmRegistry.get_environment(serviceName, key, name, out var environment, out var environmentLength) != 0) return -1;
        if (environmentLength == 0 || environment is null) return 0;
        var errors = 0;
        var entries = NssmDoubleNull.ToStrings(environment, environmentLength);
        for (var index = 0; index < entries.Length; index++)
        {
            value.String = (index > 0 ? "+" : ":") + entries[index];
            if (setting_dump_string(serviceName, RegSz, name, value, null) != 0) errors++;
            value.String = null;
        }
        return errors > 0 ? 1 : 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 592, "static int setting_set_priority(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_set_priority(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key) return -1;
        var priority = value?.String ?? defaultValue as string;
        try
        {
            if (priority is null) { DeleteValue(key, name); return 0; }
            var index = Array.FindIndex(PriorityStrings, item => NssmCore.str_equiv(item, priority) != 0);
            if (index < 0) return -1;
            if (defaultValue is string defaultText && NssmCore.str_equiv(priority, defaultText) != 0) { DeleteValue(key, name); return 0; }
            return NssmRegistry.set_number(key, name, PriorityConstants[index]) == 0 ? 1 : -1;
        }
        catch (Exception exception) when (IsRegistryException(exception)) { return -1; }
    }

    [NssmUpstreamFunction("src/settings.cpp", 629, "static int setting_get_priority(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_get_priority(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not RegistryKey key || value is null) return -1;
        var ret = NssmRegistry.get_number(key, name, out var constant, false);
        if (ret == 0)
        {
            if (value_from_string(name, value, defaultValue as string ?? string.Empty) == -1) return -1;
            return 0;
        }
        if (ret < 0) return -1;
        var index = Array.IndexOf(PriorityConstants, constant);
        if (index < 0) index = 3;
        return value_from_string(name, value, PriorityStrings[index]);
    }

    [NssmUpstreamFunction("src/settings.cpp", 644, "static int setting_dump_priority(const TCHAR *service_name, void *key_ptr, const TCHAR *name, void *setting_ptr, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.affinity_priority_and_environment_match_upstream")]
    public static int setting_dump_priority(string serviceName, object? parameter, string name, object? settingParameter, NssmSettingValue? value, string? additional)
    {
        if (settingParameter is not NssmTranslatedSetting setting || value is null) return -1;
        var ret = setting_get_priority(serviceName, parameter, name, setting.DefaultValue, value, null);
        return ret == 1 ? setting_dump_string(serviceName, RegSz, name, value, null) : ret;
    }

    [NssmUpstreamFunction("src/settings.cpp", 652, "static int native_set_dependon(const TCHAR *service_name, SC_HANDLE service_handle, TCHAR **dependencies, unsigned long *dependencieslen, value_t *value, int type)", "NssmSettingsTranslationTests.dependency_protocol_matches_upstream")]
    public static int native_set_dependon(string serviceName, IntPtr serviceHandle, out string? dependencies, out uint dependenciesLength, NssmSettingValue? value, int type)
    {
        dependencies = null;
        dependenciesLength = 0;
        if (value?.String is not { Length: > 0 } text) return 0;
        var operation = 0;
        if (text[0] == '+') operation = 1;
        else if (text[0] == '-') operation = -1;
        else if (text[0] == ':') text = text[1..];
        if (operation != 0)
        {
            text = text[1..];
            var current = GetServiceDependencies(serviceName, serviceHandle, type);
            if (current.Length > 0)
            {
                var block = NssmDoubleNull.FromStrings(current);
                var candidate = type == DependencyGroups ? "+" + text.TrimStart('+') : text;
                var ret = operation > 0
                    ? NssmDoubleNull.append_to_double_null(block, checked((uint)block.Length), out dependencies, out dependenciesLength, candidate, 0, false)
                    : NssmDoubleNull.remove_from_double_null(block, checked((uint)block.Length), out dependencies, out dependenciesLength, candidate, 0, false);
                return ret;
            }
            if (operation < 0) return 0;
        }
        if (NssmDoubleNull.unformat_double_null(text, checked((uint)text.Length), out dependencies, out dependenciesLength) != 0) return -1;
        if (type == DependencyGroups && dependencies is not null)
        {
            dependencies = NssmDoubleNull.FromStrings(NssmDoubleNull.ToStrings(dependencies, dependenciesLength).Select(item => "+" + item.TrimStart('+')));
            dependenciesLength = checked((uint)dependencies.Length);
        }
        return 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 738, "static int native_set_dependongroup(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_dependongroup(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero) return -1;
        var services = GetServiceDependencies(serviceName, handle, DependencyServices);
        if (value?.String is not { Length: > 0 }) return SetServiceDependencies(handle, services) ? 0 : -1;
        if (native_set_dependon(serviceName, handle, out var groupsBlock, out var groupsLength, value, DependencyGroups) != 0) return -1;
        var groups = NssmDoubleNull.ToStrings(groupsBlock, groupsLength);
        return SetServiceDependencies(handle, services.Concat(groups)) ? 1 : -1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 789, "static int native_get_dependongroup(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_dependongroup(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        NativeGetDependOn(serviceName, parameter, name, value, DependencyGroups);

    [NssmUpstreamFunction("src/settings.cpp", 818, "static int setting_dump_dependon(const TCHAR *service_name, SC_HANDLE service_handle, const TCHAR *name, int type, value_t *value)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int setting_dump_dependon(string serviceName, IntPtr serviceHandle, string name, int type, NssmSettingValue value)
    {
        if (serviceHandle == IntPtr.Zero) return -1;
        var dependencies = GetServiceDependencies(serviceName, serviceHandle, type);
        var errors = 0;
        for (var index = 0; index < dependencies.Length; index++)
        {
            value.String = (index > 0 ? "+" : ":") + dependencies[index];
            if (setting_dump_string(serviceName, RegSz, name, value, null) != 0) errors++;
            value.String = null;
        }
        return errors > 0 ? 1 : 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 849, "static int native_dump_dependongroup(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_dump_dependongroup(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        parameter is IntPtr handle && value is not null ? setting_dump_dependon(serviceName, handle, name, DependencyGroups, value) : -1;

    [NssmUpstreamFunction("src/settings.cpp", 853, "static int native_set_dependonservice(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_dependonservice(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero) return -1;
        var groups = GetServiceDependencies(serviceName, handle, DependencyGroups);
        if (value?.String is not { Length: > 0 }) return SetServiceDependencies(handle, groups) ? 0 : -1;
        if (native_set_dependon(serviceName, handle, out var servicesBlock, out var servicesLength, value, DependencyServices) != 0) return -1;
        var services = NssmDoubleNull.ToStrings(servicesBlock, servicesLength);
        return SetServiceDependencies(handle, services.Concat(groups)) ? 1 : -1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 904, "static int native_get_dependonservice(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_dependonservice(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        NativeGetDependOn(serviceName, parameter, name, value, DependencyServices);

    [NssmUpstreamFunction("src/settings.cpp", 933, "static int native_dump_dependonservice(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_dump_dependonservice(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        parameter is IntPtr handle && value is not null ? setting_dump_dependon(serviceName, handle, name, DependencyServices, value) : -1;

    [NssmUpstreamFunction("src/settings.cpp", 937, "int native_set_description(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_description(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero) return -1;
        var description = value?.String;
        var nativeDescription = new NativeMethods.ServiceDescription { Description = description is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(description) };
        var structure = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDescription>());
        try
        {
            Marshal.StructureToPtr(nativeDescription, structure, false);
            if (!NativeMethods.ChangeServiceConfig2(handle, NativeMethods.ServiceConfigDescription, structure)) return -1;
            return string.IsNullOrEmpty(description) ? 0 : 1;
        }
        finally
        {
            if (nativeDescription.Description != IntPtr.Zero) Marshal.FreeHGlobal(nativeDescription.Description);
            Marshal.FreeHGlobal(structure);
        }
    }

    [NssmUpstreamFunction("src/settings.cpp", 950, "int native_get_description(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_description(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value is null) return -1;
        NativeMethods.QueryServiceConfig2(handle, NativeMethods.ServiceConfigDescription, IntPtr.Zero, 0, out var required);
        if (required == 0) { value.String = null; return 0; }
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(handle, NativeMethods.ServiceConfigDescription, buffer, required, out _)) return -1;
            var description = Marshal.PtrToStructure<NativeMethods.ServiceDescription>(buffer);
            var text = Marshal.PtrToStringUni(description.Description) ?? string.Empty;
            return text.Length == 0 ? 0 : value_from_string(name, value, text);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [NssmUpstreamFunction("src/settings.cpp", 963, "int native_set_displayname(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_displayname(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero) return -1;
        var displayName = value?.String ?? serviceName;
        if (!ChangeServiceConfig(handle, displayName: displayName)) return -1;
        return NssmCore.str_equiv(displayName, serviceName) == 0 ? 1 : 0;
    }

    [NssmUpstreamFunction("src/settings.cpp", 987, "int native_get_displayname(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_displayname(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        NativeGetConfigString(parameter, name, value, config => config.DisplayName);

    [NssmUpstreamFunction("src/settings.cpp", 1000, "int native_set_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        using var key = NssmRegistry.open_service_registry(serviceName, NssmRegistry.KeySetValue, true);
        return key is null ? -1 : setting_set_environment(serviceName, key, name, defaultValue, value, additional);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1009, "int native_get_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        using var key = NssmRegistry.open_service_registry(serviceName, NssmRegistry.KeyRead, true);
        if (key is null || value is null) return -1;
        value.String = null;
        value.Numeric = 0;
        return setting_get_environment(serviceName, key, name, defaultValue, value, additional);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1019, "static int native_dump_environment(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_dump_environment(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        using var key = NssmRegistry.open_service_registry(serviceName, NssmRegistry.KeyRead, true);
        return key is null ? -1 : setting_dump_environment(serviceName, key, name, defaultValue, value, additional);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1028, "int native_set_imagepath(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_imagepath(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value?.String is null) return -1;
        return ChangeServiceConfig(handle, binaryPath: value.String) ? 1 : -1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1046, "int native_get_imagepath(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_imagepath(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        NativeGetConfigString(parameter, name, value, config => config.BinaryPathName);

    [NssmUpstreamFunction("src/settings.cpp", 1059, "int native_set_name(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_name(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) => -1;

    [NssmUpstreamFunction("src/settings.cpp", 1064, "int native_get_name(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_name(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        value is null ? -1 : value_from_string(name, value, serviceName);

    [NssmUpstreamFunction("src/settings.cpp", 1068, "int native_set_objectname(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_objectname(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero) return -1;
        var username = additional ?? value?.String ?? "LocalSystem";
        var password = additional is null ? null : value?.String;
        var wellKnown = NssmAccount.well_known_username(username);
        var localSystem = wellKnown is not null && NssmCore.str_equiv(wellKnown, "LocalSystem") != 0;
        var virtualAccount = NssmAccount.is_virtual_account(serviceName, username) != 0;
        if (wellKnown is not null) { username = wellKnown; password = string.Empty; }
        else if (!virtualAccount && password is null) return -1;
        var serviceType = ServiceNoChange;
        if (!localSystem)
        {
            if (!TryQueryServiceConfig(handle, out var config)) return -1;
            serviceType = config.ServiceType & ~NativeMethods.ServiceInteractiveProcess;
        }
        if (wellKnown is null && !virtualAccount && NssmAccount.grant_logon_as_service(username) != 0) return -1;
        if (!NativeMethods.ChangeServiceConfig(handle, serviceType, ServiceNoChange, ServiceNoChange, null, null, IntPtr.Zero, IntPtr.Zero, username, password, null)) return -1;
        return localSystem ? 0 : 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1138, "int native_get_objectname(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_objectname(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional) =>
        NativeGetConfigString(parameter, name, value, config => config.ServiceStartName);

    [NssmUpstreamFunction("src/settings.cpp", 1151, "int native_dump_objectname(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_dump_objectname(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (value is null) return -1;
        var ret = native_get_objectname(serviceName, parameter, name, defaultValue, value, additional);
        if (ret != 1) return ret;
        if (value.String?.StartsWith("NT Service", StringComparison.OrdinalIgnoreCase) == true)
            value.String = NssmAccount.virtual_account(serviceName);
        else if (value.String is not null && NssmAccount.well_known_username(value.String) is null)
        {
            var password = NssmSettingValue.FromString("****");
            return setting_dump_string(serviceName, RegSz, name, password, value.String);
        }
        return setting_dump_string(serviceName, RegSz, name, value, null);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1174, "int native_set_startup(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_startup(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value?.String is null) return -1;
        var index = Array.FindIndex(StartupStrings, item => NssmCore.str_equiv(item, value.String) != 0);
        if (index < 0) return -1;
        var startup = index switch { 2 => ServiceDemandStart, 3 => ServiceDisabled, _ => ServiceAutoStart };
        if (!NativeMethods.ChangeServiceConfig(handle, ServiceNoChange, startup, ServiceNoChange, null, null, IntPtr.Zero, IntPtr.Zero, null, null, null)) return -1;
        var delayed = new NativeMethods.ServiceDelayedAutoStartInfo { Delayed = index == 1 };
        var structure = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDelayedAutoStartInfo>());
        try
        {
            Marshal.StructureToPtr(delayed, structure, false);
            if (!NativeMethods.ChangeServiceConfig2(handle, NativeMethods.ServiceConfigDelayedAutoStartInfo, structure) && Marshal.GetLastWin32Error() != ErrorInvalidLevel) return -1;
        }
        finally { Marshal.FreeHGlobal(structure); }
        return 1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1227, "int native_get_startup(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_startup(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value is null || !TryQueryServiceConfig(handle, out var config)) return -1;
        var index = config.StartType switch { ServiceAutoStart when IsDelayed(handle) => 1, ServiceAutoStart => 0, ServiceDemandStart => 2, ServiceDisabled => 3, _ => -1 };
        return index < 0 ? -1 : value_from_string(name, value, StartupStrings[index]);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1247, "int native_set_type(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_set_type(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value?.String is null) return -1;
        uint type = NativeMethods.ServiceWin32OwnProcess;
        if (NssmCore.str_equiv(value.String, "SERVICE_INTERACTIVE_PROCESS") != 0) type |= NativeMethods.ServiceInteractiveProcess;
        else if (NssmCore.str_equiv(value.String, "SERVICE_WIN32_OWN_PROCESS") == 0) return -1;
        if ((type & NativeMethods.ServiceInteractiveProcess) != 0)
        {
            if (!TryQueryServiceConfig(handle, out var config) || NssmCore.str_equiv(config.ServiceStartName, "LocalSystem") == 0) return -1;
        }
        return NativeMethods.ChangeServiceConfig(handle, type, ServiceNoChange, ServiceNoChange, null, null, IntPtr.Zero, IntPtr.Zero, null, null, null) ? 1 : -1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1295, "int native_get_type(const TCHAR *service_name, void *param, const TCHAR *name, void *default_value, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.native_settings_validate_null_handles")]
    public static int native_get_type(string serviceName, object? parameter, string name, object? defaultValue, NssmSettingValue? value, string? additional)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value is null || !TryQueryServiceConfig(handle, out var config)) return -1;
        value.Numeric = config.ServiceType;
        var text = config.ServiceType switch
        {
            ServiceKernelDriver => "SERVICE_KERNEL_DRIVER",
            ServiceFileSystemDriver => "SERVICE_FILE_SYSTEM_DRIVER",
            NativeMethods.ServiceWin32OwnProcess => "SERVICE_WIN32_OWN_PROCESS",
            ServiceWin32ShareProcess => "SERVICE_WIN32_SHARE_PROCESS",
            NativeMethods.ServiceWin32OwnProcess | NativeMethods.ServiceInteractiveProcess => "SERVICE_INTERACTIVE_PROCESS",
            ServiceWin32ShareProcess | NativeMethods.ServiceInteractiveProcess => "SERVICE_WIN32_SHARE_PROCESS|SERVICE_INTERACTIVE_PROCESS",
            _ => "?"
        };
        return value_from_string(name, value, text);
    }

    [NssmUpstreamFunction("src/settings.cpp", 1319, "int set_setting(const TCHAR *service_name, HKEY key, settings_t *setting, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dispatch_table_matches_upstream_order_and_defaults")]
    public static int set_setting(string serviceName, RegistryKey key, NssmTranslatedSetting setting, NssmSettingValue? value, string? additional)
    {
        if (key is null) return -1;
        var ret = setting.Set?.Invoke(serviceName, key, setting.Name, setting.DefaultValue, value, additional) ?? -1;
        PrintSetResult(ret, setting.Name, serviceName);
        return ret;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1333, "int set_setting(const TCHAR *service_name, SC_HANDLE service_handle, settings_t *setting, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dispatch_table_matches_upstream_order_and_defaults")]
    public static int set_setting(string serviceName, IntPtr serviceHandle, NssmTranslatedSetting setting, NssmSettingValue? value, string? additional)
    {
        if (serviceHandle == IntPtr.Zero) return -1;
        var ret = setting.Set?.Invoke(serviceName, serviceHandle, setting.Name, setting.DefaultValue, value, additional) ?? -1;
        PrintSetResult(ret, setting.Name, serviceName);
        return ret;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1352, "int get_setting(const TCHAR *service_name, HKEY key, settings_t *setting, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dispatch_table_matches_upstream_order_and_defaults")]
    public static int get_setting(string serviceName, RegistryKey key, NssmTranslatedSetting setting, NssmSettingValue value, string? additional)
    {
        if (is_string_type(setting.Type)) value.String = setting.DefaultValue as string;
        else if (is_numeric_type(setting.Type)) value.Numeric = setting.DefaultValue is null ? 0 : Convert.ToUInt32(setting.DefaultValue);
        else return -1;
        var ret = setting.Get?.Invoke(serviceName, key, setting.Name, setting.DefaultValue, value, additional) ?? -1;
        if (ret < 0) NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_GET_SETTING_FAILED"), setting.Name, serviceName);
        return ret;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1373, "int get_setting(const TCHAR *service_name, SC_HANDLE service_handle, settings_t *setting, value_t *value, const TCHAR *additional)", "NssmSettingsTranslationTests.dispatch_table_matches_upstream_order_and_defaults")]
    public static int get_setting(string serviceName, IntPtr serviceHandle, NssmTranslatedSetting setting, NssmSettingValue value, string? additional)
    {
        if (serviceHandle == IntPtr.Zero) return -1;
        return setting.Get?.Invoke(serviceName, serviceHandle, setting.Name, null, value, additional) ?? -1;
    }

    [NssmUpstreamFunction("src/settings.cpp", 1378, "int dump_setting(const TCHAR *service_name, HKEY key, SC_HANDLE service_handle, settings_t *setting)", "NssmSettingsTranslationTests.dispatch_table_matches_upstream_order_and_defaults")]
    public static int dump_setting(string serviceName, RegistryKey? key, IntPtr serviceHandle, NssmTranslatedSetting setting)
    {
        object? parameter = setting.Native ? serviceHandle : key;
        if (setting.Native && serviceHandle == IntPtr.Zero) return -1;
        var value = new NssmSettingValue();
        if (setting.Dump is not null) return setting.Dump(serviceName, parameter, setting.Name, setting, value, null);
        var ret = setting.Native
            ? get_setting(serviceName, serviceHandle, setting, value, null)
            : key is null ? -1 : get_setting(serviceName, key, setting, value, null);
        return ret == 1 ? setting_dump_string(serviceName, setting.Type, setting.Name, value, null) : ret;
    }

    public static IReadOnlyList<NssmTranslatedSetting> Settings { get; } =
    [
        Registry("Application", RegExpandSz, "", setting_set_string, setting_get_string, setting_not_dumpable),
        Registry("AppParameters", RegExpandSz, "", setting_set_string, setting_get_string),
        Registry("AppDirectory", RegExpandSz, "", setting_set_string, setting_get_string),
        Registry("AppExit", RegSz, "Restart", setting_set_exit_action, setting_get_exit_action, setting_dump_exit_action, AdditionalMandatory),
        Registry("AppEvents", RegSz, "", setting_set_hook, setting_get_hook, setting_dump_hooks, AdditionalMandatory),
        Registry("AppAffinity", RegSz, null, setting_set_affinity, setting_get_affinity),
        Registry("AppEnvironment", RegMultiSz, null, setting_set_environment, setting_get_environment, setting_dump_environment, AdditionalCrlf),
        Registry("AppEnvironmentExtra", RegMultiSz, null, setting_set_environment, setting_get_environment, setting_dump_environment, AdditionalCrlf),
        Registry("AppNoConsole", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppPriority", RegSz, "NORMAL_PRIORITY_CLASS", setting_set_priority, setting_get_priority, setting_dump_priority),
        Registry("AppRestartDelay", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppStdin", RegExpandSz, null, setting_set_string, setting_get_string),
        Registry("AppStdinShareMode", RegDword, 2u, setting_set_number, setting_get_number),
        Registry("AppStdinCreationDisposition", RegDword, 3u, setting_set_number, setting_get_number),
        Registry("AppStdinFlagsAndAttributes", RegDword, 128u, setting_set_number, setting_get_number),
        Registry("AppStdout", RegExpandSz, null, setting_set_string, setting_get_string),
        Registry("AppStdoutShareMode", RegDword, 3u, setting_set_number, setting_get_number),
        Registry("AppStdoutCreationDisposition", RegDword, 4u, setting_set_number, setting_get_number),
        Registry("AppStdoutFlagsAndAttributes", RegDword, 128u, setting_set_number, setting_get_number),
        Registry("AppStdoutCopyAndTruncate", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppStderr", RegExpandSz, null, setting_set_string, setting_get_string),
        Registry("AppStderrShareMode", RegDword, 3u, setting_set_number, setting_get_number),
        Registry("AppStderrCreationDisposition", RegDword, 4u, setting_set_number, setting_get_number),
        Registry("AppStderrFlagsAndAttributes", RegDword, 128u, setting_set_number, setting_get_number),
        Registry("AppStderrCopyAndTruncate", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppStopMethodSkip", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppStopMethodConsole", RegDword, 1500u, setting_set_number, setting_get_number),
        Registry("AppStopMethodWindow", RegDword, 1500u, setting_set_number, setting_get_number),
        Registry("AppStopMethodThreads", RegDword, 1500u, setting_set_number, setting_get_number),
        Registry("AppKillProcessTree", RegDword, 1u, setting_set_number, setting_get_number),
        Registry("AppThrottle", RegDword, 1500u, setting_set_number, setting_get_number),
        Registry("AppRedirectHook", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateFiles", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateOnline", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateSeconds", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateBytes", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateBytesHigh", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppRotateDelay", RegDword, 0u, setting_set_number, setting_get_number),
        Registry("AppTimestampLog", RegDword, 0u, setting_set_number, setting_get_number),
        Native("DependOnGroup", RegMultiSz, null, native_set_dependongroup, native_get_dependongroup, native_dump_dependongroup, AdditionalCrlf),
        Native("DependOnService", RegMultiSz, null, native_set_dependonservice, native_get_dependonservice, native_dump_dependonservice, AdditionalCrlf),
        Native("Description", RegSz, "", native_set_description, native_get_description),
        Native("DisplayName", RegSz, null, native_set_displayname, native_get_displayname),
        Native("Environment", RegMultiSz, null, native_set_environment, native_get_environment, native_dump_environment, AdditionalCrlf),
        Native("ImagePath", RegExpandSz, null, native_set_imagepath, native_get_imagepath, setting_not_dumpable),
        Native("ObjectName", RegSz, "LocalSystem", native_set_objectname, native_get_objectname, native_dump_objectname),
        Native("Name", RegSz, null, native_set_name, native_get_name, setting_not_dumpable),
        Native("Start", RegSz, null, native_set_startup, native_get_startup),
        Native("Type", RegSz, null, native_set_type, native_get_type)
    ];

    public static NssmTranslatedSetting? Find(string name) => Settings.FirstOrDefault(item => NssmCore.str_equiv(item.Name, name) != 0);

    public static int Get(string serviceName, string settingName, string? additional, NssmSettingValue value)
    {
        var setting = Find(settingName);
        if (setting is null) return -1;
        if (setting.Native)
        {
            using var handles = OpenService(serviceName, NativeMethods.ServiceQueryConfig);
            return handles is null ? -1 : get_setting(serviceName, handles.Service, setting, value, additional);
        }
        using var key = NssmRegistry.open_registry(serviceName, NssmRegistry.KeyRead);
        return key is null ? -1 : get_setting(serviceName, key, setting, value, additional);
    }

    public static int Set(string serviceName, string settingName, string? additional, NssmSettingValue? value)
    {
        var setting = Find(settingName);
        if (setting is null) return -1;
        if (setting.Native)
        {
            using var handles = OpenService(serviceName, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
            return handles is null
                ? -1
                : setting.Set?.Invoke(serviceName, handles.Service, setting.Name, setting.DefaultValue, value, additional) ?? -1;
        }
        using var key = NssmRegistry.open_registry(serviceName, NssmRegistry.KeyWrite);
        return key is null
            ? -1
            : setting.Set?.Invoke(serviceName, key, setting.Name, setting.DefaultValue, value, additional) ?? -1;
    }

    public static int SetObjectNameSecure(string serviceName, string? username, char[] password)
    {
        using var handles = OpenService(serviceName, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
        if (handles is null) return -1;

        username ??= NssmAccount.LocalSystemAccount;
        var wellKnown = NssmAccount.well_known_username(username);
        var localSystem = wellKnown is not null && NssmCore.str_equiv(wellKnown, NssmAccount.LocalSystemAccount) != 0;
        var virtualAccount = NssmAccount.is_virtual_account(serviceName, username) != 0;
        if (wellKnown is not null) username = wellKnown;

        var serviceType = ServiceNoChange;
        if (!localSystem)
        {
            if (!TryQueryServiceConfig(handles.Service, out var config)) return -1;
            serviceType = config.ServiceType & ~NativeMethods.ServiceInteractiveProcess;
        }

        if (wellKnown is null && !virtualAccount && NssmAccount.grant_logon_as_service(username) != 0) return -1;

        char[]? emptyPassword = wellKnown is null ? null : ['\0'];
        var effectivePassword = emptyPassword ?? password;
        if (effectivePassword.Length == 0 || effectivePassword[^1] != '\0') return -1;
        var pinned = GCHandle.Alloc(effectivePassword, GCHandleType.Pinned);
        try
        {
            if (!NativeMethods.ChangeServiceConfigWithPassword(handles.Service, serviceType, ServiceNoChange, ServiceNoChange,
                    null, null, IntPtr.Zero, IntPtr.Zero, username, pinned.AddrOfPinnedObject(), null)) return -1;
        }
        finally
        {
            pinned.Free();
            if (emptyPassword is not null) Array.Clear(emptyPassword);
        }

        return localSystem ? 0 : 1;
    }

    public static int Dump(string serviceName) => Dump(serviceName, serviceName);

    public static int Dump(string serviceName, string outputServiceName)
    {
        using var handles = OpenService(serviceName, NativeMethods.ServiceQueryConfig);
        if (handles is null) return -1;
        using var key = NssmRegistry.open_registry(serviceName, null, NssmRegistry.KeyRead, false);
        var nativeService = key is null;
        var errors = 0;
        foreach (var setting in Settings)
        {
            if (nativeService && !setting.Native) continue;
            if (dump_setting(outputServiceName, key, handles.Service, setting) != 0) errors++;
        }
        return errors > 0 ? -1 : 0;
    }

    private static NssmTranslatedSetting Registry(string name, uint type, object? defaultValue, NssmSettingOperation set, NssmSettingOperation get, NssmSettingOperation? dump = null, int additional = 0) =>
        new(name, type, defaultValue, false, additional, set, get, dump);
    private static NssmTranslatedSetting Native(string name, uint type, object? defaultValue, NssmSettingOperation set, NssmSettingOperation get, NssmSettingOperation? dump = null, int additional = 0) =>
        new(name, type, defaultValue, true, additional, set, get, dump);

    private static bool IsRegistryException(Exception exception) => exception is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException;
    private static bool HasValue(RegistryKey key, string name) => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);
    private static void DeleteValue(RegistryKey key, string name) => key.DeleteValue(name, false);
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private static void PrintSetResult(int result, string settingName, string serviceName)
    {
        var id = result switch
        {
            0 => "NSSM_MESSAGE_RESET_SETTING",
            > 0 => "NSSM_MESSAGE_SET_SETTING",
            _ => "NSSM_MESSAGE_SET_SETTING_FAILED"
        };
        NssmEvent.print_message(result < 0 ? Console.Error : Console.Out, NssmEvent.message_id(id), settingName, serviceName);
    }

    private static bool ValidHookName(string hookEvent, string hookAction)
    {
        if (NssmCore.str_equiv(hookEvent, "Exit") != 0) return NssmCore.str_equiv(hookAction, "Post") != 0;
        if (NssmCore.str_equiv(hookEvent, "Power") != 0) return NssmCore.str_equiv(hookAction, "Change") != 0 || NssmCore.str_equiv(hookAction, "Resume") != 0;
        if (NssmCore.str_equiv(hookEvent, "Rotate") != 0 || NssmCore.str_equiv(hookEvent, "Start") != 0) return NssmCore.str_equiv(hookAction, "Pre") != 0 || NssmCore.str_equiv(hookAction, "Post") != 0;
        return NssmCore.str_equiv(hookEvent, "Stop") != 0 && NssmCore.str_equiv(hookAction, "Pre") != 0;
    }

    private static int AffinityStringToMask(string? text, out ulong mask)
    {
        mask = 0;
        if (text is null) return 0;
        if (text.Length == 0) return 0;
        foreach (var item in text.Split(','))
        {
            if (item.Length == 0) return 4;
            var dash = item.IndexOf('-');
            if (dash < 0)
            {
                if (!uint.TryParse(item, out var cpu)) return 4;
                if (cpu >= 64) return 2;
                mask |= 1UL << checked((int)cpu);
                continue;
            }
            if (dash == item.Length - 1) return 3;
            if (!uint.TryParse(item[..dash], out var first) || !uint.TryParse(item[(dash + 1)..], out var last) || first >= 64) return 3;
            for (var cpu = first; cpu <= last && cpu < 64; cpu++) mask |= 1UL << checked((int)cpu);
        }
        return 0;
    }

    private static int AffinityMaskToString(ulong mask, out string? text)
    {
        text = null;
        if (mask == 0) return 0;
        var ranges = new List<string>();
        for (var cpu = 0; cpu < 64; cpu++)
        {
            if ((mask & (1UL << cpu)) == 0) continue;
            var first = cpu;
            while (cpu + 1 < 64 && (mask & (1UL << (cpu + 1))) != 0) cpu++;
            text = cpu == first ? first.ToString() : cpu == first + 1 ? $"{first},{cpu}" : $"{first}-{cpu}";
            ranges.Add(text);
        }
        text = string.Join(',', ranges);
        return 0;
    }

    private static int NativeGetDependOn(string serviceName, object? parameter, string name, NssmSettingValue? value, int type)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value is null) return -1;
        var dependencies = GetServiceDependencies(serviceName, handle, type);
        if (dependencies.Length == 0) { value.String = null; return 0; }
        var block = NssmDoubleNull.FromStrings(dependencies);
        if (NssmDoubleNull.format_double_null(block, checked((uint)block.Length), out var formatted, out _) != 0 || formatted is null) return -1;
        return value_from_string(name, value, formatted.TrimEnd('\0'));
    }

    private static string[] GetServiceDependencies(string serviceName, IntPtr serviceHandle, int type)
    {
        if (!TryQueryServiceConfig(serviceHandle, out var config)) return [];
        return config.Dependencies.Where(item => type == DependencyGroups ? item.StartsWith('+') : !item.StartsWith('+')).ToArray();
    }

    private static bool SetServiceDependencies(IntPtr serviceHandle, IEnumerable<string> dependencies)
    {
        var values = dependencies.ToArray();
        var block = values.Length == 0 ? "\0\0" : string.Join('\0', values) + "\0\0";
        var pointer = Marshal.StringToHGlobalUni(block);
        try { return NativeMethods.ChangeServiceConfig(serviceHandle, ServiceNoChange, ServiceNoChange, ServiceNoChange, null, null, IntPtr.Zero, pointer, null, null, null); }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static int NativeGetConfigString(object? parameter, string name, NssmSettingValue? value, Func<NativeConfig, string> selector)
    {
        if (parameter is not IntPtr handle || handle == IntPtr.Zero || value is null || !TryQueryServiceConfig(handle, out var config)) return -1;
        return value_from_string(name, value, selector(config));
    }

    private static bool ChangeServiceConfig(IntPtr handle, string? binaryPath = null, string? displayName = null) =>
        NativeMethods.ChangeServiceConfig(handle, ServiceNoChange, ServiceNoChange, ServiceNoChange, binaryPath, null, IntPtr.Zero, IntPtr.Zero, null, null, displayName);

    private static bool TryQueryServiceConfig(IntPtr handle, out NativeConfig config)
    {
        config = default!;
        NativeMethods.QueryServiceConfig(handle, IntPtr.Zero, 0, out var required);
        if (required == 0 || Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer) return false;
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig(handle, buffer, required, out _)) return false;
            var native = Marshal.PtrToStructure<NativeMethods.QueryServiceConfigData>(buffer);
            config = new NativeConfig(
                native.ServiceType,
                native.StartType,
                Marshal.PtrToStringUni(native.BinaryPathName) ?? string.Empty,
                Marshal.PtrToStringUni(native.ServiceStartName) ?? "LocalSystem",
                Marshal.PtrToStringUni(native.DisplayName) ?? string.Empty,
                ReadMultiString(native.Dependencies));
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string[] ReadMultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var result = new List<string>();
        for (var offset = 0; ;)
        {
            var item = Marshal.PtrToStringUni(pointer + offset) ?? string.Empty;
            if (item.Length == 0) return result.ToArray();
            result.Add(item);
            offset += checked((item.Length + 1) * sizeof(char));
        }
    }

    private static bool IsDelayed(IntPtr handle)
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.ServiceDelayedAutoStartInfo>();
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            return NativeMethods.QueryServiceConfig2(handle, NativeMethods.ServiceConfigDelayedAutoStartInfo, buffer, size, out _)
                && Marshal.PtrToStructure<NativeMethods.ServiceDelayedAutoStartInfo>(buffer).Delayed;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static NativeServiceHandles? OpenService(string serviceName, uint access)
    {
        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero) return null;
        var service = NativeMethods.OpenService(manager, serviceName, access);
        if (service == IntPtr.Zero) { NativeMethods.CloseServiceHandle(manager); return null; }
        return new NativeServiceHandles(manager, service);
    }

    private sealed class NativeServiceHandles(IntPtr manager, IntPtr service) : IDisposable
    {
        public IntPtr Manager { get; } = manager;
        public IntPtr Service { get; } = service;
        public void Dispose() { NativeMethods.CloseServiceHandle(Service); NativeMethods.CloseServiceHandle(Manager); }
    }

    private sealed record NativeConfig(uint ServiceType, uint StartType, string BinaryPathName, string ServiceStartName, string DisplayName, string[] Dependencies);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessAffinityMask(IntPtr process, out ulong processAffinityMask, out ulong systemAffinityMask);
}
