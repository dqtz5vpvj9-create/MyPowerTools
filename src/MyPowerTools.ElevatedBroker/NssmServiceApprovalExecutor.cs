using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Broker;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Windows;

namespace MyPowerTools.ElevatedBroker;

internal static class NssmServiceApprovalExecutor
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> Operations =
    [
        "nssm-manager.install", "nssm-manager.apply", "nssm-manager.remove", "nssm-manager.control", "nssm-manager.migrate",
        "nssm-manager.registry-set", "nssm-manager.registry-reset", "nssm-manager.imagepath", "nssm-manager.rollback"
    ];

    public static async Task<int> ExecuteAsync(string[] commandLine, AuditLog audit)
    {
        var requestPath = GetOption(commandLine, "--request-file");
        var token = GetOption(commandLine, "--token") ?? "";
        var digest = GetOption(commandLine, "--digest") ?? "";
        var brokerHash = GetOption(commandLine, "--broker-sha256") ?? "";
        if (requestPath is null || !IsHex(token, 32) || !IsHex(digest, 64) || !IsHex(brokerHash, 64)) return 2;
        var resultPath = Path.Combine(Path.GetDirectoryName(requestPath)!, token + ".result.json");
        string operation = "invalid";
        string serviceName = "";
        try
        {
            ValidateRequestPath(requestPath, token);
            ValidateCallerIdentity(requestPath);
            var information = new FileInfo(requestPath);
            if (information.Length is <= 0 or > MaximumRequestBytes) throw new InvalidDataException("Request size is invalid.");
            var bytes = await File.ReadAllBytesAsync(requestPath).ConfigureAwait(false);
            if (!FixedEquals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), digest)) throw new InvalidDataException("Request digest mismatch.");
            VerifyBroker(brokerHash);
            var root = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 })?.AsObject() ?? throw new InvalidDataException("Request JSON is invalid.");
            ValidateRoot(root, token, brokerHash);
            operation = root["operation"]!.GetValue<string>();
            var arguments = root["arguments"]!.AsObject();
            ValidateArguments(operation, arguments);
            serviceName = ResolveServiceName(operation, arguments);
            NssmRegistryStore.ValidateServiceName(serviceName);
            var registry = new NssmRegistryStore();
            var services = new WindowsServiceManager(registry);
            ValidatePrestate(operation, arguments, registry, serviceName);
            audit.Append(NewAudit(serviceName, operation, "requested", ""));
            var payload = Execute(operation, arguments, registry, services, serviceName);
            WriteResult(resultPath, token, digest, true, "completed", payload);
            audit.Append(NewAudit(serviceName, operation, "success", RollbackSummary(operation, serviceName)));
            return 0;
        }
        catch (Exception exception)
        {
            TryWriteFailure(resultPath, token, digest, exception);
            audit.Append(NewAudit(serviceName, operation, "failed", exception.GetType().Name));
            return 5;
        }
    }

    private static JsonNode Execute(string operation, JsonObject arguments, NssmRegistryStore registry, WindowsServiceManager services, string serviceName)
    {
        switch (operation)
        {
            case "nssm-manager.install":
            {
                var configuration = ReadConfiguration(arguments);
                try
                {
                    var executable = TrustedManagedExecutable(arguments);
                    _ = NssmRegistry.create_messages(executable);
                    services.Install(configuration, executable);
                    return JsonSerializer.SerializeToNode(services.Query(serviceName), Json)!;
                }
                finally { ClearPassword(configuration); }
            }
            case "nssm-manager.apply":
            {
                var before = registry.CaptureMigrationSnapshot(serviceName, services.Query(serviceName).State);
                var configuration = ReadConfiguration(arguments);
                try
                {
                    try { services.Change(configuration); }
                    catch (Exception applyError)
                    {
                        try { RestoreSnapshot(before, registry, services); }
                        catch (Exception rollbackError) { throw new AggregateException("Service configuration failed and rollback could not be completed.", applyError, rollbackError); }
                        throw;
                    }
                    return JsonSerializer.SerializeToNode(services.Query(serviceName), Json)!;
                }
                finally { ClearPassword(configuration); }
            }
            case "nssm-manager.remove":
                services.Delete(serviceName);
                return new JsonObject { ["removed"] = serviceName };
            case "nssm-manager.control":
            {
                var action = RequiredString(arguments, "action").ToLowerInvariant();
                var startArguments = arguments["startArguments"] is null ? null : RequiredStringArray(arguments, "startArguments", allowEmpty: true);
                var result = action switch { "start" => services.Start(serviceName, startArguments), "stop" => services.Stop(serviceName), "restart" => services.Restart(serviceName, startArguments), "pause" => services.Pause(serviceName), "continue" => services.Continue(serviceName), "rotate" => services.Rotate(serviceName), _ => throw new InvalidDataException("Control action is invalid.") };
                return JsonSerializer.SerializeToNode(result, Json)!;
            }
            case "nssm-manager.migrate":
                return Migrate(arguments, registry, services, serviceName);
            case "nssm-manager.registry-set":
            {
                var parameter = RequiredString(arguments, "parameter");
                var setting = NssmSettingsTranslation.Find(parameter) ?? throw new InvalidDataException($"Unknown NSSM parameter '{parameter}'.");
                var values = RequiredStringArray(arguments, "values");
                if (setting.Name.Equals("ObjectName", StringComparison.OrdinalIgnoreCase) && arguments["passwordPipe"] is JsonValue passwordPipe)
                {
                    var password = ReadPassword(passwordPipe.GetValue<string>());
                    try
                    {
                        var secureResult = NssmSettingsTranslation.SetObjectNameSecure(serviceName,
                            OptionalString(arguments, "subparameter"), password);
                        if (secureResult < 0) throw new InvalidOperationException($"Failed to set NSSM parameter '{setting.Name}'.");
                        return new JsonObject { ["updated"] = serviceName, ["result"] = secureResult, ["parameter"] = setting.Name };
                    }
                    finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }
                }
                var text = (setting.Additional & NssmSettingsTranslation.AdditionalCrlf) != 0
                    ? string.Join("\r\n", values)
                    : string.Join(' ', values);
                var result = NssmSettingsTranslation.Set(serviceName, setting.Name, OptionalString(arguments, "subparameter"), NssmSettingValue.FromString(text));
                if (result < 0) throw new InvalidOperationException($"Failed to set NSSM parameter '{setting.Name}'.");
                return new JsonObject { ["updated"] = serviceName, ["result"] = result, ["parameter"] = setting.Name };
            }
            case "nssm-manager.registry-reset":
            {
                var parameter = RequiredString(arguments, "parameter");
                var setting = NssmSettingsTranslation.Find(parameter) ?? throw new InvalidDataException($"Unknown NSSM parameter '{parameter}'.");
                var result = NssmSettingsTranslation.Set(serviceName, setting.Name, OptionalString(arguments, "subparameter"), null);
                if (result < 0) throw new InvalidOperationException($"Failed to reset NSSM parameter '{setting.Name}'.");
                return new JsonObject { ["reset"] = serviceName, ["result"] = result, ["parameter"] = setting.Name };
            }
            case "nssm-manager.imagepath":
            {
                var imagePath = TrustedServiceImage(arguments);
                services.ChangeImagePath(serviceName, imagePath);
                return new JsonObject { ["imagePath"] = registry.ReadImagePath(serviceName) };
            }
            case "nssm-manager.rollback":
                return Rollback(registry, services, serviceName);
            default:
                throw new InvalidDataException("Operation is not allowed.");
        }
    }

    private static JsonNode Migrate(JsonObject arguments, NssmRegistryStore registry, WindowsServiceManager services, string serviceName)
    {
        if (registry.ReadImagePath(serviceName).Contains("nssm-manager.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Service is already hosted by nssm-manager.exe; the existing rollback snapshot was preserved.");
        var executable = TrustedManagedExecutable(arguments);
        _ = NssmRegistry.create_messages(executable);
        var state = services.Query(serviceName).State;
        var snapshot = registry.CaptureMigrationSnapshot(serviceName, state);
        var snapshotPath = MigrationSnapshotPath(serviceName, createDirectory: true);
        var temporary = snapshotPath + ".tmp";
        if (File.Exists(snapshotPath)) EnsureNoReparsePoint(snapshotPath);
        if (File.Exists(temporary)) { EnsureNoReparsePoint(temporary); File.Delete(temporary); }
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, Json);
                stream.Flush(true);
            }
            File.Move(temporary, snapshotPath, true);
        }
        finally { try { File.Delete(temporary); } catch { } }
        if (state != NssmServiceState.Stopped) services.Stop(serviceName);
        try
        {
            services.Migrate(serviceName, executable);
            var result = RestoreServiceState(serviceName, state, services);
            return JsonSerializer.SerializeToNode(result, Json)!;
        }
        catch (Exception migrationError)
        {
            try { RestoreSnapshot(snapshot, registry, services); }
            catch (Exception rollbackError) { throw new AggregateException("Service migration failed and rollback could not be completed.", migrationError, rollbackError); }
            throw;
        }
    }

    private static JsonNode Rollback(NssmRegistryStore registry, WindowsServiceManager services, string serviceName)
    {
        var snapshotPath = MigrationSnapshotPath(serviceName, createDirectory: false);
        EnsureNoReparsePoint(snapshotPath);
        using var stream = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var snapshot = JsonSerializer.Deserialize<NssmMigrationSnapshot>(stream, Json) ?? throw new InvalidDataException("Migration snapshot is invalid.");
        if (!snapshot.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Migration snapshot service name mismatch.");
        RestoreSnapshot(snapshot, registry, services);
        return JsonSerializer.SerializeToNode(services.Query(serviceName), Json)!;
    }

    private static void RestoreSnapshot(NssmMigrationSnapshot snapshot, NssmRegistryStore registry, WindowsServiceManager services)
    {
        services.Stop(snapshot.ServiceName);
        services.Change(snapshot.Configuration);
        services.ChangeImagePath(snapshot.ServiceName, snapshot.OriginalImagePath);
        registry.RestoreMigrationSnapshot(snapshot);
        RestoreServiceState(snapshot.ServiceName, snapshot.State, services);
        if (!string.Equals(registry.ReadImagePath(snapshot.ServiceName), snapshot.OriginalImagePath, StringComparison.Ordinal)) throw new InvalidOperationException("ImagePath rollback verification failed.");
    }

    private static NssmServiceSnapshot RestoreServiceState(string serviceName, NssmServiceState state, WindowsServiceManager services)
    {
        if (state is NssmServiceState.Running or NssmServiceState.StartPending or NssmServiceState.ContinuePending) return services.Start(serviceName);
        if (state is NssmServiceState.Paused or NssmServiceState.PausePending)
        {
            try { _ = services.Start(serviceName); }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("BadControlResponse:7:", StringComparison.Ordinal)) { }
            var deadline = Environment.TickCount64 + 30000;
            while (services.QueryState(serviceName) != NssmServiceState.Paused)
            {
                if (Environment.TickCount64 >= deadline) throw new TimeoutException($"Service '{serviceName}' did not return to its throttled paused state.");
                Thread.Sleep(100);
            }
            return services.Query(serviceName);
        }
        return services.Query(serviceName);
    }

    private static void ValidateRoot(JsonObject root, string token, string brokerHash)
    {
        var expected = new[] { "schemaVersion", "token", "moduleId", "operation", "createdAt", "expiresAt", "arguments", "broker" };
        if (root.Count != expected.Length || expected.Any(name => !root.ContainsKey(name))) throw new InvalidDataException("Request fields are invalid.");
        if (root["schemaVersion"]?.GetValue<int>() != 1 || root["token"]?.GetValue<string>() != token || root["moduleId"]?.GetValue<string>() != "nssm-manager") throw new InvalidDataException("Request identity is invalid.");
        var operation = root["operation"]?.GetValue<string>() ?? "";
        if (!Operations.Contains(operation)) throw new InvalidDataException("Operation is not allowed.");
        if (!DateTimeOffset.TryParse(root["createdAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) || !DateTimeOffset.TryParse(root["expiresAt"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires) || created > DateTimeOffset.UtcNow.AddMinutes(1) || expires <= DateTimeOffset.UtcNow || expires - created > TimeSpan.FromMinutes(5)) throw new InvalidDataException("Request lifetime is invalid.");
        var broker = root["broker"]?.AsObject() ?? throw new InvalidDataException("Broker identity is missing.");
        if (broker.Count != 2 || !FixedEquals(broker["sha256"]?.GetValue<string>() ?? "", brokerHash) || !Path.GetFullPath(broker["path"]?.GetValue<string>() ?? "").Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Broker identity is invalid.");
        if (root["arguments"] is not JsonObject) throw new InvalidDataException("Arguments must be an object.");
    }

    private static void ValidatePrestate(string operation, JsonObject arguments, NssmRegistryStore registry, string serviceName)
    {
        if (operation == "nssm-manager.install") { if (registry.Exists(serviceName)) throw new InvalidOperationException("Service already exists."); return; }
        var expected = RequiredString(arguments, "expectedImagePath");
        if (!string.Equals(registry.ReadImagePath(serviceName), expected, StringComparison.Ordinal)) throw new InvalidOperationException("Service ImagePath changed after approval.");
    }

    private static void ValidateArguments(string operation, JsonObject arguments)
    {
        var allowed = operation switch
        {
            "nssm-manager.install" => new HashSet<string>(["configuration", "executablePath", "passwordPipe"], StringComparer.Ordinal),
            "nssm-manager.apply" => new HashSet<string>(["configuration", "expectedImagePath", "passwordPipe"], StringComparer.Ordinal),
            "nssm-manager.remove" => new HashSet<string>(["serviceName", "expectedImagePath"], StringComparer.Ordinal),
            "nssm-manager.control" => new HashSet<string>(["serviceName", "action", "startArguments", "expectedImagePath"], StringComparer.Ordinal),
            "nssm-manager.migrate" => new HashSet<string>(["serviceName", "executablePath", "expectedImagePath"], StringComparer.Ordinal),
            "nssm-manager.registry-set" => new HashSet<string>(["serviceName", "parameter", "subparameter", "values", "expectedImagePath", "passwordPipe"], StringComparer.Ordinal),
            "nssm-manager.registry-reset" => new HashSet<string>(["serviceName", "parameter", "subparameter", "expectedImagePath"], StringComparer.Ordinal),
            "nssm-manager.imagepath" => new HashSet<string>(["serviceName", "imagePath", "expectedImagePath"], StringComparer.Ordinal),
            "nssm-manager.rollback" => new HashSet<string>(["serviceName", "expectedImagePath"], StringComparer.Ordinal),
            _ => throw new InvalidDataException("Operation is not allowed.")
        };
        var unknown = arguments.Select(item => item.Key).FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null) throw new InvalidDataException($"Argument '{unknown}' is not allowed for {operation}.");
    }

    private static string TrustedManagedExecutable(JsonObject arguments)
    {
        var sourcePath = Path.GetFullPath(RequiredString(arguments, "executablePath"));
        if (!File.Exists(sourcePath) || !Path.GetFileName(sourcePath).Equals("nssm-manager.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed executable path is invalid.");
        EnsureNoReparsePoint(sourcePath);
        return MaterializeProtectedExecutable(sourcePath);
    }

    private static string TrustedServiceImage(JsonObject arguments)
    {
        var values = ParseCommandLine(RequiredString(arguments, "imagePath"));
        if (values.Length == 0) throw new InvalidDataException("Service ImagePath has no executable.");
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(values[0]));
        if (!File.Exists(path)) throw new InvalidDataException("Service image path does not exist.");
        EnsureNoReparsePoint(path);
        if (!WindowsProtectedExecutable.IsProtectedLocation(path, out var reason)) throw new InvalidDataException($"Service image path is not ACL-protected: {reason}");
        values[0] = path;
        return string.Join(' ', values.Select(QuoteWindowsArgument));
    }

    private static string MaterializeProtectedExecutable(string sourcePath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NSSM service execution requires Windows.");
        var destinationDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyPowerTools", "bin", "nssm-manager", "2.24.101");
        EnsureNoReparsePointOnExistingAncestor(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        EnsureNoReparsePoint(destinationDirectory);
        ProtectDirectory(destinationDirectory);
        var destinationPath = Path.Combine(destinationDirectory, "nssm-manager.exe");
        var temporaryPath = Path.Combine(destinationDirectory, ".nssm-manager-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sourceHash = SHA256.HashData(source);
        source.Position = 0;
        try
        {
            // A managed service that is currently running holds nssm-manager.exe as its image, so
            // the replace below fails with a sharing violation and blocks every later install or
            // migration. The already-materialized copy is the same host, so nothing has to move.
            if (ProtectedFileStaging.AlreadyMatches(destinationPath, sourceHash))
            {
                ValidateProtectedExecutable(destinationPath, destinationDirectory);
                return destinationPath;
            }

            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(true);
            }
            using (var verification = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, SHA256.HashData(verification))) throw new InvalidDataException("Protected NSSM executable copy verification failed.");
            File.Move(temporaryPath, destinationPath, true);
            ValidateProtectedExecutable(destinationPath, destinationDirectory);
            return destinationPath;
        }
        finally { try { File.Delete(temporaryPath); } catch { } CryptographicOperations.ZeroMemory(sourceHash); }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateProtectedExecutable(string executablePath, string directoryPath)
    {
        EnsureNoReparsePoint(executablePath);
        var security = new DirectoryInfo(directoryPath).GetAccessControl(AccessControlSections.Access);
        if (!security.AreAccessRulesProtected) throw new UnauthorizedAccessException("Protected NSSM executable directory still inherits writable ACLs.");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var dangerous = FileSystemRights.WriteData |
                        FileSystemRights.AppendData |
                        FileSystemRights.WriteExtendedAttributes |
                        FileSystemRights.WriteAttributes |
                        FileSystemRights.Delete |
                        FileSystemRights.DeleteSubdirectoriesAndFiles |
                        FileSystemRights.ChangePermissions |
                        FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && users.Equals(rule.IdentityReference) && (rule.FileSystemRights & dangerous) != 0)
                throw new UnauthorizedAccessException("Protected NSSM executable directory grants write access to standard users.");
    }
    private static void EnsureNoReparsePointOnExistingAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current)) current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Service host directory has no existing ancestor.");
        EnsureNoReparsePoint(current);
    }

    private static NssmServiceConfiguration ReadConfiguration(JsonObject arguments)
    {
        var configuration = ReadConfigurationWithoutPassword(arguments);
        return arguments["passwordPipe"] is JsonValue value
            ? configuration with { ServicePassword = ReadPassword(value.GetValue<string>()) }
            : configuration;
    }
    private static NssmServiceConfiguration ReadConfigurationWithoutPassword(JsonObject arguments) => arguments["configuration"]?.Deserialize<NssmServiceConfiguration>(Json) ?? throw new InvalidDataException("Configuration is missing.");
    private static char[] ReadPassword(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 128 || !pipeName.StartsWith("mpt-nssm-secret-", StringComparison.Ordinal)) throw new InvalidDataException("Password pipe name is invalid.");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
        pipe.Connect(30000);
        Span<byte> lengthBytes = stackalloc byte[4];
        pipe.ReadExactly(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length is < 0 or > 4096) throw new InvalidDataException("Service password exceeds the protected transport limit.");
        pipe.ReadExactly(lengthBytes);
        var protectedLength = BitConverter.ToInt32(lengthBytes);
        if (protectedLength is < 16 or > 4112 || protectedLength % 16 != 0 || protectedLength < length) throw new InvalidDataException("Protected password payload length is invalid.");
        var secret = new byte[protectedLength];
        try
        {
            pipe.ReadExactly(secret);
            if (!CryptUnprotectMemory(secret, (uint)secret.Length, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectMemory");
            var encoding = new UTF8Encoding(false, true);
            var characters = new char[encoding.GetCharCount(secret, 0, length) + 1];
            _ = encoding.GetChars(secret, 0, length, characters, 0);
            return characters;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private static void ClearPassword(NssmServiceConfiguration configuration)
    {
        if (configuration.ServicePassword is { Length: > 0 } password)
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
    }
    private static string ResolveServiceName(string operation, JsonObject arguments) => operation is "nssm-manager.install" or "nssm-manager.apply" ? ReadConfigurationWithoutPassword(arguments).Name : RequiredString(arguments, "serviceName");
    private static string RequiredString(JsonObject root, string name) { var value = root[name]?.GetValue<string>() ?? ""; return string.IsNullOrWhiteSpace(value) || value.Length > 32768 ? throw new InvalidDataException($"{name} is invalid.") : value; }
    private static string? OptionalString(JsonObject root, string name) => root[name] is null ? null : root[name]!.GetValue<string>();
    private static string[] RequiredStringArray(JsonObject root, string name, bool allowEmpty = false)
    {
        var values = root[name]?.Deserialize<string[]>(Json) ?? throw new InvalidDataException($"{name} is missing.");
        if ((!allowEmpty && values.Length == 0) || values.Length > 4096 || values.Any(value => value.Length > 32768 || value.IndexOf('\0') >= 0)) throw new InvalidDataException($"{name} is invalid.");
        return values;
    }
    private static string MigrationSnapshotPath(string serviceName, bool createDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NSSM migration snapshots require Windows.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyPowerTools", "state", "tools", "nssm-manager", "migrations");
        if (createDirectory)
        {
            EnsureNoReparsePointOnExistingAncestor(directory);
            Directory.CreateDirectory(directory);
            EnsureNoReparsePoint(directory);
            ProtectDirectory(directory);
        }
        else
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("NSSM migration snapshot directory does not exist.");
            EnsureNoReparsePoint(directory);
        }
        return Path.Combine(directory, serviceName + ".json");
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CommandLineToArgvW");
        try
        {
            var values = new string[count];
            for (var index = 0; index < count; index++) values[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "";
            return values;
        }
        finally { LocalFree(pointer); }
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); slashes = 0; continue; }
            result.Append('\\', slashes); slashes = 0; result.Append(character);
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }

    private static void ValidateRequestPath(string path, string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NSSM service approval requires Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        var caller = identity.User ?? throw new UnauthorizedAccessException("The elevated caller SID is unavailable.");
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "broker-requests", "nssm-manager", caller.Value));
        path = Path.GetFullPath(path);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != token + ".json" || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Request path is invalid.");
        EnsureNoReparsePoint(root);
    }

    private static void ValidateCallerIdentity(string requestPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NSSM service approval requires Windows.");
        var owner = new FileInfo(requestPath).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (owner is null || identity.User is null || !owner.Equals(identity.User)) throw new UnauthorizedAccessException("Request owner does not match the elevated caller identity.");
    }

    private static void VerifyBroker(string expectedHash)
    {
        var path = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Broker path is unavailable."));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!FixedEquals(hash, expectedHash)) throw new InvalidDataException("Broker hash mismatch.");
    }

    private static void WriteResult(string path, string token, string digest, bool success, string message, JsonNode payload)
    {
        var root = new JsonObject { ["schemaVersion"] = 1, ["token"] = token, ["requestDigest"] = digest, ["success"] = success, ["message"] = message, ["payload"] = payload };
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(root.ToJsonString(Json)); writer.Flush(); stream.Flush(true);
    }
    private static void TryWriteFailure(string path, string token, string digest, Exception exception)
    {
        try
        {
            var payload = new JsonObject { ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name };
            if (exception is Win32Exception win32) payload["nativeErrorCode"] = win32.NativeErrorCode;
            WriteResult(path, token, digest, false, exception.Message, payload);
        }
        catch { }
    }
    private static string? GetOption(string[] commandLine, string name) { for (var index = 0; index + 1 < commandLine.Length; index++) if (commandLine[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return commandLine[index + 1]; return null; }
    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left.ToLowerInvariant()), Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
    private static void EnsureNoReparsePoint(string path) { var item = File.Exists(path) ? new FileInfo(path) as FileSystemInfo : new DirectoryInfo(path); while (item is not null) { if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Path contains a reparse point."); item = item switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null }; } }
    private static BrokerAuditEntry NewAudit(string service, string operation, string result, string rollback) => new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "nssm-manager", operation, "elevated", service, "Approved NSSM service operation", true, result, rollback);
    private static string RollbackSummary(string operation, string service) => operation switch { "nssm-manager.install" => $"delete {service}", "nssm-manager.apply" => $"restore configuration for {service}", "nssm-manager.migrate" or "nssm-manager.rollback" or "nssm-manager.imagepath" => $"restore ImagePath for {service}", _ => "" };

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectMemory([In, Out] byte[] data, uint dataLength, uint flags);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
