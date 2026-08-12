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

    public IReadOnlyList<ModulePickerItemViewModel> Modules { get; }
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
        Lines.Clear();
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

            Lines.Add(line);
        }

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
