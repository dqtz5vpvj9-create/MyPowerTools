using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>Recovery state for an unreachable tool surface (docs/desktop/ui-recovery-contract.md).</summary>
public sealed partial class ExternalSdkToolViewModel
{
    private string _recoveryActionLabel = "Try again";
    private string _recoveryHint = "Reloads this tool.";
    private string _recoveryAttemptText = "";
    private string _recoveryProgressText = "";
    private bool _isRecovering;

    public string RecoveryActionLabel
    {
        get => _recoveryActionLabel;
        private set => SetProperty(ref _recoveryActionLabel, value);
    }

    public string RecoveryHint
    {
        get => _recoveryHint;
        private set => SetProperty(ref _recoveryHint, value);
    }

    public string RecoveryAttemptText
    {
        get => _recoveryAttemptText;
        private set
        {
            if (SetProperty(ref _recoveryAttemptText, value))
            {
                OnPropertyChanged(nameof(HasRecoveryAttemptText));
            }
        }
    }

    public bool HasRecoveryAttemptText => RecoveryAttemptText.Length > 0;

    public string RecoveryProgressText
    {
        get => _recoveryProgressText;
        private set => SetProperty(ref _recoveryProgressText, value);
    }

    public bool IsRecovering
    {
        get => _isRecovering;
        private set
        {
            if (SetProperty(ref _isRecovering, value))
            {
                OnPropertyChanged(nameof(IsRecoveryIdle));
            }
        }
    }

    public bool IsRecoveryIdle => !IsRecovering;

    internal void BeginRecovery()
    {
        RecoveryProgressText = "Working…";
        IsRecovering = true;
    }

    internal void ReportRecoveryProgress(string text) => RecoveryProgressText = text;

    internal void EndRecovery() => IsRecovering = false;

    internal void ApplyRecoveryPresentation(ShellRecoveryPresentation presentation)
    {
        RecoveryActionLabel = presentation.ActionLabel;
        RecoveryHint = presentation.ActionHint;
        RecoveryAttemptText = presentation.AttemptText;
    }
}
