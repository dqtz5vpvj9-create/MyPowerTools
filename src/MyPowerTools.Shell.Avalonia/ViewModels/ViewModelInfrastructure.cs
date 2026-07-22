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
        Func<string, Task> navigateTool)
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
                isEnabled: tool.CanOpen));
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

public sealed class ShellNavigationItemViewModel : ObservableViewModel
{
    private bool _isSelected;
    private bool _isLabelVisible = true;
    private double _itemWidth = 216;
    private string _selectionText = "";

    public ShellNavigationItemViewModel(
        string label,
        ICommand navigateCommand,
        string? displayLabel = null,
        string? iconGlyph = null,
        bool isMonogram = false,
        bool isEnabled = true)
    {
        Label = label;
        NavigateCommand = navigateCommand;
        DisplayLabel = displayLabel ?? ResolveDisplayLabel(label);
        IconGlyph = iconGlyph ?? ResolveIconGlyph(label);
        IsMonogram = isMonogram;
        IsEnabled = isEnabled;
    }

    public string Label { get; }
    public string DisplayLabel { get; }
    public string IconGlyph { get; }
    public bool IsMonogram { get; }
    public bool IsEnabled { get; }
    public ICommand NavigateCommand { get; }

    private static string ResolveDisplayLabel(string label) => label switch
    {
        "Home" => "Dashboard",
        "Tools" => "All tools",
        "Notifications" => "Remote notifications",
        "ADB Forwarder" => "ADB Forwarder",
        "ScreenEase" => "ScreenEase",
        "Doubao Agent" => "豆包 Computer Use",
        "SmartBird" => "SmartBird 温度管理器",
        "Settings" => "General",
        _ => label
    };

    private static string ResolveIconGlyph(string label) => label switch
    {
        "Home" or "Dashboard" => "\uE80F",
        "Tools" or "Modules" => "\uE71D",
        "Activity" => "\uE823",
        "Notifications" => "\uEA8F",
        "ADB Forwarder" => "\uE968",
        "ScreenEase" => "\uE706",
        "Doubao Agent" => "\uE77B",
        "SmartBird" => "\uEC15",
        "Settings" => "\uE713",
        "System" or "Diagnostics" => "\uE9D9",
        "Commands" => "\uE756",
        "Logs" => "\uE8A5",
        "Packages" => "\uE7B8",
        _ => "\uE946"
    };

    public bool IsLabelVisible
    {
        get => _isLabelVisible;
        private set => SetProperty(ref _isLabelVisible, value);
    }

    public double ItemWidth
    {
        get => _itemWidth;
        private set => SetProperty(ref _itemWidth, value);
    }

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

    internal void SetCompact(bool compact)
    {
        IsLabelVisible = !compact;
        ItemWidth = compact ? 52 : 216;
    }
}
