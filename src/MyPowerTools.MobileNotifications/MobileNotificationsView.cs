using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.RemoteNotifications.Configuration;
using RemoteNotifications.Surface.Services;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Remote Notifications tool page for phones.
///
/// It is a code-built, single-column surface that mirrors the approved prototype: a real inbox grouped
/// by day, unread dots, search and label filters, 全部已读, on-demand settings, the background switch
/// and a single bottom sheet for the full message. The desktop surface opens a detail window per
/// message and talks to a service unit; neither exists on Android, so this page drives the module's
/// commands instead and expands details in a sheet.
/// </summary>
public sealed partial class MobileNotificationsView : UserControl, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly MobileNotificationsViewModel _viewModel;
    private readonly DispatcherTimer _relativeTimeTimer;

    public MobileNotificationsView(MptAvaloniaSurfaceContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        var settingsStore = new RemoteNotificationSettingsStore(
            Path.Combine(context.DataDirectory, "settings.json"));
        var store = new RemoteNotificationsLegacyStore(settingsStore, context.DataDirectory);
        _viewModel = new MobileNotificationsViewModel(context, store, settingsStore);
        DataContext = _viewModel;

        Content = BuildLayout();
        _viewModel.Labels.CollectionChanged += (_, _) => RebuildLabelStrip();
        _viewModel.PropertyChanged += (_, _) => UpdateChrome();
        _viewModel.KeySectionRequested += FocusKeySection;

        // The back key must close the sheet before the Shell leaves the tool page.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

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
        // Status colours come from the SDK pill tokens for the real connection state.
        MobileNotificationTheme.StyleStatusPill(_statusPillBorder, _statusPill, _viewModel.ConnectionState);
        _statusPill.Text = _viewModel.StatusText;

        _pageTitle.Text = _viewModel.PageTitle;
        _settingsTitle.Text = _viewModel.SettingsTitle;

        // The inbox header stays friendly: friendly counts and the real last-sync time only. The raw
        // endpoint belongs to the connection settings, and the full state to the error details.
        var detail = _viewModel.LastPollText;
        if (!string.IsNullOrWhiteSpace(_viewModel.SyncResult))
        {
            detail = $"{detail} · {_viewModel.SyncResult}";
        }

        _statusDetail.Text = $"{detail} · {_viewModel.CountText}";

        _searchButton.Content = _viewModel.IsSearchVisible ? "收起" : "搜索";

        // Settings is a page of its own in the prototype: the inbox header, search and filters belong
        // to the inbox and must not sit above it.
        _headerBorder.IsVisible = _viewModel.IsInboxVisible;
        _searchRow.IsVisible = _viewModel.IsInboxVisible && _viewModel.IsSearchVisible;
        _labelStrip.IsVisible = _viewModel.IsInboxVisible && _viewModel.Labels.Count > 1;
        _inboxPanel.IsVisible = _viewModel.IsInboxVisible;
        _settingsPanel.IsVisible = _viewModel.IsSettingsVisible;
        _settingsBackButton.IsVisible = _viewModel.IsSettingsVisible;

        // Semantic text colours use the SDK's success/warning text classes for the real state.
        MobileNotificationTheme.SetClass(
            _keyStatus,
            MobileNotificationTheme.WarningTextClass,
            _viewModel.KeyNeedsAttention);
        MobileNotificationTheme.SetClass(
            _settingsFeedback,
            MobileNotificationTheme.SuccessTextClass,
            string.Equals(_viewModel.SettingsFeedbackState, "success", StringComparison.Ordinal));
        MobileNotificationTheme.SetClass(
            _settingsFeedback,
            MobileNotificationTheme.WarningTextClass,
            string.Equals(_viewModel.SettingsFeedbackState, "error", StringComparison.Ordinal));

        UpdateSheet();
        UpdateDetail();
    }

    private void UpdateSheet()
    {
        var wasVisible = _sheetOverlay.IsVisible;
        _sheetOverlay.IsVisible = _viewModel.IsSheetVisible;
        _detailPanel.IsVisible = _viewModel.IsDetailVisible;
        _clearPanel.IsVisible = _viewModel.IsClearConfirmVisible;

        // Focus moves into the sheet when it opens, and back to the row that opened it when it closes.
        if (_sheetOverlay.IsVisible && !wasVisible)
        {
            Dispatcher.UIThread.Post(() => _detailReadButton.Focus(), DispatcherPriority.Input);
        }
    }

    private void UpdateDetail()
    {
        if (_viewModel.Detail is not { } detail)
        {
            return;
        }

        _detailTitle.Text = detail.Title;
        _detailMeta.Text = detail.MetaText;
        _detailBody.Text = detail.Body;
        _detailPosition.Text = detail.PositionText;
        _detailPosition.IsVisible = detail.HasPosition;
        _detailPreviousButton.IsEnabled = _viewModel.CanShowDetailPrevious;
        _detailNextButton.IsEnabled = _viewModel.CanShowDetailNext;
        _detailReadButton.Content = detail.MarkReadText;
    }

    private void RebuildLabelStrip()
    {
        _labelPanel.Children.Clear();
        _labelButtons.Clear();
        foreach (var chip in _viewModel.Labels)
        {
            var button = MobileNotificationTheme.FilterButton(chip.Label);
            button.Name = $"LabelChip_{chip.FilterValue ?? "all"}";
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

    /// <summary>
    /// Selection is expressed with the SDK's <c>MptMobileFilter.active</c> class; colours, radius,
    /// touch height and pressed states all come from the shared theme, and an unread label keeps the
    /// theme's secondary foreground.
    /// </summary>
    private static void ApplyChipStyle(Button button, MobileNotificationLabelViewModel chip)
    {
        button.FontWeight = chip.IsSelected ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal;
        MobileNotificationTheme.SetClass(button, MobileNotificationTheme.FilterActiveClass, chip.IsSelected);
    }

    private void FocusKeySection()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _keyCard.BringIntoView();
            _keyBox.Focus();
        }, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- input

    /// <summary>
    /// Page-local Back, offered by the host through <see cref="IMptAvaloniaSurfaceBackHandler"/> before
    /// it leaves the tool page. The topmost layer consumes the key first: the open bottom sheet, then
    /// the settings page, then the search field. <see langword="false"/> hands Back back to the Shell.
    /// </summary>
    public bool TryHandleBack()
    {
        var wasSheetVisible = _viewModel.IsSheetVisible;
        if (!_viewModel.TryHandleBack())
        {
            return false;
        }

        if (wasSheetVisible)
        {
            RestoreSheetInvokerFocus();
        }

        return true;
    }

    /// <summary>
    /// Escape follows the same path as the host's Back key, so desktop and tests cannot diverge from
    /// the device behaviour. An unconsumed Escape bubbles to the Shell's own shortcuts.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape)
        {
            return;
        }

        args.Handled = TryHandleBack();
    }

    // ---------------------------------------------------------------- activation

    /// <summary>
    /// Tapping the tray notification opens this tool page with
    /// <c>mypowertools://remote-notification?id=…</c>; show that message's detail sheet so the user
    /// lands on the content they tapped.
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
            // visible on the sheet, so this is not worth a modal error.
        }
    }

}
