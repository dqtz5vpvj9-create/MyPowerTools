using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

/// <summary>
/// A gesture resolved for RegisterEventHotKey. Parsing stays in the shared
/// <see cref="KeyboardShortcutGesture"/> so macOS accepts exactly the gestures Windows accepts,
/// including the Win/Meta tokens, which map to Command here.
/// </summary>
public sealed record MacHotkeyGesture(uint CarbonModifiers, ushort MacKeyCode, string NormalizedGesture)
{
    public static bool TryParse(string gesture, out MacHotkeyGesture? parsed, out string error)
    {
        parsed = null;
        if (!KeyboardShortcutGesture.TryParse(gesture, requireModifier: true, out var parsedGesture, out error))
        {
            return false;
        }

        if (!MacKeyCodes.TryMapKey(parsedGesture!.VirtualKey, out var macKeyCode))
        {
            error = $"Hotkey gesture '{parsedGesture.NormalizedGesture}' has no macOS virtual key code.";
            return false;
        }

        parsed = new MacHotkeyGesture(
            MacKeyCodes.ToCarbonModifiers(parsedGesture.Modifiers),
            macKeyCode,
            parsedGesture.NormalizedGesture);
        return true;
    }
}
