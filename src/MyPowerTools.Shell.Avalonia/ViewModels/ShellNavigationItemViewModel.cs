using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class ShellNavigationItemViewModel : ObservableViewModel
{
    private bool _isSelected;
    private bool _isLabelVisible = true;
    private double _itemWidth = 216;
    private string _selectionText = "";
    private string _displayLabel;
    private readonly string _defaultDisplayLabel;
    private bool _isClosable;

    public ShellNavigationItemViewModel(
        string label,
        ICommand navigateCommand,
        string? displayLabel = null,
        string? iconGlyph = null,
        bool isMonogram = false,
        bool isEnabled = true,
        ICommand? closeCommand = null,
        bool isClosable = false,
        string? openTitle = null)
    {
        Label = label;
        NavigateCommand = navigateCommand;
        _defaultDisplayLabel = displayLabel ?? ResolveDisplayLabel(label);
        _displayLabel = string.IsNullOrWhiteSpace(openTitle)
            ? _defaultDisplayLabel
            : openTitle;
        IconGlyph = iconGlyph ?? ResolveIconGlyph(label);
        IsMonogram = isMonogram;
        IsEnabled = isEnabled;
        CloseCommand = closeCommand;
        _isClosable = isClosable;
    }

    public string Label { get; }
    public string DisplayLabel => _displayLabel;
    public string IconGlyph { get; }
    public bool IsMonogram { get; }
    public bool IsEnabled { get; }
    public ICommand NavigateCommand { get; }
    public ICommand? CloseCommand { get; }
    public bool CanClose => CloseCommand is not null;

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

    public bool IsClosable
    {
        get => _isClosable;
        set
        {
            if (SetProperty(ref _isClosable, value))
            {
                OnPropertyChanged(nameof(IsCloseButtonVisible));
            }
        }
    }

    public bool IsCloseButtonVisible => IsClosable && IsLabelVisible;

    internal void SetCompact(bool compact)
    {
        IsLabelVisible = !compact;
        ItemWidth = compact ? 52 : 216;
        OnPropertyChanged(nameof(IsCloseButtonVisible));
    }

    internal void SetOpenTitle(string title)
    {
        var resolved = string.IsNullOrWhiteSpace(title) ? _defaultDisplayLabel : title;
        if (string.Equals(_displayLabel, resolved, StringComparison.Ordinal))
        {
            return;
        }
        _displayLabel = resolved;
        OnPropertyChanged(nameof(DisplayLabel));
    }

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
}
