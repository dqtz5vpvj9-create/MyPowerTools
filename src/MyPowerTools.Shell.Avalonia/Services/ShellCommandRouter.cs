using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

public static class ShellCommandRouter
{
    public static bool TryHandleShellCommand(
        string commandId,
        Func<string, Task> refreshModule,
        out Task handled)
    {
        handled = Task.CompletedTask;
        if (TryMatchSuffix(commandId, ".status.refresh", out var moduleId))
        {
            handled = refreshModule(moduleId);
            return true;
        }

        return false;
    }

    public static bool TryHandleShellCommandStream(
        string commandId,
        Func<string, Task> refreshModule,
        out IAsyncEnumerable<CommandExecutionStatus> handled)
    {
        if (TryHandleShellCommand(commandId, refreshModule, out var action))
        {
            handled = RunShellCommandAction(commandId, action);
            return true;
        }

        handled = AsyncEnumerable.Empty<CommandExecutionStatus>();
        return false;
    }

    private static async IAsyncEnumerable<CommandExecutionStatus> RunShellCommandAction(string commandId, Task action)
    {
        yield return new CommandExecutionStatus("running", $"{RunningVerb(commandId)}: {commandId}", false, 1);
        await action;
        yield return new CommandExecutionStatus("succeeded", SuccessMessage(commandId), true, 2);
    }

    public static string SuccessMessage(string commandId)
    {
        return $"{SuccessVerb(commandId)}: {commandId}";
    }

    private static string RunningVerb(string commandId)
    {
        return commandId.EndsWith(".status.refresh", StringComparison.OrdinalIgnoreCase)
            ? "refreshing"
            : "opening";
    }

    private static string SuccessVerb(string commandId)
    {
        return commandId.EndsWith(".status.refresh", StringComparison.OrdinalIgnoreCase)
            ? "refreshed"
            : "opened";
    }

    private static bool TryMatchSuffix(string commandId, string suffix, out string moduleId)
    {
        moduleId = "";
        if (!commandId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        moduleId = commandId[..^suffix.Length];
        return !string.IsNullOrWhiteSpace(moduleId);
    }
}
