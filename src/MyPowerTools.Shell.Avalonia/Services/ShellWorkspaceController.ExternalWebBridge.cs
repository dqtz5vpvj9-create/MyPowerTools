using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using MyPowerTools.Platform;
using MyPowerTools.Platform.Abstractions;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private static readonly Lazy<ISecretStore> PlatformSecrets = new(
        () => PlatformPackFactory.Create().Secrets);

    private async Task<string> HandleExternalWebBridgeRequestAsync(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route,
        string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "" : "";
        var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "" : "";
        try
        {
            JsonNode? result = type switch
            {
                "command.invoke" => await InvokeBridgeCommandAsync(descriptor, route, root),
                "settings.get" => ReadBridgeSetting(descriptor, root),
                "settings.set" => WriteBridgeSetting(descriptor, root),
                "secrets.get" => await ReadBridgeSecretAsync(descriptor, root),
                "secrets.set" => await WriteBridgeSecretAsync(descriptor, root),
                "navigation.openExternal" => OpenBridgeExternal(root),
                "event.publish" => await PublishBridgeEventAsync(descriptor, root),
                _ => throw new InvalidOperationException($"Unsupported WebBridge request '{type}'.")
            };
            return new JsonObject
            {
                ["version"] = "1.0",
                ["id"] = id,
                ["type"] = type + ".result",
                ["payload"] = result
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["version"] = "1.0",
                ["id"] = id,
                ["type"] = type + ".result",
                ["error"] = new JsonObject
                {
                    ["code"] = "bridge.request.failed",
                    ["message"] = ex.GetBaseException().Message
                }
            }.ToJsonString();
        }
    }

    private async Task<JsonNode?> InvokeBridgeCommandAsync(
        HostProto.ToolDescriptor descriptor,
        HostProto.ToolRoute route,
        JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var commandId = payload.GetProperty("commandId").GetString()
                        ?? throw new InvalidDataException("commandId is required.");
        var command = descriptor.Commands.FirstOrDefault(item =>
                          string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase))
                      ?? throw new KeyNotFoundException($"Command '{commandId}' is not declared by {descriptor.ToolId}.");
        return JsonValue.Create(await InvokeExternalToolCommandAsync(
            descriptor,
            route,
            command,
            CancellationToken.None));
    }

    private static JsonNode? ReadBridgeSetting(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var name = root.GetProperty("payload").GetProperty("name").GetString() ?? "";
        var values = LoadBridgeSettings(descriptor);
        return values[name]?.DeepClone();
    }

    private static JsonNode? WriteBridgeSetting(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var name = payload.GetProperty("name").GetString()
                   ?? throw new InvalidDataException("Setting name is required.");
        var values = LoadBridgeSettings(descriptor);
        values[name] = JsonNode.Parse(payload.GetProperty("value").GetRawText());
        var path = descriptor.Settings?.ValuesPath
                   ?? throw new InvalidDataException("Tool settings are not configured.");
        File.WriteAllText(path, values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return null;
    }

    private static JsonObject LoadBridgeSettings(HostProto.ToolDescriptor descriptor)
    {
        var path = descriptor.Settings?.ValuesPath
                   ?? throw new InvalidDataException("Tool settings are not configured.");
        return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject values
            ? values
            : new JsonObject();
    }

    private static async Task<JsonNode?> ReadBridgeSecretAsync(
        HostProto.ToolDescriptor descriptor,
        JsonElement root)
    {
        var name = ValidateBridgeSecretName(descriptor, root);
        var value = await PlatformSecrets.Value.ReadAsync(
            SecretReference.Create(descriptor.ToolId, name),
            CancellationToken.None);
        return value is null ? null : JsonValue.Create(value);
    }

    private static async Task<JsonNode?> WriteBridgeSecretAsync(
        HostProto.ToolDescriptor descriptor,
        JsonElement root)
    {
        var name = ValidateBridgeSecretName(descriptor, root);
        var value = root.GetProperty("payload").GetProperty("value").GetString() ?? "";
        await PlatformSecrets.Value.SaveAsync(
            descriptor.ToolId,
            name,
            value,
            CancellationToken.None);
        return null;
    }

    private static string ValidateBridgeSecretName(HostProto.ToolDescriptor descriptor, JsonElement root)
    {
        var name = root.GetProperty("payload").GetProperty("name").GetString() ?? "";
        if (descriptor.Settings is null || !descriptor.Settings.Secrets.Contains(name))
        {
            throw new UnauthorizedAccessException($"Secret '{name}' is not declared by {descriptor.ToolId}.");
        }
        return name;
    }

    private static JsonNode? OpenBridgeExternal(JsonElement root)
    {
        var value = root.GetProperty("payload").GetProperty("url").GetString() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("External navigation requires an HTTP(S) URL without embedded credentials.");
        }
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return null;
    }

    private async Task<JsonNode?> PublishBridgeEventAsync(
        HostProto.ToolDescriptor descriptor,
        JsonElement root)
    {
        var payload = root.GetProperty("payload");
        var topic = payload.TryGetProperty("type", out var topicNode)
            ? topicNode.GetString() ?? "event"
            : "event";
        var eventPayload = payload.TryGetProperty("payload", out var eventNode) &&
                           eventNode.ValueKind == JsonValueKind.Object
            ? eventNode.GetRawText()
            : "{}";
        var eventSeq = await _toolEvents.PublishAsync(descriptor.ToolId, topic, eventPayload);
        SetStatus($"{descriptor.Title}: {topic} (event {eventSeq})");
        return JsonValue.Create(eventSeq);
    }
}
