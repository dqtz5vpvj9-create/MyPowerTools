using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Controls;
using MyPowerTools.Abstractions;

namespace MyPowerTools.AvaloniaSdk;

public interface IMptAvaloniaSurfaceFactory
{
    Control CreateSurface(MptAvaloniaSurfaceContext context);
}

public sealed record MptAvaloniaSurfaceContext(
    string ToolId,
    string RouteId,
    string DataDirectory,
    string Theme,
    Func<string, JsonObject?, CancellationToken, Task<CommandExecutionResult>> ExecuteCommandAsync,
    Func<string, string, JsonObject?, Task> NavigateAsync,
    Action<MptSurfaceLogEntry> Log);

public sealed record MptSurfaceLogEntry(
    string Level,
    string Message,
    DateTimeOffset Time,
    JsonObject? Properties = null);

public static class MptSurfaceBridge
{
    public const string ContractVersion = "1.0";
}

/// <summary>
/// Minimal INotifyPropertyChanged base shared by Shell ViewModels and dotnet-surface tool ViewModels.
/// Tools that build their own Surface controls derive from this so they get property-change notification
/// without depending on a specific MVVM toolkit or the Shell assembly.
/// </summary>
public abstract class MptObservableViewModel : INotifyPropertyChanged
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

/// <summary>
/// Re-entrancy-guarded async command shared by Shell and surface tools. Prevents double-execution
/// of slow operations (like command invocation) and reports faults to an optional hook.
/// </summary>
public sealed class MptAsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private int _isRunning;

    public MptAsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, string? operationName = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        OperationName = operationName;
    }

    public string? OperationName { get; }

    public event EventHandler? CanExecuteChanged;

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? Volatile.Read(ref _isRunning) == 0;

    public async void Execute(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // Surface tools are responsible for their own error display; log to avoid silent swallow.
            System.Diagnostics.Debug.WriteLine($"Surface command {OperationName} failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }
}

/// <summary>
/// Product lifecycle state for tool surface pages, mirroring the Shell's ToolProductState.
/// </summary>
public enum ToolSurfaceState
{
    Loading,
    Empty,
    Ready,
    Failed
}

/// <summary>
/// Product-state ViewModel base for dotnet-surface tools, mirroring the Shell's
/// ToolProductPageViewModel so tool pages get loading/empty/ready/failed states without depending
/// on the Shell assembly.
/// </summary>
public abstract class ToolSurfacePageViewModel : MptObservableViewModel
{
    private ToolSurfaceState _productState;
    private string _errorMessage;
    private readonly string _title;
    private readonly string _subtitle;

    protected ToolSurfacePageViewModel(string title, string subtitle, ToolSurfaceState productState, string errorMessage = "")
    {
        _title = title;
        _subtitle = subtitle;
        _productState = productState;
        _errorMessage = errorMessage;
    }

    public string Title => _title;
    public string Subtitle => _subtitle;
    public string State => _productState.ToString().ToLowerInvariant();

    public ToolSurfaceState ProductState
    {
        get => _productState;
        protected set
        {
            if (SetProperty(ref _productState, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(State));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        protected set => SetProperty(ref _errorMessage, value);
    }

    public bool IsLoading => ProductState == ToolSurfaceState.Loading;
    public bool IsEmpty => ProductState == ToolSurfaceState.Empty;
    public bool IsReady => ProductState == ToolSurfaceState.Ready;
    public bool IsFailed => ProductState == ToolSurfaceState.Failed;

    public void SetProductState(ToolSurfaceState state, string errorMessage = "")
    {
        ErrorMessage = errorMessage;
        ProductState = state;
    }
}
