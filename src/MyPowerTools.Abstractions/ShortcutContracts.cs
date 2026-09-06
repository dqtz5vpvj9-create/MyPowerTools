namespace MyPowerTools.Abstractions;

/// <summary>A platform-qualified binding. "all" applies on every desktop platform.</summary>
public sealed record ShortcutBinding(string Gesture, string Platform = "all");

/// <summary>
/// A discoverable action, including actions without a default binding. Context is an exact,
/// tool-supplied page identifier, not executable code. Scope is system, application or tool.
/// </summary>
public sealed record ShortcutDefinition(
    string Id,
    string CommandId,
    string Title,
    string Owner,
    string Scope,
    IReadOnlyList<ShortcutBinding> DefaultBindings,
    string ToolId = "",
    string Context = "",
    bool AllowInTextInput = false,
    bool EnabledByDefault = true,
    bool Available = true,
    string LegacyModuleId = "",
    string LegacyHotkeyId = "",
    string Description = "")
{
    public string ModuleId { get; init; } = "";
}
