using System.Text.Json.Nodes;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class CommandItemViewModel
{
    public async Task ExecuteAsync()
    {
        if (RequiresDangerousConfirmation && !IsDangerousConfirmed)
        {
            ExecutionState = "blocked";
            ExecutionMessage = DangerConfirmationText;
            _executeCommandWrapper.NotifyCanExecuteChanged();
            return;
        }

        if (!ValidateParameters())
        {
            ExecutionState = "blocked";
            ExecutionMessage = ValidationMessage;
            _executeCommandWrapper.NotifyCanExecuteChanged();
            return;
        }

        _activeInvocationId = Guid.NewGuid().ToString("N");
        _activeExecution?.Dispose();
        _activeExecution = new CancellationTokenSource();
        ClearProgressEvents();
        ExecutionState = "running";
        ExecutionMessage = $"Running {Title}.";
        try
        {
            if (_executeCommand is null)
            {
                ApplyExecutionStatus(new CommandExecutionStatus("succeeded", $"succeeded: {Title}"));
            }
            else
            {
                await foreach (var result in _executeCommand(CommandId, BuildArgs(), _activeInvocationId, _activeExecution.Token)
                    .WithCancellation(_activeExecution.Token))
                {
                    ApplyExecutionStatus(result);
                }
            }
        }
        catch (OperationCanceledException) when (ExecutionState == "cancelling")
        {
            ApplyExecutionStatus(new CommandExecutionStatus("cancelled", $"Cancelled {Title}."));
        }
        catch (Exception ex)
        {
            ApplyExecutionStatus(new CommandExecutionStatus("failed", ex.Message));
        }
        finally
        {
            _activeExecution?.Dispose();
            _activeExecution = null;
            _activeInvocationId = "";
            _executeCommandWrapper.NotifyCanExecuteChanged();
            _cancelCommandWrapper.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(ExecuteLabel));
        }
    }

    public async Task CancelAsync()
    {
        if (!CanCancel)
        {
            return;
        }

        var invocationId = _activeInvocationId;
        ExecutionState = "cancelling";
        ExecutionMessage = $"Cancelling {Title}.";
        try
        {
            var result = _cancelCommand is null
                ? new CommandCancellationStatus(false, invocationId, "unsupported", "Cancellation is not available for this command.")
                : await _cancelCommand(invocationId);
            ExecutionMessage = result.Message;
            AddCancellationProgress(result);
            if (!result.Accepted)
            {
                ExecutionState = string.Equals(result.State, "completed", StringComparison.OrdinalIgnoreCase)
                    ? "ready"
                    : "failed";
            }
            else
            {
                _activeExecution?.Cancel();
            }
        }
        catch (Exception ex)
        {
            ExecutionState = "failed";
            ExecutionMessage = ex.Message;
        }
    }

    private void ClearProgressEvents()
    {
        ProgressEvents.Clear();
        OnPropertyChanged(nameof(HasProgressEvents));
    }

    private void ApplyExecutionStatus(CommandExecutionStatus result)
    {
        ExecutionState = string.IsNullOrWhiteSpace(result.State) ? "succeeded" : result.State;
        ExecutionMessage = string.IsNullOrWhiteSpace(result.Message)
            ? $"{ExecutionStateLabel}: {Title}"
            : result.Message;
        ProgressEvents.Add(new CommandProgressItemViewModel(
            result.Sequence <= 0 ? ProgressEvents.Count + 1 : result.Sequence,
            ExecutionStateLabel,
            ExecutionMessage,
            result.IsTerminal));
        OnPropertyChanged(nameof(HasProgressEvents));
        OnPropertyChanged(nameof(StdoutPreview));
        OnPropertyChanged(nameof(StderrPreview));
        OnPropertyChanged(nameof(HasStdout));
        OnPropertyChanged(nameof(HasStderr));
        OnPropertyChanged(nameof(ResultSummary));
    }

    public JsonObject BuildArgs()
    {
        if (!ValidateParameters())
        {
            throw new InvalidOperationException(ValidationMessage);
        }

        var args = new JsonObject();
        foreach (var parameter in Parameters)
        {
            if (!parameter.ShouldEmit)
            {
                continue;
            }

            args[parameter.Id] = parameter.ToJsonNode();
        }

        return args;
    }

    public bool ValidateParameters()
    {
        var messages = new List<string>();
        foreach (var parameter in Parameters)
        {
            var message = parameter.Validate();
            parameter.SetValidationMessage(message);
            if (message.Length > 0)
            {
                messages.Add(message);
            }
        }

        ValidationMessage = string.Join(" ", messages);
        return messages.Count == 0;
    }
}
