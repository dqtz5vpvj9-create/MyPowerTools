using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed partial class MptHostRuntime
{
    private readonly ConcurrentDictionary<string, ShortcutRegistrationState> _shortcutRegistrations = new(StringComparer.OrdinalIgnoreCase);
    private Func<CancellationToken, Task>? _synchronizeShortcuts;
    private string _systemShortcutStatus = "Waiting for Runner platform hotkeys.";

    public void ConfigureShortcutSynchronization(Func<CancellationToken, Task>? synchronize, string status)
    {
        _synchronizeShortcuts = synchronize;
        _systemShortcutStatus = status;
    }

    public void SetShortcutRegistration(ShortcutRegistrationState state) => _shortcutRegistrations[state.BindingId] = state;
    public void RemoveShortcutRegistration(string bindingId) => _shortcutRegistrations.TryRemove(bindingId, out _);

    public ShortcutCatalogSnapshot GetShortcutCatalog()
    {
        var enabled = EnabledModules().Select(module => module.Module.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commands = new List<ShortcutDefinition>(ShortcutCatalog.ShellCommands);
        foreach (var module in Modules)
        {
            var moduleId = module.Module.Manifest.Id;
            foreach (var hotkey in module.Module.Manifest.Hotkeys)
            {
                if (string.IsNullOrWhiteSpace(hotkey.Id) || string.IsNullOrWhiteSpace(hotkey.CommandId)) continue;
                commands.Add(new(ShortcutCatalog.LegacyId(moduleId, hotkey.Id), hotkey.CommandId,
                    _commandIndex.Find(hotkey.CommandId)?.Title ?? hotkey.CommandId,
                    module.Module.Manifest.DisplayName, "system",
                    string.IsNullOrWhiteSpace(hotkey.Default) ? [] : [new(hotkey.Default)],
                    EnabledByDefault: hotkey.EnabledByDefault, Available: enabled.Contains(moduleId),
                    LegacyModuleId: moduleId, LegacyHotkeyId: hotkey.Id, Description: hotkey.Reason ?? "") { ModuleId = moduleId });
            }
        }
        foreach (var tool in _toolRegistry.Tools)
            commands.AddRange(tool.Shortcuts.Select(command => command with { Available = enabled.Contains(tool.OwnerModuleId) }));
        return new(_hotkeyStore.ReadShortcuts(), commands
            .DistinctBy(command => command.Id, StringComparer.OrdinalIgnoreCase).ToArray(),
            _shortcutRegistrations.Values.ToArray(), _platform.OperatingSystem, _systemShortcutStatus);
    }

    public IReadOnlyList<RuntimeHotkeyBinding> ListManagedHotkeyBindings()
    {
        var catalog = GetShortcutCatalog();
        return ShortcutCatalog.Effective(catalog).Where(item => item.Definition.Scope == "system")
            .OrderByDescending(item => item.IsUser).ThenBy(item => item.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new RuntimeHotkeyBinding(item.BindingId, item.Definition.LegacyModuleId,
                item.Definition.CommandId, item.Gesture, "system", item.Definition.Title,
                IsDefault: !item.IsUser,
                CommandArgsJson: _hotkeyStore.Get(item.Definition.LegacyModuleId, item.Definition.LegacyHotkeyId)?.CommandArgsJson ?? "{}"))
            .ToArray();
    }

    public string GetShortcutIdForBinding(string bindingId) =>
        ShortcutCatalog.Effective(GetShortcutCatalog()).FirstOrDefault(item => item.BindingId == bindingId)?.Definition.Id ?? bindingId;

    private Sdk.SettingsSnapshotDocument GetShortcutSettings()
    {
        var catalog = GetShortcutCatalog();
        return new(ShortcutCatalog.SettingsModuleId, catalog.Configuration.Revision,
            (JsonObject)JsonSerializer.SerializeToNode(catalog, ShortcutCatalog.JsonOptions)!, DateTimeOffset.UtcNow);
    }

    private async Task<SettingsUpdateResult> UpdateShortcutSettingsAsync(Sdk.SettingsPatch patch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edits = patch.Patch["edits"]?.Deserialize<ShortcutEdit[]>(ShortcutCatalog.JsonOptions)
            ?? throw new InvalidDataException("A shortcut edit array is required.");
        _hotkeyStore.UpdateShortcuts(patch.ExpectedRevision, edits, GetShortcutCatalog().Commands);
        string state = "applied", message = "Keyboard shortcuts saved. Application bindings apply immediately.";
        // The persisted configuration is authoritative even if one OS registration fails.
        try
        {
            if (_synchronizeShortcuts is not null)
                await _synchronizeShortcuts(CancellationToken.None);
        }
        catch (Exception ex)
        {
            state = "apply-failed";
            message = $"Saved; system registration needs attention: {ex.Message}";
        }
        _eventBus.Publish("runner", "shortcuts.updated", new JsonObject { ["revision"] = _hotkeyStore.ReadShortcuts().Revision });
        _eventBus.Publish("runner", "hotkeys.updated", new JsonObject());
        return new(GetShortcutSettings(), state, message);
    }
}
