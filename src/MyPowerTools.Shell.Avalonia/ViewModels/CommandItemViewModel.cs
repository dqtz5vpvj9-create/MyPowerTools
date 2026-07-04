using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed record DashboardCardViewModel(
    string ModuleId,
    string PackageId,
    string Title,
    string State,
    string Summary,
    IReadOnlyList<MetricViewModel> Metrics,
    IReadOnlyList<ShellActionViewModel> Actions,
    ICommand DetailsCommand);

public sealed class CommandItemViewModel : ObservableViewModel
{
    private readonly Func<string, JsonObject, string, CancellationToken, IAsyncEnumerable<CommandExecutionStatus>>? _executeCommand;
    private readonly Func<string, Task<CommandCancellationStatus>>? _cancelCommand;
    private readonly AsyncRelayCommand _executeCommandWrapper;
    private readonly AsyncRelayCommand _cancelCommandWrapper;
    private CancellationTokenSource? _activeExecution;
    private string _activeInvocationId = "";
    private string _validationMessage = "";
    private string _executionState = "ready";
    private string _executionMessage = "";
    private string _executionPreview;

    public CommandItemViewModel(
        string commandId,
        string moduleId,
        string title,
        string subtitle,
        string dangerLevel,
        bool requiresElevation,
        string moduleLabel,
        string riskLabel,
        string parameterSummary,
        bool hasParameters,
        Func<string, JsonObject, string, CancellationToken, IAsyncEnumerable<CommandExecutionStatus>>? executeCommand,
        Func<string, Task<CommandCancellationStatus>>? cancelCommand = null,
        IReadOnlyList<CommandParameterViewModel>? parameters = null)
    {
        CommandId = commandId;
        ModuleId = moduleId;
        Title = title;
        Subtitle = subtitle;
        DangerLevel = dangerLevel;
        RequiresElevation = requiresElevation;
        ModuleLabel = moduleLabel;
        RiskLabel = riskLabel;
        Parameters = parameters ?? [];
        ProgressEvents = [];
        ParameterSummary = parameterSummary.Length == 0 && Parameters.Count > 0
            ? $"{Parameters.Count} parameter(s): {string.Join(", ", Parameters.Select(parameter => parameter.Label))}"
            : parameterSummary;
        HasParameters = hasParameters || Parameters.Count > 0;
        _executeCommand = executeCommand;
        _cancelCommand = cancelCommand;
        _executionPreview = BuildExecutionPreview();
        _executeCommandWrapper = new AsyncRelayCommand(ExecuteAsync, () => !HasValidationError);
        _cancelCommandWrapper = new AsyncRelayCommand(CancelAsync, () => CanCancel);
        ExecuteCommand = _executeCommandWrapper;
        CancelCommand = _cancelCommandWrapper;

        foreach (var parameter in Parameters)
        {
            parameter.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(CommandParameterViewModel.Value) or nameof(CommandParameterViewModel.BooleanValue))
                {
                    ValidateParameters();
                    ExecutionPreview = BuildExecutionPreview();
                    _executeCommandWrapper.NotifyCanExecuteChanged();
                }
            };
        }

        ValidateParameters();
    }

    public string CommandId { get; }
    public string ModuleId { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string DangerLevel { get; }
    public bool RequiresElevation { get; }
    public string ModuleLabel { get; }
    public string RiskLabel { get; }
    public string ParameterSummary { get; }
    public bool HasParameters { get; }
    public IReadOnlyList<CommandParameterViewModel> Parameters { get; }
    public ObservableCollection<CommandProgressItemViewModel> ProgressEvents { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public string ExecuteLabel => IsRunning ? "Running" : HasParameters ? "Run with parameters" : "Run";
    public string CancelLabel => ExecutionState == "cancelling" ? "Cancelling" : "Cancel";
    public bool HasValidationError => ValidationMessage.Length > 0;
    public bool HasExecutionMessage => ExecutionMessage.Length > 0;
    public bool HasProgressEvents => ProgressEvents.Count > 0;
    public bool IsRunning => ExecutionState is "accepted" or "running" or "cancelling";
    public bool CanCancel => IsRunning && _activeInvocationId.Length > 0;
    public string ExecutionStateLabel => ExecutionState switch
    {
        "running" => "Running",
        "accepted" => "Accepted",
        "cancelling" => "Cancelling",
        "succeeded" => "Succeeded",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "blocked" => "Needs input",
        _ => "Ready"
    };

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public string ExecutionState
    {
        get => _executionState;
        private set
        {
            if (SetProperty(ref _executionState, value))
            {
                OnPropertyChanged(nameof(ExecutionStateLabel));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(ExecuteLabel));
                OnPropertyChanged(nameof(CancelLabel));
                _cancelCommandWrapper.NotifyCanExecuteChanged();
            }
        }
    }

    public string ExecutionMessage
    {
        get => _executionMessage;
        private set
        {
            if (SetProperty(ref _executionMessage, value))
            {
                OnPropertyChanged(nameof(HasExecutionMessage));
            }
        }
    }

    public string ExecutionPreview
    {
        get => _executionPreview;
        private set => SetProperty(ref _executionPreview, value);
    }

    public async Task ExecuteAsync()
    {
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

    private string BuildExecutionPreview()
    {
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
