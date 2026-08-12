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

public sealed record ModulePickerItemViewModel(string ModuleId, string DisplayName, bool IsSelected, string SelectionText, ICommand SelectCommand);
public sealed record LogLineViewModel(
    string Time,
    string Level,
    string Message,
    DateTimeOffset Timestamp = default,
    string ModuleId = "");
public sealed record NotificationItemViewModel(string Id, string Time, string ModuleId, string Level, string Title, string Body, bool IsRead);
public sealed record ShellAlertViewModel(string Id, string Level, string Title, string Body);
public sealed record ShellActionViewModel(string CommandId, string Title, string Style, bool IsPrimary, string ButtonClasses, ICommand ExecuteCommand);
public sealed record MetricViewModel(string Label, string Value);
/// <summary>
/// Read-only overview row for the global keyboard shortcuts section (PowerToys
/// Keyboard Manager style): every registered hotkey across all modules plus the
/// built-in command palette, with its registration state. Per-module editing
/// lives on each module's settings page.
/// </summary>
public sealed class GlobalHotkeyViewModel
{
    public GlobalHotkeyViewModel(
        string owner,
        string id,
        string commandId,
        string gesture,
        string state,
        string message,
        bool isDefault,
        string defaultGesture)
    {
        Owner = string.IsNullOrWhiteSpace(owner) ? "MyPowerTools" : owner;
        Id = id;
        CommandId = commandId;
        Gesture = string.IsNullOrWhiteSpace(gesture) ? "(unassigned)" : gesture;
        State = state;
        Message = message;
        IsDefault = isDefault;
        DefaultGesture = defaultGesture;
        IsDisabled = string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase);
        IsConflict = string.Equals(state, "conflict", StringComparison.OrdinalIgnoreCase);
        IsRegistered = string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(state, "registered", StringComparison.OrdinalIgnoreCase);
        StateLabel = IsDisabled
            ? "Disabled"
            : IsConflict
                ? "Conflict"
                : IsRegistered
                    ? "Active"
                    : string.IsNullOrWhiteSpace(state) ? "Pending" : state;
    }

    public string Owner { get; }
    public string OwnerLabel => $"Owner: {Owner}";
    public string Id { get; }
    public string CommandId { get; }
    public string Gesture { get; }
    public string State { get; }
    public string Message { get; }
    public bool IsDefault { get; }
    public string DefaultGesture { get; }
    public bool IsDisabled { get; }
    public bool IsConflict { get; }
    public bool IsRegistered { get; }
    public string StateLabel { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}

public sealed class HotkeyBindingViewModel : ObservableViewModel
{
    private string _gesture;
    private string _resultPrompt = "";
    private bool _resetRequested;
    private bool _enabled;

    public HotkeyBindingViewModel(
        string id,
        string gesture,
        string commandId,
        string state,
        string message,
        bool hasConflict,
        ICommand? resetCommand,
        string defaultGesture = "")
    {
        Id = id;
        _gesture = gesture;
        _enabled = !string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase);
        OriginalGesture = gesture;
        OriginalEnabled = _enabled;
        DefaultGesture = string.IsNullOrWhiteSpace(defaultGesture) ? gesture : defaultGesture;
        CommandId = commandId;
        State = state;
        Message = message;
        HasConflict = hasConflict;
        ResetCommand = resetCommand ?? new AsyncRelayCommand(() =>
        {
            ResetRequested = true;
            Gesture = DefaultGesture;
            ResultPrompt = $"Hotkey reset to default {DefaultGesture}.";
            return Task.CompletedTask;
        });
        CommandArgsPreview = $"{commandId} args: default binding";
        _resultPrompt = hasConflict
            ? "Resolve the conflict or reset this binding before registering."
            : StateLabel;
    }

    public string Id { get; }
    public string OriginalGesture { get; private set; }
    public bool OriginalEnabled { get; private set; }
    public string DefaultGesture { get; }
    public string CommandId { get; }
    public string State { get; }
    public string Message { get; }
    public bool HasConflict { get; }
    public ICommand ResetCommand { get; }
    public bool ResetRequested
    {
        get => _resetRequested;
        private set
        {
            if (SetProperty(ref _resetRequested, value))
            {
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }
    public string CommandArgsPreview { get; }
    public bool IsRegistered => string.Equals(State, "ok", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(State, "registered", StringComparison.OrdinalIgnoreCase);
    public bool IsDisabled => !Enabled;
    public bool CanEdit => Enabled;
    public bool IsDirty => ResetRequested ||
        Enabled != OriginalEnabled ||
        !string.Equals(Gesture, OriginalGesture, StringComparison.Ordinal);
    public string StateLabel => !Enabled
        ? "Disabled"
        : HasConflict
            ? "Conflict"
            : IsRegistered
                ? "Registered"
                : string.Equals(State, "disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Pending registration"
                    : State;
    public bool HasResultPrompt => ResultPrompt.Length > 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            ResultPrompt = value
                ? $"Hotkey enabled with gesture {Gesture}."
                : "Hotkey will be disabled after saving.";
            OnPropertyChanged(nameof(IsDisabled));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(StateLabel));
        }
    }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                ResultPrompt = IsDirty
                    ? $"Hotkey changed from {OriginalGesture} to {Gesture}."
                    : StateLabel;
                OnPropertyChanged(nameof(IsDirty));
            }
        }
    }

    public string ResultPrompt
    {
        get => _resultPrompt;
        private set
        {
            if (SetProperty(ref _resultPrompt, value))
            {
                OnPropertyChanged(nameof(HasResultPrompt));
            }
        }
    }

    public void AcceptCurrentValue()
    {
        OriginalGesture = Gesture;
        OriginalEnabled = Enabled;
        ResetRequested = false;
        ResultPrompt = Enabled
            ? "Shortcut saved; runtime registration status will refresh shortly."
            : "Shortcut disabled.";
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(StateLabel));
    }
}
