using System.Text.Json;
using System.Text.Json.Nodes;
using NssmManager.Compatibility;
using NssmManager.Contracts;
using NssmManager.Runtime;
using NssmManager.Windows;

return await RuntimeProgram.RunAsync();

internal static class RuntimeProgram
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private static readonly HashSet<string> Commands =
    [
        "nssm-manager.health", "nssm-manager.list", "nssm-manager.get", "nssm-manager.validate",
        "nssm-manager.install", "nssm-manager.apply", "nssm-manager.remove", "nssm-manager.control",
        "nssm-manager.migrate", "nssm-manager.rollback"
    ];

    public static async Task<int> RunAsync()
    {
        var id = "invalid";
        try
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false) ?? throw new InvalidDataException("Missing JSON-RPC request.");
            if (line.Length > 1024 * 1024) throw new InvalidDataException("JSON-RPC request exceeds 1 MiB.");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            id = root.GetProperty("id").GetString() ?? "invalid";
            if (root.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("jsonrpc must be 2.0.");
            var command = root.GetProperty("commandId").GetString() ?? "";
            if (!Commands.Contains(command)) throw new InvalidDataException($"Unknown command '{command}'.");
            var arguments = root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object ? JsonNode.Parse(argsElement.GetRawText())!.AsObject() : new JsonObject();
            var payload = await ExecuteAsync(command, arguments).ConfigureAwait(false);
            Write(id, "ready", payload, null);
        }
        catch (Exception exception) { Write(id, "failed", null, new JsonObject { ["code"] = ErrorCode(exception), ["message"] = exception.Message }); }
        return 0;
    }

    private static async Task<JsonNode> ExecuteAsync(string command, JsonObject arguments)
    {
        var registry = new NssmRegistryStore();
        var services = new WindowsServiceManager(registry);
        if (command is "nssm-manager.install" or "nssm-manager.apply" or "nssm-manager.remove" or "nssm-manager.control" or "nssm-manager.migrate" or "nssm-manager.rollback")
        {
            var serviceName = command is "nssm-manager.install" or "nssm-manager.apply"
                ? ReadConfiguration(arguments).Name
                : RequiredString(arguments, "serviceName");
            if (command is not "nssm-manager.install") arguments["expectedImagePath"] = registry.ReadImagePath(serviceName);
            if (command is "nssm-manager.install" or "nssm-manager.migrate") arguments["executablePath"] ??= NssmElevatedClient.ResolveManagedExecutable();
            if (arguments.ContainsKey("password")) throw new InvalidDataException("Passwords must use the protected Broker pipe transport.");
            return await NssmElevatedClient.ExecuteAsync(command, arguments).ConfigureAwait(false);
        }
        JsonNode payload = command switch
        {
            "nssm-manager.health" => new JsonObject { ["platform"] = "windows", ["version"] = "2.24.101", ["elevated"] = IsElevated() },
            "nssm-manager.list" => JsonSerializer.SerializeToNode(services.List(), Json)!,
            "nssm-manager.get" => JsonSerializer.SerializeToNode(services.ReadConfiguration(RequiredString(arguments, "serviceName")), Json)!,
            "nssm-manager.validate" => Validate(arguments),
            _ => throw new InvalidOperationException("Command dispatch failed.")
        };
        return payload;
    }

    private static JsonNode Validate(JsonObject arguments)
    {
        var configuration = ReadConfiguration(arguments);
        NssmRegistryStore.ValidateServiceName(configuration.Name);
        if (string.IsNullOrWhiteSpace(configuration.Application)) throw new ArgumentException("Application is required.");
        return new JsonObject { ["valid"] = true, ["configuration"] = JsonSerializer.SerializeToNode(configuration, Json) };
    }

    private static NssmServiceConfiguration ReadConfiguration(JsonObject arguments) => arguments["configuration"]?.Deserialize<NssmServiceConfiguration>(Json) ?? throw new ArgumentException("configuration is required.");
    private static string RequiredString(JsonObject arguments, string name) { var value = arguments[name]?.GetValue<string>() ?? ""; return string.IsNullOrWhiteSpace(value) || value.Length > 4096 ? throw new ArgumentException($"{name} is invalid.") : value; }
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    private static string ErrorCode(Exception exception) => exception is UnauthorizedAccessException ? "permission.required" : exception is ArgumentException or InvalidDataException ? "validation.failed" : exception is System.ComponentModel.Win32Exception native ? $"win32.{native.NativeErrorCode}" : "runtime.failed";
    private static void Write(string id, string state, JsonNode? payload, JsonObject? error) => Console.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["state"] = state, ["payload"] = payload, ["error"] = error } }.ToJsonString(Json));
}
