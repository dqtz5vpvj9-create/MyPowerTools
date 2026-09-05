using System.Text.Json;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Runtime;

public sealed record ShortcutOverride(string Id, IReadOnlyList<ShortcutBinding> Bindings, bool Disabled = false);
public sealed record ShortcutEdit(string Id, IReadOnlyList<ShortcutBinding> Bindings, bool Disabled = false, bool Reset = false);
public sealed record ShortcutConfiguration(ulong Revision, IReadOnlyList<ShortcutOverride> Overrides);
public sealed record ShortcutRegistrationState(string BindingId, string ShortcutId, string RequestedGesture,
    string ActualGesture, string State, string Message);
public sealed record ShortcutCatalogSnapshot(ShortcutConfiguration Configuration,
    IReadOnlyList<ShortcutDefinition> Commands, IReadOnlyList<ShortcutRegistrationState> Registrations,
    string Platform, string SystemStatus = "Awaiting platform registration.");
public sealed record EffectiveShortcut(ShortcutDefinition Definition, string BindingId, string Gesture, bool IsUser);

/// <summary>Wire format over the existing HostControl settings RPC; Runner is the only writer.</summary>
public static class ShortcutCatalog
{
    public const string SettingsModuleId = "runner.shortcuts";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Normalize(string gesture)
    {
        if (!KeyboardShortcutGesture.TryParse(gesture, out var parsed, out var error))
            throw new InvalidDataException(error);
        return parsed!.NormalizedGesture;
    }

    public static string LegacyId(string moduleId, string id) =>
        id.StartsWith(moduleId + ".", StringComparison.OrdinalIgnoreCase) ? id : $"{moduleId}.{id}";

    public static IReadOnlyList<EffectiveShortcut> Effective(ShortcutCatalogSnapshot snapshot)
    {
        var overrides = snapshot.Configuration.Overrides.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<EffectiveShortcut>();
        foreach (var definition in snapshot.Commands.Where(item => item.Available))
        {
            overrides.TryGetValue(definition.Id, out var custom);
            if (custom?.Disabled ?? !definition.EnabledByDefault) continue;
            var bindings = custom?.Bindings ?? definition.DefaultBindings;
            var index = 0;
            foreach (var binding in bindings)
            {
                var bindingId = index++ == 0 ? definition.Id : $"{definition.Id}::{index - 1}";
                if (binding.Platform != "all" && !binding.Platform.Equals(snapshot.Platform, StringComparison.OrdinalIgnoreCase)) continue;
                try { result.Add(new(definition, bindingId, Normalize(binding.Gesture), custom is not null)); }
                catch (InvalidDataException) { /* A bad shipped default must not disable other shortcuts. */ }
            }
        }
        return result;
    }

    public static bool Overlaps(ShortcutDefinition left, ShortcutDefinition right)
    {
        if (left.Scope != right.Scope) return false; // Tool overrides application; system has a different owner.
        if (left.Scope == "tool" && !left.ToolId.Equals(right.ToolId, StringComparison.OrdinalIgnoreCase)) return false;
        return left.Context.Length == 0 || right.Context.Length == 0 || left.Context == right.Context;
    }

    public static EffectiveShortcut? Resolve(ShortcutCatalogSnapshot snapshot, string gesture,
        string toolId, string context, bool textInput, bool overlayOpen)
    {
        if (overlayOpen) return null;
        return Effective(snapshot)
            .Where(item => item.Definition.Scope != "system" &&
                item.Gesture.Equals(gesture, StringComparison.OrdinalIgnoreCase) &&
                (item.Definition.Scope != "tool" || item.Definition.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase)) &&
                (item.Definition.Context.Length == 0 || item.Definition.Context == context) &&
                (!textInput || item.Definition.AllowInTextInput))
            .OrderByDescending(item => item.Definition.Scope == "tool")
            .ThenByDescending(item => item.IsUser)
            .ThenBy(item => item.Definition.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static IReadOnlyList<ShortcutDefinition> ShellCommands { get; } = BuildShellCommands();

    private static IReadOnlyList<ShortcutDefinition> BuildShellCommands()
    {
        var result = new List<ShortcutDefinition>
        {
            new("runner.command-palette", "shell.command-palette.open", "Open MyPowerTools search (background)",
                "MyPowerTools", "system", [new("Ctrl+Alt+Space")],
                LegacyModuleId: "runner", LegacyHotkeyId: "command-palette"),
            new("shell.command-palette.open", "shell.command-palette.open", "Search tools and commands", "MyPowerTools", "application",
                [new("Ctrl+K", "windows"), new("Ctrl+K", "linux"), new("Cmd+K", "macos"),
                 new("Ctrl+Shift+P", "windows"), new("Ctrl+Shift+P", "linux"), new("Cmd+Shift+P", "macos")], AllowInTextInput: true),
            Local("shell.shortcuts.open", "Keyboard shortcuts", "Ctrl+Shift+K", "Cmd+Shift+K", true),
            Local("shell.navigation.back", "Go back", "Alt+Left", "Alt+Left", false),
            new("shell.refresh", "shell.refresh", "Refresh current workspace", "MyPowerTools", "application",
                [new("F5"), new("Ctrl+R", "windows"), new("Ctrl+R", "linux"), new("Cmd+R", "macos")])
        };
        foreach (var (id, title, key) in new[] { ("home", "Home", 1), ("tools", "All tools", 2),
            ("activity", "Activity", 3), ("settings", "Settings", 5), ("system", "System", 6) })
            result.Add(Local("shell.navigate." + id, "Open " + title, $"Ctrl+{key}", $"Cmd+{key}", false));
        return result;
    }

    private static ShortcutDefinition Local(string id, string title, string other, string mac, bool inText) =>
        new(id, id, title, "MyPowerTools", "application",
            [new(other, "windows"), new(other, "linux"), new(mac, "macos")], AllowInTextInput: inText);
}
