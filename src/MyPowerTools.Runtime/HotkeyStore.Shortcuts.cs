using MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed partial class HotkeyStore
{
    public ShortcutConfiguration ReadShortcuts()
    {
        lock (_gate)
        {
            var snapshot = Load();
            var legacy = snapshot.Overrides.Where(item => !item.UseDefaultBindings &&
                (item.Bindings is not null || !string.IsNullOrWhiteSpace(item.Gesture) || item.Disabled)).Select(item => new ShortcutOverride(
                ShortcutCatalog.LegacyId(item.ModuleId, item.HotkeyId),
                item.Bindings?.ToArray() ?? [new ShortcutBinding(item.Gesture)], item.Disabled));
            return new(snapshot.Revision, legacy.Concat(snapshot.Shortcuts)
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    /// <summary>Merge one editor transaction without losing legacy command arguments or unrelated overrides.</summary>
    public ShortcutConfiguration UpdateShortcuts(ulong expectedRevision, IReadOnlyList<ShortcutEdit> edits,
        IReadOnlyList<ShortcutDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(edits);
        lock (_gate)
        {
            var snapshot = Load();
            if (snapshot.Revision != expectedRevision)
                throw new SettingsConflictException(ShortcutCatalog.SettingsModuleId, snapshot.Revision, expectedRevision);
            if (edits.Any(item => item is null)) throw new InvalidDataException("An edit cannot be null.");
            if (edits.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edits.Count)
                throw new InvalidDataException("The import contains duplicate action IDs.");
            var legacy = snapshot.Overrides.ToList();
            var local = snapshot.Shortcuts.ToList();
            foreach (var edit in edits)
            {
                if (string.IsNullOrWhiteSpace(edit.Id)) throw new InvalidDataException("An action ID is required.");
                var bindings = (edit.Bindings ?? throw new InvalidDataException("Bindings must be an array."))
                    .Select(binding =>
                    {
                        if (binding is null) throw new InvalidDataException("A binding cannot be null.");
                        var platform = (binding.Platform ?? "all").ToLowerInvariant();
                        if (platform is not ("all" or "windows" or "linux" or "macos"))
                            throw new InvalidDataException($"Unknown platform: {platform}");
                        return new ShortcutBinding(ShortcutCatalog.Normalize(binding.Gesture), platform);
                    }).Distinct().ToArray();
                var previous = legacy.FirstOrDefault(item =>
                    ShortcutCatalog.LegacyId(item.ModuleId, item.HotkeyId).Equals(edit.Id, StringComparison.OrdinalIgnoreCase));
                var definition = definitions.FirstOrDefault(item => item.Id.Equals(edit.Id, StringComparison.OrdinalIgnoreCase));
                var moduleId = definition?.LegacyModuleId ?? previous?.ModuleId ?? "";
                var hotkeyId = definition?.LegacyHotkeyId ?? previous?.HotkeyId ?? "";
                local.RemoveAll(item => item.Id.Equals(edit.Id, StringComparison.OrdinalIgnoreCase));
                legacy.RemoveAll(item => ShortcutCatalog.LegacyId(item.ModuleId, item.HotkeyId).Equals(edit.Id, StringComparison.OrdinalIgnoreCase));
                if (edit.Reset)
                {
                    if (previous is not null && !string.IsNullOrWhiteSpace(previous.CommandArgsJson) && previous.CommandArgsJson != "{}")
                        legacy.Add(previous with { Gesture = "", Disabled = false, Bindings = null, UseDefaultBindings = true });
                    continue;
                }
                if (moduleId.Length > 0 && hotkeyId.Length > 0)
                    legacy.Add(new HotkeyOverride(moduleId, hotkeyId, bindings.FirstOrDefault()?.Gesture ?? "",
                        edit.Disabled, previous?.CommandArgsJson ?? "{}") { Bindings = bindings });
                else
                    local.Add(new(edit.Id, bindings, edit.Disabled)); // Preserve uninstalled tools during import.
            }
            Save(snapshot with { Revision = snapshot.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow,
                Overrides = legacy.OrderBy(item => item.ModuleId).ThenBy(item => item.HotkeyId).ToArray(),
                Shortcuts = local.OrderBy(item => item.Id).ToArray() });
            return ReadShortcuts();
        }
    }
}
