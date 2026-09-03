using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

/// <summary>
/// Translates the shared gesture parser output into the two encodings macOS needs.
/// <see cref="KeyboardShortcutGesture"/> normalizes every gesture to Windows virtual key
/// codes, so both the Carbon hotkey registry and synthesized CGEvents go through this table
/// instead of re-parsing gesture strings. Modifiers stay literal: Ctrl is the Control key and
/// Win/Meta is Command, so a rebound gesture means on macOS exactly what it says.
/// </summary>
internal static class MacKeyCodes
{
    /// <summary>Carbon modifier masks from <c>Events.h</c>, used by RegisterEventHotKey.</summary>
    internal const uint CmdKeyMask = 0x0100;
    internal const uint ShiftKeyMask = 0x0200;
    internal const uint OptionKeyMask = 0x0800;
    internal const uint ControlKeyMask = 0x1000;

    /// <summary>CGEventFlags from <c>CGEventTypes.h</c>, used when synthesizing key events.</summary>
    internal const ulong FlagShift = 0x00020000;
    internal const ulong FlagControl = 0x00040000;
    internal const ulong FlagOption = 0x00080000;
    internal const ulong FlagCommand = 0x00100000;

    /// <summary>Virtual key codes of the modifier keys themselves (<c>kVK_Command</c> and friends).</summary>
    internal const ushort KeyCodeCommand = 0x37;
    internal const ushort KeyCodeShift = 0x38;
    internal const ushort KeyCodeOption = 0x3A;
    internal const ushort KeyCodeControl = 0x3B;

    private static readonly Dictionary<uint, ushort> MacKeyCodeByVirtualKey = new()
    {
        // Letters (kVK_ANSI_A ... kVK_ANSI_Z); the ANSI layout order is not alphabetical.
        [0x41] = 0x00, // A
        [0x42] = 0x0B, // B
        [0x43] = 0x08, // C
        [0x44] = 0x02, // D
        [0x45] = 0x0E, // E
        [0x46] = 0x03, // F
        [0x47] = 0x05, // G
        [0x48] = 0x04, // H
        [0x49] = 0x22, // I
        [0x4A] = 0x26, // J
        [0x4B] = 0x28, // K
        [0x4C] = 0x25, // L
        [0x4D] = 0x2E, // M
        [0x4E] = 0x2D, // N
        [0x4F] = 0x1F, // O
        [0x50] = 0x23, // P
        [0x51] = 0x0C, // Q
        [0x52] = 0x0F, // R
        [0x53] = 0x01, // S
        [0x54] = 0x11, // T
        [0x55] = 0x20, // U
        [0x56] = 0x09, // V
        [0x57] = 0x0D, // W
        [0x58] = 0x07, // X
        [0x59] = 0x10, // Y
        [0x5A] = 0x06, // Z

        // Digits (kVK_ANSI_0 ... kVK_ANSI_9).
        [0x30] = 0x1D,
        [0x31] = 0x12,
        [0x32] = 0x13,
        [0x33] = 0x14,
        [0x34] = 0x15,
        [0x35] = 0x17,
        [0x36] = 0x16,
        [0x37] = 0x1A,
        [0x38] = 0x1C,
        [0x39] = 0x19,

        // Function keys. macOS defines kVK_F1 through kVK_F20 only.
        [0x70] = 0x7A, // F1
        [0x71] = 0x78, // F2
        [0x72] = 0x63, // F3
        [0x73] = 0x76, // F4
        [0x74] = 0x60, // F5
        [0x75] = 0x61, // F6
        [0x76] = 0x62, // F7
        [0x77] = 0x64, // F8
        [0x78] = 0x65, // F9
        [0x79] = 0x6D, // F10
        [0x7A] = 0x67, // F11
        [0x7B] = 0x6F, // F12
        [0x7C] = 0x69, // F13
        [0x7D] = 0x6B, // F14
        [0x7E] = 0x71, // F15
        [0x7F] = 0x6A, // F16
        [0x80] = 0x40, // F17
        [0x81] = 0x4F, // F18
        [0x82] = 0x50, // F19
        [0x83] = 0x5A, // F20

        // Named keys accepted by KeyboardShortcutGesture.
        [0x20] = 0x31, // Space
        [0x09] = 0x30, // Tab
        [0x0D] = 0x24, // Enter / Return
        [0x1B] = 0x35, // Escape
        [0x08] = 0x33, // Backspace -> kVK_Delete
        [0x2E] = 0x75, // Delete -> kVK_ForwardDelete
        [0x2D] = 0x72, // Insert -> kVK_Help (the physical key macOS reports)
        [0x24] = 0x73, // Home
        [0x23] = 0x77, // End
        [0x21] = 0x74, // PageUp
        [0x22] = 0x79, // PageDown
        [0x25] = 0x7B, // Left
        [0x26] = 0x7E, // Up
        [0x27] = 0x7C, // Right
        [0x28] = 0x7D, // Down

        // Punctuation, keyed by the Windows OEM virtual keys, for gestures that carry them.
        [0xBA] = 0x29, // ;
        [0xBB] = 0x18, // =
        [0xBC] = 0x2B, // ,
        [0xBD] = 0x1B, // -
        [0xBE] = 0x2F, // .
        [0xBF] = 0x2C, // /
        [0xC0] = 0x32, // `
        [0xDB] = 0x21, // [
        [0xDC] = 0x2A, // \
        [0xDD] = 0x1E, // ]
        [0xDE] = 0x27  // '
    };

    internal static bool TryMapKey(uint virtualKey, out ushort macKeyCode)
    {
        return MacKeyCodeByVirtualKey.TryGetValue(virtualKey, out macKeyCode);
    }

    internal static uint ToCarbonModifiers(uint modifiers)
    {
        uint carbon = 0;
        if ((modifiers & KeyboardShortcutGesture.ModControl) != 0)
        {
            carbon |= ControlKeyMask;
        }

        if ((modifiers & KeyboardShortcutGesture.ModAlt) != 0)
        {
            carbon |= OptionKeyMask;
        }

        if ((modifiers & KeyboardShortcutGesture.ModShift) != 0)
        {
            carbon |= ShiftKeyMask;
        }

        if ((modifiers & KeyboardShortcutGesture.ModWin) != 0)
        {
            carbon |= CmdKeyMask;
        }

        return carbon;
    }

    /// <summary>
    /// The modifier keys of a gesture in press order, paired with the CGEventFlags bit each one
    /// adds. Releasing walks the same list backwards.
    /// </summary>
    internal static IReadOnlyList<(ushort KeyCode, ulong Flag)> ModifierSequence(uint modifiers)
    {
        var sequence = new List<(ushort, ulong)>(4);
        if ((modifiers & KeyboardShortcutGesture.ModControl) != 0)
        {
            sequence.Add((KeyCodeControl, FlagControl));
        }

        if ((modifiers & KeyboardShortcutGesture.ModAlt) != 0)
        {
            sequence.Add((KeyCodeOption, FlagOption));
        }

        if ((modifiers & KeyboardShortcutGesture.ModShift) != 0)
        {
            sequence.Add((KeyCodeShift, FlagShift));
        }

        if ((modifiers & KeyboardShortcutGesture.ModWin) != 0)
        {
            sequence.Add((KeyCodeCommand, FlagCommand));
        }

        return sequence;
    }
}
