using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class GeneralSettingsView : UserControl
{
    private const double SettingsMaxWidth = MptThemeTokens.LayoutSettingsMaxWidth;
    private const double TwoColumnMinWidth = MptThemeTokens.LayoutSettingsTwoColumnMinWidth;

    private readonly StackPanel _generalRoot;
    private readonly Grid _appearanceLayoutGrid;
    private readonly StackPanel _themeControlPanel;
    private readonly Grid _secondarySettingsGrid;
    private readonly Border _keyboardSettingsCard;
    private readonly Grid _maintenanceGrid;
    private readonly Control _openSystemButton;
    private bool? _compact;

    public GeneralSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _generalRoot = RequireControl<StackPanel>("GeneralRoot");
        _appearanceLayoutGrid = RequireControl<Grid>("AppearanceLayoutGrid");
        _themeControlPanel = RequireControl<StackPanel>("ThemeControlPanel");
        _secondarySettingsGrid = RequireControl<Grid>("SecondarySettingsGrid");
        _keyboardSettingsCard = RequireControl<Border>("KeyboardSettingsCard");
        _maintenanceGrid = RequireControl<Grid>("MaintenanceGrid");
        _openSystemButton = RequireControl<Control>("OpenSystemButton");
        SizeChanged += OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var availableWidth = Math.Max(0, e.NewSize.Width);
        _generalRoot.Width = Math.Min(SettingsMaxWidth, availableWidth);
        ApplyResponsiveLayout(availableWidth < TwoColumnMinWidth);
    }

    private void ApplyResponsiveLayout(bool compact)
    {
        if (_compact == compact)
        {
            return;
        }

        _compact = compact;
        if (compact)
        {
            _appearanceLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _appearanceLayoutGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            _appearanceLayoutGrid.ColumnSpacing = 0;
            _appearanceLayoutGrid.RowSpacing = 16;
            Grid.SetColumn(_themeControlPanel, 0);
            Grid.SetRow(_themeControlPanel, 1);

            _secondarySettingsGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _secondarySettingsGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            _secondarySettingsGrid.ColumnSpacing = 0;
            _secondarySettingsGrid.RowSpacing = 16;
            Grid.SetColumn(_keyboardSettingsCard, 0);
            Grid.SetRow(_keyboardSettingsCard, 1);

            _maintenanceGrid.ColumnDefinitions = new ColumnDefinitions("*");
            _maintenanceGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            _maintenanceGrid.ColumnSpacing = 0;
            _maintenanceGrid.RowSpacing = 16;
            Grid.SetColumn(_openSystemButton, 0);
            Grid.SetRow(_openSystemButton, 1);
            _openSystemButton.HorizontalAlignment = HorizontalAlignment.Left;
            return;
        }

        _appearanceLayoutGrid.ColumnDefinitions = new ColumnDefinitions("*,320");
        _appearanceLayoutGrid.RowDefinitions = new RowDefinitions("Auto");
        _appearanceLayoutGrid.ColumnSpacing = 24;
        _appearanceLayoutGrid.RowSpacing = 0;
        Grid.SetColumn(_themeControlPanel, 1);
        Grid.SetRow(_themeControlPanel, 0);

        _secondarySettingsGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
        _secondarySettingsGrid.RowDefinitions = new RowDefinitions("Auto");
        _secondarySettingsGrid.ColumnSpacing = 16;
        _secondarySettingsGrid.RowSpacing = 0;
        Grid.SetColumn(_keyboardSettingsCard, 1);
        Grid.SetRow(_keyboardSettingsCard, 0);

        _maintenanceGrid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
        _maintenanceGrid.RowDefinitions = new RowDefinitions("Auto");
        _maintenanceGrid.ColumnSpacing = 16;
        _maintenanceGrid.RowSpacing = 0;
        Grid.SetColumn(_openSystemButton, 1);
        Grid.SetRow(_openSystemButton, 0);
        _openSystemButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private T RequireControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"General settings control '{name}' was not found.");
    }
}
