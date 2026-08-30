using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NssmManager.Compatibility;
using NssmManager.Contracts;

namespace NssmManager.Windows;

public sealed class WindowsServiceManager
{
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private readonly NssmRegistryStore _registry;

    public WindowsServiceManager(NssmRegistryStore? registry = null) => _registry = registry ?? new NssmRegistryStore();

    public void Install(NssmServiceConfiguration configuration, string executablePath) => _ = install_service(configuration, executablePath);

    [NssmUpstreamFunction("src/service.cpp", 1220, "int install_service(nssm_service_t *service)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public int install_service(NssmServiceConfiguration configuration, string executablePath)
    {
        NssmRegistryStore.ValidateServiceName(configuration.Name);
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("Executable path must be absolute.");
        WindowsAccountRights.GrantLogOnAsService(configuration.ServiceAccount);
        using var manager = OpenManager(NativeMethods.ScManagerCreateService);
        using var dependencies = NativeMultiString.Create(Dependencies(configuration));
        using var password = PinnedPassword.Pin(configuration.ServicePassword);
        ValidateInteractive(configuration);
        var service = NativeMethods.CreateServiceWithPassword(manager.Handle, configuration.Name, EmptyAs(configuration.DisplayName, configuration.Name), NativeMethods.ServiceAllAccess, ServiceType(configuration), Startup(configuration.StartupType), NativeMethods.ServiceErrorNormal, Quote(executablePath), null, IntPtr.Zero, dependencies.Pointer, Account(configuration.ServiceAccount, forChange: false), password.Pointer);
        if (service == IntPtr.Zero) throw Win32("CreateService");
        using var handle = new ServiceHandle(service);
        try
        {
            ConfigureDescription(configuration.Name, handle.Handle, configuration.Description);
            ConfigureDelayedStart(configuration.Name, handle.Handle, configuration.StartupType == NssmStartupType.DelayedAutomatic);
            _registry.WriteParameters(configuration);
            set_service_recovery(configuration.Name, handle.Handle);
        }
        catch
        {
            NativeMethods.DeleteService(handle.Handle);
            throw;
        }
        return 0;
    }

    public void Change(NssmServiceConfiguration configuration, string? executablePath = null) => _ = edit_service(configuration, true, executablePath);

    [NssmUpstreamFunction("src/service.cpp", 1257, "int edit_service(nssm_service_t *service, bool editing)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public int edit_service(NssmServiceConfiguration configuration, bool editing, string? executablePath = null)
    {
        configuration = configuration with { Name = ResolveServiceName(configuration.Name) };
        var currentAccount = _registry.Read(configuration.Name).ServiceAccount;
        ValidateInteractive(configuration);
        var accountChanged = !currentAccount.Equals(configuration.ServiceAccount, StringComparison.OrdinalIgnoreCase);
        if (accountChanged) WindowsAccountRights.GrantLogOnAsService(configuration.ServiceAccount);
        using var manager = OpenManager(NativeMethods.ScManagerConnect);
        using var service = OpenService(manager, configuration.Name, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
        using var dependencies = NativeMultiString.Create(Dependencies(configuration));
        using var password = PinnedPassword.Pin(configuration.ServicePassword);
        var account = accountChanged || configuration.ServicePassword is not null ? Account(configuration.ServiceAccount, forChange: true) : null;
        _registry.WriteParameters(configuration);
        var ok = NativeMethods.ChangeServiceConfigWithPassword(service.Handle, ServiceType(configuration), Startup(configuration.StartupType), NativeMethods.ServiceErrorNormal, executablePath is null ? null : Quote(executablePath), null, IntPtr.Zero, dependencies.Pointer, account, password.Pointer, EmptyAs(configuration.DisplayName, configuration.Name));
        if (!ok) throw Win32("ChangeServiceConfig");
        try { ConfigureDescription(configuration.Name, service.Handle, configuration.Description); }
        catch (Exception exception) { NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_DESCRIPTION_FAILED"), configuration.Name, exception.Message); }
        try { ConfigureDelayedStart(configuration.Name, service.Handle, configuration.StartupType == NssmStartupType.DelayedAutomatic); }
        catch (Exception exception) { NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_DELAYED_AUTO_START_INFO_FAILED"), configuration.Name, exception.Message); }
        try { set_service_recovery(configuration.Name, service.Handle); }
        catch (Exception exception) { NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_FAILURE_ACTIONS_FAILED"), configuration.Name, exception.Message); }
        return 0;
    }

    public void Delete(string serviceName) => _ = remove_service(serviceName);

    [NssmUpstreamFunction("src/service.cpp", 1513, "int remove_service(nssm_service_t *service)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public int remove_service(string serviceName)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.Delete);
        if (!NativeMethods.DeleteService(service.Handle)) throw Win32("DeleteService");
        return 0;
    }

    public NssmServiceSnapshot Query(string serviceName)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.ServiceQueryStatus);
        return Snapshot(service.CanonicalName, Query(service.Handle));
    }

    public NssmServiceState QueryState(string serviceName)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.ServiceQueryStatus);
        return State(Query(service.Handle).CurrentState);
    }

    public NssmServiceConfiguration ReadConfiguration(string serviceName)
    {
        serviceName = ResolveServiceName(serviceName);
        var configuration = _registry.Read(serviceName);
        var native = ReadNativeConfiguration(serviceName);
        return configuration with
        {
            DisplayName = native.DisplayName,
            Description = native.Description,
            ServiceAccount = native.ServiceAccount,
            StartupType = native.StartupType,
            Interactive = (native.ServiceType & NativeMethods.ServiceInteractiveProcess) != 0,
            DependOnService = native.Dependencies.Where(value => !value.StartsWith('+')).ToArray(),
            DependOnGroup = native.Dependencies.Where(value => value.StartsWith('+')).Select(value => value.TrimStart('+')).ToArray(),
            ServiceEnvironment = ReadServiceEnvironment(serviceName)
        };
    }

    public void ProbeEditConfiguration(string serviceName)
    {
        var native = ReadNativeConfiguration(serviceName);
        _ = NssmAccount.is_localsystem(native.ServiceAccount);
    }

    public object GetNativeSetting(string serviceName, string parameter)
    {
        NssmRegistryStore.ValidateServiceName(serviceName);
        if (parameter.Equals("Name", StringComparison.OrdinalIgnoreCase)) return serviceName;
        if (parameter.Equals("Environment", StringComparison.OrdinalIgnoreCase)) return ReadServiceEnvironment(serviceName);
        var native = ReadNativeConfiguration(serviceName);
        return parameter.ToLowerInvariant() switch
        {
            "displayname" => native.DisplayName,
            "description" => native.Description,
            "imagepath" => native.ImagePath,
            "objectname" => native.ServiceAccount,
            "start" => native.StartupType switch
            {
                NssmStartupType.Automatic => "SERVICE_AUTO_START",
                NssmStartupType.DelayedAutomatic => "SERVICE_DELAYED_AUTO_START",
                NssmStartupType.Disabled => "SERVICE_DISABLED",
                _ => "SERVICE_DEMAND_START"
            },
            "type" => ServiceTypeText(native.ServiceType),
            "dependonservice" => native.Dependencies.Where(value => !value.StartsWith('+')).ToArray(),
            "dependongroup" => native.Dependencies.Where(value => value.StartsWith('+')).Select(value => value.TrimStart('+')).ToArray(),
            _ => throw new ArgumentException($"Unknown native setting '{parameter}'.")
        };
    }

    public IReadOnlyList<NssmServiceSnapshot> List(bool includeNative = false) => list_nssm_services(includeNative);

    [NssmUpstreamFunction("src/service.cpp", 2267, "int list_nssm_services(int argc, TCHAR **argv)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public IReadOnlyList<NssmServiceSnapshot> list_nssm_services(bool includeNative = false)
    {
        using var manager = OpenManager(NativeMethods.ScManagerEnumerateService);
        uint required;
        uint returned;
        uint resume = 0;
        NativeMethods.EnumServicesStatusEx(manager.Handle, NativeMethods.ScEnumProcessInfo, NativeMethods.ServiceWin32, NativeMethods.ServiceStateAll, IntPtr.Zero, 0, out required, out returned, ref resume, null);
        if (required == 0 && Marshal.GetLastWin32Error() != 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            resume = 0;
            if (!NativeMethods.EnumServicesStatusEx(manager.Handle, NativeMethods.ScEnumProcessInfo, NativeMethods.ServiceWin32, NativeMethods.ServiceStateAll, buffer, required, out _, out returned, ref resume, null)) throw Win32("EnumServicesStatusEx");
            var itemSize = Marshal.SizeOf<NativeMethods.EnumServiceStatusProcess>();
            var results = new List<NssmServiceSnapshot>(checked((int)returned));
            for (var index = 0; index < returned; index++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.EnumServiceStatusProcess>(buffer + checked(index * itemSize));
                var name = Marshal.PtrToStringUni(item.ServiceName) ?? "";
                if (_registry.IsCompatible(name)) results.Add(Snapshot(name, item.Status));
                else if (includeNative)
                {
                    var displayName = Marshal.PtrToStringUni(item.DisplayName) ?? name;
                    results.Add(new NssmServiceSnapshot(name, displayName, "", "", _registry.ReadImagePath(name), State(item.Status.CurrentState), NssmStartupType.Manual, item.Status.ProcessId, false, false));
                }
            }
            return results.ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public NssmServiceSnapshot Start(string serviceName, string[]? arguments = null) => control_service(0, serviceName, NativeMethods.ServiceRunning, arguments);
    public NssmServiceSnapshot Stop(string serviceName) => control_service(NativeMethods.ServiceControlStop, serviceName, NativeMethods.ServiceStopped);
    public NssmServiceSnapshot Pause(string serviceName) => control_service(NativeMethods.ServiceControlPause, serviceName, NativeMethods.ServicePaused);
    public NssmServiceSnapshot Continue(string serviceName) => control_service(NativeMethods.ServiceControlContinue, serviceName, NativeMethods.ServiceRunning);
    public NssmServiceSnapshot Rotate(string serviceName) => control_service(NativeMethods.ServiceControlRotate, serviceName, 0);
    public NssmServiceSnapshot Restart(string serviceName, string[]? arguments = null) { Stop(serviceName); return Start(serviceName, arguments); }

    public IReadOnlyList<NssmProcessSnapshot> GetProcessTree(string serviceName) => service_process_tree(serviceName);

    [NssmUpstreamFunction("src/service.cpp", 2328, "int service_process_tree(int argc, TCHAR **argv)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public IReadOnlyList<NssmProcessSnapshot> service_process_tree(string serviceName)
    {
        WindowsPrivileges.TryEnableDebugPrivilege();
        var rootId = Query(serviceName).ProcessId;
        if (rootId == 0) return [];
        var entries = SnapshotProcesses();
        var children = entries.GroupBy(entry => entry.ParentProcessId).ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<NssmProcessSnapshot>();
        var visited = new HashSet<uint>();
        DateTime? rootStarted = ProcessStartTime(rootId);
        Walk(rootId, 0);
        return result;

        void Walk(uint processId, int depth)
        {
            if (!visited.Add(processId)) return;
            var entry = entries.FirstOrDefault(item => item.ProcessId == processId);
            var image = ProcessImage(processId, entry.ExecutableFile);
            result.Add(new NssmProcessSnapshot(processId, depth, image));
            if (!children.TryGetValue(processId, out var descendants)) return;
            foreach (var child in descendants.OrderBy(item => item.ProcessId))
            {
                var childStarted = ProcessStartTime(child.ProcessId);
                if (rootStarted.HasValue && childStarted.HasValue && childStarted.Value < rootStarted.Value) continue;
                Walk(child.ProcessId, depth + 1);
            }
        }
    }

    public void Migrate(string serviceName, string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("Executable path must be absolute.");
        ChangeImagePath(serviceName, Quote(executablePath));
    }

    public void ChangeImagePath(string serviceName, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || imagePath.IndexOf('\0') >= 0) throw new ArgumentException("Service ImagePath is invalid.");
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.ServiceChangeConfig);
        if (!NativeMethods.ChangeServiceConfig(service.Handle, ServiceNoChange, ServiceNoChange, ServiceNoChange, imagePath, null, IntPtr.Zero, IntPtr.Zero, null, null, null)) throw Win32("ChangeServiceConfig(ImagePath)");
    }

    [NssmUpstreamFunction("src/service.cpp", 1640, "void set_service_recovery(nssm_service_t *service)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public static void set_service_recovery(string serviceName, IntPtr serviceHandle)
    {
        if (serviceHandle == IntPtr.Zero) return;
        var size = Marshal.SizeOf<NativeMethods.ServiceFailureActionsFlag>();
        var data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new NativeMethods.ServiceFailureActionsFlag { FailureActionsOnNonCrashFailures = true }, data, false);
            if (!NativeMethods.ChangeServiceConfig2(serviceHandle, NativeMethods.ServiceConfigFailureActionsFlag, data))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorInvalidLevel)
                    NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_FAILURE_ACTIONS_FAILED"), serviceName, new Win32Exception(error).Message);
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public void set_service_recovery(string serviceName)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.ServiceChangeConfig);
        set_service_recovery(service.CanonicalName, service.Handle);
    }

    [NssmUpstreamFunction("src/service.cpp", 1363, "int control_service(unsigned long control, int argc, TCHAR **argv, bool return_status)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public NssmServiceSnapshot control_service(uint control, string serviceName, uint desired, string[]? arguments, bool returnStatus)
    {
        var access = service_control_access(control);
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, access);
        var initialState = NativeMethods.ServiceStopped;
        var controlStatus = new NativeMethods.ServiceStatus();
        if (control == 0)
        {
            var serviceArguments = service_start_arguments(serviceName, arguments);
            if (!NativeMethods.StartService(service.Handle, (uint)serviceArguments.Length, serviceArguments) && Marshal.GetLastWin32Error() != 997) throw Win32("StartService");
        }
        else if (!NativeMethods.ControlService(service.Handle, control, out controlStatus))
        {
            var error = Marshal.GetLastWin32Error();
            if (control == NativeMethods.ServiceControlStop && error == 1062) return Snapshot(service.CanonicalName, Query(service.Handle));
            if (error != 997) throw new Win32Exception(error, "ControlService");
        }
        else initialState = controlStatus.CurrentState;

        var status = new NssmServiceStatus();
        var cutoff = control == 0 ? StartControlCutoff(service.CanonicalName) : 0u;
        if (NssmServiceTranslation.await_service_control_response(control, service.Handle, ref status, initialState, cutoff) != 0)
            throw new InvalidOperationException($"BadControlResponse:{status.CurrentState}:{control}");
        return Snapshot(service.CanonicalName, Query(service.Handle));
    }

    public static uint service_control_access(uint control) => NativeMethods.ServiceQueryStatus | control switch
        {
            0 => NativeMethods.ServiceStart,
            NativeMethods.ServiceControlContinue or NativeMethods.ServiceControlPause => NativeMethods.ServicePauseContinue,
            NativeMethods.ServiceControlStop => NativeMethods.ServiceStop,
            NativeMethods.ServiceControlRotate => NativeMethods.ServiceUserDefinedControl,
            _ => 0
        };

    [NssmUpstreamFunction("src/service.cpp", 1508, "int control_service(unsigned long control, int argc, TCHAR **argv)", "NssmServiceTranslationTests.manager_operations_validate_arguments")]
    public NssmServiceSnapshot control_service(uint control, string serviceName, uint desired, string[]? arguments = null) =>
        control_service(control, serviceName, desired, arguments, false);

    public static string[] service_start_arguments(string serviceName, string[]? arguments) =>
        [serviceName, .. arguments ?? []];

    private uint StartControlCutoff(string serviceName)
    {
        if (!_registry.IsCompatible(serviceName)) return 0;
        try
        {
            return _registry.Get(serviceName, "AppThrottle") switch
            {
                uint number => number,
                int number => unchecked((uint)number),
                _ => 1500u
            };
        }
        catch { return 1500; }
    }

    private NssmServiceSnapshot Snapshot(string name, NativeMethods.ServiceStatusProcess status)
    {
        var compatible = _registry.IsCompatible(name);
        var config = new NssmServiceConfiguration { Name = name };
        if (compatible)
        {
            try { config = ReadConfiguration(name); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException) { }
        }
        var native = ReadNativeConfiguration(name);
        var imagePath = native.ImagePath;
        return new NssmServiceSnapshot(name, EmptyAs(config.DisplayName, name), config.Description, config.Application, imagePath, State(status.CurrentState), config.StartupType, status.ProcessId, compatible, imagePath.Contains("nssm-manager", StringComparison.OrdinalIgnoreCase));
    }

    private NativeServiceConfiguration ReadNativeConfiguration(string serviceName)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, serviceName, NativeMethods.ServiceQueryConfig);
        NativeMethods.QueryServiceConfig(service.Handle, IntPtr.Zero, 0, out var required);
        if (required == 0 || Marshal.GetLastWin32Error() != NativeMethods.ErrorInsufficientBuffer) throw Win32("QueryServiceConfig");
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig(service.Handle, buffer, required, out _)) throw Win32("QueryServiceConfig");
            var value = Marshal.PtrToStructure<NativeMethods.QueryServiceConfigData>(buffer);
            var delayed = value.StartType == 2 && QueryDelayedStart(service.Handle);
            var startup = value.StartType switch { 2 when delayed => NssmStartupType.DelayedAutomatic, 2 => NssmStartupType.Automatic, 4 => NssmStartupType.Disabled, _ => NssmStartupType.Manual };
            return new NativeServiceConfiguration(
                value.ServiceType,
                startup,
                Marshal.PtrToStringUni(value.BinaryPathName) ?? "",
                Marshal.PtrToStringUni(value.ServiceStartName) ?? "LocalSystem",
                Marshal.PtrToStringUni(value.DisplayName) ?? service.CanonicalName,
                QueryDescription(service.Handle),
                ReadMultiString(value.Dependencies));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool QueryDelayedStart(IntPtr service)
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.ServiceDelayedAutoStartInfo>();
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            return NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDelayedAutoStartInfo, buffer, size, out _)
                && Marshal.PtrToStructure<NativeMethods.ServiceDelayedAutoStartInfo>(buffer).Delayed;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string QueryDescription(IntPtr service)
    {
        NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDescription, IntPtr.Zero, 0, out var required);
        if (required == 0) return "";
        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!NativeMethods.QueryServiceConfig2(service, NativeMethods.ServiceConfigDescription, buffer, required, out _)) throw Win32("QueryServiceConfig2(Description)");
            var description = Marshal.PtrToStructure<NativeMethods.ServiceDescription>(buffer);
            return Marshal.PtrToStringUni(description.Description) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private string[] ReadServiceEnvironment(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{NssmRegistryStore.ServicesRoot}\{serviceName}");
        return key?.GetValue("Environment") as string[] ?? [];
    }

    private static string[] ReadMultiString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var values = new List<string>();
        var offset = 0;
        while (true)
        {
            var value = Marshal.PtrToStringUni(pointer + offset) ?? "";
            if (value.Length == 0) return values.ToArray();
            values.Add(value);
            offset += checked((value.Length + 1) * sizeof(char));
        }
    }

    private static string ServiceTypeText(uint value) => value switch
    {
        0x00000001 => "SERVICE_KERNEL_DRIVER",
        0x00000002 => "SERVICE_FILE_SYSTEM_DRIVER",
        0x00000010 => "SERVICE_WIN32_OWN_PROCESS",
        0x00000020 => "SERVICE_WIN32_SHARE_PROCESS",
        0x00000110 => "SERVICE_INTERACTIVE_PROCESS",
        0x00000120 => "SERVICE_WIN32_SHARE_PROCESS|SERVICE_INTERACTIVE_PROCESS",
        _ => "SERVICE_UNKNOWN"
    };

    private static NativeMethods.ServiceStatusProcess Query(IntPtr service)
    {
        var size = Marshal.SizeOf<NativeMethods.ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try { if (!NativeMethods.QueryServiceStatusEx(service, NativeMethods.ScStatusProcessInfo, buffer, (uint)size, out _)) throw Win32("QueryServiceStatusEx"); return Marshal.PtrToStructure<NativeMethods.ServiceStatusProcess>(buffer); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ServiceHandle OpenManager(uint access) { var handle = NativeMethods.OpenSCManager(null, null, access); return handle == IntPtr.Zero ? throw Win32("OpenSCManager") : new ServiceHandle(handle); }
    public string ResolveServiceName(string name)
    {
        using var manager = OpenManager(NativeMethods.ScManagerConnect | NativeMethods.ScManagerEnumerateService);
        using var service = OpenService(manager, name, NativeMethods.ServiceQueryStatus);
        return service.CanonicalName;
    }

    private static ServiceHandle OpenService(ServiceHandle manager, string name, uint access)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0) throw new ArgumentException("Service name is invalid.", nameof(name));
        var handle = NssmServiceTranslation.open_service(manager.Handle, name, access, out var canonicalName, 256);
        if (handle == IntPtr.Zero) throw Win32("OpenService");
        canonicalName ??= name;
        NssmRegistryStore.ValidateServiceName(canonicalName);
        return new ServiceHandle(handle, canonicalName);
    }
    private static Win32Exception Win32(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static string Quote(string path) => path.StartsWith('"') || !path.Contains(' ') ? path : $"\"{path}\"";
    private static string EmptyAs(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
    private static uint Startup(NssmStartupType value) => value switch { NssmStartupType.Automatic or NssmStartupType.DelayedAutomatic => 2, NssmStartupType.Disabled => 4, _ => 3 };
    private static uint ServiceType(NssmServiceConfiguration value) => NativeMethods.ServiceWin32OwnProcess | (value.Interactive ? NativeMethods.ServiceInteractiveProcess : 0);
    private static void ValidateInteractive(NssmServiceConfiguration value) { if (value.Interactive && !value.ServiceAccount.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Interactive services must run as LocalSystem."); }
    private static string? Account(string value, bool forChange) => value.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ? forChange ? "LocalSystem" : null : value.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ? @"NT AUTHORITY\LocalService" : value.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) ? @"NT AUTHORITY\NetworkService" : value;
    private static string? Dependencies(NssmServiceConfiguration value) { var all = value.DependOnService.Concat(value.DependOnGroup.Select(group => "+" + group.TrimStart('+'))).ToArray(); return all.Length == 0 ? null : string.Join('\0', all) + "\0\0"; }
    private static NssmServiceState State(uint value) => value switch { 1 => NssmServiceState.Stopped, 2 => NssmServiceState.StartPending, 3 => NssmServiceState.StopPending, 4 => NssmServiceState.Running, 5 => NssmServiceState.ContinuePending, 6 => NssmServiceState.PausePending, 7 => NssmServiceState.Paused, _ => NssmServiceState.Unknown };
    private static NativeMethods.ProcessEntry32[] SnapshotProcesses()
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.ToolhelpSnapshotProcess, 0);
        if (snapshot == new IntPtr(-1)) throw Win32("CreateToolhelp32Snapshot");
        try
        {
            var items = new List<NativeMethods.ProcessEntry32>();
            var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>(), ExecutableFile = "" };
            if (!NativeMethods.Process32First(snapshot, ref entry)) throw Win32("Process32First");
            do
            {
                items.Add(entry);
                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>();
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
            return items.ToArray();
        }
        finally { NativeMethods.CloseHandle(snapshot); }
    }
    private static DateTime? ProcessStartTime(uint processId) { try { using var process = Process.GetProcessById(checked((int)processId)); return process.StartTime.ToUniversalTime(); } catch { return null; } }
    private static string ProcessImage(uint processId, string fallback)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle != IntPtr.Zero)
        {
            try
            {
                var capacity = 32768u;
                var path = new StringBuilder(checked((int)capacity));
                if (NativeMethods.QueryFullProcessImageName(handle, 0, path, ref capacity)) return path.ToString();
            }
            finally { NativeMethods.CloseHandle(handle); }
        }
        return string.IsNullOrWhiteSpace(fallback) ? "???" : fallback;
    }

    private static void ConfigureDescription(string serviceName, IntPtr service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        var structure = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDescription>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.ServiceDescription { Description = text }, structure, false);
            if (!NativeMethods.ChangeServiceConfig2(service, NativeMethods.ServiceConfigDescription, structure))
                NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_DESCRIPTION_FAILED"), serviceName, new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(structure); Marshal.FreeHGlobal(text); }
    }

    private static void ConfigureDelayedStart(string serviceName, IntPtr service, bool delayed)
    {
        var structure = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.ServiceDelayedAutoStartInfo>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.ServiceDelayedAutoStartInfo { Delayed = delayed }, structure, false);
            if (!NativeMethods.ChangeServiceConfig2(service, NativeMethods.ServiceConfigDelayedAutoStartInfo, structure))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NativeMethods.ErrorInvalidLevel)
                    NssmEvent.log_event(1, NssmEvent.message_id("NSSM_EVENT_SERVICE_CONFIG_DELAYED_AUTO_START_INFO_FAILED"), serviceName, new Win32Exception(error).Message);
            }
        }
        finally { Marshal.FreeHGlobal(structure); }
    }

    private sealed class ServiceHandle : IDisposable
    {
        public ServiceHandle(IntPtr handle, string canonicalName = "") { Handle = handle; CanonicalName = canonicalName; }
        public IntPtr Handle { get; private set; }
        public string CanonicalName { get; }
        public void Dispose() { if (Handle != IntPtr.Zero) { NativeMethods.CloseServiceHandle(Handle); Handle = IntPtr.Zero; } }
    }

    private sealed class NativeMultiString : IDisposable
    {
        private NativeMultiString(IntPtr pointer) => Pointer = pointer;
        public IntPtr Pointer { get; private set; }
        public static NativeMultiString Create(string? value) => new(value is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(value));
        public void Dispose() { if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; } }
    }

    private sealed class PinnedPassword : IDisposable
    {
        private GCHandle _handle;
        private char[]? _owned;
        private PinnedPassword(char[]? value)
        {
            if (value is null) return;
            if (value.Length == 0 || value[^1] != '\0')
            {
                _owned = new char[value.Length + 1];
                value.CopyTo(_owned, 0);
                value = _owned;
            }
            _handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            Pointer = _handle.AddrOfPinnedObject();
        }
        public IntPtr Pointer { get; private set; }
        public static PinnedPassword Pin(char[]? value) => new(value);
        public void Dispose()
        {
            Pointer = IntPtr.Zero;
            if (_handle.IsAllocated) _handle.Free();
            if (_owned is not null)
            {
                Array.Clear(_owned);
                _owned = null;
            }
        }
    }

    private sealed record NativeServiceConfiguration(
        uint ServiceType,
        NssmStartupType StartupType,
        string ImagePath,
        string ServiceAccount,
        string DisplayName,
        string Description,
        string[] Dependencies);
}
