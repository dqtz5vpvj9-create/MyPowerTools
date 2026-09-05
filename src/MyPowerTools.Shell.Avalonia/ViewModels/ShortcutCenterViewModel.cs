using System.Text.Json;
using System.Windows.Input;
using MyPowerTools.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed record ShortcutExport(int SchemaVersion, IReadOnlyList<ShortcutOverride> Overrides);

public sealed record ShortcutRow(ShortcutDefinition Definition, string Bindings, string Source,
    string Status, string Details, bool IsModified, bool IsConflict, bool IsUnbound)
{
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Owner => Definition.Owner;
    public string ScopeLabel => Definition.Scope switch
    {
        "system" => "System · active in background",
        "tool" => Definition.Context.Length == 0 ? "Tool · active workspace" : "Tool · " + Definition.Context,
        _ => "Application · focused window"
    };
    public string Summary => $"{Owner} · {ScopeLabel} · {Source}";
}

/// <summary>One editor for defaults, user overrides and actual registration outcomes.</summary>
public sealed class ShortcutCenterViewModel : ShellPageViewModel, IDisposable
{
    private readonly ShortcutConfigurationClient _client;
    private readonly Dictionary<string, string> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ShortcutRow> _rows = [];
    private ShortcutRow? _selected;
    private string _query = "", _filter = "All", _ownerFilter = "All tools", _platform;
    private string _bindingText = "", _status = "Loading shortcuts…";
    private bool _busy, _recording, _disposed, _platformInitialized;
    private IReadOnlyList<ShortcutEdit>? _undo;

    public ShortcutCenterViewModel(ShortcutConfigurationClient client) : base("Keyboard shortcuts",
        "One place for background, application and tool actions. Defaults remain separate from your changes.")
    {
        _client = client;
        _platform = client.Snapshot.Platform;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanEdit);
        ToggleDisabledCommand = new AsyncRelayCommand(ToggleDisabledAsync, () => CanEdit);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => CanEdit);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => !IsBusy && _undo is not null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        _client.Changed += OnChanged;
        Rebuild();
    }

    public IReadOnlyList<string> Filters { get; } = ["All", "Modified", "Conflicts", "System", "Unbound"];
    public IReadOnlyList<string> Platforms { get; } = ["windows", "macos", "linux"];
    public IReadOnlyList<string> Owners => new[] { "All tools" }.Concat(_rows.Select(row => row.Owner).Distinct().Order()).ToArray();
    public IReadOnlyList<ShortcutRow> Rows => _rows.Where(row =>
        (OwnerFilter == "All tools" || row.Owner == OwnerFilter) &&
        (Filter switch { "Modified" => row.IsModified, "Conflicts" => row.IsConflict,
            "System" => row.Definition.Scope == "system", "Unbound" => row.IsUnbound, _ => true }) &&
        Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            $"{row.Id} {row.Definition.ToolId} {row.Definition.ModuleId} {row.Title} {row.Owner} {row.Bindings} {row.ScopeLabel}".Contains(word, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    public string Query { get => _query; set { if (SetProperty(ref _query, value ?? "")) OnPropertyChanged(nameof(Rows)); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value ?? "All")) OnPropertyChanged(nameof(Rows)); } }
    public string OwnerFilter { get => _ownerFilter; set { if (SetProperty(ref _ownerFilter, value ?? "All tools")) OnPropertyChanged(nameof(Rows)); } }
    public string Summary => $"{_rows.Count} actions · {_rows.Count(row => row.IsModified)} modified · {_rows.Count(row => row.IsConflict)} conflicts · running on {_client.Snapshot.Platform}";
    public string SystemStatus => _client.Snapshot.SystemStatus;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            NotifyCommands();
        }
    }
    public bool CanEdit => !IsBusy && Selected is not null && _client.IsLoaded;
    public bool HasSelection => Selected is not null;
    public bool IsRecording
    {
        get => _recording;
        set { if (SetProperty(ref _recording, value)) OnPropertyChanged(nameof(RecordLabel)); }
    }
    public string RecordLabel => IsRecording ? "Stop recording" : "Record a key";
    public ICommand SaveCommand { get; }
    public ICommand ToggleDisabledCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RefreshCommand { get; }

    public ShortcutRow? Selected
    {
        get => _selected;
        set
        {
            if (value is null || ReferenceEquals(value, _selected)) return;
            StoreDraft();
            _selected = value;
            IsRecording = false;
            LoadDraft();
            SelectionChanged();
        }
    }
    public string EditorPlatform
    {
        get => _platform;
        set
        {
            if (string.IsNullOrEmpty(value) || _platform == value) return;
            StoreDraft(); _platform = value; OnPropertyChanged(); LoadDraft();
        }
    }
    public string BindingText
    {
        get => _bindingText;
        set
        {
            if (SetProperty(ref _bindingText, value ?? "")) OnPropertyChanged(nameof(Preview));
        }
    }
    public string SelectedDescription => Selected is null ? "Select an action to edit its bindings." :
        $"{Selected.Id}\n{Selected.Definition.Description}\n{Selected.Details}".Trim();
    public string DisabledLabel => IsDisabled ? "Enable shortcut" : "Disable shortcut";
    private bool IsDisabled => Selected is not null &&
        (_client.Snapshot.Configuration.Overrides.FirstOrDefault(item => item.Id == Selected.Id)?.Disabled ?? !Selected.Definition.EnabledByDefault);
    public string Preview
    {
        get
        {
            if (Selected is null) return "";
            try
            {
                var gestures = ParseLines();
                var preview = _client.Snapshot with { Platform = EditorPlatform };
                var collisions = ShortcutCatalog.Effective(preview).Where(item => item.Definition.Id != Selected.Id &&
                    ShortcutCatalog.Overlaps(item.Definition, Selected.Definition) &&
                    gestures.Contains(item.Gesture, StringComparer.OrdinalIgnoreCase)).Select(item => item.Definition.Title).Distinct().ToArray();
                return collisions.Length > 0
                    ? "Overlaps: " + string.Join(", ", collisions) + ". Explicit overrides win, then action ID. Change a binding or disable the other action."
                    : gestures.Count == 0 ? "No key will be assigned on this platform. Buttons and menus remain usable."
                    : "One binding per line. Save applies immediately; other platforms are preserved.";
            }
            catch (InvalidDataException ex) { return ex.Message; }
        }
    }

    public void Record(string gesture)
    {
        if (!IsRecording || !CanEdit) return;
        BindingText = string.IsNullOrWhiteSpace(BindingText) ? gesture : BindingText.TrimEnd() + Environment.NewLine + gesture;
        IsRecording = false;
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await _client.RefreshAsync(); Status = "Loaded. Select an action; record or type one key combination per line."; }
        catch (Exception ex) { Status = $"Could not load shortcuts: {ex.Message}. Existing bindings are retained. Use Refresh to retry."; }
        finally { IsBusy = false; }
    }

    public async Task SaveAsync()
    {
        if (!CanEdit || Selected is null) return;
        try
        {
            var gestures = ParseLines();
            var definition = Selected.Definition;
            var current = _client.Snapshot.Configuration.Overrides.FirstOrDefault(item => item.Id == definition.Id);
            var original = current?.Bindings ?? definition.DefaultBindings;
            // Expand all-platform entries for the unaffected platforms before replacing this one.
            // Removing F5 on Windows must not silently remove it on Linux/macOS as well.
            var retained = original.SelectMany(binding => binding.Platform == "all"
                ? Platforms.Select(platform => new ShortcutBinding(binding.Gesture, platform)) : [binding])
                .Where(binding => binding.Platform != EditorPlatform);
            var bindings = retained.Concat(gestures.Select(gesture => new ShortcutBinding(gesture, EditorPlatform))).Distinct().ToArray();
            await ApplyAsync([new(definition.Id, bindings, IsDisabled)]);
        }
        catch (Exception ex) { Status = $"Not saved: {ex.Message}"; }
    }

    public Task ToggleDisabledAsync()
    {
        if (!CanEdit || Selected is null) return Task.CompletedTask;
        var original = _client.Snapshot.Configuration.Overrides.FirstOrDefault(item => item.Id == Selected.Id)?.Bindings
            ?? Selected.Definition.DefaultBindings;
        return ApplyAsync([new(Selected.Id, original, !IsDisabled)]);
    }
    public Task ResetAsync() => CanEdit && Selected is not null
        ? ApplyAsync([new(Selected.Id, [], Reset: true)]) : Task.CompletedTask;

    public async Task UndoAsync()
    {
        if (IsBusy || _undo is null) return;
        var edits = _undo;
        await ApplyAsync(edits);
    }

    public string Export() => JsonSerializer.Serialize(new ShortcutExport(1, _client.Snapshot.Configuration.Overrides), ShortcutCatalog.JsonOptions);

    public async Task ImportAsync(string json)
    {
        if (IsBusy) return;
        try
        {
            var document = JsonSerializer.Deserialize<ShortcutExport>(json, ShortcutCatalog.JsonOptions);
            if (document is null || document.SchemaVersion != 1 || document.Overrides is null)
                throw new InvalidDataException("Expected a schemaVersion 1 shortcut export.");
            // Runner validates the entire edit set before committing the file. Unknown tool IDs
            // are deliberately preserved so exports can be shared across installations.
            await ApplyAsync(document.Overrides.Select(item => new ShortcutEdit(item.Id, item.Bindings, item.Disabled)).ToArray());
        }
        catch (Exception ex) { Status = $"Import not applied: {ex.Message}"; }
    }

    public void ReportFileOperation(string message) => Status = message;

    private async Task ApplyAsync(IReadOnlyList<ShortcutEdit> edits)
    {
        if (IsBusy) return;
        IsBusy = true;
        var before = _client.Snapshot.Configuration.Overrides.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var undo = edits.Select(edit => before.TryGetValue(edit.Id, out var old)
            ? new ShortcutEdit(old.Id, old.Bindings, old.Disabled) : new ShortcutEdit(edit.Id, [], Reset: true)).ToArray();
        try
        {
            await _client.ApplyAsync(edits);
            _undo = undo;
            foreach (var edit in edits)
                foreach (var platform in Platforms) _drafts.Remove(edit.Id + "|" + platform);
            LoadDraft();
            SelectionChanged();
            Status = "Saved. Application shortcuts are active immediately. System shortcuts show the actual registration outcome in the list.";
        }
        catch (Exception ex)
        {
            Status = $"Save not confirmed: {ex.Message}. Refresh to reconcile current settings; your typed draft is retained.";
        }
        finally { IsBusy = false; }
    }

    private IReadOnlyList<string> ParseLines() => BindingText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => ShortcutCatalog.Normalize(line.Trim()))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private void StoreDraft()
    {
        if (Selected is not null) _drafts[Selected.Id + "|" + EditorPlatform] = BindingText;
    }
    private void LoadDraft()
    {
        if (Selected is null) { BindingText = ""; return; }
        var bindings = _client.Snapshot.Configuration.Overrides.FirstOrDefault(item => item.Id == Selected.Id)?.Bindings
            ?? Selected.Definition.DefaultBindings;
        BindingText = _drafts.GetValueOrDefault(Selected.Id + "|" + EditorPlatform) ?? string.Join(Environment.NewLine,
            bindings.Where(binding => binding.Platform == "all" || binding.Platform == EditorPlatform)
                .Select(binding => ShortcutConfigurationClient.Display(binding.Gesture, EditorPlatform)).Distinct());
        OnPropertyChanged(nameof(Preview));
    }
    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(Selected)); OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(DisabledLabel)); OnPropertyChanged(nameof(Preview));
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        foreach (var command in new[] { SaveCommand, ToggleDisabledCommand, ResetCommand, UndoCommand, RefreshCommand })
            (command as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }
    private void OnChanged() { if (!_disposed) Rebuild(); }
    private void Rebuild()
    {
        var snapshot = _client.Snapshot;
        if (!_platformInitialized && _client.IsLoaded)
        {
            // The constructor cache can precede the first Runner response. Start editing the
            // confirmed platform, then preserve explicit platform selections on later refreshes.
            _platform = snapshot.Platform;
            _platformInitialized = true;
            OnPropertyChanged(nameof(EditorPlatform));
        }
        var effective = ShortcutCatalog.Effective(snapshot);
        var definitions = snapshot.Commands.Concat(snapshot.Configuration.Overrides
            .Where(item => !snapshot.Commands.Any(command => command.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(item => new ShortcutDefinition(item.Id, item.Id, item.Id, "Unavailable tool", "tool", [], Available: false)));
        _rows = definitions.Select(definition =>
        {
            var custom = snapshot.Configuration.Overrides.FirstOrDefault(item => item.Id == definition.Id);
            var active = effective.Where(item => item.Definition.Id == definition.Id).ToArray();
            var configured = (custom?.Bindings ?? definition.DefaultBindings)
                .Where(binding => binding.Platform == "all" || binding.Platform == snapshot.Platform).ToArray();
            var conflicts = active.SelectMany(item => effective.Where(other => other.Definition.Id != definition.Id &&
                other.Gesture.Equals(item.Gesture, StringComparison.OrdinalIgnoreCase) && ShortcutCatalog.Overlaps(definition, other.Definition)))
                .Select(item => item.Definition.Title).Distinct().ToArray();
            var registrations = snapshot.Registrations.Where(item => item.ShortcutId == definition.Id).ToArray();
            var disabled = custom?.Disabled ?? !definition.EnabledByDefault;
            var state = !definition.Available ? "Unavailable; override retained" : disabled ? "Disabled" : active.Length == 0 ? "Unbound" :
                definition.Scope == "system" ? string.Join(", ", active.Select(binding =>
                    registrations.FirstOrDefault(item => item.BindingId == binding.BindingId)?.State ?? "pending")) :
                conflicts.Length > 0 ? "Conflict · precedence applies" : "Active in declared scope";
            // Even after disabling, a failed OS unregistration must remain visible.
            if (registrations.Any(item => item.State == "unregister-failed")) state = "OS unregister failed · old key may remain active";
            var details = string.Join("\n", registrations.Select(item =>
                $"{item.RequestedGesture}: {item.Message}" + (item.ActualGesture.Length == 0 ? "" : $" Active: {item.ActualGesture}")));
            if (conflicts.Length > 0) details += "\nOverlaps: " + string.Join(", ", conflicts);
            return new ShortcutRow(definition, configured.Length == 0 ? "—" : string.Join(" / ", configured.Select(item =>
                ShortcutConfigurationClient.Display(item.Gesture, snapshot.Platform))), custom is null ? "Default" : "User",
                state, details, custom is not null, conflicts.Length > 0 || registrations.Any(item => item.State == "conflict"), configured.Length == 0);
        }).OrderBy(row => row.Owner).ThenBy(row => row.Title).ToArray();
        if (_selected is not null) _selected = _rows.FirstOrDefault(row => row.Id == _selected.Id);
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(Owners)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(SystemStatus));
        SelectionChanged();
    }

    public void Dispose() { _disposed = true; IsRecording = false; _client.Changed -= OnChanged; }
}
