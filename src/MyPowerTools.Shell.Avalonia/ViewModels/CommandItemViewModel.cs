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

public sealed partial class CommandItemViewModel : ObservableViewModel
{
    internal const int MaximumProgressEvents = 200;

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
    private string _stdoutPreview = "";
    private string _stderrPreview = "";
    private bool _isDangerousConfirmed;

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
        IReadOnlyList<CommandParameterViewModel>? parameters = null,
        string actionKind = "command",
        string icon = "",
        string category = "")
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
        ActionKind = actionKind;
        Icon = icon;
        Category = category;
        _executeCommand = executeCommand;
        _cancelCommand = cancelCommand;
        _executionPreview = BuildExecutionPreview();
        _executeCommandWrapper = new AsyncRelayCommand(
            ExecuteAsync,
            () => !HasValidationError && (!RequiresDangerousConfirmation || IsDangerousConfirmed));
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

    private string _shortcutHint = "";
    public string ShortcutHint { get => _shortcutHint; set => SetProperty(ref _shortcutHint, value); }
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
    public string ActionKind { get; }
    public string Icon { get; }
    public string Category { get; }
    public bool IsNavigation => string.Equals(ActionKind, "navigation", StringComparison.OrdinalIgnoreCase);
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category)
        ? IsNavigation ? "Tool" : "Command"
        : Category;
    public string IconGlyph => NormalizeIcon(Icon) switch
    {
        "settings" or "configuration" => "\uE713",
        "diagnostics" or "health" => "\uE9D9",
        "notifications" or "notification" or "bell" => "\uEA8F",
        "network" or "forward" or "port" => "\uE968",
        "display" or "screen" => "\uE7F4",
        "run" or "command" or "terminal" => "\uE756",
        _ when IsNavigation => "\uE8A7",
        _ => "\uE945"
    };
    public IReadOnlyList<CommandParameterViewModel> Parameters { get; }

    /// <summary>
    /// Streamed progress lines, capped at <see cref="MaximumProgressEvents"/>. A chatty command
    /// emits one entry per sidecar output line, and the palette renders them in a non-virtualizing
    /// list, so the oldest entries are dropped rather than letting the list grow without bound.
    /// </summary>
    public ObservableCollection<CommandProgressItemViewModel> ProgressEvents { get; }
    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public string ExecuteLabel => IsRunning
        ? IsNavigation ? "Opening" : "Running"
        : IsNavigation ? "Open" : HasParameters ? "Run with parameters" : "Run";
    public string CancelLabel => ExecutionState == "cancelling" ? "Cancelling" : "Cancel";
    public bool HasValidationError => ValidationMessage.Length > 0;
    public bool HasExecutionMessage => ExecutionMessage.Length > 0;
    public bool HasProgressEvents => ProgressEvents.Count > 0;
    public bool IsRunning => ExecutionState is "accepted" or "running" or "cancelling";
    public bool CanCancel => IsRunning && _activeInvocationId.Length > 0;
    public bool RequiresDangerousConfirmation => RequiresElevation ||
        DangerLevel.Contains("danger", StringComparison.OrdinalIgnoreCase) ||
        DangerLevel.Contains("elevated", StringComparison.OrdinalIgnoreCase) ||
        RiskLabel.Contains("broker", StringComparison.OrdinalIgnoreCase);
    public string DangerConfirmationText => RequiresDangerousConfirmation
        ? $"Confirm required before running {CommandId}."
        : "";
    public bool HasExpandableError => ExecutionState == "failed" && ExecutionMessage.Length > 0;
    public string StdoutPreview => _stdoutPreview;
    public string StderrPreview => _stderrPreview;
    public string ResultSummary => HasExecutionMessage ? ExecutionMessage : ExecutionPreview;
    public bool HasStdout => StdoutPreview.Length > 0;
    public bool HasStderr => StderrPreview.Length > 0;
    public string ExecutionStateLabel => ExecutionState switch
    {
        "stdout" => "stdout",
        "stderr" => "stderr",
        "running" => "Running",
        "accepted" => "Accepted",
        "cancelling" => "Cancelling",
        "succeeded" => "Succeeded",
        "failed" => "Failed",
        "cancelled" => "Cancelled",
        "blocked" => "Needs input",
        _ => "Ready"
    };

    public bool IsDangerousConfirmed
    {
        get => _isDangerousConfirmed;
        set
        {
            if (SetProperty(ref _isDangerousConfirmed, value))
            {
                _executeCommandWrapper.NotifyCanExecuteChanged();
            }
        }
    }

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
                OnPropertyChanged(nameof(HasExpandableError));
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
                OnPropertyChanged(nameof(ResultSummary));
                OnPropertyChanged(nameof(HasExpandableError));
            }
        }
    }

    public string ExecutionPreview
    {
        get => _executionPreview;
        private set => SetProperty(ref _executionPreview, value);
    }

    private static string NormalizeIcon(string icon)
    {
        return icon.Trim().ToLowerInvariant();
    }

}
