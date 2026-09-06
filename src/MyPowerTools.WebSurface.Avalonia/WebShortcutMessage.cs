using System.Text.Json;

namespace MyPowerTools.WebSurface.Avalonia;

/// <summary>Decodes the input context carried by both native WebView bridges.</summary>
public static class WebShortcutMessage
{
    public const int MaximumMessageLength = 4096;
    public const int MaximumGestureLength = 32;

    /// <summary>
    /// Read the current gesture/context envelope or a legacy bare gesture. Legacy messages
    /// conservatively retain text-input ownership because they cannot describe the focused element.
    /// </summary>
    public static bool TryRead(string? message, out string gesture, out bool textInput)
    {
        gesture = "";
        textInput = true;
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaximumMessageLength) return false;
        var trimmed = message.Trim();
        if (!trimmed.StartsWith('{'))
        {
            if (trimmed.Length > MaximumGestureLength) return false;
            gesture = trimmed;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("gesture", out var key) || key.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("textInput", out var input) ||
                input.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            var candidate = key.GetString();
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaximumGestureLength) return false;
            gesture = candidate;
            textInput = input.GetBoolean();
            return true;
        }
        catch (JsonException) { return false; }
    }
}
