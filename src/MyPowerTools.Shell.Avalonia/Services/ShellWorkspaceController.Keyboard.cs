using Avalonia.Input;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    public async Task HandleKeyDownAsync(KeyEventArgs eventArguments)
    {
        var shortcut = ShellKeyboardShortcut.Resolve(eventArguments.Key, eventArguments.KeyModifiers);
        if (shortcut.Action == ShellKeyboardAction.None)
        {
            return;
        }

        eventArguments.Handled = true;
        await ApplyKeyboardShortcutSafelyAsync(shortcut);
    }

    public Task HandleShortcutAsync(Key key, KeyModifiers modifiers)
    {
        var shortcut = ShellKeyboardShortcut.Resolve(key, modifiers);
        return shortcut.Action == ShellKeyboardAction.None
            ? Task.CompletedTask
            : ApplyKeyboardShortcutSafelyAsync(shortcut);
    }

    private async Task ApplyKeyboardShortcutSafelyAsync(ShellKeyboardShortcutResult shortcut)
    {
        try
        {
            await ApplyKeyboardShortcutAsync(shortcut);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }
}
