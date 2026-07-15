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

public sealed class SettingsCenterViewModel : ShellPageViewModel
{
    private readonly AsyncRelayCommand _saveCommand;
    private string _originalRawJson;
    private string _rawJson;
    private string _statusText;
    private int _dirtyCount;
    private string _patchPreview = "";
    private string _validationMessage = "";
    private string _saveResultState = "";
    private string _saveResultTitle = "";
    private string _saveResultMessage = "";
    private string _saveResultRevision = "";

    public SettingsCenterViewModel(
        string selectedModuleId,
        string selectedModuleName,
        ulong revision,
        string rawJson,
        string statusText,
        IReadOnlyList<ModulePickerItemViewModel> modules,
        IReadOnlyList<SettingsFieldViewModel> fields,
        IReadOnlyList<HotkeyBindingViewModel>? hotkeys = null,
        Func<SettingsCenterViewModel, Task>? saveSettings = null)
        : base("Module settings", selectedModuleName, selectedModuleId.Length == 0 ? "empty" : "ready")
    {
        SelectedModuleId = selectedModuleId;
        Revision = revision;
        _rawJson = rawJson;
        _originalRawJson = rawJson;
        _statusText = statusText;
        Modules = modules;
        Fields = fields;
        Hotkeys = hotkeys ?? [];
        _saveCommand = new AsyncRelayCommand(
            () => saveSettings?.Invoke(this) ?? Task.CompletedTask,
            () => CanSave);
        SaveCommand = _saveCommand;

        foreach (var field in Fields)
        {
            field.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(SettingsFieldViewModel.Value)
                    or nameof(SettingsFieldViewModel.BooleanValue)
                    or nameof(SettingsFieldViewModel.SelectedOption)
                    or nameof(SettingsFieldViewModel.ValidationMessage))
                {
                    RefreshStagedChanges();
                }
            };
        }

        foreach (var hotkey in Hotkeys)
        {
            hotkey.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(HotkeyBindingViewModel.Gesture)
                    or nameof(HotkeyBindingViewModel.Enabled)
                    or nameof(HotkeyBindingViewModel.ResetRequested)
                    or nameof(HotkeyBindingViewModel.IsDirty))
                {
                    RefreshStagedChanges();
                    OnPropertyChanged(nameof(HasHotkeyConflicts));
                    OnPropertyChanged(nameof(HotkeyStatusText));
                }
            };
        }

        RefreshStagedChanges();
    }

    public string SelectedModuleId { get; }
    public ulong Revision { get; private set; }
    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; }
    public IReadOnlyList<SettingsFieldViewModel> Fields { get; }
    public IReadOnlyList<HotkeyBindingViewModel> Hotkeys { get; }
    public bool HasNoModules => Modules.Count == 0;
    public bool HasFields => Fields.Count > 0;
    public bool HasHotkeys => Hotkeys.Count > 0;
    public bool HasHotkeyConflicts => Hotkeys.Any(hotkey => hotkey.Enabled && hotkey.HasConflict);
    public string HotkeyStatusText => HasHotkeyConflicts
        ? "One or more hotkeys conflict with another runtime binding."
        : $"{Hotkeys.Count(hotkey => hotkey.Enabled)} of {Hotkeys.Count} shortcuts enabled.";
    public bool UsesRawJson => SelectedModuleId.Length > 0 && Fields.Count == 0;
    public ICommand SaveCommand { get; }
    public bool HasChanges => DirtyCount > 0;
    public bool HasPatchPreview => PatchPreview.Length > 0;
    public bool HasValidationErrors => ValidationMessage.Length > 0;
    public bool HasSaveResult => SaveResultState.Length > 0 || SaveResultMessage.Length > 0;
    public bool HasSaveResultRevision => SaveResultRevision.Length > 0;
    public bool IsRawJsonDirty => !string.Equals(RawJson.Trim(), _originalRawJson.Trim(), StringComparison.Ordinal);
    public bool CanSave => SelectedModuleId.Length > 0 && HasChanges && !HasValidationErrors;
    public string ChangeSummary => HasChanges ? $"{DirtyCount} staged change(s)" : "No staged changes.";

    public int DirtyCount
    {
        get => _dirtyCount;
        private set
        {
            if (SetProperty(ref _dirtyCount, value))
            {
                OnPropertyChanged(nameof(HasChanges));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(ChangeSummary));
            }
        }
    }

    public string PatchPreview
    {
        get => _patchPreview;
        private set
        {
            if (SetProperty(ref _patchPreview, value))
            {
                OnPropertyChanged(nameof(HasPatchPreview));
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
                OnPropertyChanged(nameof(HasValidationErrors));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public string RawJson
    {
        get => _rawJson;
        set
        {
            if (SetProperty(ref _rawJson, value))
            {
                RefreshStagedChanges();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string SaveResultState
    {
        get => _saveResultState;
        private set
        {
            if (SetProperty(ref _saveResultState, value))
            {
                OnPropertyChanged(nameof(HasSaveResult));
            }
        }
    }

    public string SaveResultTitle
    {
        get => _saveResultTitle;
        private set => SetProperty(ref _saveResultTitle, value);
    }

    public string SaveResultMessage
    {
        get => _saveResultMessage;
        private set
        {
            if (SetProperty(ref _saveResultMessage, value))
            {
                OnPropertyChanged(nameof(HasSaveResult));
            }
        }
    }

    public string SaveResultRevision
    {
        get => _saveResultRevision;
        private set
        {
            if (SetProperty(ref _saveResultRevision, value))
            {
                OnPropertyChanged(nameof(HasSaveResultRevision));
            }
        }
    }

    public void ApplySaveResult(string state, string title, string message, ulong revision, bool saved)
    {
        SaveResultState = string.IsNullOrWhiteSpace(state)
            ? (saved ? "stored" : "failed")
            : state;
        SaveResultTitle = string.IsNullOrWhiteSpace(title)
            ? (saved ? "Settings saved" : "Settings save failed")
            : title;
        SaveResultMessage = message;
        SaveResultRevision = revision == 0 ? "" : $"Revision {revision}";
        StatusText = message;

        if (saved)
        {
            Revision = revision;
            OnPropertyChanged(nameof(Revision));
            _originalRawJson = RawJson;
            foreach (var field in Fields)
            {
                field.AcceptCurrentValue();
            }
            foreach (var hotkey in Hotkeys)
            {
                hotkey.AcceptCurrentValue();
            }

            RefreshStagedChanges();
        }
    }

    public void RefreshStagedChanges()
    {
        if (UsesRawJson)
        {
            var changed = IsRawJsonDirty;
            var rawDirtyHotkeys = HotkeyDirtySummaries();
            DirtyCount = (changed ? 1 : 0) + rawDirtyHotkeys.Length;
            PatchPreview = string.Join(
                Environment.NewLine,
                (changed ? new[] { $"rawJson: {RawJson.Length} character(s) staged." } : Array.Empty<string>()).Concat(rawDirtyHotkeys));
            ValidationMessage = ValidateRawJson();
            _saveCommand.NotifyCanExecuteChanged();
            return;
        }

        var validationMessages = Fields
            .Select(field => field.ValidationMessage)
            .Where(message => message.Length > 0)
            .ToArray();
        var dirtyFields = Fields
            .Where(field => field.IsDirty)
            .Select(field => field.DirtySummary)
            .ToArray();
        var dirtyHotkeys = HotkeyDirtySummaries();
        DirtyCount = dirtyFields.Length + dirtyHotkeys.Length;
        PatchPreview = string.Join(Environment.NewLine, dirtyFields.Concat(dirtyHotkeys));
        ValidationMessage = string.Join(" ", validationMessages);
        _saveCommand.NotifyCanExecuteChanged();
    }

    private string[] HotkeyDirtySummaries()
    {
        return Hotkeys
            .Where(hotkey => hotkey.IsDirty || hotkey.ResetRequested)
            .Select(hotkey => hotkey.ResetRequested
                ? $"{hotkey.Id}: reset to {hotkey.DefaultGesture}"
                : hotkey.OriginalEnabled != hotkey.Enabled
                    ? $"{hotkey.Id}: {(hotkey.Enabled ? "enable" : "disable")} with {hotkey.Gesture}"
                    : $"{hotkey.Id}: {hotkey.OriginalGesture} -> {hotkey.Gesture}")
            .ToArray();
    }

    private string ValidateRawJson()
    {
        if (!UsesRawJson || string.IsNullOrWhiteSpace(RawJson))
        {
            return "";
        }

        try
        {
            return JsonNode.Parse(RawJson) is JsonObject ? "" : "Raw settings must be a JSON object.";
        }
        catch (JsonException ex)
        {
            return $"Raw settings JSON is invalid: {ex.Message}";
        }
    }
}
