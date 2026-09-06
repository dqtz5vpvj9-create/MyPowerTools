using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace MyPowerTools.AvaloniaSdk;

/// <summary>
/// An active Surface exposes its existing actions to the host. Bindings and titles belong in
/// tool.json; a Surface never registers native hotkeys or owns another configuration file.
/// </summary>
public interface IMptShortcutCommandSource
{
    string ShortcutToolId { get; }
    string ShortcutContext { get; }
    IReadOnlyList<MptShortcutCommand> GetShortcutCommands();
}

public sealed record MptShortcutCommand(string Id, Func<Task> ExecuteAsync, Func<bool>? CanExecute = null)
{
    public static MptShortcutCommand FromCommand(string id, ICommand command) =>
        new(id, () =>
        {
            if (command is MptAsyncRelayCommand asyncCommand) return asyncCommand.ExecuteAsync();
            command.Execute(null);
            return Task.CompletedTask;
        }, () => command.CanExecute(null));
}

/// <summary>
/// Inherited host bindings keep tooltips synchronized without a static event retaining
/// collectible Surface assemblies. Use CommandId on a button and optionally Label.
/// </summary>
public sealed class MptShortcutHint : AvaloniaObject
{
    public static readonly AttachedProperty<string?> CommandIdProperty =
        AvaloniaProperty.RegisterAttached<MptShortcutHint, Control, string?>("CommandId");
    public static readonly AttachedProperty<string?> LabelProperty =
        AvaloniaProperty.RegisterAttached<MptShortcutHint, Control, string?>("Label");
    public static readonly AttachedProperty<IReadOnlyDictionary<string, string>?> BindingsProperty =
        AvaloniaProperty.RegisterAttached<MptShortcutHint, Control, IReadOnlyDictionary<string, string>?>(
            "Bindings", inherits: true);

    static MptShortcutHint()
    {
        CommandIdProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        BindingsProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
        LabelProperty.Changed.AddClassHandler<Control>((control, _) => Update(control));
    }

    public static string? GetCommandId(Control control) => control.GetValue(CommandIdProperty);
    public static void SetCommandId(Control control, string? id) => control.SetValue(CommandIdProperty, id);
    public static string? GetLabel(Control control) => control.GetValue(LabelProperty);
    public static void SetLabel(Control control, string? label) => control.SetValue(LabelProperty, label);
    public static IReadOnlyDictionary<string, string>? GetBindings(Control control) => control.GetValue(BindingsProperty);
    public static void SetBindings(Control control, IReadOnlyDictionary<string, string>? bindings) => control.SetValue(BindingsProperty, bindings);

    private static void Update(Control control)
    {
        if (GetCommandId(control) is not { Length: > 0 } id) return;
        var label = GetLabel(control) ?? (control as ContentControl)?.Content as string ?? id;
        var bindings = GetBindings(control);
        var gesture = bindings is not null && bindings.TryGetValue(id, out var value) ? value : "";
        ToolTip.SetTip(control, gesture.Length == 0 ? label : $"{label} ({gesture})");
    }
}
