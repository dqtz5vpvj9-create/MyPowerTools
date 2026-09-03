using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

/// <summary>
/// Sends a shortcut to the frontmost application by synthesizing CGEvents, the macOS counterpart
/// of the Windows SendInput path. Modifiers are pressed in order with the matching CGEventFlags,
/// the key is tapped, then the modifiers are released in reverse.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeyboardShortcutService : IKeyboardShortcutService
{
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const int EventSourceStatePrivate = -1;
    private const uint HidEventTap = 0;

    public Task<KeyboardShortcutResult> SendAsync(string gesture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!KeyboardShortcutGesture.TryParse(gesture, requireModifier: false, out var parsed, out var parseError))
        {
            return Task.FromResult(new KeyboardShortcutResult(false, "validation-failed", parseError));
        }

        if (!MacKeyCodes.TryMapKey(parsed!.VirtualKey, out var macKeyCode))
        {
            return Task.FromResult(new KeyboardShortcutResult(
                false,
                "validation-failed",
                $"Keyboard shortcut '{parsed.NormalizedGesture}' has no macOS virtual key code."));
        }

        if (!MacAccessibility.IsTrusted())
        {
            return Task.FromResult(new KeyboardShortcutResult(
                false,
                "permission-required",
                $"macOS 拒绝合成按键事件，无法发送 '{parsed.NormalizedGesture}'：{MacAccessibility.PermissionHint}"));
        }

        return Task.FromResult(Send(parsed, macKeyCode));
    }

    private static KeyboardShortcutResult Send(KeyboardShortcutGesture gesture, ushort macKeyCode)
    {
        var modifiers = MacKeyCodes.ModifierSequence(gesture.Modifiers);
        var source = CGEventSourceCreate(EventSourceStatePrivate);
        try
        {
            ulong flags = 0;
            foreach (var modifier in modifiers)
            {
                flags |= modifier.Flag;
                if (!Post(source, modifier.KeyCode, keyDown: true, flags))
                {
                    return CreationFailed(gesture);
                }
            }

            if (!Post(source, macKeyCode, keyDown: true, flags) ||
                !Post(source, macKeyCode, keyDown: false, flags))
            {
                return CreationFailed(gesture);
            }

            for (var index = modifiers.Count - 1; index >= 0; index--)
            {
                flags &= ~modifiers[index].Flag;
                if (!Post(source, modifiers[index].KeyCode, keyDown: false, flags))
                {
                    return CreationFailed(gesture);
                }
            }
        }
        finally
        {
            if (source != 0)
            {
                CFRelease(source);
            }
        }

        return new KeyboardShortcutResult(true, "sent", $"Sent shortcut '{gesture.NormalizedGesture}'.");
    }

    private static bool Post(nint source, ushort macKeyCode, bool keyDown, ulong flags)
    {
        var keyboardEvent = CGEventCreateKeyboardEvent(source, macKeyCode, keyDown);
        if (keyboardEvent == 0)
        {
            return false;
        }

        CGEventSetFlags(keyboardEvent, flags);
        CGEventPost(HidEventTap, keyboardEvent);
        CFRelease(keyboardEvent);
        return true;
    }

    private static KeyboardShortcutResult CreationFailed(KeyboardShortcutGesture gesture)
    {
        return new KeyboardShortcutResult(
            false,
            "failed",
            $"CGEventCreateKeyboardEvent returned null while sending '{gesture.NormalizedGesture}'.");
    }

    [DllImport(ApplicationServices)]
    private static extern nint CGEventSourceCreate(int stateId);

    [DllImport(ApplicationServices)]
    private static extern nint CGEventCreateKeyboardEvent(
        nint source,
        ushort virtualKey,
        [MarshalAs(UnmanagedType.I1)] bool keyDown);

    [DllImport(ApplicationServices)]
    private static extern void CGEventSetFlags(nint keyboardEvent, ulong flags);

    [DllImport(ApplicationServices)]
    private static extern void CGEventPost(uint tap, nint keyboardEvent);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
