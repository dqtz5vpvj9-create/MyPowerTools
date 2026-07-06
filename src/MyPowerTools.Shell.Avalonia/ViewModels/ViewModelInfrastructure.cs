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

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public void NotifyCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public abstract class ShellPageViewModel : ObservableViewModel
{
    private string _title;
    private string _subtitle;
    private string _state;

    protected ShellPageViewModel(string title, string subtitle = "", string state = "ready")
    {
        _title = title;
        _subtitle = subtitle;
        _state = state;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

public sealed class ShellChromeViewModel : ObservableViewModel
{
    private string _statusText = "";
    private string _runnerStatusText = "";
    private bool _isCommandPaletteOpen;
    private bool _isPermissionPromptOpen;

    public ShellChromeViewModel(
        IReadOnlyList<string> pageLabels,
        Func<string, Task>? navigate = null,
        Func<Task>? refresh = null,
        Func<Task>? openCommandPalette = null,
        Func<Task>? closeCommandPalette = null,
        Func<Task>? dismissPermissionPrompt = null)
    {
        NavigationItems = pageLabels
            .Select(label => new ShellNavigationItemViewModel(
                label,
                new AsyncRelayCommand(() => navigate?.Invoke(label) ?? Task.CompletedTask)))
            .ToArray();
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        OpenCommandPaletteCommand = new AsyncRelayCommand(() => openCommandPalette?.Invoke() ?? Task.CompletedTask);
        CloseCommandPaletteCommand = new AsyncRelayCommand(() => closeCommandPalette?.Invoke() ?? Task.CompletedTask);
        DismissPermissionPromptCommand = new AsyncRelayCommand(() => dismissPermissionPrompt?.Invoke() ?? Task.CompletedTask);
    }

    public IReadOnlyList<ShellNavigationItemViewModel> NavigationItems { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommandPaletteCommand { get; }
    public ICommand CloseCommandPaletteCommand { get; }
    public ICommand DismissPermissionPromptCommand { get; }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string RunnerStatusText
    {
        get => _runnerStatusText;
        set => SetProperty(ref _runnerStatusText, value);
    }

    public bool IsCommandPaletteOpen
    {
        get => _isCommandPaletteOpen;
        set => SetProperty(ref _isCommandPaletteOpen, value);
    }

    public bool IsPermissionPromptOpen
    {
        get => _isPermissionPromptOpen;
        set => SetProperty(ref _isPermissionPromptOpen, value);
    }

    public void SelectPage(string page)
    {
        foreach (var item in NavigationItems)
        {
            item.IsSelected = string.Equals(item.Label, page, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public sealed class ShellNavigationItemViewModel : ObservableViewModel
{
    private bool _isSelected;
    private string _selectionText = "";

    public ShellNavigationItemViewModel(string label, ICommand navigateCommand)
    {
        Label = label;
        NavigateCommand = navigateCommand;
    }

    public string Label { get; }
    public ICommand NavigateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                SelectionText = value ? "Selected" : "";
            }
        }
    }

    public string SelectionText
    {
        get => _selectionText;
        private set => SetProperty(ref _selectionText, value);
    }
}
