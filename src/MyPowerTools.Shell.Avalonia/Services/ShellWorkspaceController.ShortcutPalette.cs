using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private CommandPaletteViewModel AddShortcutPaletteCommands(string query, CommandPaletteViewModel original)
    {
        var commands = original.Commands.ToList();
        var actions = _shortcuts.Snapshot.Commands.Where(item => item.Scope == "application" ||
            (item.Scope == "tool" && item.ToolId == _currentToolId &&
             (item.Context.Length == 0 || item.Context == ActiveShortcutSource?.ShortcutContext)));
        foreach (var action in actions)
        {
            if (!query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
                $"{action.Title} {action.Owner} {action.Id} {(action.Id == "shell.shortcuts.open" ? "keyboard shortcuts 键盘 快捷键" : "")}".Contains(word, StringComparison.OrdinalIgnoreCase))) continue;
            if (action.Scope == "tool" && ActiveShortcutSource?.GetShortcutCommands().Any(item => item.Id == action.Id) != true) continue;
            commands.Add(new CommandItemViewModel(action.Id, action.Owner, action.Title,
                action.Scope == "tool" ? "Current tool action" : "Application action", "normal", false,
                action.Owner, "", "", false, (id, args, invocation, token) => ExecuteShortcutPaletteActionAsync(id, token),
                actionKind: "navigation", icon: "settings", category: "Keyboard shortcuts"));
        }
        foreach (var command in commands) command.ShortcutHint = _shortcuts.Hint(command.CommandId);
        return new(query, commands.DistinctBy(item => item.CommandId).ToArray());
    }

    private async IAsyncEnumerable<CommandExecutionStatus> ExecuteShortcutPaletteActionAsync(string id,
        [EnumeratorCancellation] CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await CloseCommandPaletteAsync();
        var executed = await ExecuteShortcutActionAsync(id);
        yield return executed ? new("succeeded", "Action completed.") : new("failed", "Action is unavailable or failed; see the workspace status.");
    }
}
