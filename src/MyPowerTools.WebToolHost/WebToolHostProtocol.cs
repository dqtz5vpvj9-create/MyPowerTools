using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPowerTools.WebToolHost;

internal sealed record HostCommand
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("clipX")]
    public int ClipX { get; init; }

    [JsonPropertyName("clipY")]
    public int ClipY { get; init; }

    [JsonPropertyName("clipWidth")]
    public int ClipWidth { get; init; }

    [JsonPropertyName("clipHeight")]
    public int ClipHeight { get; init; }

    [JsonPropertyName("direction")]
    public string Direction { get; init; } = "";

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

internal sealed record HostEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("pid")] int ProcessId,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion = 1);

internal static class WebToolHostProtocol
{
    public const int MaximumFrameLength = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly object OutputLock = new();

    public static HostCommand? ParseCommand(string line)
    {
        if (line.Length > MaximumFrameLength)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<HostCommand>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteState(string state, string message = "", string phase = "")
    {
        WritePayload(
            new HostEvent("state", state, message, phase, Environment.ProcessId),
            JsonOptions);
    }

    public static void WriteShortcut(string gesture)
    {
        WritePayload(new
        {
            type = "shortcut",
            gesture,
            pid = Environment.ProcessId,
            protocolVersion = 1
        }, JsonOptions);
    }

    public static void WriteFocusMove(string direction)
    {
        WritePayload(new
        {
            type = "focusMove",
            direction,
            pid = Environment.ProcessId,
            protocolVersion = 1
        }, JsonOptions);
    }

    public static void WriteBridgeRequest(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        WritePayload(new
        {
            type = "bridgeRequest",
            payload = document.RootElement.Clone(),
            pid = Environment.ProcessId,
            protocolVersion = 1
        }, JsonOptions);
    }

    private static void WritePayload<T>(T value, JsonSerializerOptions options)
    {
        var payload = JsonSerializer.Serialize(value, options);
        lock (OutputLock)
        {
            Console.Out.WriteLine(payload);
            Console.Out.Flush();
        }
    }
}
