using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>
/// The single recovery surface for page and tool failures (docs/desktop/ui-recovery-contract.md).
/// The primary button runs the current rung of the recovery ladder; while it runs the page shows
/// live progress, and a failure that survives the rung re-renders with what was tried and the next,
/// stronger action.
/// </summary>
public sealed class UnavailablePageViewModel : ShellPageViewModel
{
    private readonly Func<Task>? _recover;
    private bool _isRecovering;
    private string _progressText = "";

    public UnavailablePageViewModel(
        string title,
        string message,
        Func<Task>? retry = null,
        Func<Task>? returnToSafety = null,
        string details = "")
        : this(
            title,
            message,
            new ShellRecoveryPresentation("Try again", "", ""),
            retry,
            returnToSafety,
            details)
    {
    }

    internal UnavailablePageViewModel(
        string title,
        string message,
        ShellRecoveryPresentation presentation,
        Func<Task>? recover,
        Func<Task>? returnToSafety,
        string details)
        : base(title, "", "error")
    {
        _recover = recover;
        Message = message;
        Details = details ?? "";
        ActionLabel = presentation.ActionLabel;
        ActionHint = presentation.ActionHint;
        AttemptText = presentation.AttemptText;
        HasRetry = recover is not null;
        HasReturnAction = returnToSafety is not null;
        RetryCommand = new AsyncRelayCommand(RunRecoveryAsync, operationName: $"Recover {title}");
        ReturnCommand = new AsyncRelayCommand(() => returnToSafety?.Invoke() ?? Task.CompletedTask, operationName: $"Leave {title}");
    }

    public string Message { get; }
    public string Details { get; }
    public bool HasDetails => Details.Length > 0;
    public string ActionLabel { get; }
    public string ActionHint { get; }
    public bool HasActionHint => ActionHint.Length > 0;
    public string AttemptText { get; }
    public bool HasAttemptText => AttemptText.Length > 0;
    public bool HasRetry { get; }
    public bool HasReturnAction { get; }
    public ICommand RetryCommand { get; }
    public ICommand ReturnCommand { get; }

    public bool IsRecovering
    {
        get => _isRecovering;
        private set
        {
            if (SetProperty(ref _isRecovering, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsRecovering;

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    internal void ReportProgress(string text)
    {
        ProgressText = text;
    }

    private async Task RunRecoveryAsync()
    {
        if (_recover is null)
        {
            return;
        }

        ProgressText = "Working…";
        IsRecovering = true;
        try
        {
            await _recover().ConfigureAwait(true);
        }
        finally
        {
            IsRecovering = false;
        }
    }
}
