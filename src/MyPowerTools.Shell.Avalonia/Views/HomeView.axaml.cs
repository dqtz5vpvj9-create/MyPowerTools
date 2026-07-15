using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class HomeView : UserControl
{
    private const double DashboardMaxWidth = MptThemeTokens.LayoutDashboardMaxWidth;
    private const double WideDashboardMinWidth = MptThemeTokens.LayoutDashboardTwoColumnMinWidth;

    private readonly StackPanel _dashboardRoot;
    private readonly Grid _overviewGrid;
    private readonly Grid _overviewStatus;
    private readonly Grid _dashboardColumns;
    private readonly StackPanel _primaryDashboardColumn;
    private readonly Border _modulesDashboardCard;
    private bool? _usesCompactLayout;

    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
        _dashboardRoot = RequireControl<StackPanel>("DashboardRoot");
        _overviewGrid = RequireControl<Grid>("OverviewGrid");
        _overviewStatus = RequireControl<Grid>("OverviewStatus");
        _dashboardColumns = RequireControl<Grid>("DashboardColumns");
        _primaryDashboardColumn = RequireControl<StackPanel>("PrimaryDashboardColumn");
        _modulesDashboardCard = RequireControl<Border>("ModulesDashboardCard");

        SizeChanged += OnHomeSizeChanged;
    }

    private void OnHomeSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var availableWidth = Math.Max(0, e.NewSize.Width);
        _dashboardRoot.Width = Math.Min(DashboardMaxWidth, availableWidth);
        ApplyResponsiveLayout(availableWidth < WideDashboardMinWidth);
    }

    private void ApplyResponsiveLayout(bool useCompactLayout)
    {
        if (_usesCompactLayout == useCompactLayout)
        {
            return;
        }

        _usesCompactLayout = useCompactLayout;

        if (useCompactLayout)
        {
            _overviewGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _overviewGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetColumn(_overviewStatus, 0);
            Grid.SetRow(_overviewStatus, 1);
            _overviewStatus.Margin = MptThemeTokens.AuditPanelMargin;

            _dashboardColumns.ColumnDefinitions = new ColumnDefinitions("*");
            _dashboardColumns.RowDefinitions = new RowDefinitions("Auto,Auto");
            _dashboardColumns.ColumnSpacing = 0;
            _dashboardColumns.RowSpacing = 16;
            Grid.SetColumn(_modulesDashboardCard, 0);
            Grid.SetRow(_modulesDashboardCard, 1);

            _primaryDashboardColumn.Width = double.NaN;
            _primaryDashboardColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
            _modulesDashboardCard.Width = double.NaN;
            _modulesDashboardCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        _overviewGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _overviewGrid.RowDefinitions = new RowDefinitions("Auto");
        Grid.SetColumn(_overviewStatus, 1);
        Grid.SetRow(_overviewStatus, 0);
        _overviewStatus.Margin = MptThemeTokens.LeftContentMargin;

        _dashboardColumns.ColumnDefinitions = new ColumnDefinitions("5*,3*");
        _dashboardColumns.RowDefinitions = new RowDefinitions("Auto");
        _dashboardColumns.ColumnSpacing = 16;
        _dashboardColumns.RowSpacing = 0;
        Grid.SetColumn(_modulesDashboardCard, 1);
        Grid.SetRow(_modulesDashboardCard, 0);

        _primaryDashboardColumn.Width = double.NaN;
        _primaryDashboardColumn.HorizontalAlignment = HorizontalAlignment.Stretch;
        _modulesDashboardCard.Width = double.NaN;
        _modulesDashboardCard.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private T RequireControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Home control '{name}' was not found.");
    }
}
