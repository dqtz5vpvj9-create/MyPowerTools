using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.RemoteNotifications.Configuration;
using RemoteNotifications.Surface.Services;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Remote Notifications tool page for phones.
///
/// It is deliberately a code-built, single-column surface: header status, an explicit background
/// switch, search, label chips and an inline-expanding notification list, with settings in an
/// overlay panel. The desktop surface opens a detail window per message and talks to a service
/// unit; neither exists on Android, so this page drives the module's commands instead and expands
/// details in place.
/// </summary>
public sealed partial class MobileNotificationsView : UserControl, IMptAvaloniaSurfaceActivationHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly MobileNotificationsViewModel _viewModel;
    private readonly DispatcherTimer _relativeTimeTimer;

    public MobileNotificationsView(MptAvaloniaSurfaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        MobileNotificationPalette.Current = MobileNotificationPalette.ForTheme(context.Theme);

        var settingsStore = new RemoteNotificationSettingsStore(
            Path.Combine(context.DataDirectory, "settings.json"));
        var store = new RemoteNotificationsLegacyStore(settingsStore, context.DataDirectory);
        _viewModel = new MobileNotificationsViewModel(context, store, settingsStore);
        DataContext = _viewModel;

        Content = BuildLayout();
        _viewModel.Labels.CollectionChanged += (_, _) => RebuildLabelStrip();
        _viewModel.PropertyChanged += (_, _) => UpdateChrome();

        _relativeTimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _relativeTimeTimer.Tick += (_, _) => _viewModel.RefreshRelativeTimes();

        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.Activate();
            _relativeTimeTimer.Start();
            UpdateChrome();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _relativeTimeTimer.Stop();
            _viewModel.Deactivate();
        };
        RebuildLabelStrip();
        UpdateChrome();
    }

    // ---------------------------------------------------------------- chrome

    private void UpdateChrome()
    {
        _pageTitle.Text = _viewModel.PageTitle;
        _statusPill.Text = _viewModel.StatusText;
        _statusPill.Foreground = Parse(_viewModel.StatusColor);
        _statusPillBorder.Background = Parse(_viewModel.StatusBackground);

        var detail = _viewModel.LastPollText;
        if (!string.IsNullOrWhiteSpace(_viewModel.ServerText))
        {
            detail = $"{_viewModel.ServerText} · {detail}";
        }

        if (!string.IsNullOrWhiteSpace(_viewModel.SyncResult))
        {
            detail = $"{detail} · {_viewModel.SyncResult}";
        }

        _statusDetail.Text = $"{detail} · {_viewModel.CountText}";

        _syncButton.Content = _viewModel.SyncButtonText;
        _syncButton.IsEnabled = _viewModel.IsNotBusy;
        _markReadButton.IsVisible = _viewModel.IsInboxVisible && _viewModel.Labels.Any(chip => chip.IsUnread);
        _markReadButton.IsEnabled = _viewModel.IsNotBusy;

        _backgroundText.Text = _viewModel.BackgroundToggleText;
        _backgroundHint.Text = _viewModel.BackgroundHint;
        _backgroundButton.Content = _viewModel.BackgroundActionText;
        _backgroundButton.IsEnabled = _viewModel.CanToggleBackground && _viewModel.IsNotBusy;
        _backgroundButtonInSettings.Content = _viewModel.BackgroundActionText;
        _backgroundButtonInSettings.IsEnabled = _viewModel.CanToggleBackground && _viewModel.IsNotBusy;

        _errorCard.IsVisible = _viewModel.HasError;
        _errorText.Text = _viewModel.ErrorText;
        _errorDetails.Text = _viewModel.ErrorDetails;
        _errorDetails.IsVisible = _viewModel.IsErrorDetailsVisible;
        _errorDetailsButton.Content = _viewModel.ErrorDetailsActionText;

        _healthStrip.IsVisible = _viewModel.HasHealthText;
        _healthText.Text = _viewModel.HealthText;

        _searchRow.IsVisible = _viewModel.IsSearchVisible;
        _labelStrip.IsVisible = _viewModel.Labels.Count > 1;

        _emptyText.Text = _viewModel.EmptyText;
        _emptyText.IsVisible = _viewModel.ShowsEmptyState && _viewModel.IsInboxVisible;
        _messageList.IsVisible = !_viewModel.IsSettingsVisible;

        _settingsPanel.IsVisible = _viewModel.IsSettingsVisible;
        _settingsButton.Content = _viewModel.IsSettingsVisible ? "返回" : "设置";
        _keyStatus.Text = _viewModel.KeyStatusText;
        _keyStatus.Foreground = Parse(_viewModel.KeyStatusColor);
        _settingsFeedback.Text = _viewModel.SettingsFeedback;
        _settingsFeedback.Foreground = Parse(_viewModel.SettingsFeedbackColor);
        _settingsFeedback.IsVisible = _viewModel.HasSettingsFeedback;
    }

    private void RebuildLabelStrip()
    {
        _labelPanel.Children.Clear();
        _labelButtons.Clear();
        foreach (var chip in _viewModel.Labels)
        {
            var button = new Button
            {
                Content = chip.Label,
                MinHeight = 38,
                Padding = new Avalonia.Thickness(14, 6),
                FontSize = 13
            };
            button.Click += (_, _) => chip.SelectCommand.Execute(null);
            ApplyChipStyle(button, chip);
            chip.PropertyChanged += (_, _) =>
            {
                ApplyChipStyle(button, chip);
                UpdateChrome();
            };
            _labelButtons[chip] = button;
            _labelPanel.Children.Add(button);
        }

        UpdateChrome();
    }

    private void ApplyChipStyle(Button button, MobileNotificationLabelViewModel chip)
    {
        button.FontWeight = chip.IsSelected ? FontWeight.SemiBold : FontWeight.Normal;
        button.Background = Parse(chip.IsSelected ? "#2563EB" : chip.IsUnread ? "#DBEAFE" : "#E5E7EB");
        button.Foreground = Parse(chip.IsSelected ? "#FFFFFF" : "#111827");
    }

    // ---------------------------------------------------------------- activation

    /// <summary>
    /// Tapping the tray notification opens this tool page with
    /// <c>mypowertools://remote-notification?id=…</c>; expand that message so the user lands on the
    /// content they tapped.
    /// </summary>
    public ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var uri = (request.ActivationUri ?? "").Trim();
        const string prefix = "mypowertools://remote-notification?id=";
        if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(false);
        }

        var messageId = Uri.UnescapeDataString(uri[prefix.Length..]);
        var activated = _viewModel.TryActivate(messageId);
        if (activated)
        {
            Dispatcher.UIThread.Post(UpdateChrome);
        }

        return ValueTask.FromResult(activated);
    }

    private async Task CopyAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            await ClipboardExtensions.SetTextAsync(clipboard, text).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Clipboard access can be denied by the OS while the app is not focused; the text stays
            // visible on the card, so this is not worth a modal error.
        }
    }

    private static IBrush Parse(string color) => new SolidColorBrush(Color.Parse(color));
}
