using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using NssmManager.Compatibility;
using NssmManager.Contracts;

namespace NssmManager.Windows;

public sealed class NssmServiceData : IDisposable
{
    public bool Native { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public uint Startup { get; set; }
    public string? Username { get; set; }
    public char[]? Password { get; set; }
    public uint Type { get; set; }
    public string Image { get; set; } = string.Empty;
    public string Executable { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string? Environment { get; set; }
    public string? ExtraEnvironment { get; set; }
    public string? InitialEnvironment { get; set; }
    public ulong Affinity { get; set; }
    public string? Dependencies { get; set; }
    public uint DependenciesLength { get; set; }
    public uint Priority { get; set; }
    public uint NoConsole { get; set; }
    public uint StdinSharing { get; set; }
    public uint StdinDisposition { get; set; }
    public uint StdinFlags { get; set; }
    public uint StdoutSharing { get; set; }
    public uint StdoutDisposition { get; set; }
    public uint StdoutFlags { get; set; }
    public uint StderrSharing { get; set; }
    public uint StderrDisposition { get; set; }
    public uint StderrFlags { get; set; }
    public uint ThrottleDelay { get; set; }
    public uint StopMethod { get; set; }
    public uint KillConsoleDelay { get; set; }
    public uint KillWindowDelay { get; set; }
    public uint KillThreadsDelay { get; set; }
    public bool KillProcessTree { get; set; }
    public IntPtr Handle { get; set; }
    public IntPtr ProcessHandle { get; set; }
    public uint ProcessId { get; set; }
    public bool AllowRestart { get; set; }
    public uint Throttle { get; set; }
    public uint RestartDelay { get; set; }
    public bool Disposed { get; private set; }

    internal void MarkDisposed() => Disposed = true;

    public void Dispose() => NssmServiceTranslation.cleanup_nssm_service(this);
}

public sealed record NssmQueryServiceConfig(
    uint ServiceType,
    uint StartType,
    uint ErrorControl,
    string BinaryPathName,
    string LoadOrderGroup,
    uint TagId,
    string[] Dependencies,
    string ServiceStartName,
    string DisplayName);

public struct NssmServiceStatus
{
    public uint ServiceType;
    public uint CurrentState;
    public uint ControlsAccepted;
    public uint Win32ExitCode;
    public uint ServiceSpecificExitCode;
    public uint CheckPoint;
    public uint WaitHint;
}

/// <summary>Function-level translation of the platform-facing portions of service.cpp.</summary>
public static class NssmServiceTranslation
{
    public const uint NssmServiceControlStart = 0;
    public const int DependencyServices = 1;
    public const int DependencyGroups = 2;
    public const int DependencyAll = DependencyServices | DependencyGroups;
    public const uint NssmStartupAutomatic = 0;
    public const uint NssmStartupDelayed = 1;
    public const uint NssmStartupManual = 2;
    public const uint NssmStartupDisabled = 3;
    public const uint ServiceNoChange = 0xffffffff;
    public const uint ServiceStatusDeadline = 20000;
    public const uint WaitHintMargin = 2000;
    public const uint NormalPriorityClass = 0x00000020;
    public const uint IdlePriorityClass = 0x00000040;
    public const uint HighPriorityClass = 0x00000080;
    public const uint RealtimePriorityClass = 0x00000100;
    public const uint BelowNormalPriorityClass = 0x00004000;
    public const uint AboveNormalPriorityClass = 0x00008000;

    [NssmUpstreamFunction("src/service.cpp", 26, "static inline int service_control_response(unsigned long control, unsigned long status)", "NssmServiceTranslationTests.core_service_helpers_match_upstream")]
    public static int service_control_response(uint control, uint status) => control switch
    {
        NssmServiceControlStart => status switch { NativeMethods.ServiceStartPending => 1, NativeMethods.ServiceRunning => 0, _ => -1 },
        NativeMethods.ServiceControlStop or NativeMethods.ServiceControlShutdown => status switch { NativeMethods.ServiceRunning or NativeMethods.ServiceStopPending => 1, NativeMethods.ServiceStopped => 0, _ => -1 },
        NativeMethods.ServiceControlPause => status switch { NativeMethods.ServicePausePending => 1, NativeMethods.ServicePaused => 0, _ => -1 },
        NativeMethods.ServiceControlContinue => status switch { NativeMethods.ServiceContinuePending => 1, NativeMethods.ServiceRunning => 0, _ => -1 },
        NativeMethods.ServiceControlInterrogate or NativeMethods.ServiceControlRotate => 0,
        _ => 0
    };

    [NssmUpstreamFunction("src/service.cpp", 86, "static inline int await_service_control_response(unsigned long control, SC_HANDLE service_handle, SERVICE_STATUS *service_status, unsigned long initial_status, unsigned long cutoff)", "NssmServiceTranslationTests.core_service_helpers_match_upstream")]
    public static int await_service_control_response(uint control, IntPtr serviceHandle, ref NssmServiceStatus serviceStatus, uint initialStatus, uint cutoff)
    {
        var tries = 0;
        uint checkpoint = 0, waitHint = 0, waited = 0;
        while (NativeMethods.QueryServiceStatus(serviceHandle, out var status))
        {
            serviceStatus = FromNative(status);
            var response = service_control_response(control, status.CurrentState);
            if (response == 0) return 0;
            if (response > 0 || status.CurrentState == initialStatus)
            {
                if (status.CheckPoint != checkpoint || status.WaitHint != waitHint) tries = 0;
                checkpoint = status.CheckPoint;
                waitHint = status.WaitHint;
                if (++tries > 10) tries = 10;
                var wait = checked((uint)(50 * tries));
                if (cutoff != 0)
                {
                    if (waited > cutoff) return response;
                    waited = unchecked(waited + wait);
                }
                Thread.Sleep(checked((int)wait));
            }
            else return response;
        }
        return -1;
    }

    [NssmUpstreamFunction("src/service.cpp", 112, "static inline int await_service_control_response(unsigned long control, SC_HANDLE service_handle, SERVICE_STATUS *service_status, unsigned long initial_status)", "NssmServiceTranslationTests.core_service_helpers_match_upstream")]
    public static int await_service_control_response(uint control, IntPtr serviceHandle, ref NssmServiceStatus serviceStatus, uint initialStatus) =>
        await_service_control_response(control, serviceHandle, ref serviceStatus, initialStatus, 0);

    [NssmUpstreamFunction("src/service.cpp", 135, "int affinity_mask_to_string(__int64 mask, TCHAR **string)", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static int affinity_mask_to_string(ulong mask, out string? value)
    {
        value = null;
        if (mask == 0) return 0;
        try
        {
            var ranges = new List<(int First, int Last)>();
            for (var cpu = 0; cpu < 64; cpu++)
            {
                if ((mask & (1UL << cpu)) == 0) continue;
                if (ranges.Count == 0 || ranges[^1].Last != cpu - 1) ranges.Add((cpu, cpu));
                else ranges[^1] = (ranges[^1].First, cpu);
            }
            value = string.Join(',', ranges.Select(range => range.Last == range.First
                ? range.First.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : range.Last == range.First + 1
                    ? $"{range.First},{range.Last}"
                    : $"{range.First}-{range.Last}"));
            return 0;
        }
        catch (OutOfMemoryException) { return 2; }
    }

    [NssmUpstreamFunction("src/service.cpp", 189, "int affinity_string_to_mask(TCHAR *string, __int64 *mask)", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static int affinity_string_to_mask(string? value, out ulong mask)
    {
        mask = 0;
        if (value is null) return 0;
        var offset = 0;
        while (offset < value.Length)
        {
            if (!ReadNumber(value, ref offset, out var first)) return 4;
            if (first >= 64) return 2;
            var last = first;
            if (offset < value.Length)
            {
                if (value[offset] == ',') offset++;
                else if (value[offset] == '-')
                {
                    offset++;
                    if (offset >= value.Length || !ReadNumber(value, ref offset, out last)) return 3;
                    if (offset < value.Length && value[offset] != ',') return 3;
                    if (offset < value.Length) offset++;
                }
                else return 3;
            }
            if (last < first || last >= 64) return 3;
            for (var cpu = first; cpu <= last; cpu++) mask |= 1UL << cpu;
        }
        return 0;
    }

    [NssmUpstreamFunction("src/service.cpp", 253, "unsigned long priority_mask()", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static uint priority_mask() => RealtimePriorityClass | HighPriorityClass | AboveNormalPriorityClass | NormalPriorityClass | BelowNormalPriorityClass | IdlePriorityClass;

    [NssmUpstreamFunction("src/service.cpp", 257, "int priority_constant_to_index(unsigned long constant)", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static int priority_constant_to_index(uint constant) => (constant & priority_mask()) switch
    {
        RealtimePriorityClass => 0,
        HighPriorityClass => 1,
        AboveNormalPriorityClass => 2,
        BelowNormalPriorityClass => 4,
        IdlePriorityClass => 5,
        _ => 3
    };

    [NssmUpstreamFunction("src/service.cpp", 268, "unsigned long priority_index_to_constant(int index)", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static uint priority_index_to_constant(int index) => index switch
    {
        0 => RealtimePriorityClass,
        1 => HighPriorityClass,
        2 => AboveNormalPriorityClass,
        4 => BelowNormalPriorityClass,
        5 => IdlePriorityClass,
        _ => NormalPriorityClass
    };

    [NssmUpstreamFunction("src/service.cpp", 279, "static inline unsigned long throttle_milliseconds(unsigned long throttle)", "NssmServiceTranslationTests.affinity_and_priority_match_upstream")]
    public static uint throttle_milliseconds(uint throttle)
    {
        if (throttle > 7) throttle = 8;
        uint result = 1;
        for (uint index = 1; index < throttle; index++) result *= 2;
        return result * 1000;
    }

    [NssmUpstreamFunction("src/service.cpp", 286, "void set_service_environment(nssm_service_t *service)", "NssmServiceTranslationTests.service_environment_round_trips")]
    public static void set_service_environment(NssmServiceData? service)
    {
        if (service is null) return;
        if (service.Environment is not null) NssmEnvironment.duplicate_environment_strings(service.Environment);
        if (service.ExtraEnvironment is null) return;
        var extra = NssmEnvironment.copy_environment_block(service.ExtraEnvironment);
        if (extra is not null) NssmEnvironment.set_environment_block(extra);
    }

    [NssmUpstreamFunction("src/service.cpp", 302, "void unset_service_environment(nssm_service_t *service)", "NssmServiceTranslationTests.service_environment_round_trips")]
    public static void unset_service_environment(NssmServiceData? service)
    {
        if (service is not null) NssmEnvironment.duplicate_environment_strings(service.InitialEnvironment);
    }

    [NssmUpstreamFunction("src/service.cpp", 324, "SC_HANDLE open_service_manager(unsigned long access)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static IntPtr open_service_manager(uint access) => NativeMethods.OpenSCManager(null, "ServicesActive", access);

    [NssmUpstreamFunction("src/service.cpp", 335, "SC_HANDLE open_service(SC_HANDLE services, TCHAR *service_name, unsigned long access, TCHAR *canonical_name, unsigned long canonical_namelen)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static IntPtr open_service(IntPtr services, string serviceName, uint access, out string? canonicalName, uint canonicalNameLength)
    {
        canonicalName = null;
        if (services == IntPtr.Zero || string.IsNullOrEmpty(serviceName)) return IntPtr.Zero;
        var handle = NativeMethods.OpenService(services, serviceName, access);
        if (handle != IntPtr.Zero)
        {
            canonicalName = CanonicalServiceName(services, serviceName, canonicalNameLength) ?? serviceName;
            return handle;
        }
        if (Marshal.GetLastWin32Error() != NativeMethods.ErrorServiceDoesNotExist || canonicalNameLength == 0) return IntPtr.Zero;
        foreach (var item in EnumerateServices(services, NativeMethods.ServiceStateAll))
        {
            if (NssmCore.str_equiv(item.DisplayName, serviceName) == 0) continue;
            canonicalName = Truncate(item.Name, canonicalNameLength);
            return NativeMethods.OpenService(services, item.Name, access);
        }
        return NativeMethods.OpenService(services, serviceName, access);
    }

    [NssmUpstreamFunction("src/service.cpp", 414, "QUERY_SERVICE_CONFIG *query_service_config(const TCHAR *service_name, SC_HANDLE service_handle)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static NssmQueryServiceConfig? query_service_config(string serviceName, IntPtr serviceHandle)
    {
        if (serviceHandle == IntPtr.Zero) return null;
        NativeMethods.QueryServiceConfig(serviceHandle, IntPtr.Zero, 0, out var required);
        if (required == 0 || Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer) return null;
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig(serviceHandle, buffer, required, out _)) return null;
            var config = Marshal.PtrToStructure<NativeMethods.QueryServiceConfigData>(buffer);
            return new NssmQueryServiceConfig(config.ServiceType, config.StartType, config.ErrorControl,
                Text(config.BinaryPathName), Text(config.LoadOrderGroup), config.TagId, ReadMultiString(config.Dependencies),
                Text(config.ServiceStartName), Text(config.DisplayName));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [NssmUpstreamFunction("src/service.cpp", 443, "int prepend_service_group_identifier(TCHAR *group, TCHAR **canon)", "NssmServiceTranslationTests.dependency_helpers_match_upstream")]
    public static int prepend_service_group_identifier(string? group, out string? canon)
    {
        canon = group;
        if (string.IsNullOrEmpty(group) || group[0] == '+') return 0;
        try { canon = "+" + group; return 0; }
        catch (OutOfMemoryException) { canon = null; return 1; }
    }

    [NssmUpstreamFunction("src/service.cpp", 464, "int append_to_dependencies(TCHAR *dependencies, unsigned long dependencieslen, TCHAR *string, TCHAR **newdependencies, unsigned long *newlen, int type)", "NssmServiceTranslationTests.dependency_helpers_match_upstream")]
    public static int append_to_dependencies(string? dependencies, uint dependenciesLength, string? value, out string? newDependencies, out uint newLength, int type)
    {
        newLength = 0;
        var canonical = value;
        if (type == DependencyGroups && prepend_service_group_identifier(value, out canonical) != 0) { newDependencies = null; return 1; }
        return NssmDoubleNull.append_to_double_null(dependencies, dependenciesLength, out newDependencies, out newLength, canonical, 0, false);
    }

    [NssmUpstreamFunction("src/service.cpp", 478, "int remove_from_dependencies(TCHAR *dependencies, unsigned long dependencieslen, TCHAR *string, TCHAR **newdependencies, unsigned long *newlen, int type)", "NssmServiceTranslationTests.dependency_helpers_match_upstream")]
    public static int remove_from_dependencies(string? dependencies, uint dependenciesLength, string? value, out string? newDependencies, out uint newLength, int type)
    {
        newLength = 0;
        var canonical = value;
        if (type == DependencyGroups && prepend_service_group_identifier(value, out canonical) != 0) { newDependencies = null; return 1; }
        return NssmDoubleNull.remove_from_double_null(dependencies, dependenciesLength, out newDependencies, out newLength, canonical, 0, false);
    }

    [NssmUpstreamFunction("src/service.cpp", 492, "int set_service_dependencies(const TCHAR *service_name, SC_HANDLE service_handle, TCHAR *buffer)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static int set_service_dependencies(string serviceName, IntPtr serviceHandle, string? buffer)
    {
        if (serviceHandle == IntPtr.Zero) return -1;
        var values = NssmDoubleNull.ToStrings(buffer);
        if (values.Length > 0)
        {
            var groups = ReadServiceGroups();
            var manager = open_service_manager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
            if (manager == IntPtr.Zero) return 1;
            try
            {
                for (var index = 0; index < values.Length; index++)
                {
                    if (values[index].StartsWith('+'))
                    {
                        var match = groups.FirstOrDefault(group => NssmCore.str_equiv(group, values[index][1..]) != 0);
                        if (match is null) return 5;
                        values[index] = "+" + match;
                    }
                    else
                    {
                        var dependency = open_service(manager, values[index], NativeMethods.ServiceQueryStatus, out var canonical, 256);
                        if (dependency == IntPtr.Zero) return 5;
                        NativeMethods.CloseServiceHandle(dependency);
                        values[index] = canonical ?? values[index];
                    }
                }
            }
            finally { NativeMethods.CloseServiceHandle(manager); }
        }
        var block = NssmDoubleNull.FromStrings(values);
        var pointer = Marshal.StringToHGlobalUni(block);
        try
        {
            return NativeMethods.ChangeServiceConfig(serviceHandle, ServiceNoChange, ServiceNoChange, ServiceNoChange,
                null, null, IntPtr.Zero, pointer, null, null, null) ? 0 : -1;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    [NssmUpstreamFunction("src/service.cpp", 615, "int get_service_dependencies(const TCHAR *service_name, SC_HANDLE service_handle, TCHAR **buffer, unsigned long *bufsize, int type)", "NssmServiceTranslationTests.dependency_helpers_match_upstream")]
    public static int get_service_dependencies(string serviceName, IntPtr serviceHandle, out string? buffer, out uint bufferSize, int type)
    {
        buffer = null;
        bufferSize = 0;
        var config = query_service_config(serviceName, serviceHandle);
        if (config is null) return 3;
        var selected = type == DependencyAll ? config.Dependencies : config.Dependencies.Where(item =>
            (item.StartsWith('+') && (type & DependencyGroups) != 0) || (!item.StartsWith('+') && (type & DependencyServices) != 0)).ToArray();
        if (selected.Length == 0) return 0;
        buffer = NssmDoubleNull.FromStrings(selected);
        bufferSize = checked((uint)buffer.Length);
        return 0;
    }

    [NssmUpstreamFunction("src/service.cpp", 676, "int get_service_dependencies(const TCHAR *service_name, SC_HANDLE service_handle, TCHAR **buffer, unsigned long *bufsize)", "NssmServiceTranslationTests.dependency_helpers_match_upstream")]
    public static int get_service_dependencies(string serviceName, IntPtr serviceHandle, out string? buffer, out uint bufferSize) =>
        get_service_dependencies(serviceName, serviceHandle, out buffer, out bufferSize, DependencyAll);

    [NssmUpstreamFunction("src/service.cpp", 680, "int set_service_description(const TCHAR *service_name, SC_HANDLE service_handle, TCHAR *buffer)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static int set_service_description(string serviceName, IntPtr serviceHandle, string? buffer)
    {
        if (serviceHandle == IntPtr.Zero) return 1;
        var text = Marshal.StringToHGlobalUni(string.IsNullOrEmpty(buffer) ? string.Empty : buffer);
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDescription>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.ServiceDescription { Description = text }, data, false);
            return NativeMethods.ChangeServiceConfig2(serviceHandle, NativeMethods.ServiceConfigDescription, data) ? 0 : 1;
        }
        finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(text); }
    }

    [NssmUpstreamFunction("src/service.cpp", 696, "int get_service_description(const TCHAR *service_name, SC_HANDLE service_handle, unsigned long len, TCHAR *buffer)", "NssmServiceTranslationTests.service_manager_helpers_reject_invalid_handles")]
    public static int get_service_description(string serviceName, IntPtr serviceHandle, uint length, out string buffer)
    {
        buffer = string.Empty;
        if (serviceHandle == IntPtr.Zero) return 4;
        NativeMethods.QueryServiceConfig2(serviceHandle, NativeMethods.ServiceConfigDescription, IntPtr.Zero, 0, out var required);
        if (Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer) return 4;
        var data = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(serviceHandle, NativeMethods.ServiceConfigDescription, data, required, out _)) return 3;
            var description = Marshal.PtrToStructure<NativeMethods.ServiceDescription>(data);
            buffer = Truncate(Text(description.Description), length);
            return 0;
        }
        catch (OutOfMemoryException) { return 2; }
        finally { Marshal.FreeHGlobal(data); }
    }

    [NssmUpstreamFunction("src/service.cpp", 727, "int get_service_startup(const TCHAR *service_name, SC_HANDLE service_handle, const QUERY_SERVICE_CONFIG *qsc, unsigned long *startup)", "NssmServiceTranslationTests.startup_and_username_match_upstream")]
    public static int get_service_startup(string serviceName, IntPtr serviceHandle, NssmQueryServiceConfig? config, out uint startup)
    {
        startup = NssmStartupAutomatic;
        if (config is null) return 1;
        startup = config.StartType switch { 3 => NssmStartupManual, 4 => NssmStartupDisabled, _ => NssmStartupAutomatic };
        if (startup != NssmStartupAutomatic || serviceHandle == IntPtr.Zero) return 0;
        var size = checked((uint)Marshal.SizeOf<NativeMethods.ServiceDelayedAutoStartInfo>());
        var data = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (NativeMethods.QueryServiceConfig2(serviceHandle, NativeMethods.ServiceConfigDelayedAutoStartInfo, data, size, out _))
            {
                if (Marshal.PtrToStructure<NativeMethods.ServiceDelayedAutoStartInfo>(data).Delayed) startup = NssmStartupDelayed;
                return 0;
            }
            return Marshal.GetLastWin32Error() == NativeMethods.ErrorInvalidLevel ? 0 : 3;
        }
        catch (OutOfMemoryException) { return 2; }
        finally { Marshal.FreeHGlobal(data); }
    }

    [NssmUpstreamFunction("src/service.cpp", 771, "int get_service_username(const TCHAR *service_name, const QUERY_SERVICE_CONFIG *qsc, TCHAR **username, size_t *usernamelen)", "NssmServiceTranslationTests.startup_and_username_match_upstream")]
    public static int get_service_username(string serviceName, NssmQueryServiceConfig? config, out string? username, out nuint usernameLength)
    {
        username = null;
        usernameLength = 0;
        if (config is null) return 1;
        if (string.IsNullOrEmpty(config.ServiceStartName) || NssmAccount.is_localsystem(config.ServiceStartName) != 0) return 0;
        try
        {
            username = new string(config.ServiceStartName.AsSpan());
            usernameLength = checked((nuint)username.Length);
            return 0;
        }
        catch (OutOfMemoryException) { return 2; }
    }

    [NssmUpstreamFunction("src/service.cpp", 798, "void set_nssm_service_defaults(nssm_service_t *service)", "NssmServiceTranslationTests.defaults_and_cleanup_match_upstream")]
    public static void set_nssm_service_defaults(NssmServiceData? service)
    {
        if (service is null) return;
        service.Type = NativeMethods.ServiceWin32OwnProcess;
        service.Priority = NormalPriorityClass;
        service.StdinSharing = 2;
        service.StdinDisposition = 3;
        service.StdinFlags = 128;
        service.StdoutSharing = 3;
        service.StdoutDisposition = 4;
        service.StdoutFlags = 128;
        service.StderrSharing = 3;
        service.StderrDisposition = 4;
        service.StderrFlags = 128;
        service.ThrottleDelay = 1500;
        service.StopMethod = uint.MaxValue;
        service.KillConsoleDelay = 1500;
        service.KillWindowDelay = 1500;
        service.KillThreadsDelay = 1500;
        service.KillProcessTree = true;
    }

    [NssmUpstreamFunction("src/service.cpp", 821, "nssm_service_t *alloc_nssm_service()", "NssmServiceTranslationTests.defaults_and_cleanup_match_upstream")]
    public static NssmServiceData? alloc_nssm_service()
    {
        try { return new NssmServiceData(); }
        catch (OutOfMemoryException) { return null; }
    }

    [NssmUpstreamFunction("src/service.cpp", 828, "void cleanup_nssm_service(nssm_service_t *service)", "NssmServiceTranslationTests.defaults_and_cleanup_match_upstream")]
    public static void cleanup_nssm_service(NssmServiceData? service)
    {
        if (service is null || service.Disposed) return;
        if (service.Password is not null)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(service.Password.AsSpan()));
            service.Password = null;
        }
        if (service.Handle != IntPtr.Zero) { NativeMethods.CloseServiceHandle(service.Handle); service.Handle = IntPtr.Zero; }
        if (service.ProcessHandle != IntPtr.Zero) { NativeMethods.CloseHandle(service.ProcessHandle); service.ProcessHandle = IntPtr.Zero; }
        service.MarkDisposed();
    }

    private static NssmServiceStatus FromNative(NativeMethods.ServiceStatus value) => new()
    {
        ServiceType = value.ServiceType,
        CurrentState = value.CurrentState,
        ControlsAccepted = value.ControlsAccepted,
        Win32ExitCode = value.Win32ExitCode,
        ServiceSpecificExitCode = value.ServiceSpecificExitCode,
        CheckPoint = value.CheckPoint,
        WaitHint = value.WaitHint
    };

    private static bool ReadNumber(string value, ref int offset, out int number)
    {
        var start = offset;
        while (offset < value.Length && char.IsAsciiDigit(value[offset])) offset++;
        return int.TryParse(value.AsSpan(start, offset - start), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
    }

    private static string Text(IntPtr pointer) => Marshal.PtrToStringUni(pointer) ?? string.Empty;
    private static string Truncate(string value, uint length) => length == 0 ? string.Empty : value[..Math.Min(value.Length, checked((int)length - 1))];

    private static string[] ReadMultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var values = new List<string>();
        for (var offset = 0; ;)
        {
            var value = Marshal.PtrToStringUni(pointer + offset) ?? string.Empty;
            if (value.Length == 0) return values.ToArray();
            values.Add(value);
            offset += checked((value.Length + 1) * sizeof(char));
        }
    }

    private static string[] ReadServiceGroups()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ServiceGroupOrder", false);
        return key?.GetValue("List", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string[] ?? [];
    }

    private static string? CanonicalServiceName(IntPtr manager, string serviceName, uint capacity)
    {
        if (capacity == 0) return null;
        var display = new StringBuilder(256);
        var displayLength = 256u;
        if (!NativeMethods.GetServiceDisplayName(manager, serviceName, display, ref displayLength)) return null;
        var key = new StringBuilder(checked((int)capacity));
        var keyLength = capacity;
        return NativeMethods.GetServiceKeyName(manager, display.ToString(), key, ref keyLength) ? key.ToString() : null;
    }

    private static IReadOnlyList<(string Name, string DisplayName)> EnumerateServices(IntPtr manager, uint state)
    {
        uint resume = 0;
        NativeMethods.EnumServicesStatusEx(manager, NativeMethods.ScEnumProcessInfo, NativeMethods.ServiceWin32, state, IntPtr.Zero, 0, out var required, out _, ref resume, null);
        if (required == 0 || Marshal.GetLastWin32Error() != NativeMethods.ErrorMoreData) return [];
        var data = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            resume = 0;
            if (!NativeMethods.EnumServicesStatusEx(manager, NativeMethods.ScEnumProcessInfo, NativeMethods.ServiceWin32, state, data, required, out _, out var count, ref resume, null)) return [];
            var size = Marshal.SizeOf<NativeMethods.EnumServiceStatusProcess>();
            var values = new List<(string, string)>(checked((int)count));
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.EnumServiceStatusProcess>(data + checked((int)index * size));
                values.Add((Text(item.ServiceName), Text(item.DisplayName)));
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(data); }
    }
}
