using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI;
using MyPowerTools.UI.Controls;
using MyPowerTools.WebSurface.Avalonia;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ShellChromeView : UserControl
{
    private const double CompactNavigationWidth = 64;
    private const double ExpandedNavigationWidth = MptThemeTokens.LayoutSidebarWidth;
    private const double CaptionReserveWidth = 168;
    private const double MacTrafficLightInset = 72;
    private const double SearchMinWidth = 280;
    private const double SearchMaxWidth = MptThemeTokens.LayoutSearchMaxWidth;
    private const double ContentMaxWidth = MptThemeTokens.LayoutPageMaxWidth;
    private const double ContentHorizontalMargin = 56;
    private const double TopBarHeight = MptThemeTokens.LayoutTopBarHeight;

    /// <summary>
    /// Opt-in marker for a managed surface that lays itself out for a finite viewport and owns its own
    /// scrolling (a message list with a composer pinned to the bottom, a centred dialog layer). Only a
    /// surface that declares this class switches the page scroller to a bounded, non-scrolling frame;
    /// every other surface and every normal page keeps the ordinary page scrolling, so tools such as
    /// Paste Image - which have no ScrollViewer of their own - are never clipped.
    /// </summary>
    internal const string ViewportSurfaceClass = "MptViewportSurface";

    private readonly Grid _globalOverlayHost;
    private readonly Grid _permissionOverlayHost;
    private readonly Border _commandFlyout;
    private readonly ContentControl _contentHost;
    private readonly ScrollViewer _contentScrollHost;
    private readonly Grid _shellLayoutGrid;
    private readonly Grid _titleBarGrid;
    private readonly Grid _brandHost;
    private readonly TextBlock _brandTitle;
    private readonly TextBlock _toolSectionLabel;
    private readonly StackPanel _mainNavigationStack;
    private readonly StackPanel _footerNavigationStack;
    private readonly Border _navigationHost;
    private readonly MptButton _navigationModeButton;
    private ShellNavigationMode _navigationMode = ShellNavigationMode.Expanded;
    private WebSurfaceOcclusionState? _webSurfaceOcclusion;
    private ExternalSdkToolView? _managedSurfaceView;

    public WebSurfaceOcclusionState? WebSurfaceOcclusion
    {
        get => _webSurfaceOcclusion;
        set
        {
            _webSurfaceOcclusion = value;
            UpdateNativeWebSurfaceVisibility();
        }
    }

    public ShellChromeView()
    {
        AvaloniaXamlLoader.Load(this);
        _commandFlyout = this.FindControl<Border>("CommandFlyout")
            ?? throw new InvalidOperationException("Shell command flyout was not found.");
        _globalOverlayHost = this.FindControl<Grid>("GlobalOverlayHost")
            ?? throw new InvalidOperationException("Shell global overlay host was not found.");
        _permissionOverlayHost = this.FindControl<Grid>("PermissionOverlayHost")
            ?? throw new InvalidOperationException("Shell permission overlay host was not found.");
        _contentHost = this.FindControl<ContentControl>("ContentHost")
            ?? throw new InvalidOperationException("Shell content host was not found.");
        _contentScrollHost = this.FindControl<ScrollViewer>("ContentScrollHost")
            ?? throw new InvalidOperationException("Shell content scroll host was not found.");
        _shellLayoutGrid = this.FindControl<Grid>("ShellLayoutGrid")
            ?? throw new InvalidOperationException("Shell layout grid was not found.");
        _titleBarGrid = this.FindControl<Grid>("TitleBarGrid")
            ?? throw new InvalidOperationException("Shell title bar grid was not found.");
        _brandHost = this.FindControl<Grid>("BrandHost")
            ?? throw new InvalidOperationException("Shell brand host was not found.");
        _brandTitle = this.FindControl<TextBlock>("BrandTitle")
            ?? throw new InvalidOperationException("Shell brand title was not found.");
        _toolSectionLabel = this.FindControl<TextBlock>("ToolSectionLabel")
            ?? throw new InvalidOperationException("Shell tool section label was not found.");
        _mainNavigationStack = this.FindControl<StackPanel>("MainNavigationStack")
            ?? throw new InvalidOperationException("Shell main navigation stack was not found.");
        _footerNavigationStack = this.FindControl<StackPanel>("FooterNavigationStack")
            ?? throw new InvalidOperationException("Shell footer navigation stack was not found.");
        _navigationHost = this.FindControl<Border>("NavigationHost")
            ?? throw new InvalidOperationException("Shell navigation host was not found.");
        _navigationModeButton = this.FindControl<MptButton>("NavigationModeButton")
            ?? throw new InvalidOperationException("Shell navigation mode button was not found.");
        SizeChanged += OnShellSizeChanged;
        DataContextChanged += (_, _) => ApplyLayout(Bounds.Width);
        _contentHost.PropertyChanged += OnContentHostPropertyChanged;
        _globalOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        _permissionOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        UpdateNativeWebSurfaceVisibility();
    }

    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width);
    }

    private void OnContentHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property != ContentControl.ContentProperty)
        {
            return;
        }

        DetachManagedSurfaceView();
        if (eventArguments.NewValue is ExternalSdkToolView toolView)
        {
            _managedSurfaceView = toolView;
            toolView.ManagedSurfaceChanged += OnManagedSurfaceChanged;
        }

        UpdatePageScrollMode();
    }

    private void DetachManagedSurfaceView()
    {
        if (_managedSurfaceView is not null)
        {
            _managedSurfaceView.ManagedSurfaceChanged -= OnManagedSurfaceChanged;
            _managedSurfaceView = null;
        }
    }

    private void OnManagedSurfaceChanged(object? sender, EventArgs eventArguments) => UpdatePageScrollMode();

    /// <summary>
    /// A surface that declares <see cref="ViewportSurfaceClass"/> is a full-height page layout that
    /// owns its own scrolling; the page scroller must give it the finite viewport instead of measuring
    /// it with unbounded height (which pushed composers and centred dialogs below the fold). Any other
    /// hosted surface keeps the page scrolling it has always had.
    /// </summary>
    private void UpdatePageScrollMode()
    {
        var selfScrolling = _managedSurfaceView?.ManagedSurface is { } surface &&
                            surface.Classes.Contains(ViewportSurfaceClass);
        _contentScrollHost.VerticalScrollBarVisibility = selfScrolling
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private void OnOverlayVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArguments)
    {
        if (eventArguments.Property == IsVisibleProperty)
        {
            UpdateNativeWebSurfaceVisibility();
        }
    }

    private void UpdateNativeWebSurfaceVisibility()
    {
        _webSurfaceOcclusion?.SetOccluded(
            _globalOverlayHost.IsVisible || _permissionOverlayHost.IsVisible);
    }

    private void OnNavigationModeButtonClick(object? sender, RoutedEventArgs eventArguments)
    {
        NavigationMode = NavigationMode switch
        {
            ShellNavigationMode.Expanded => ShellNavigationMode.Compact,
            ShellNavigationMode.Compact => ShellNavigationMode.Hidden,
            _ => ShellNavigationMode.Expanded
        };
    }

    public ShellNavigationMode NavigationMode
    {
        get => _navigationMode;
        set
        {
            if (_navigationMode == value)
            {
                return;
            }

            _navigationMode = value;
            ApplyLayout(Bounds.Width);
        }
    }

    private void ApplyLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var compact = NavigationMode == ShellNavigationMode.Compact;
        var hidden = NavigationMode == ShellNavigationMode.Hidden;
        var navigationWidth = NavigationMode switch
        {
            ShellNavigationMode.Expanded => ExpandedNavigationWidth,
            ShellNavigationMode.Compact => CompactNavigationWidth,
            _ => 0
        };
        var titleLeadingInset = OperatingSystem.IsMacOS() ? MacTrafficLightInset : 0;
        var captionReserveWidth = OperatingSystem.IsMacOS() ? 0 : CaptionReserveWidth;
        var titleNavigationWidth = navigationWidth + titleLeadingInset;
        _shellLayoutGrid.ColumnDefinitions[0].Width = new GridLength(navigationWidth);
        _titleBarGrid.ColumnDefinitions[0].Width = new GridLength(titleNavigationWidth);
        _titleBarGrid.ColumnDefinitions[2].Width = new GridLength(captionReserveWidth);
        _commandFlyout.Width = Math.Clamp(
            width - titleNavigationWidth - captionReserveWidth - 48,
            SearchMinWidth,
            SearchMaxWidth);
        _contentHost.Width = Math.Min(
            Math.Max(0, width - navigationWidth - ContentHorizontalMargin),
            ContentMaxWidth);
        var titleContentWidth = Math.Max(0, width - titleNavigationWidth - captionReserveWidth);
        var commandFlyoutLeft = titleNavigationWidth + Math.Max(0, (titleContentWidth - _commandFlyout.Width) / 2);
        _commandFlyout.Margin = new Thickness(commandFlyoutLeft, TopBarHeight, 0, 0);
        _navigationHost.IsVisible = !hidden;
        _brandHost.IsVisible = !hidden;
        _brandTitle.IsVisible = !compact && !hidden;
        _toolSectionLabel.IsVisible = !compact && !hidden;
        _brandHost.ColumnSpacing = compact ? 0 : 12;
        var brandMargin = compact
            ? MptThemeTokens.ShellBrandCompactMargin
            : MptThemeTokens.ShellBrandExpandedMargin;
        _brandHost.Margin = new Thickness(
            brandMargin.Left + titleLeadingInset,
            brandMargin.Top,
            brandMargin.Right,
            brandMargin.Bottom);
        _mainNavigationStack.Margin = compact
            ? MptThemeTokens.ShellNavigationCompactMargin
            : MptThemeTokens.ShellNavigationExpandedMargin;
        _footerNavigationStack.Margin = compact
            ? MptThemeTokens.ShellFooterCompactMargin
            : MptThemeTokens.ShellFooterExpandedMargin;

        if (DataContext is ShellChromeViewModel viewModel)
        {
            viewModel.SetNavigationCompact(compact || hidden);
        }

        ToolTip.SetTip(_navigationModeButton, NavigationMode switch
        {
            ShellNavigationMode.Expanded => "Navigation: expanded. Activate for icons only.",
            ShellNavigationMode.Compact => "Navigation: icons only. Activate to hide.",
            _ => "Navigation: hidden. Activate to expand."
        });
        _navigationModeButton.Content = NavigationMode == ShellNavigationMode.Hidden
            ? "›"
            : "☰";
    }
}

public enum ShellNavigationMode
{
    Hidden,
    Compact,
    Expanded
}
