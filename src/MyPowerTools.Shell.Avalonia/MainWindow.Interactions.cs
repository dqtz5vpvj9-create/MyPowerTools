using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;

namespace MyPowerTools.Shell.Avalonia;

public sealed partial class MainWindow
{
    public async Task FocusCommandPaletteAsync()
    {
        await _workspaceOpened.Task.ConfigureAwait(true);
        var workspace = _workspace;
        if (workspace is not null)
        {
            await workspace.FocusCommandPaletteAsync().ConfigureAwait(true);
        }
    }

    internal Task HandleForwardedWebToolShortcutAsync(string gesture)
    {
        return ShellKeyboardShortcut.TryParseGesture(gesture, out var key, out var modifiers)
            ? _workspace?.HandleShortcutAsync(key, modifiers) ?? Task.CompletedTask
            : Task.CompletedTask;
    }

    private void OnShellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var workspace = _workspace;
        if (workspace is not null)
        {
            RunWindowUiEvent(
                () => workspace.HandleKeyDownAsync(e),
                "Handle Shell keyboard input");
        }
    }

    private void RunWindowUiEvent(Func<Task> action, string operation)
    {
        _workspace?.RunScopedUiEvent(action, operation);
    }

    private void ApplyWindowsChrome()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var darkMode = isDark ? 1 : 0;
        var cornerPreference = 2; // DWMWCP_ROUND
        var captionColor = isDark ? 0x00202020 : 0x00F3F3F3;
        var textColor = isDark ? 0x00FFFFFF : 0x001A1A1A;

        _ = SetWindowText(handle, _windowCaption);
        SetDwmAttribute(handle, 20, darkMode);          // DWMWA_USE_IMMERSIVE_DARK_MODE
        SetDwmAttribute(handle, 33, cornerPreference);  // DWMWA_WINDOW_CORNER_PREFERENCE
        SetDwmAttribute(handle, 35, captionColor);      // DWMWA_CAPTION_COLOR
        SetDwmAttribute(handle, 36, textColor);         // DWMWA_TEXT_COLOR
    }

    private static void SetDwmAttribute(IntPtr handle, int attribute, int value)
    {
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr windowHandle, string text);

    private static T RequireControl<T>(Control root, string name)
        where T : Control
    {
        return root.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Shell chrome control '{name}' was not found.");
    }
}
