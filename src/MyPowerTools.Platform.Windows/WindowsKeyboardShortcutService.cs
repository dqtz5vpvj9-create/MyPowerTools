using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsKeyboardShortcutService : IKeyboardShortcutService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtendedKey = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkShift = 0x10;
    private const uint VkLWin = 0x5B;

    public Task<KeyboardShortcutResult> SendAsync(string gesture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!KeyboardShortcutGesture.TryParse(gesture, requireModifier: false, out var parsed, out var parseError))
        {
            return Task.FromResult(new KeyboardShortcutResult(false, "validation-failed", parseError));
        }

        var inputs = BuildInputs(parsed!);
        var inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (inserted != inputs.Length)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return Task.FromResult(new KeyboardShortcutResult(
                false,
                "failed",
                $"SendInput inserted {inserted} of {inputs.Length} shortcut events: {error}"));
        }

        return Task.FromResult(new KeyboardShortcutResult(
            true,
            "sent",
            $"Sent shortcut '{parsed!.NormalizedGesture}'."));
    }

    private static INPUT[] BuildInputs(KeyboardShortcutGesture gesture)
    {
        var inputs = new List<INPUT>(10);
        AddModifierDown(inputs, gesture.Modifiers);
        inputs.Add(CreateInput((ushort)gesture.VirtualKey, keyUp: false));
        inputs.Add(CreateInput((ushort)gesture.VirtualKey, keyUp: true));
        AddModifierUp(inputs, gesture.Modifiers);
        return inputs.ToArray();
    }

    private static void AddModifierDown(List<INPUT> inputs, uint modifiers)
    {
        if ((modifiers & KeyboardShortcutGesture.ModControl) != 0)
        {
            inputs.Add(CreateInput((ushort)VkControl, keyUp: false));
        }

        if ((modifiers & KeyboardShortcutGesture.ModAlt) != 0)
        {
            inputs.Add(CreateInput((ushort)VkMenu, keyUp: false));
        }

        if ((modifiers & KeyboardShortcutGesture.ModShift) != 0)
        {
            inputs.Add(CreateInput((ushort)VkShift, keyUp: false));
        }

        if ((modifiers & KeyboardShortcutGesture.ModWin) != 0)
        {
            inputs.Add(CreateInput((ushort)VkLWin, keyUp: false));
        }
    }

    private static void AddModifierUp(List<INPUT> inputs, uint modifiers)
    {
        if ((modifiers & KeyboardShortcutGesture.ModWin) != 0)
        {
            inputs.Add(CreateInput((ushort)VkLWin, keyUp: true));
        }

        if ((modifiers & KeyboardShortcutGesture.ModShift) != 0)
        {
            inputs.Add(CreateInput((ushort)VkShift, keyUp: true));
        }

        if ((modifiers & KeyboardShortcutGesture.ModAlt) != 0)
        {
            inputs.Add(CreateInput((ushort)VkMenu, keyUp: true));
        }

        if ((modifiers & KeyboardShortcutGesture.ModControl) != 0)
        {
            inputs.Add(CreateInput((ushort)VkControl, keyUp: true));
        }
    }

    private static INPUT CreateInput(ushort virtualKey, bool keyUp)
    {
        var flags = keyUp ? KeyEventKeyUp : 0U;
        if (IsExtendedKey(virtualKey))
        {
            flags |= KeyEventExtendedKey;
        }

        return new INPUT
        {
            Type = InputKeyboard,
            U = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    ScanCode = (ushort)MapVirtualKey(virtualKey, 0),
                    Flags = flags,
                    Time = 0,
                    ExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static bool IsExtendedKey(uint virtualKey)
    {
        return virtualKey is
            (>= 0x21 and <= 0x28) or // PageUp/PageDown/End/Home/arrows
            0x2D or 0x2E;             // Insert/Delete
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputs, INPUT[] inputArray, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;

        [FieldOffset(0)]
        public MOUSEINPUT Mouse;

        [FieldOffset(0)]
        public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }
}
