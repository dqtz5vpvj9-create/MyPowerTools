using Microsoft.Win32;
using NssmManager.Contracts;

namespace NssmManager.Compatibility;

public sealed class NssmRegistryStore
{
    public const string ServicesRoot = @"SYSTEM\CurrentControlSet\Services";

    public bool Exists(string serviceName)
    {
        ValidateServiceName(serviceName);
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}");
        return key is not null;
    }

    public bool IsCompatible(string serviceName)
    {
        ValidateServiceName(serviceName);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}\Parameters");
            return key?.GetValue("Application") is string value && value.Length > 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public NssmServiceConfiguration Read(string serviceName)
    {
        ValidateServiceName(serviceName);
        var parameterStatus = NssmRegistry.get_parameters(serviceName, expand: false, out var translated);
        if (parameterStatus == 1) throw new InvalidOperationException($"Service '{serviceName}' has no NSSM Parameters key.");
        if (parameterStatus != 0) throw new InvalidOperationException($"Service '{serviceName}' has invalid NSSM Parameters (status {parameterStatus}).");
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}")
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        using var parameters = service.OpenSubKey(NssmSettings.ParametersKey)
            ?? throw new InvalidOperationException($"Service '{serviceName}' has no NSSM Parameters key.");
        using var exits = parameters.OpenSubKey(NssmSettings.ExitKey);
        using var hooks = parameters.OpenSubKey(NssmSettings.HooksKey);
        return translated with
        {
            DisplayName = ReadString(service, "DisplayName", serviceName),
            Description = ReadString(service, "Description"),
            ServiceAccount = NormalizeAccount(ReadString(service, "ObjectName", "LocalSystem")),
            StartupType = ReadStartup(service),
            Interactive = (ReadDword(service, "Type", 16) & 0x100) != 0,
            DependOnService = ReadMulti(service, "DependOnService").Where(value => !value.StartsWith('+')).ToArray(),
            DependOnGroup = ReadMulti(service, "DependOnService").Where(value => value.StartsWith('+')).Select(value => value.TrimStart('+')).ToArray(),
            ServiceEnvironment = ReadMulti(service, "Environment"),
            ExitRules = ReadExitRules(exits),
            Hooks = ReadHooks(hooks)
        };
    }

    public void WriteParameters(NssmServiceConfiguration value)
    {
        Validate(value);
        using var service = Registry.LocalMachine.CreateSubKey($@"{ServicesRoot}\{value.Name}", writable: true);
        WriteMulti(service, "Environment", value.ServiceEnvironment);
        var status = NssmRegistry.create_parameters(value, editing: true);
        if (status != 0) throw new InvalidOperationException($"create_parameters() failed with NSSM status {status}.");
        using var parameters = service.CreateSubKey(NssmSettings.ParametersKey, writable: true);
        WriteExitRules(parameters, value);
        WriteHooks(parameters, value.Hooks);
    }

    public object? Get(string serviceName, string parameter, string? subparameter = null)
    {
        var descriptor = NssmSettings.Find(parameter);
        if (descriptor.Native) return GetNative(serviceName, parameter);
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}\Parameters")
            ?? throw new InvalidOperationException($"Service '{serviceName}' has no Parameters key.");
        if (descriptor.Kind == NssmSettingKind.ExitAction)
        {
            using var exits = key.OpenSubKey(NssmSettings.ExitKey);
            return exits?.GetValue(NormalizeExitCode(subparameter)) as string ?? "Restart";
        }
        if (descriptor.Kind == NssmSettingKind.Hook)
        {
            var segments = RequireHookSubparameter(subparameter);
            ValidateHook(segments[0], segments[1]);
            using var hook = key.OpenSubKey($@"{NssmSettings.HooksKey}\{segments[0]}");
            return hook?.GetValue(segments[1], "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }
        return key.GetValue(parameter, descriptor.DefaultValue, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Set(string serviceName, string parameter, string? subparameter, IReadOnlyList<string> values)
    {
        var descriptor = NssmSettings.Find(parameter);
        if (descriptor.Native) throw new InvalidOperationException($"Native parameter '{parameter}' must be changed through SCM.");
        using var key = Registry.LocalMachine.CreateSubKey($@"{ServicesRoot}\{serviceName}\Parameters", writable: true);
        if (descriptor.Kind == NssmSettingKind.ExitAction)
        {
            var action = ParseAction(RequireValue(values));
            using var exits = key.CreateSubKey(NssmSettings.ExitKey, writable: true);
            exits.SetValue(NormalizeExitCode(subparameter), action.ToString(), RegistryValueKind.String);
            return;
        }
        if (descriptor.Kind == NssmSettingKind.Hook)
        {
            var segments = RequireHookSubparameter(subparameter);
            ValidateHook(segments[0], segments[1]);
            using var hook = key.CreateSubKey($@"{NssmSettings.HooksKey}\{segments[0]}", writable: true);
            hook.SetValue(segments[1], string.Join(' ', values), RegistryValueKind.ExpandString);
            return;
        }
        if (descriptor.Kind == NssmSettingKind.MultiString)
        {
            key.SetValue(parameter, values.ToArray(), RegistryValueKind.MultiString);
            return;
        }
        if (descriptor.Kind == NssmSettingKind.Dword)
        {
            if (!uint.TryParse(RequireValue(values), out var number)) throw new ArgumentException($"{parameter} requires an unsigned integer.");
            if (descriptor.DefaultValue is uint defaultNumber && number == defaultNumber) key.DeleteValue(parameter, false);
            else key.SetValue(parameter, unchecked((int)number), RegistryValueKind.DWord);
            return;
        }
        var text = string.Join(' ', values);
        if (descriptor.Kind == NssmSettingKind.Priority)
        {
            var priorities = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["REALTIME_PRIORITY_CLASS"] = 0x100,
                ["HIGH_PRIORITY_CLASS"] = 0x80,
                ["ABOVE_NORMAL_PRIORITY_CLASS"] = 0x8000,
                ["NORMAL_PRIORITY_CLASS"] = 0x20,
                ["BELOW_NORMAL_PRIORITY_CLASS"] = 0x4000,
                ["IDLE_PRIORITY_CLASS"] = 0x40
            };
            if (!priorities.TryGetValue(text, out var priority)) throw new ArgumentException($"Invalid priority '{text}'.");
            if (priority == 0x20) key.DeleteValue(parameter, false);
            else key.SetValue(parameter, unchecked((int)priority), RegistryValueKind.DWord);
            return;
        }
        if (descriptor.Kind == NssmSettingKind.Affinity)
        {
            if (text.Equals("All", StringComparison.OrdinalIgnoreCase) || text.Equals("Default", StringComparison.OrdinalIgnoreCase)) { key.DeleteValue(parameter, false); return; }
            ValidateAffinity(text);
        }
        key.SetValue(parameter, text, descriptor.Kind == NssmSettingKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public void Reset(string serviceName, string parameter, string? subparameter = null)
    {
        var descriptor = NssmSettings.Find(parameter);
        if (descriptor.Native) throw new InvalidOperationException($"Native parameter '{parameter}' must be reset through SCM.");
        using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}\Parameters", writable: true)
            ?? throw new InvalidOperationException($"Service '{serviceName}' has no Parameters key.");
        if (descriptor.Kind == NssmSettingKind.ExitAction)
        {
            using var exits = key.OpenSubKey(NssmSettings.ExitKey, writable: true);
            exits?.DeleteValue(NormalizeExitCode(subparameter), false);
            return;
        }
        if (descriptor.Kind == NssmSettingKind.Hook)
        {
            var segments = RequireHookSubparameter(subparameter);
            ValidateHook(segments[0], segments[1]);
            using var hook = key.OpenSubKey($@"{NssmSettings.HooksKey}\{segments[0]}", writable: true);
            hook?.DeleteValue(segments[1], false);
            return;
        }
        key.DeleteValue(parameter, false);
    }

    public string ReadImagePath(string serviceName)
    {
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}")
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        return ReadString(service, "ImagePath");
    }

    public void WriteImagePath(string serviceName, string imagePath)
    {
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}", writable: true)
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        service.SetValue("ImagePath", imagePath, RegistryValueKind.ExpandString);
    }

    public NssmMigrationSnapshot CaptureMigrationSnapshot(string serviceName, NssmServiceState state)
    {
        ValidateServiceName(serviceName);
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}") ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        var keys = new List<NssmRegistryKeySnapshot>();
        var scmValueNames = new HashSet<string>(["ImagePath", "Type", "Start", "ErrorControl", "DisplayName", "ObjectName", "DependOnService", "DependOnGroup", "Description", "DelayedAutoStart", "Environment"], StringComparer.OrdinalIgnoreCase);
        keys.Add(CaptureKey("", service, scmValueNames));
        using var parameters = service.OpenSubKey(NssmSettings.ParametersKey);
        if (parameters is not null) CaptureTree(NssmSettings.ParametersKey, parameters, keys);
        return new NssmMigrationSnapshot(1, serviceName, ReadImagePath(serviceName), Read(serviceName), DateTimeOffset.UtcNow, state, keys.ToArray());
    }

    public void RestoreMigrationSnapshot(NssmMigrationSnapshot snapshot)
    {
        ValidateServiceName(snapshot.ServiceName);
        var registryKeys = snapshot.RegistryKeys ?? [];
        if (registryKeys.Length == 0) { WriteImagePath(snapshot.ServiceName, snapshot.OriginalImagePath); WriteParameters(snapshot.Configuration); return; }
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{snapshot.ServiceName}", writable: true) ?? throw new InvalidOperationException($"Service '{snapshot.ServiceName}' does not exist.");
        service.DeleteSubKeyTree(NssmSettings.ParametersKey, false);
        foreach (var keySnapshot in registryKeys.OrderBy(item => item.RelativePath.Count(character => character == '\\')))
        {
            using var key = string.IsNullOrEmpty(keySnapshot.RelativePath) ? service : service.CreateSubKey(keySnapshot.RelativePath, writable: true);
            if (string.IsNullOrEmpty(keySnapshot.RelativePath))
            {
                var capturedNames = keySnapshot.Values.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var name in new[] { "ImagePath", "Type", "Start", "ErrorControl", "DisplayName", "ObjectName", "DependOnService", "DependOnGroup", "Description", "DelayedAutoStart", "Environment" }) if (!capturedNames.Contains(name)) key.DeleteValue(name, false);
            }
            foreach (var value in keySnapshot.Values) RestoreValue(key, value);
        }
    }

    public static void ValidateServiceName(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 256 || serviceName.IndexOfAny(['\\', '/', '\0']) >= 0)
            throw new ArgumentException("Service name is invalid.", nameof(serviceName));
    }

    private object? GetNative(string serviceName, string parameter)
    {
        using var service = Registry.LocalMachine.OpenSubKey($@"{ServicesRoot}\{serviceName}")
            ?? throw new InvalidOperationException($"Service '{serviceName}' does not exist.");
        return parameter.ToLowerInvariant() switch
        {
            "name" => serviceName,
            "start" => ReadStartup(service) switch
            {
                NssmStartupType.Automatic => "SERVICE_AUTO_START",
                NssmStartupType.DelayedAutomatic => "SERVICE_DELAYED_AUTO_START",
                NssmStartupType.Disabled => "SERVICE_DISABLED",
                _ => "SERVICE_DEMAND_START"
            },
            "type" => (ReadDword(service, "Type", 16) & 0x100) != 0 ? "SERVICE_INTERACTIVE_PROCESS" : "SERVICE_WIN32_OWN_PROCESS",
            "dependonservice" => ReadMulti(service, "DependOnService").Where(value => !value.StartsWith('+')).ToArray(),
            "dependongroup" => ReadMulti(service, "DependOnService").Where(value => value.StartsWith('+')).Select(value => value.TrimStart('+')).ToArray(),
            "environment" => ReadMulti(service, parameter),
            _ => service.GetValue(parameter, "", RegistryValueOptions.DoNotExpandEnvironmentNames)
        };
    }

    private static void Validate(NssmServiceConfiguration value)
    {
        ValidateServiceName(value.Name);
        if (string.IsNullOrWhiteSpace(value.Application)) throw new ArgumentException("Application is required.");
        if (value.Environment.Concat(value.EnvironmentExtra).Any(item => item.IndexOf('=') <= 0)) throw new ArgumentException("Environment values must use KEY=VALUE form.");
        if (!value.Affinity.Equals("All", StringComparison.OrdinalIgnoreCase) && !value.Affinity.Equals("Default", StringComparison.OrdinalIgnoreCase)) ValidateAffinity(value.Affinity);
        var priorities = new[] { "REALTIME_PRIORITY_CLASS", "HIGH_PRIORITY_CLASS", "ABOVE_NORMAL_PRIORITY_CLASS", "NORMAL_PRIORITY_CLASS", "BELOW_NORMAL_PRIORITY_CLASS", "IDLE_PRIORITY_CLASS" };
        if (!priorities.Contains(value.Priority, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException($"Invalid priority '{value.Priority}'.");
    }

    private static string ReadString(RegistryKey key, string name, string fallback = "") => key.GetValue(name, fallback, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? fallback;
    private static uint ReadDword(RegistryKey key, string name, uint fallback = 0) => key.GetValue(name) is int value ? unchecked((uint)value) : fallback;
    private static string[] ReadMulti(RegistryKey key, string name) => key.GetValue(name) as string[] ?? [];
    private static string NormalizeAccount(string account) => account.Equals(@"LocalSystem", StringComparison.OrdinalIgnoreCase) ? "LocalSystem" : account;
    private static NssmStartupType ReadStartup(RegistryKey key) => ReadDword(key, "Start", 3) switch { 2 when ReadDword(key, "DelayedAutoStart") != 0 => NssmStartupType.DelayedAutomatic, 2 => NssmStartupType.Automatic, 4 => NssmStartupType.Disabled, _ => NssmStartupType.Manual };
    private static NssmExitAction ParseAction(string? value, NssmExitAction? fallback = null) =>
        string.IsNullOrEmpty(value) && fallback.HasValue
            ? fallback.Value
            : Enum.TryParse<NssmExitAction>(value, true, out var action)
                ? action
                : throw new ArgumentException($"Invalid exit action '{value}'.");
    private static string NormalizeExitCode(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, "Default", StringComparison.OrdinalIgnoreCase) ? "" : uint.TryParse(value, out var code) ? code.ToString() : throw new ArgumentException($"Invalid exit code '{value}'.");
    private static string RequireValue(IReadOnlyList<string> values) => values.Count == 0 ? throw new ArgumentException("A value is required.") : string.Join(' ', values);
    private static string[] RequireHookSubparameter(string? value) { var parts = (value ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); return parts.Length == 2 ? parts : throw new ArgumentException("Hook requires Event/Action subparameter."); }
    private static void ValidateHook(string eventName, string actionName)
    {
        var valid = eventName.ToLowerInvariant() switch
        {
            "exit" => actionName.Equals("Post", StringComparison.OrdinalIgnoreCase),
            "power" => actionName.Equals("Change", StringComparison.OrdinalIgnoreCase) || actionName.Equals("Resume", StringComparison.OrdinalIgnoreCase),
            "rotate" or "start" => actionName.Equals("Pre", StringComparison.OrdinalIgnoreCase) || actionName.Equals("Post", StringComparison.OrdinalIgnoreCase),
            "stop" => actionName.Equals("Pre", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid) throw new ArgumentException($"Invalid NSSM hook '{eventName}/{actionName}'.");
    }
    private static void ValidateAffinity(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = item.Split('-', 2);
            if (!int.TryParse(bounds[0], out var first) || first is < 0 or > 63) throw new ArgumentException($"Invalid affinity '{value}'.");
            var last = bounds.Length == 1 ? first : int.TryParse(bounds[1], out var parsed) ? parsed : -1;
            if (last < first || last > 63) throw new ArgumentException($"Invalid affinity '{value}'.");
        }
    }
    private static NssmExitRule[] ReadExitRules(RegistryKey? key) => key is null ? [] : key.GetValueNames().Where(name => uint.TryParse(name, out _)).Select(name => new NssmExitRule(uint.Parse(name), ParseAction(key.GetValue(name) as string))).ToArray();
    private static NssmHook[] ReadHooks(RegistryKey? key) => key is null ? [] : key.GetSubKeyNames().SelectMany(eventName =>
    {
        using var eventKey = key.OpenSubKey(eventName);
        return eventKey?.GetValueNames().Select(actionName => new NssmHook(eventName, actionName, eventKey.GetValue(actionName, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "")) ?? [];
    }).ToArray();
    private static void WriteString(RegistryKey key, string name, string value, RegistryValueKind kind, bool required = false, string? defaultValue = null) { if (!required && (string.IsNullOrEmpty(value) || value == defaultValue)) key.DeleteValue(name, false); else key.SetValue(name, value, kind); }
    private static void WriteMulti(RegistryKey key, string name, string[] values) { if (values.Length == 0) key.DeleteValue(name, false); else key.SetValue(name, values, RegistryValueKind.MultiString); }
    private static void WriteDword(RegistryKey key, string name, uint value, uint defaultValue = 0) { if (value == defaultValue) key.DeleteValue(name, false); else key.SetValue(name, unchecked((int)value), RegistryValueKind.DWord); }
    private static void WriteFlag(RegistryKey key, string name, bool value) => WriteDword(key, name, value ? 1u : 0u);
    private static void WriteExitRules(RegistryKey parameters, NssmServiceConfiguration value) { using var exits = parameters.CreateSubKey(NssmSettings.ExitKey, writable: true); foreach (var name in exits.GetValueNames()) exits.DeleteValue(name, false); exits.SetValue("", value.DefaultExitAction.ToString(), RegistryValueKind.String); foreach (var rule in value.ExitRules.Where(rule => rule.ExitCode.HasValue)) exits.SetValue(rule.ExitCode!.Value.ToString(), rule.Action.ToString(), RegistryValueKind.String); }
    private static void WriteHooks(RegistryKey parameters, NssmHook[] hooks) { parameters.DeleteSubKeyTree(NssmSettings.HooksKey, false); foreach (var hook in hooks) { ValidateHook(hook.Event, hook.Action); using var key = parameters.CreateSubKey($@"{NssmSettings.HooksKey}\{hook.Event}", writable: true); key.SetValue(hook.Action, hook.Command, RegistryValueKind.ExpandString); } }
    private static NssmRegistryKeySnapshot CaptureKey(string relativePath, RegistryKey key, HashSet<string>? filter = null) => new(relativePath, key.GetValueNames().Where(name => filter is null || filter.Contains(name)).Select(name => CaptureValue(key, name)).ToArray());
    private static NssmRegistryValueSnapshot CaptureValue(RegistryKey key, string name)
    {
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => new(name, (int)kind, value as string ?? "", null, null, null),
            RegistryValueKind.MultiString => new(name, (int)kind, null, value as string[] ?? [], null, null),
            RegistryValueKind.DWord => new(name, (int)kind, null, null, value is int number ? unchecked((uint)number) : 0u, null),
            RegistryValueKind.Binary => new(name, (int)kind, null, null, null, value as byte[] ?? []),
            _ => throw new InvalidDataException($"Unsupported registry value kind {kind} at '{key.Name}\\{name}'.")
        };
    }
    private static void CaptureTree(string relativePath, RegistryKey key, List<NssmRegistryKeySnapshot> snapshots)
    {
        snapshots.Add(CaptureKey(relativePath, key));
        foreach (var childName in key.GetSubKeyNames()) { using var child = key.OpenSubKey(childName)!; CaptureTree(relativePath + "\\" + childName, child, snapshots); }
    }
    private static void RestoreValue(RegistryKey key, NssmRegistryValueSnapshot value)
    {
        var kind = (RegistryValueKind)value.Kind;
        object data = kind switch
        {
            RegistryValueKind.String or RegistryValueKind.ExpandString => value.StringValue ?? "",
            RegistryValueKind.MultiString => value.MultiStringValue ?? [],
            RegistryValueKind.DWord => unchecked((int)(value.DwordValue ?? 0)),
            RegistryValueKind.Binary => value.BinaryValue ?? [],
            _ => throw new InvalidDataException($"Unsupported registry value kind {kind} in migration snapshot.")
        };
        key.SetValue(value.Name, data, kind);
    }
}
