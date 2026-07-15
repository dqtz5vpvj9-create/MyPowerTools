using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ShellChromeView : UserControl
{
    private const double CompactBreakpoint = 1080;
    private const double CompactNavigationWidth = 64;
    private const double ExpandedNavigationWidth = MptThemeTokens.LayoutSidebarWidth;
    private const double CaptionReserveWidth = 168;
    private const double SearchMinWidth = 280;
    private const double SearchMaxWidth = MptThemeTokens.LayoutSearchMaxWidth;
    private const double ContentMaxWidth = MptThemeTokens.LayoutPageMaxWidth;
    private const double ContentHorizontalMargin = 56;
    private const double TopBarHeight = MptThemeTokens.LayoutTopBarHeight;

    private readonly Grid _titleSearchHost;
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
    private bool? _isCompact;

    public ShellChromeView()
    {
        AvaloniaXamlLoader.Load(this);
        _titleSearchHost = this.FindControl<Grid>("TitleSearchHost")
            ?? throw new InvalidOperationException("Shell title search host was not found.");
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
        SizeChanged += OnShellSizeChanged;
        DataContextChanged += (_, _) => ApplyResponsiveLayout(Bounds.Width);
        _globalOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        _permissionOverlayHost.PropertyChanged += OnOverlayVisibilityChanged;
        UpdateNativeWebSurfaceVisibility();
    }

    private void OnShellSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
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
        NativeWebSurfaceCoordinator.SetShellOverlayVisible(
            _globalOverlayHost.IsVisible || _permissionOverlayHost.IsVisible);
    }

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0)
        {
            return;
        }

        var compact = width < CompactBreakpoint;
        var navigationWidth = compact ? CompactNavigationWidth : ExpandedNavigationWidth;
        _shellLayoutGrid.ColumnDefinitions[0].Width = new GridLength(navigationWidth);
        _titleBarGrid.ColumnDefinitions[0].Width = new GridLength(navigationWidth);
        _titleSearchHost.Width = Math.Clamp(
            width - navigationWidth - CaptionReserveWidth - 48,
            SearchMinWidth,
            SearchMaxWidth);
        _commandFlyout.Width = _titleSearchHost.Width;
        _contentHost.Width = Math.Min(
            Math.Max(0, width - navigationWidth - ContentHorizontalMargin),
            ContentMaxWidth);
        var titleContentWidth = Math.Max(0, width - navigationWidth - CaptionReserveWidth);
        var commandFlyoutLeft = navigationWidth + Math.Max(0, (titleContentWidth - _commandFlyout.Width) / 2);
        _commandFlyout.Margin = new Thickness(commandFlyoutLeft, TopBarHeight, 0, 0);

        if (_isCompact == compact)
        {
            return;
        }

        _isCompact = compact;
        _brandTitle.IsVisible = !compact;
        _toolSectionLabel.IsVisible = !compact;
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
            viewModel.SetNavigationCompact(compact);
        }
    }
}
