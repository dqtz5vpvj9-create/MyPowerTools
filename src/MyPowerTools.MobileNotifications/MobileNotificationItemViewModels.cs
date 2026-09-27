using MyPowerTools.AvaloniaSdk;
using RemoteNotifications.Surface.ViewModels;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// One notification card on the phone list. Wraps the shipped
/// <see cref="RemoteNotificationMessageViewModel"/> (label, relative time, reference-block display
/// text, icon and session formatting all come from the desktop product) and adds the only
/// phone-specific state: whether the card is expanded inline instead of opening a desktop window.
/// </summary>
public sealed class MobileNotificationCardViewModel : MptObservableViewModel
{
    private bool _isExpanded;
    private bool _isUnread;
    private const int PreviewLength = 180;

    public MobileNotificationCardViewModel(RemoteNotificationMessageViewModel message, bool isUnread)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        _isUnread = isUnread;
    }

    public RemoteNotificationMessageViewModel Message { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsUnread
    {
        get => _isUnread;
        set => SetProperty(ref _isUnread, value);
    }

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Collapsed text: the reply body with the quoted request removed, trimmed for a phone row.</summary>
    public string Preview
    {
        get
        {
            var text = (Message.DisplayMessage ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (text.Length <= PreviewLength)
            {
                return text;
            }

            return $"{text[..PreviewLength].TrimEnd()}…";
        }
    }

    public string Detail => Message.DisplayMessage ?? "";
    public string Label => Message.Label;
    public string RelativeTime => Message.RelativeTime;
    public string AbsoluteTime => Message.AbsoluteTime;
    public string SessionDisplay => Message.HasSession ? Message.SessionDisplay : "";
    public bool HasSession => Message.HasSession;
    public string IconGlyph => Message.IconGlyph;
    public string IconBackground => Message.IconBackground;
    public string IconForeground => Message.IconForeground;
    public bool HasCustomChannel => Message.HasCustomChannel;
    public string Channel => Message.Channel;

    public void RefreshRelativeTime()
    {
        Message.RefreshRelativeTime();
        OnPropertyChanged(nameof(RelativeTime));
    }
}

/// <summary>A label filter chip in the horizontal strip above the list.</summary>
public sealed class MobileNotificationLabelViewModel : MptObservableViewModel
{
    private bool _isSelected;
    private bool _isUnread;

    public MobileNotificationLabelViewModel(
        string label,
        string? filterValue,
        bool isSelected,
        bool isUnread,
        Func<string?, Task> select)
    {
        Label = label;
        FilterValue = filterValue;
        _isSelected = isSelected;
        _isUnread = isUnread;
        SelectCommand = new MptAsyncRelayCommand(() => select(FilterValue));
    }

    public string Label { get; }
    public string? FilterValue { get; }
    public System.Windows.Input.ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsUnread
    {
        get => _isUnread;
        set => SetProperty(ref _isUnread, value);
    }
}
