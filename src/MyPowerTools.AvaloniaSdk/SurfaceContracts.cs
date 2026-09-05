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

/// <summary>
/// Optional contract for a surface that can consume an external activation after the Shell
/// dynamically loads and navigates to it.
/// </summary>
public interface IMptAvaloniaSurfaceActivationHandler
{
    ValueTask<bool> ActivateAsync(
        ToolActivationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MptAvaloniaSurfaceContext(
    string ToolId,
    string RouteId,
    string DataDirectory,
    string Theme,
    Func<string, JsonObject?, CancellationToken, Task<CommandExecutionResult>> ExecuteCommandAsync,
    Func<string, string, JsonObject?, Task> NavigateAsync,
    IServiceUnitClient ServiceUnits,
    Action<MptSurfaceLogEntry> Log,
    Func<Action<MptSurfaceEvent>, IDisposable>? SubscribeEvents = null)
{
    /// <summary>
    /// Optional host capability for embedding a process-isolated web surface. Older hosts leave
    /// this property <see langword="null"/> and tools should offer their external-browser path.
    /// </summary>
    public IMptWebSurfaceService? WebSurfaces { get; init; }
    public Func<string?, Task>? OpenShortcutSettingsAsync { get; init; }
}

public enum MptWebSurfaceState
{
    Loading,
    Ready,
    Unavailable,
    Failed
}

public sealed record MptWebSurfaceRequest(
    string ToolId,
    string RouteId,
    Uri Source,
    IReadOnlyList<Uri> AllowedOrigins,
    Func<string, CancellationToken, Task<string>>? HandleBridgeRequestAsync = null);

public sealed class MptWebSurfaceStateChangedEventArgs(
    MptWebSurfaceState state,
    string message = "") : EventArgs
{
    public MptWebSurfaceState State { get; } = state;
    public string Message { get; } = message;
}

public interface IMptWebSurfaceSession : IDisposable
{
    Control View { get; }
    MptWebSurfaceState State { get; }
    event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;
    void Reload();
}

public interface IMptWebSurfaceService
{
    IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request);
}

/// <summary>
/// A lightweight projection of the Runner host-event stream for the active tool surface.
/// The Shell reuses its existing stream, so surfaces receive module updates without polling
/// or opening another IPC connection.
/// </summary>
public sealed record MptSurfaceEvent(
    ulong Sequence,
    string SourceId,
    string Type,
    DateTimeOffset Time,
    JsonObject Payload);

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

    public bool CanExecute(object? parameter) => Volatile.Read(ref _isRunning) == 0 && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Surface command {OperationName} failed: {ex.Message}");
            MptCommandFaultBoundary.TraceFault(OperationName ?? "(unnamed)", ex);
        }
    }

    /// <summary>Await the same action used by ICommand without swallowing its error at a second entry point.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null) || Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0) return;
        NotifyCanExecuteChanged();
        try { await _execute(); }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
            NotifyCanExecuteChanged();
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
