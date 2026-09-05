using System.Text.Json;
using MyPowerTools.WebSurface.Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Runtime;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private readonly ShortcutConfigurationClient _shortcuts = new();
    private readonly HashSet<string> _runningShortcutActions = new(StringComparer.OrdinalIgnoreCase);

    public async Task HandleKeyDownAsync(KeyEventArgs eventArguments)
    {
        if (eventArguments.Handled || eventArguments.Key == Key.ImeProcessed) return;
        // Escape belongs to an open overlay, never to a background tool cancellation action.
        if (_chromeViewModel.IsCommandPaletteOpen && eventArguments.Key == Key.Escape)
        {
            eventArguments.Handled = true;
            await CloseCommandPaletteAsync();
            return;
        }
        var gesture = ShortcutKeyAdapter.Format(eventArguments.Key, eventArguments.KeyModifiers);
        if (gesture is null || !_shortcuts.IsLoaded) return;
        var source = eventArguments.Source as Control;
        if (source is not null && TopLevel.GetTopLevel(source) != TopLevel.GetTopLevel(_contentHost)) return;
        if (source is MenuItem || source?.GetVisualAncestors().Any(item => item is MenuItem or Menu || item is ComboBox { IsDropDownOpen: true }) == true) return;
        var textInput = source is TextBox || source?.GetVisualAncestors().Any(item => item is TextBox) == true;
        var match = ResolveShortcut(gesture, textInput);
        if (match is null) return;
        // Reserve the winning action even when CanExecute is false; never fall through.
        eventArguments.Handled = true;
        await ExecuteShortcutActionAsync(match.Definition.Id);
    }

    public async Task HandleShortcutAsync(Key key, KeyModifiers modifiers)
    {
        var gesture = ShortcutKeyAdapter.Format(key, modifiers);
        if (gesture is null || !_shortcuts.IsLoaded) return;
        // Legacy web forwarding has no input-context metadata. Only explicit text-safe actions
        // may run through it; current native Surfaces use the normal routed-event path.
        var match = ResolveShortcut(gesture, textInput: true);
        if (match is not null) await ExecuteShortcutActionAsync(match.Definition.Id);
    }

    public async Task HandleWebShortcutAsync(string message)
    {
        if (!_shortcuts.IsLoaded || message.Length > 4096) return;
        var gesture = message;
        var textInput = true;
        if (message.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(message);
                gesture = json.RootElement.GetProperty("gesture").GetString() ?? "";
                textInput = json.RootElement.TryGetProperty("textInput", out var input) && input.GetBoolean();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return; }
        }
        if (!ShortcutKeyAdapter.TryParse(gesture, out var key, out var modifiers)) return;
        var canonical = ShortcutKeyAdapter.Format(key, modifiers);
        if (canonical is null) return;
        var match = ResolveShortcut(canonical, textInput);
        if (match is not null) await ExecuteShortcutActionAsync(match.Definition.Id);
    }

    private IMptShortcutCommandSource? ActiveShortcutSource
    {
        get
        {
            var root = GetCurrentExternalSdkToolView()?.ManagedSurface;
            if (root is null) return null;
            return new[] { root }.Concat(root.GetVisualDescendants().OfType<Control>())
                .OfType<IMptShortcutCommandSource>()
                .FirstOrDefault(source => source.ShortcutToolId.Equals(_currentToolId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private EffectiveShortcut? ResolveShortcut(string gesture, bool textInput) => ShortcutCatalog.Resolve(
        _shortcuts.Snapshot, gesture, _currentToolId, ActiveShortcutSource?.ShortcutContext ?? _currentToolRouteId,
        textInput, _chromeViewModel.IsCommandPaletteOpen || _chromeViewModel.IsPermissionPromptOpen);

    private async Task<bool> ExecuteShortcutActionAsync(string id)
    {
        if (!_runningShortcutActions.Add(id)) return false;
        try
        {
            if (_shortcuts.Snapshot.Commands.FirstOrDefault(command => command.Id == id)?.Scope == "tool")
            {
                var action = ActiveShortcutSource?.GetShortcutCommands().FirstOrDefault(command => command.Id == id);
                if (action is null || !(action.CanExecute?.Invoke() ?? true)) return false;
                await action.ExecuteAsync();
                return true;
            }
            switch (id)
            {
                case "shell.command-palette.open": await FocusCommandPaletteAsync(); break;
                case "shell.shortcuts.open": await ShowPageAsync(ShortcutsPage); break;
                case "shell.navigation.back": await GoBackWithShortcutsAsync(); break;
                case "shell.refresh": await RefreshAsync(); break;
                case "shell.navigate.home": await ShowPageAsync(HomePage); break;
                case "shell.navigate.tools": await ShowPageAsync(ToolsPage); break;
                case "shell.navigate.activity": await ShowPageAsync(ActivityPage); break;
                case "shell.navigate.settings": await ShowPageAsync(SettingsPage); break;
                case "shell.navigate.system": await ShowPageAsync(SystemPage); break;
                default: return false;
            }
            return true;
        }
        catch (Exception ex) { SetStatus($"Shortcut action failed: {ex.Message}"); return false; }
        finally { _runningShortcutActions.Remove(id); }
    }

    private async Task RefreshShortcutCatalogAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await _shortcuts.RefreshAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Load shortcut configuration", ex, "shortcuts");
            // Keep the last confirmed bindings. Do not re-enable defaults on transport failure.
        }
    }

    private void OnShortcutConfigurationChanged()
    {
        if (IsDisposed) return;
        MptShortcutHint.SetBindings(_contentHost, _shortcuts.Hints());
        if (_webSurfaceService is IConfigurableWebShortcuts web)
            web.UpdateShortcutBindings(ShortcutCatalog.Effective(_shortcuts.Snapshot)
                .Where(item => item.Definition.Scope == "application")
                .Select(item => new WebShortcutBinding(item.Gesture, item.Definition.AllowInTextInput)).ToArray());
        if (_commandPaletteViewModel is not null)
            foreach (var item in _commandPaletteViewModel.Commands) item.ShortcutHint = _shortcuts.Hint(item.CommandId);
    }
}
