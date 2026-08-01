using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
using MyPowerTools.AvaloniaSdk;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

// Shell-side ObservableViewModel is now a thin alias over the SDK-shared MptObservableViewModel,
// so dotnet-surface tool ViewModels and Shell ViewModels share the same change-notification base.
public abstract class ObservableViewModel : MptObservableViewModel
{
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
    private string _selectedNavigationKey = "Home";
    private ExternalSdkToolViewModel? _headerContent;

    public ShellChromeViewModel(
        IReadOnlyList<string> pageLabels,
        Func<string, Task>? navigate = null,
        Func<Task>? refresh = null,
        Func<Task>? openCommandPalette = null,
        Func<Task>? closeCommandPalette = null,
        Func<Task>? dismissPermissionPrompt = null,
        string runtimeModeLabel = "INSTALLED",
        string runtimeLocation = "",
        string runtimeIdentityText = "INSTALLED")
    {
        RuntimeModeLabel = runtimeModeLabel;
        RuntimeLocation = runtimeLocation;
        RuntimeIdentityText = runtimeIdentityText;
        NavigationItems = pageLabels
            .Select(label => new ShellNavigationItemViewModel(
                label,
                new AsyncRelayCommand(() => navigate?.Invoke(label) ?? Task.CompletedTask)))
            .ToArray();
        TopNavigationItems = NavigationItems
            .Where(item => item.Label is "Home" or "Dashboard" or "Settings")
            .ToArray();
        ToolNavigationItems = new ObservableCollection<ShellNavigationItemViewModel>(
            NavigationItems.Where(item => item.Label is "Tools"));
        FooterNavigationItems = NavigationItems
            .Where(item => item.Label is "System")
            .ToArray();
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        OpenCommandPaletteCommand = new AsyncRelayCommand(() => openCommandPalette?.Invoke() ?? Task.CompletedTask);
        CloseCommandPaletteCommand = new AsyncRelayCommand(() => closeCommandPalette?.Invoke() ?? Task.CompletedTask);
        DismissPermissionPromptCommand = new AsyncRelayCommand(() => dismissPermissionPrompt?.Invoke() ?? Task.CompletedTask);
    }

    public IReadOnlyList<ShellNavigationItemViewModel> NavigationItems { get; }
    public IReadOnlyList<ShellNavigationItemViewModel> TopNavigationItems { get; }
    public ObservableCollection<ShellNavigationItemViewModel> ToolNavigationItems { get; }
    public IReadOnlyList<ShellNavigationItemViewModel> FooterNavigationItems { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenCommandPaletteCommand { get; }
    public ICommand CloseCommandPaletteCommand { get; }
    public ICommand DismissPermissionPromptCommand { get; }
    public string RuntimeModeLabel { get; }
    public string RuntimeLocation { get; }
    public string RuntimeIdentityText { get; }
    public bool HasRuntimeLocation => !string.IsNullOrWhiteSpace(RuntimeLocation);

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

    public ExternalSdkToolViewModel? HeaderContent
    {
        get => _headerContent;
        set
        {
            if (SetProperty(ref _headerContent, value))
            {
                OnPropertyChanged(nameof(HasHeaderContent));
            }
        }
    }

    public bool HasHeaderContent => HeaderContent is not null;

    public void SelectPage(string page)
    {
        _selectedNavigationKey = page;
        ApplyNavigationSelection();
    }

    public void SelectTool(string toolId)
    {
        _selectedNavigationKey = ToolNavigationKey(toolId);
        ApplyNavigationSelection();
    }

    public void SetDiscoveredTools(
        IReadOnlyList<ToolCardViewModel> tools,
        Func<string, Task> navigateTool,
        Func<string, Task>? closeWebTool = null,
        Func<string, bool>? isWebToolOpen = null,
        Func<string, string?>? resolveOpenTitle = null)
    {
        var allTools = NavigationItems.First(item => item.Label is "Tools");
        ToolNavigationItems.Clear();
        ToolNavigationItems.Add(allTools);

        foreach (var tool in tools.DistinctBy(item => item.ToolId, StringComparer.OrdinalIgnoreCase))
        {
            var toolId = tool.ToolId;
            ToolNavigationItems.Add(new ShellNavigationItemViewModel(
                ToolNavigationKey(toolId),
                new AsyncRelayCommand(() => navigateTool(toolId), operationName: $"NavigateTool:{toolId}"),
                displayLabel: tool.Title,
                iconGlyph: tool.IconGlyph,
                isMonogram: true,
                isEnabled: tool.CanOpen,
                closeCommand: tool.IsWebSurface && closeWebTool is not null
                    ? new AsyncRelayCommand(
                        () => closeWebTool(toolId),
                        operationName: $"CloseWebTool:{toolId}")
                    : null,
                isClosable: tool.IsWebSurface && isWebToolOpen?.Invoke(toolId) == true,
                openTitle: resolveOpenTitle?.Invoke(toolId)));
        }

        ApplyNavigationSelection();
    }

    public void SetNavigationCompact(bool compact)
    {
        foreach (var item in EnumerateNavigationItems())
        {
            item.SetCompact(compact);
        }
    }

    public void SetWebToolOpenState(string toolId, bool isOpen)
    {
        var item = ToolNavigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, ToolNavigationKey(toolId), StringComparison.OrdinalIgnoreCase));
        if (item is not null)
        {
            item.IsClosable = isOpen;
        }
    }

    public void RenameOpenTool(string toolId, string title)
    {
        var item = ToolNavigationItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, ToolNavigationKey(toolId), StringComparison.OrdinalIgnoreCase));
        item?.SetOpenTitle(title);
    }

    private void ApplyNavigationSelection()
    {
        foreach (var item in EnumerateNavigationItems())
        {
            item.IsSelected = string.Equals(
                item.Label,
                _selectedNavigationKey,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private IEnumerable<ShellNavigationItemViewModel> EnumerateNavigationItems()
    {
        return TopNavigationItems
            .Concat(ToolNavigationItems)
            .Concat(FooterNavigationItems)
            .Distinct();
    }

    private static string ToolNavigationKey(string toolId) => $"tool:{toolId}";
}
