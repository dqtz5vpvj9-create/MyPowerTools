using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Executable;
using NssmManager.Runtime;
using NssmManager.Supervisor;
using NssmManager.Windows;

return await NssmManagerProgram._tmain(args);

internal static class NssmManagerProgram
{
    private const string Version = "NSSM 2.24-101-g897c7ad 64-bit 2017-04-26";
    public static Task<int> RunAsync(string[] commandLine) => _tmain(commandLine);

    [NssmUpstreamFunction("src/nssm.cpp", 242, "int _tmain(int argc, TCHAR **argv)", "NssmCliDifferentialTests.elevated_mutations_preserve_exit_codes")]
    internal static Task<int> _tmain(string[] commandLine)
    {
        if (NssmConsole.check_console()) NssmUtf8.setup_utf8();
        var isAdministrator = NssmCore.check_admin();
        if (NssmImports.get_imports() != 0) return Task.FromResult(111);
        if (commandLine.Length == 0)
        {
            if (isAdministrator) _ = NssmRegistry.create_messages();
            if (WindowsServiceDispatcher.TryRun(name => new ManagedServiceRuntime(name), out var error)) return Task.FromResult(0);
            if (error != 1063) Console.Error.WriteLine(new Win32Exception(error).Message);
            return Task.FromResult(NssmCore.usage(1));
        }
        try { return Task.FromResult(Execute(commandLine)); }
        catch (Win32Exception exception) { Console.Error.WriteLine($"{exception.Message} ({exception.NativeErrorCode})"); return Task.FromResult(4); }
        catch (Exception exception) { Console.Error.WriteLine(exception.Message); return Task.FromResult(3); }
    }

    private static int Execute(string[] commandLine)
    {
        if (NssmCore.is_version(commandLine[0])) { Console.WriteLine(Version); return 0; }
        var command = commandLine[0].ToLowerInvariant();
        var registry = new NssmRegistryStore();
        var services = new WindowsServiceManager(registry);
        var executable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unavailable."));
        switch (command)
        {
            case "version": Console.WriteLine(Version); return 0;
            case "install": return pre_install_service(commandLine[1..], executable);
            case "remove": return pre_remove_service(commandLine[1..], registry);
            case "start": return ControlCommand(Service(commandLine), "start", "START", registry, commandLine[2..]);
            case "stop": return ControlCommand(Service(commandLine), "stop", "STOP", registry);
            case "restart":
                var restartService = Service(commandLine);
                var stopResult = ControlCommand(restartService, "stop", "STOP", registry);
                return stopResult == 0 ? ControlCommand(restartService, "start", "START", registry, commandLine[2..]) : stopResult;
            case "pause": return ControlCommand(Service(commandLine), "pause", "PAUSE", registry);
            case "continue": return ControlCommand(Service(commandLine), "continue", "CONTINUE", registry);
            case "rotate": return ControlCommand(Service(commandLine), "rotate", "ROTATE", registry);
            case "status": return Status(commandLine, services, false);
            case "statuscode": return Status(commandLine, services, true);
            case "list": foreach (var item in services.List(commandLine.Length > 1 && commandLine[1].Equals("all", StringComparison.OrdinalIgnoreCase))) Console.WriteLine(item.Name); return 0;
            case "processes": return Processes(commandLine, services);
            case "get":
            case "set":
            case "reset":
            case "unset":
            case "dump":
            case "edit": return pre_edit_service(commandLine, registry);
            case "migrate": Migrate(Service(commandLine), executable, registry); return 0;
            case "rollback":
                var rollbackService = services.ResolveServiceName(Service(commandLine));
                ElevateForService("nssm-manager.rollback", rollbackService, registry);
                return 0;
            default: return NssmCore.usage(1);
        }
    }

    [NssmUpstreamFunction("src/service.cpp", 849, "int pre_install_service(int argc, TCHAR **argv)", "NssmCliDifferentialTests.elevated_mutations_preserve_exit_codes")]
    private static int pre_install_service(string[] arguments, string executable)
    {
        if (arguments.Length < 2) return OpenTool("install", arguments.FirstOrDefault());
        var application = arguments[1];
        var flagsLength = arguments.Skip(2).Sum(value => value.Length + 1);
        if (flagsLength > 16383) return 2;
        var configuration = new NssmServiceConfiguration
        {
            Name = arguments[0],
            DisplayName = arguments[0],
            Application = application,
            AppDirectory = NssmCore.strip_basename(application),
            AppParameters = arguments.Length > 2 ? string.Join(' ', arguments[2..]) : ""
        };
        var request = new JsonObject
        {
            ["configuration"] = JsonSerializer.SerializeToNode(configuration, NssmManagerJsonContext.Default.NssmServiceConfiguration),
            ["executablePath"] = executable
        };
        try { Elevate("nssm-manager.install", request); }
        catch (NssmElevatedOperationException exception)
        {
            var error = exception.NativeErrorCode.HasValue ? NssmEvent.error_string(unchecked((uint)exception.NativeErrorCode.Value)) : exception.Message;
            if (exception.Message.Equals("OpenSCManager", StringComparison.OrdinalIgnoreCase))
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPEN_SERVICE_MANAGER_FAILED"));
                return 2;
            }
            if (exception.Message.Equals("CreateService", StringComparison.OrdinalIgnoreCase))
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_CREATESERVICE_FAILED"), error);
                return 5;
            }
            Console.Error.WriteLine(error);
            return 6;
        }
        Console.WriteLine($"Service \"{configuration.Name}\" installed successfully!");
        return 0;
    }

    [NssmUpstreamFunction("src/service.cpp", 891, "int pre_edit_service(int argc, TCHAR **argv)", "NssmCliDifferentialTests.elevated_mutations_preserve_exit_codes")]
    private static int pre_edit_service(string[] commandLine, NssmRegistryStore registry)
    {
        if (commandLine.Length < 2) return NssmCore.usage(1);
        var services = new WindowsServiceManager(registry);
        try
        {
            commandLine[1] = services.ResolveServiceName(commandLine[1]);
            _ = services.Query(commandLine[1]);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1060)
        {
            PrintOpenServiceFailure(exception.NativeErrorCode);
            return 3;
        }
        services.ProbeEditConfiguration(commandLine[1]);
        return commandLine[0].ToLowerInvariant() switch
        {
            "get" => Get(commandLine, registry),
            "set" => Set(commandLine, registry),
            "reset" or "unset" => Reset(commandLine, registry),
            "dump" => Dump(commandLine, registry),
            "edit" => OpenEditor(commandLine),
            _ => NssmCore.usage(1)
        };
    }

    private static int Get(string[] commandLine, NssmRegistryStore registry)
    {
        if (commandLine.Length < 3) return NssmCore.usage(1);
        var setting = FindSetting(commandLine[2]);
        if (setting is null) return 1;
        if (!setting.Native && !registry.IsCompatible(commandLine[1]))
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_NATIVE_PARAMETER"), setting.Name, "NSSM");
            return 1;
        }
        var additional = Additional(commandLine, setting, NssmSettingsTranslation.AdditionalGetting, 3);
        if ((setting.Additional & NssmSettingsTranslation.AdditionalGetting) != 0 && additional is null) return 1;
        var value = new NssmSettingValue();
        if (NssmSettingsTranslation.Get(commandLine[1], setting.Name, additional, value) < 0) return 5;
        Console.WriteLine(NssmSettingsTranslation.is_numeric_type(setting.Type) ? value.Numeric : value.String ?? string.Empty);
        return 0;
    }

    private static int Set(string[] commandLine, NssmRegistryStore registry)
    {
        if (commandLine.Length < 4) return NssmCore.usage(1);
        var name = commandLine[1];
        var parameter = commandLine[2];
        var setting = FindSetting(parameter);
        if (setting is null) return 1;
        if (!setting.Native && !registry.IsCompatible(name))
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_NATIVE_PARAMETER"), setting.Name, "NSSM");
            return 1;
        }
        var remainder = 3;
        string? additional = null;
        if ((setting.Additional & NssmSettingsTranslation.AdditionalSetting) != 0)
        {
            additional = Additional(commandLine, setting, NssmSettingsTranslation.AdditionalSetting, 3);
            if (additional is null) return 1;
            remainder = 4;
        }
        else if (setting.Name.Equals("ObjectName", StringComparison.OrdinalIgnoreCase))
        {
            additional = commandLine[3];
            remainder = 4;
        }
        var arguments = ServiceArguments(name, registry);
        arguments["parameter"] = parameter;
        arguments["subparameter"] = additional;
        try
        {
            if (setting.Name.Equals("ObjectName", StringComparison.OrdinalIgnoreCase))
            {
                if (commandLine.Length > remainder) throw new ArgumentException("Service account passwords cannot be supplied on the command line.");
                arguments["values"] = JsonSerializer.SerializeToNode(new[] { string.Empty }, NssmManagerJsonContext.Default.StringArray);
                char[]? password = null;
                try
                {
                    if (RequiresPassword(additional!)) password = ReadPassword();
                    var securePayload = Elevate("nssm-manager.registry-set", arguments, password);
                    PrintSettingResult(securePayload, setting.Name, name);
                    return 0;
                }
                finally { if (password is not null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }
            }
            else
            {
                var values = commandLine[remainder..];
                if (values.Length == 0) values = [string.Empty];
                arguments["values"] = JsonSerializer.SerializeToNode(values, NssmManagerJsonContext.Default.StringArray);
            }
            var payload = Elevate("nssm-manager.registry-set", arguments);
            PrintSettingResult(payload, setting.Name, name);
            return 0;
        }
        catch (Exception exception) when (exception is NssmElevatedOperationException or OperationCanceledException)
        {
            PrintSettingFailure(setting.Name, name);
            return 6;
        }
    }

    private static bool RequiresPassword(string account) =>
        !account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) &&
        !account.Equals("LocalService", StringComparison.OrdinalIgnoreCase) &&
        !account.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) &&
        !account.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase) &&
        !account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase);

    private static char[] ReadPassword()
    {
        Console.Error.Write("Service account password: ");
        var buffer = new char[4096];
        var length = 0;
        try
        {
            while (true)
            {
                char character;
                if (Console.IsInputRedirected)
                {
                    var input = Console.In.Read();
                    if (input < 0) { if (length == 0) throw new EndOfStreamException("Password input ended unexpectedly."); break; }
                    character = checked((char)input);
                    if (character is '\r' or '\n') break;
                }
                else
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Backspace) { if (length > 0) buffer[--length] = '\0'; continue; }
                    character = key.KeyChar;
                }
                if (char.IsControl(character)) continue;
                if (length == buffer.Length) throw new InvalidDataException("Service password exceeds 4096 characters.");
                buffer[length++] = character;
            }
            var result = new char[length];
            buffer.AsSpan(0, length).CopyTo(result);
            return result;
        }
        finally
        {
            if (!Console.IsInputRedirected) Console.Error.WriteLine();
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buffer.AsSpan()));
        }
    }

    private static int Reset(string[] commandLine, NssmRegistryStore registry)
    {
        if (commandLine.Length < 3) return NssmCore.usage(1);
        var setting = FindSetting(commandLine[2]);
        if (setting is null) return 1;
        if (!setting.Native && !registry.IsCompatible(commandLine[1]))
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_NATIVE_PARAMETER"), setting.Name, "NSSM");
            return 1;
        }
        var additional = Additional(commandLine, setting, NssmSettingsTranslation.AdditionalResetting, 3);
        if ((setting.Additional & NssmSettingsTranslation.AdditionalResetting) != 0 && additional is null) return 1;
        var arguments = ServiceArguments(commandLine[1], registry);
        arguments["parameter"] = setting.Name;
        arguments["subparameter"] = additional;
        try
        {
            var payload = Elevate("nssm-manager.registry-reset", arguments);
            PrintSettingResult(payload, setting.Name, commandLine[1]);
            return 0;
        }
        catch (Exception exception) when (exception is NssmElevatedOperationException or OperationCanceledException)
        {
            PrintSettingFailure(setting.Name, commandLine[1]);
            return 6;
        }
    }

    private static int Dump(string[] commandLine, NssmRegistryStore registry)
    {
        if (commandLine.Length < 2) return NssmCore.usage(1);
        var sourceName = commandLine[1];
        var targetName = commandLine.Length > 2 ? commandLine[2] : sourceName;
        var application = new NssmSettingValue();
        _ = NssmSettingsTranslation.Get(sourceName, "Application", null, application);
        if (NssmCore.quote(targetName, 512, out var quotedName) != 0 ||
            NssmCore.quote(application.String ?? string.Empty, 65536, out var quotedApplication) != 0 ||
            NssmCore.quote(NssmCore.nssm_exe(), 65536, out var quotedNssm) != 0) return 6;
        Console.WriteLine($"{quotedNssm} install {quotedName} {quotedApplication}");
        return NssmSettingsTranslation.Dump(sourceName, targetName) == 0 ? 0 : 1;
    }

    private static NssmTranslatedSetting? FindSetting(string parameter)
    {
        var setting = NssmSettingsTranslation.Find(parameter);
        if (setting is not null) return setting;
        NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_INVALID_PARAMETER"), parameter);
        foreach (var candidate in NssmSettingsTranslation.Settings) Console.Error.WriteLine(candidate.Name);
        return null;
    }

    private static string? Additional(string[] commandLine, NssmTranslatedSetting setting, int flag, int index)
    {
        if ((setting.Additional & flag) == 0) return null;
        if (commandLine.Length > index) return commandLine[index];
        NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_MISSING_SUBPARAMETER"), setting.Name);
        return null;
    }

    private static void PrintSettingResult(JsonNode payload, string settingName, string serviceName)
    {
        var result = payload["result"]?.GetValue<int>() ?? 1;
        var id = result == 0 ? "NSSM_MESSAGE_RESET_SETTING" : "NSSM_MESSAGE_SET_SETTING";
        NssmEvent.print_message(Console.Out, NssmEvent.message_id(id), settingName, serviceName);
    }

    private static void PrintSettingFailure(string settingName, string serviceName) =>
        NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_SET_SETTING_FAILED"), settingName, serviceName);

    private static int OpenEditor(string[] commandLine)
    {
        RequireService(commandLine);
        return OpenTool("edit", commandLine[1]);
    }

    private static int OpenTool(string mode, string? serviceName)
    {
        var query = $"mode={Uri.EscapeDataString(mode)}";
        if (!string.IsNullOrEmpty(serviceName)) query += $"&service={Uri.EscapeDataString(serviceName)}";
        var activationUri = $"nssm-manager://open?{query}";
        var payload = new JsonObject
        {
            ["ToolId"] = "nssm-manager",
            ["RouteId"] = "services",
            ["ActivationUri"] = activationUri,
            ["SuppressShellWindow"] = false
        }.ToJsonString();
        var uri = $"mypowertools://activate?payload={Uri.EscapeDataString(payload)}";
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return 0;
    }

    [NssmUpstreamFunction("src/service.cpp", 1203, "int pre_remove_service(int argc, TCHAR **argv)", "NssmCliDifferentialTests.elevated_mutations_preserve_exit_codes")]
    private static int pre_remove_service(string[] arguments, NssmRegistryStore registry)
    {
        if (arguments.Length < 2) return OpenTool("remove", arguments.FirstOrDefault());
        if (NssmCore.str_equiv(arguments[1], "confirm") == 0) { Console.Error.WriteLine("To remove a service without the UI, append the confirm argument."); return 100; }
        var serviceName = arguments[0];
        try
        {
            serviceName = new WindowsServiceManager(registry).ResolveServiceName(serviceName);
            ElevateForService("nssm-manager.remove", serviceName, registry);
        }
        catch (Win32Exception exception)
        {
            if (exception.Message.Equals("OpenSCManager", StringComparison.OrdinalIgnoreCase))
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPEN_SERVICE_MANAGER_FAILED"));
                return 2;
            }
            PrintOpenServiceFailure(exception.NativeErrorCode);
            return 3;
        }
        catch (NssmElevatedOperationException exception)
        {
            var errorCode = exception.NativeErrorCode ?? 1;
            if (exception.Message.Equals("OpenSCManager", StringComparison.OrdinalIgnoreCase))
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPEN_SERVICE_MANAGER_FAILED"));
                return 2;
            }
            if (exception.Message.Equals("OpenService", StringComparison.OrdinalIgnoreCase))
            {
                PrintOpenServiceFailure(errorCode);
                return 3;
            }
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_DELETESERVICE_FAILED"), NssmEvent.error_string(unchecked((uint)errorCode)));
            return 5;
        }
        Console.WriteLine($"Service \"{serviceName}\" removed successfully!");
        return 0;
    }

    private static NssmServiceSnapshot Control(string serviceName, string action, NssmRegistryStore registry, string[]? startArguments = null)
    {
        serviceName = new WindowsServiceManager(registry).ResolveServiceName(serviceName);
        var arguments = ServiceArguments(serviceName, registry);
        arguments["action"] = action;
        if (startArguments is not null) arguments["startArguments"] = JsonSerializer.SerializeToNode(startArguments, NssmManagerJsonContext.Default.StringArray);
        return Elevate("nssm-manager.control", arguments).Deserialize(NssmManagerJsonContext.Default.NssmServiceSnapshot) ?? throw new InvalidDataException("Elevated Broker returned no service state.");
    }

    private static int ControlCommand(string serviceName, string action, string control, NssmRegistryStore registry, string[]? startArguments = null)
    {
        try
        {
            var serviceManager = new WindowsServiceManager(registry);
            serviceName = serviceManager.ResolveServiceName(serviceName);
            if (action.Equals("stop", StringComparison.OrdinalIgnoreCase) && serviceManager.QueryState(serviceName) == NssmServiceState.Stopped)
            {
                Console.Error.Write($"{serviceName}: {control}: {NssmEvent.error_string(1062)}");
                return 0;
            }
            Print(Control(serviceName, action, registry, startArguments), control);
            return 0;
        }
        catch (Win32Exception exception)
        {
            return PrintControlFailure(serviceName, control, exception.Message, exception.NativeErrorCode);
        }
        catch (NssmElevatedOperationException exception)
        {
            return PrintControlFailure(serviceName, control, exception.Message, exception.NativeErrorCode);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"{serviceName}: {control}: {exception.Message}");
            return 1;
        }
    }

    private static int PrintControlFailure(string serviceName, string control, string context, int? nativeErrorCode)
    {
        if (context.Equals("OpenSCManager", StringComparison.OrdinalIgnoreCase))
        {
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPEN_SERVICE_MANAGER_FAILED"));
            return 2;
        }
        if (context.Equals("OpenService", StringComparison.OrdinalIgnoreCase))
        {
            PrintOpenServiceFailure(nativeErrorCode ?? 1060);
            return 3;
        }
        if (context.StartsWith("BadControlResponse:", StringComparison.Ordinal))
        {
            var parts = context.Split(':');
            var state = parts.Length > 1 && uint.TryParse(parts[1], out var stateCode)
                ? WindowsServiceDispatcher.service_status_text(stateCode) ?? "SERVICE_UNKNOWN"
                : "SERVICE_UNKNOWN";
            NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_BAD_CONTROL_RESPONSE"), serviceName, state, control);
            return 1;
        }
        var error = nativeErrorCode.HasValue ? NssmEvent.error_string(unchecked((uint)nativeErrorCode.Value)) : context + Environment.NewLine;
        Console.Error.Write($"{serviceName}: {control}: {error}");
        return 1;
    }

    private static void Migrate(string serviceName, string executable, NssmRegistryStore registry)
    {
        serviceName = new WindowsServiceManager(registry).ResolveServiceName(serviceName);
        var arguments = ServiceArguments(serviceName, registry);
        arguments["executablePath"] = executable;
        Elevate("nssm-manager.migrate", arguments);
    }

    private static int Processes(string[] commandLine, WindowsServiceManager services)
    {
        if (commandLine.Length < 2) return NssmCore.usage(1);
        var errors = 0;
        foreach (var serviceName in commandLine[1..])
        {
            try
            {
                foreach (var process in services.GetProcessTree(serviceName)) Console.WriteLine($"{process.ProcessId,8} {new string(' ', process.Depth)}{process.ImagePath}");
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == 1060)
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_ENUMSERVICESSTATUS_FAILED"), NssmEvent.error_string(5));
                errors++;
            }
            catch (Exception exception) { Console.Error.WriteLine($"{serviceName}: {exception.Message}"); errors++; }
        }
        return errors;
    }

    private static JsonObject ServiceArguments(string serviceName, NssmRegistryStore registry) => new()
    {
        ["serviceName"] = serviceName,
        ["expectedImagePath"] = registry.ReadImagePath(serviceName)
    };

    private static JsonNode ElevateForService(string operation, string serviceName, NssmRegistryStore registry) => Elevate(operation, ServiceArguments(serviceName, registry));
    private static JsonNode Elevate(string operation, JsonObject arguments) => NssmElevatedClient.ExecuteAsync(operation, arguments).GetAwaiter().GetResult();
    private static JsonNode Elevate(string operation, JsonObject arguments, char[]? password) => NssmElevatedClient.ExecuteAsync(operation, arguments, password).GetAwaiter().GetResult();

    private static void Print(NssmServiceSnapshot snapshot, string control) =>
        Console.Write($"{snapshot.Name}: {control}: {NssmEvent.error_string(0)}");
    private static int Status(string[] commandLine, WindowsServiceManager services, bool returnStatus)
    {
        var serviceName = Service(commandLine);
        try
        {
            var state = services.QueryState(serviceName);
            Console.WriteLine(StatusText(state));
            return returnStatus ? (int)state : 0;
        }
        catch (Win32Exception exception)
        {
            if (exception.Message.Equals("OpenSCManager", StringComparison.OrdinalIgnoreCase))
            {
                NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPEN_SERVICE_MANAGER_FAILED"));
                return returnStatus ? 0 : 2;
            }
            if (exception.Message.Equals("OpenService", StringComparison.OrdinalIgnoreCase))
            {
                PrintOpenServiceFailure(exception.NativeErrorCode);
                return returnStatus ? 0 : 3;
            }
            Console.Error.Write($"{serviceName}: {NssmEvent.error_string(unchecked((uint)exception.NativeErrorCode))}");
            return returnStatus ? 0 : 1;
        }
    }
    private static void PrintOpenServiceFailure(int error)
    {
        NssmEvent.print_message(Console.Error, NssmEvent.message_id("NSSM_MESSAGE_OPENSERVICE_FAILED"), NssmEvent.error_string(unchecked((uint)error)));
    }
    private static string StatusText(NssmServiceState state) => state switch
    {
        NssmServiceState.Stopped => "SERVICE_STOPPED",
        NssmServiceState.StartPending => "SERVICE_START_PENDING",
        NssmServiceState.StopPending => "SERVICE_STOP_PENDING",
        NssmServiceState.Running => "SERVICE_RUNNING",
        NssmServiceState.ContinuePending => "SERVICE_CONTINUE_PENDING",
        NssmServiceState.PausePending => "SERVICE_PAUSE_PENDING",
        NssmServiceState.Paused => "SERVICE_PAUSED",
        _ => "SERVICE_UNKNOWN"
    };
    private static string Service(string[] commandLine) { RequireService(commandLine); return commandLine[1]; }
    private static void RequireService(string[] commandLine) { if (commandLine.Length < 2) throw new ArgumentException("Service name is required."); }
}
