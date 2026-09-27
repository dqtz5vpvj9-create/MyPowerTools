using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace MobileLayout.Tests;
public sealed class AdaptiveLayoutTests
{
    [AvaloniaFact]
    public void Narrow_form_preserves_reading_order_and_restores_columns_and_spans()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12 };
        var a = new TextBlock { Text = "Label" };
        var b = new TextBox { Text = "Editable value" }; Grid.SetColumn(b, 1);
        var c = new Button { Content = "Save" }; Grid.SetRow(c, 1); Grid.SetColumnSpan(c, 2);
        grid.Children.AddRange([a, b, c]);
        MptAdaptiveLayout.SetStackBelow(grid, 600);
        var window = new Window { Width = 360, Height = 640, Content = grid };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Single(grid.ColumnDefinitions);
            Assert.Equal(1, Grid.GetRow(b)); Assert.Equal(2, Grid.GetRow(c)); Assert.Equal(1, Grid.GetColumnSpan(c));
            window.Width = 960; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(2, grid.ColumnDefinitions.Count);
            Assert.Equal(1, Grid.GetColumn(b)); Assert.Equal(2, Grid.GetColumnSpan(c)); Assert.Equal(12, grid.ColumnSpacing);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Data_table_becomes_labelled_cards_and_restores_without_duplicate_labels()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,120,*") };
        grid.Children.Add(new TextBlock { Text = "Phone" });
        var state = new TextBlock { Text = "Ready" }; Grid.SetColumn(state, 1); grid.Children.Add(state);
        var action = new Button { Content = "Send" }; Grid.SetColumn(action, 2); grid.Children.Add(action);
        MptAdaptiveLayout.SetStackBelow(grid, 600); MptAdaptiveLayout.SetColumnLabels(grid, "Device|State|Action");
        var window = new Window { Width = 320, Height = 640, Content = grid };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(6, grid.Children.Count); Assert.Equal(2, Grid.GetRow(action));
            Assert.Contains(grid.Children.OfType<TextBlock>(), t => t.Text == "Action");
            window.Width = 960; window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(3, grid.Children.Count); Assert.Equal(2, Grid.GetColumn(action));
        }
        finally { window.Close(); }
    }
}
