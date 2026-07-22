using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class DevSourceSettingsViewModel : ObservableViewModel
{
    private readonly DevSourceSyncService _service;
    private bool _enabled;
    private bool _syncOnRefresh;
    private string _newName = "";
    private string _newSourceDir = "";
    private string _newTargetDir = "";
    private string _newToolId = "";
    private string _newPatterns = "*.dll,*.pdb,*.deps.json";
    private string _statusText = "";

    public DevSourceSettingsViewModel(DevSourceSyncService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        var snapshot = service.Snapshot;
        _enabled = snapshot.Enabled;
        _syncOnRefresh = snapshot.SyncOnRefresh;
       Mappings = new ObservableCollection<DevSourceMappingViewModel>(
            snapshot.Mappings.Select<DevSourceMapping, DevSourceMappingViewModel>(
                mapping => DevSourceMappingViewModel.From(mapping, RemoveMapping)));
       AddMappingCommand = new AsyncRelayCommand(AddMappingAsync, operationName: "DevSourceAddMapping");
        SyncNowCommand = new AsyncRelayCommand(SyncNowAsync, operationName: "DevSourceSyncNow");
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _service.Update(settings => settings.Enabled = value);
            }
        }
    }

    public bool SyncOnRefresh
    {
        get => _syncOnRefresh;
        set
        {
            if (SetProperty(ref _syncOnRefresh, value))
            {
                _service.Update(settings => settings.SyncOnRefresh = value);
            }
        }
    }

    public ObservableCollection<DevSourceMappingViewModel> Mappings { get; }

    public string NewName
    {
        get => _newName;
        set => SetProperty(ref _newName, value);
    }

    public string NewSourceDir
    {
        get => _newSourceDir;
        set => SetProperty(ref _newSourceDir, value);
    }

    public string NewTargetDir
    {
        get => _newTargetDir;
        set => SetProperty(ref _newTargetDir, value);
    }

    public string NewToolId
    {
        get => _newToolId;
        set => SetProperty(ref _newToolId, value);
    }

    public string NewPatterns
    {
        get => _newPatterns;
        set => SetProperty(ref _newPatterns, value);
    }

    public ICommand AddMappingCommand { get; }

    public ICommand SyncNowCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private Task AddMappingAsync()
    {
        if (string.IsNullOrWhiteSpace(_newSourceDir) || string.IsNullOrWhiteSpace(_newTargetDir))
        {
            StatusText = "Source and target directories are required.";
            return Task.CompletedTask;
        }

        var name = string.IsNullOrWhiteSpace(_newName)
            ? System.IO.Path.GetFileName(_newSourceDir.TrimEnd('\\', '/'))
            : _newName.Trim();
        var mapping = new DevSourceMapping
        {
            Name = name,
            SourceDir = _newSourceDir.Trim(),
            TargetDir = _newTargetDir.Trim(),
            ToolId = string.IsNullOrWhiteSpace(_newToolId) ? null : _newToolId.Trim(),
            FilePatterns = ParsePatterns(_newPatterns)
        };
        _service.Update(settings => settings.Mappings.Add(mapping));
        Mappings.Add(DevSourceMappingViewModel.From(mapping, RemoveMapping));
        NewName = "";
        NewSourceDir = "";
        NewTargetDir = "";
        NewToolId = "";
        StatusText = $"Added '{mapping.Name}'.";
        return Task.CompletedTask;
    }

    private void RemoveMapping(DevSourceMappingViewModel item)
    {
        _service.Update(settings => settings.Mappings.RemoveAll(existing =>
            string.Equals(existing.Name, item.Name, StringComparison.Ordinal)
            && string.Equals(existing.SourceDir, item.SourceDir, StringComparison.Ordinal)));
        Mappings.Remove(item);
        StatusText = $"Removed '{item.Name}'.";
    }

    private async Task SyncNowAsync()
    {
        StatusText = "Syncing developer sources…";
        try
        {
            var outcome = await _service.SyncAllAsync();
            StatusText = outcome.Summary;
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
    }

    private static List<string> ParsePatterns(string text)
    {
        var patterns = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return patterns.Length == 0 ? new List<string> { "*" } : patterns.ToList();
    }
}

public sealed class DevSourceMappingViewModel : ObservableViewModel
{
    private readonly Action<DevSourceMappingViewModel> _remove;

    private DevSourceMappingViewModel(
        string name,
        string sourceDir,
        string targetDir,
        string toolId,
        string patterns,
        Action<DevSourceMappingViewModel> remove)
    {
        Name = name;
        SourceDir = sourceDir;
        TargetDir = targetDir;
        ToolId = toolId;
        Patterns = patterns;
        _remove = remove;
        RemoveCommand = new AsyncRelayCommand(
            () =>
            {
                remove(this);
                return Task.CompletedTask;
            },
            operationName: "DevSourceRemoveMapping");
    }

    public string Name { get; }
    public string SourceDir { get; }
    public string TargetDir { get; }
    public string ToolId { get; }
    public string Patterns { get; }
    public ICommand RemoveCommand { get; }

    public static DevSourceMappingViewModel From(DevSourceMapping mapping, Action<DevSourceMappingViewModel> remove)
    {
        return new DevSourceMappingViewModel(
            mapping.Name,
            mapping.SourceDir,
            mapping.TargetDir,
            mapping.ToolId ?? "",
            string.Join(", ", mapping.FilePatterns),
            remove);
    }
}
