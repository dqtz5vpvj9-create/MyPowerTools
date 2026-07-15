namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class CommandItemViewModel
{
    private void AddCancellationProgress(CommandCancellationStatus result)
    {
        var label = result.State.Contains("rejected", StringComparison.OrdinalIgnoreCase)
            ? "cancel rejected"
            : result.Accepted
                ? "cancel accepted"
                : "cancel denied";
        ProgressEvents.Add(new CommandProgressItemViewModel(
            ProgressEvents.Count + 1,
            $"{label}: {result.State}",
            result.Message,
            false));
        OnPropertyChanged(nameof(HasProgressEvents));
        OnPropertyChanged(nameof(ResultSummary));
    }

    private string BuildExecutionPreview()
    {
        if (IsNavigation)
        {
            return Title.StartsWith("Open ", StringComparison.OrdinalIgnoreCase)
                ? $"{Title}."
                : $"Open {Title}.";
        }

        if (!HasParameters)
        {
            return $"Preview: run {CommandId}.";
        }

        var emitted = Parameters
            .Where(parameter => parameter.ShouldEmit)
            .Select(parameter => $"{parameter.Id}={parameter.PreviewValue}")
            .ToArray();
        return emitted.Length == 0
            ? $"Preview: run {CommandId} with no arguments."
            : $"Preview: run {CommandId} with {string.Join(", ", emitted)}.";
    }
}
