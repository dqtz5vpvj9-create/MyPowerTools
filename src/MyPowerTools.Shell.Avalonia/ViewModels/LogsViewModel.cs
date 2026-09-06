using System.Collections.ObjectModel;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class LogsViewModel : ShellPageViewModel
{
    private readonly List<LogLineViewModel> _allLines = [];
    private string _searchText = "";
    private string _levelFilter = "All";
    private bool _wrapLines = true;
    private string _errorMessage = "";
    private string _actionStatus = "";

    public LogsViewModel(
        string selectedModuleName,
        IReadOnlyList<ModulePickerItemViewModel> modules,
        IReadOnlyList<LogLineViewModel> lines,
        Func<Task>? refresh = null,
        string? errorMessage = null)
        : base("Logs", selectedModuleName, modules.Count == 0 ? "empty" : "ready")
    {
        Modules = modules;
        _allLines.AddRange(lines);
        Lines = [];
        ErrorMessage = errorMessage ?? "";
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ToggleWrapCommand = new AsyncRelayCommand(ToggleWrapAsync);
        AllCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("All"));
        InfoCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Info"));
        WarningCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Warning"));
        ErrorCommand = new AsyncRelayCommand(() => SetLevelFilterAsync("Error"));
        ApplyFilter();
    }

    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; private set; }
    public ObservableCollection<LogLineViewModel> Lines { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ToggleWrapCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand InfoCommand { get; }
    public ICommand WarningCommand { get; }
    public ICommand ErrorCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                ApplyFilter();
            }
        }
    }

    public string LevelFilter
    {
        get => _levelFilter;
        set
        {
            if (SetProperty(ref _levelFilter, value))
            {
                OnPropertyChanged(nameof(AllLabel));
                OnPropertyChanged(nameof(InfoLabel));
                OnPropertyChanged(nameof(WarningLabel));
                OnPropertyChanged(nameof(ErrorLabel));
                ApplyFilter();
            }
        }
    }

    public bool WrapLines
    {
        get => _wrapLines;
        set
        {
            if (SetProperty(ref _wrapLines, value))
            {
                OnPropertyChanged(nameof(WrapText));
            }
        }
    }

    public string WrapText => WrapLines ? "Wrap: On" : "Wrap: Off";
    public string AllLabel => LevelFilter == "All" ? "● All" : "All";
    public string InfoLabel => LevelFilter == "Info" ? "● Info" : "Info";
    public string WarningLabel => LevelFilter == "Warning" ? "● Warning" : "Warning";
    public string ErrorLabel => LevelFilter == "Error" ? "● Error" : "Error";

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value ?? ""))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasNoModules => Modules.Count == 0;
    public bool HasNoLogs => Modules.Count > 0 && Lines.Count == 0;
    public string FilterSummary => $"{Lines.Count}/{_allLines.Count} 行";

    public string ActionStatus
    {
        get => _actionStatus;
        private set
        {
            if (SetProperty(ref _actionStatus, value ?? ""))
            {
                OnPropertyChanged(nameof(HasActionStatus));
            }
        }
    }

    public bool HasActionStatus => !string.IsNullOrWhiteSpace(ActionStatus);

    public string CopyText => string.Join(
        Environment.NewLine,
        Lines.Select(line => $"[{line.Time}] [{line.Level}] {line.Message}"));

    /// <summary>Updates source data in place, preserving the user's query, level, wrapping and row identity.</summary>
    public void RefreshFrom(LogsViewModel refreshed)
    {
        ArgumentNullException.ThrowIfNull(refreshed);
        if (ReferenceEquals(this, refreshed)) return;
        Modules = refreshed.Modules;
        Subtitle = refreshed.Subtitle;
        _allLines.Clear();
        _allLines.AddRange(refreshed._allLines);
        ErrorMessage = refreshed.ErrorMessage;
        OnPropertyChanged(nameof(Modules));
        OnPropertyChanged(nameof(HasNoModules));
        ApplyFilter();
    }

    public void ReportRefreshFailure(string message)
    {
        ErrorMessage = message;
        State = "error";
    }

    private async Task ToggleWrapAsync()
    {
        WrapLines = !WrapLines;
        await Task.CompletedTask;
    }

    private async Task ExportAsync()
    {
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyPowerTools",
                "logs");
            Directory.CreateDirectory(logsDir);
            var exportPath = Path.Combine(
                logsDir,
                $"logs-export-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            await File.WriteAllTextAsync(exportPath, CopyText);
            ActionStatus = $"已导出 {Lines.Count} 行到 {exportPath}";
        }
        catch (Exception ex)
        {
            ActionStatus = $"导出失败：{ex.Message}";
        }
    }

    private async Task SetLevelFilterAsync(string level)
    {
        LevelFilter = level;
        await Task.CompletedTask;
    }

    private void ApplyFilter()
    {
        var query = _searchText?.Trim() ?? "";
        var desired = new List<LogLineViewModel>();
        foreach (var line in _allLines)
        {
            if (_levelFilter != "All" &&
                !string.Equals(line.Level, _levelFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (query.Length > 0 &&
                !line.Message.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !line.Level.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            desired.Add(line);
        }

        // Reuse equal records instead of resetting ItemsSource on every refresh.
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Lines.Count && Lines[index] == desired[index]) continue;
            var existing = -1;
            for (var candidate = index + 1; candidate < Lines.Count; candidate++)
            {
                if (Lines[candidate] == desired[index]) { existing = candidate; break; }
            }
            if (existing >= 0) Lines.Move(existing, index);
            else Lines.Insert(index, desired[index]);
        }
        while (Lines.Count > desired.Count) Lines.RemoveAt(Lines.Count - 1);
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(HasNoLogs));
        OnPropertyChanged(nameof(FilterSummary));
        State = HasError
            ? "error"
            : Modules.Count == 0
                ? "empty"
                : Lines.Count == 0
                    ? "empty"
                    : "ready";
    }
}
