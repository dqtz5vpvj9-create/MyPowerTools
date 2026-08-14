using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

public sealed record WindowsHotkeyGesture(uint Modifiers, uint VirtualKey, string NormalizedGesture)
{
    public static bool TryParse(string gesture, out WindowsHotkeyGesture? parsed, out string error)
    {
        if (!KeyboardShortcutGesture.TryParse(gesture, requireModifier: true, out var parsedGesture, out error))
        {
            parsed = null;
            return false;
        }

        parsed = new WindowsHotkeyGesture(
            parsedGesture!.Modifiers,
            parsedGesture.VirtualKey,
            parsedGesture.NormalizedGesture);
        return true;
    }
}
