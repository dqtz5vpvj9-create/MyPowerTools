using System.Collections.ObjectModel;
using System.Globalization;
using MyPowerTools.AvaloniaSdk;
using RemoteNotifications.Surface.ViewModels;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// One notification row on the phone list. It wraps the shipped
/// <see cref="RemoteNotificationMessageViewModel"/> - so the label, relative time, reference-block
/// and session formatting stay the desktop product's own - and adds only the phone-specific state:
/// unread dot, the two-line preview and which day group the row belongs to.
/// </summary>
public sealed class MobileNotificationCardViewModel : MptObservableViewModel
{
    private const int PreviewLength = 220;

    private bool _isUnread;

    public MobileNotificationCardViewModel(RemoteNotificationMessageViewModel message, bool isUnread)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        _isUnread = isUnread;
    }

    public RemoteNotificationMessageViewModel Message { get; }

    public string Id => Message.Id;

    /// <summary>Unread means "arrived since this device last opened / marked the inbox as read".</summary>
    public bool IsUnread
    {
        get => _isUnread;
        set => SetProperty(ref _isUnread, value);
    }

    /// <summary>Row title: the sender's label, or the real channel when the message has no label.</summary>
    public string Title => !string.IsNullOrWhiteSpace(Message.Label)
        ? Message.Label
        : Message.HasCustomChannel
            ? Message.Channel
            : "远程通知";

    /// <summary>Collapsed text: the reply body with the quoted request kept in its shipped format.</summary>
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

    /// <summary>Full body, including the original quoted request block exactly as shipped.</summary>
    public string Detail => Message.DisplayMessage ?? "";

    public string Label => Message.Label;
    public string RelativeTime => Message.RelativeTime;
    public string AbsoluteTime => Message.AbsoluteTime;
    public string SessionDisplay => Message.HasSession ? Message.SessionDisplay : "";
    public bool HasSession => Message.HasSession;
    public string SessionId => Message.SessionId;
    public string SessionPositionText => Message.SessionPositionText;
    public bool HasSessionPosition => Message.HasSessionPosition;
    public string IconGlyph => Message.IconGlyph;
    public string IconBackground => Message.IconBackground;
    public string IconForeground => Message.IconForeground;
    public bool HasCustomChannel => Message.HasCustomChannel;
    public string Channel => Message.Channel;

    /// <summary>Where the message came from: the real sender client, or the channel it arrived on.</summary>
    public string SourceText => !string.IsNullOrWhiteSpace(Message.SourceClient)
        ? Message.SourceClient
        : Message.HasCustomChannel
            ? Message.Channel
            : "远程通知";

    /// <summary>Prototype row subtitle, fed by real sender/time values: "来源 · 时间".</summary>
    public string Subtitle => $"{SourceText} · {RelativeTime}";

    /// <summary>Local calendar day of the message, used to build the real 今天 / 昨天 / 更早 groups.</summary>
    public DateTime LocalDay
    {
        get
        {
            if (RemoteNotificationMessageViewModel.TryParseServerTimestamp(Message.Timestamp, out var parsed))
            {
                return parsed.ToLocalTime().Date;
            }

            return DateTime.Today;
        }
    }

    public void RefreshRelativeTime()
    {
        Message.RefreshRelativeTime();
        OnPropertyChanged(nameof(RelativeTime));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>Search spans the same fields the desktop surface searches, plus label and sender.</summary>
    public bool MatchesSearch(string query) =>
        query.Length == 0 ||
        Message.MatchesSearch(query) ||
        Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        SourceText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Channel.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A real day bucket in the notification list: 今天 / 昨天 / 具体日期.</summary>
public sealed class MobileNotificationDayGroupViewModel : MptObservableViewModel
{
    public MobileNotificationDayGroupViewModel(
        string title,
        IEnumerable<MobileNotificationCardViewModel> cards,
        bool isFirst)
    {
        Title = title;
        Cards = new ObservableCollection<MobileNotificationCardViewModel>(cards);
        IsFirst = isFirst;
    }

    public string Title { get; }

    /// <summary>The newest group carries the prototype's 全部已读 action.</summary>
    public bool IsFirst { get; }

    public ObservableCollection<MobileNotificationCardViewModel> Cards { get; }

    public string CountText => $"{Cards.Count} 条";
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

/// <summary>
/// Bottom-sheet content for one notification: the shipped full body (quoted block included), the
/// session position resolved through the shipped <c>RemoteNotificationSessionChain</c>, and the real
/// sender/channel/time metadata the desktop detail window shows.
/// </summary>
public sealed class MobileNotificationDetailViewModel : MptObservableViewModel
{
    public MobileNotificationDetailViewModel(MobileNotificationCardViewModel card)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
    }

    public MobileNotificationCardViewModel Card { get; }

    public string Title => Card.Title;
    public string Body => Card.Detail;
    public string AbsoluteTime => Card.AbsoluteTime;
    public string RelativeTime => Card.RelativeTime;
    public string Channel => Card.Channel;
    public bool HasCustomChannel => Card.HasCustomChannel;
    public string SessionDisplay => Card.SessionDisplay;
    public bool HasSession => Card.HasSession;
    public string SessionId => Card.SessionId;
    public bool IsUnread => Card.IsUnread;
    public string MarkReadText => Card.IsUnread ? "标为已读" : "已读";

    /// <summary>Real "N / M" position inside the session chain, when the message has a session.</summary>
    public string PositionText => Card.HasSessionPosition ? Card.SessionPositionText : "";
    public bool HasPosition => Card.HasSessionPosition;

    public string MetaText => string.Join(
        " · ",
        new[]
        {
            Card.SourceText,
            Card.HasCustomChannel ? Card.Channel : "",
            Card.HasSession ? Card.SessionDisplay : "",
            Card.AbsoluteTime
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsUnread));
        OnPropertyChanged(nameof(MarkReadText));
        OnPropertyChanged(nameof(RelativeTime));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(HasPosition));
    }

    public static string FormatDayTitle(DateTime day, DateTime today) =>
        day == today
            ? "今天"
            : day == today.AddDays(-1)
                ? "昨天"
                : day.Year == today.Year
                    ? day.ToString("M月d日", CultureInfo.InvariantCulture)
                    : day.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);
}
