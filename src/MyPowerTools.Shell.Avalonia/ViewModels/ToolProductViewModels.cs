using System.Globalization;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public enum ToolProductState
{
    Loading,
    Empty,
    Ready,
    Failed
}

public abstract class ToolProductPageViewModel : ShellPageViewModel
{
    private ToolProductState _productState;
    private string _errorMessage;

    protected ToolProductPageViewModel(
        string title,
        string subtitle,
        ToolProductState productState,
        string errorMessage = "")
        : base(title, subtitle, ToStateValue(productState))
    {
        _productState = productState;
        _errorMessage = errorMessage;
    }

    public ToolProductState ProductState
    {
        get => _productState;
        private set
        {
            if (!SetProperty(ref _productState, value))
            {
                return;
            }

            State = ToStateValue(value);
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsLoading => ProductState == ToolProductState.Loading;
    public bool IsEmpty => ProductState == ToolProductState.Empty;
    public bool IsReady => ProductState == ToolProductState.Ready;
    public bool IsFailed => ProductState == ToolProductState.Failed;

    public void SetProductState(ToolProductState state, string errorMessage = "")
    {
        ErrorMessage = errorMessage;
        ProductState = state;
        OnPropertyChanged(null);
    }

    private static string ToStateValue(ToolProductState state)
    {
        return state.ToString().ToLowerInvariant();
    }
}

public enum ToolAvailability
{
    Available,
    InDevelopment,
    Paused,
    Unavailable
}

public sealed class ToolCardViewModel : ObservableViewModel
{
    private bool _isFavorite;

    public ToolCardViewModel(
        string toolId,
        string title,
        string description,
        string category,
        string iconGlyph,
        string statusLabel,
        string statusDetail,
        ToolAvailability availability,
        bool isFavorite,
        Func<string, Task>? openTool = null,
        Func<string, bool, Task>? setFavorite = null,
        string primaryActionLabel = "Open tool")
    {
        ToolId = toolId;
        Title = title;
        Description = description;
        Category = category;
        IconGlyph = iconGlyph;
        StatusLabel = statusLabel;
        StatusDetail = statusDetail;
        Availability = availability;
        _isFavorite = isFavorite;
        PrimaryActionLabel = primaryActionLabel;

        OpenCommand = new AsyncRelayCommand(
            () => openTool?.Invoke(ToolId) ?? Task.CompletedTask,
            () => CanOpen);
        ToggleFavoriteCommand = new AsyncRelayCommand(async () =>
        {
            var requestedValue = !IsFavorite;
            if (setFavorite is not null)
            {
                await setFavorite(ToolId, requestedValue).ConfigureAwait(true);
            }

            IsFavorite = requestedValue;
        });
    }

    public string ToolId { get; }
    public string Title { get; }
    public string Description { get; }
    public string Category { get; }
    public string IconGlyph { get; }
    public string StatusLabel { get; }
    public string StatusDetail { get; }
    public ToolAvailability Availability { get; }
    public string PrimaryActionLabel { get; }
    public ICommand OpenCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public bool IsAvailable => Availability == ToolAvailability.Available;
    public bool IsInDevelopment => Availability == ToolAvailability.InDevelopment;
    public bool IsPaused => Availability == ToolAvailability.Paused;
    public bool IsUnavailable => Availability == ToolAvailability.Unavailable;
    public bool CanOpen => IsAvailable;
    public bool IsReadyStatus => IsAvailable &&
        string.Equals(StatusLabel, "Ready", StringComparison.OrdinalIgnoreCase);
    public bool IsAttentionStatus => IsInDevelopment || IsPaused ||
        (IsAvailable && !IsReadyStatus);
    public string OpenAutomationName => $"Open {Title}";

    public bool IsFavorite
    {
        get => _isFavorite;
        private set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteActionLabel));
                OnPropertyChanged(nameof(FavoriteAutomationName));
            }
        }
    }

    public string FavoriteActionLabel => IsFavorite ? "Unfavorite" : "Favorite";
    public string FavoriteAutomationName => IsFavorite
        ? $"Remove {Title} from favorites"
        : $"Add {Title} to favorites";
}

public sealed record HomeActivityItemViewModel(
    string Id,
    string ToolTitle,
    string Title,
    string StatusLabel,
    string TimeLabel,
    string Summary,
    ICommand OpenCommand);

public sealed record HomeDashboardActionViewModel(
    string Label,
    string Detail,
    string IconGlyph,
    ICommand ExecuteCommand,
    bool IsSymbolIcon = false);

public sealed record ProductActionViewModel(
    string Label,
    string Description,
    bool IsPrimary,
    ICommand ExecuteCommand);
