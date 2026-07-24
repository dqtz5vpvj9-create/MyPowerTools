using Avalonia;
using Avalonia.Controls;
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
    private const double SearchMinWidth = 280;
    private const double SearchMaxWidth = MptThemeTokens.LayoutSearchMaxWidth;
    private const double ContentMaxWidth = MptThemeTokens.LayoutPageMaxWidth;
    private const double ContentHorizontalMargin = 56;
    private const double TopBarHeight = MptThemeTokens.LayoutTopBarHeight;

    private readonly Grid _globalOverlayHost;
    private readonly Grid _permissionOverlayHost;
    private readonly Border _commandFlyout;
    private readonly ContentControl _contentHost;
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
        _globalOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        _permissionOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        UpdateNativeWebSurfaceVisibility();
    }

    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyLayout(e.NewSize.Width);
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
        _shellLayoutGrid.ColumnDefinitions[0].Width = new GridLength(navigationWidth);
        _titleBarGrid.ColumnDefinitions[0].Width = new GridLength(navigationWidth);
        _commandFlyout.Width = Math.Clamp(
            width - navigationWidth - CaptionReserveWidth - 48,
            SearchMinWidth,
            SearchMaxWidth);
        _contentHost.Width = Math.Min(
            Math.Max(0, width - navigationWidth - ContentHorizontalMargin),
            ContentMaxWidth);
        var titleContentWidth = Math.Max(0, width - navigationWidth - CaptionReserveWidth);
        var commandFlyoutLeft = navigationWidth + Math.Max(0, (titleContentWidth - _commandFlyout.Width) / 2);
        _commandFlyout.Margin = new Thickness(commandFlyoutLeft, TopBarHeight, 0, 0);
        _navigationHost.IsVisible = !hidden;
        _brandHost.IsVisible = !hidden;
        _brandTitle.IsVisible = !compact && !hidden;
        _toolSectionLabel.IsVisible = !compact && !hidden;
        _brandHost.ColumnSpacing = compact ? 0 : 12;
        _brandHost.Margin = compact
            ? MptThemeTokens.ShellBrandCompactMargin
            : MptThemeTokens.ShellBrandExpandedMargin;
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
            ? "\uE76E"
            : "\uE700";
    }
}

public enum ShellNavigationMode
{
    Hidden,
    Compact,
    Expanded
}
