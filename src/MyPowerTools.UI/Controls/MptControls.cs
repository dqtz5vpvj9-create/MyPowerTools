using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPowerTools.UI.Controls;

public sealed class MptShellWindow : Window
{
    public MptShellWindow()
    {
        Width = 1180;
        Height = 760;
        MinWidth = 920;
        MinHeight = 620;
        Background = MptTheme.AppBackground;
    }
}

public sealed class MptSidebar : StackPanel
{
    public MptSidebar()
    {
        Spacing = 6;
        Margin = new Thickness(16);
    }
}

public sealed class MptTopBar : Grid
{
    public MptTopBar()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Margin = new Thickness(16, 12, 16, 8);
    }
}

public sealed class MptSearchBox : TextBox
{
    public MptSearchBox()
    {
        PlaceholderText = "Search commands";
        MinHeight = 36;
    }
}

public sealed class MptModuleCard : Border
{
    public MptModuleCard(Control content)
    {
        Margin = new Thickness(0, 0, 16, 16);
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(8);
        BorderBrush = MptTheme.Border;
        BorderThickness = new Thickness(1);
        Background = MptTheme.CardBackground;
        Child = content;
    }
}

public sealed class MptStatusBadge : Border
{
    public MptStatusBadge(string state)
    {
        var color = MptTheme.StatusBrush(state);

        Padding = new Thickness(8, 2);
        CornerRadius = new CornerRadius(999);
        BorderBrush = color;
        BorderThickness = new Thickness(1);
        Child = new TextBlock
        {
            Text = state,
            Foreground = color,
            FontSize = 12
        };
    }
}

public sealed class MptCommandItem : Border
{
    public MptCommandItem(string title, string subtitle)
    {
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8);
        Child = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = subtitle, Foreground = MptTheme.TextSecondary, FontSize = 12 }
            }
        };
    }
}

public sealed class MptSettingsSection : StackPanel
{
    public MptSettingsSection(string title)
    {
        Spacing = 10;
        Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
    }
}

public sealed class MptLogViewer : ListBox
{
    public MptLogViewer()
    {
        FontFamily = FontFamily.Parse("Consolas");
        FontSize = 12;
    }
}

public sealed class MptNotificationItem : Border
{
    public MptNotificationItem(string title, string body)
    {
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8);
        BorderBrush = MptTheme.Border;
        BorderThickness = new Thickness(1);
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = MptTheme.TextSecondary }
            }
        };
    }
}

public sealed class MptMetricTile : Border
{
    public MptMetricTile(string label, string value)
    {
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(8);
        Background = MptTheme.AppBackground;
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = value, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = label, FontSize = 12, Foreground = MptTheme.TextSecondary }
            }
        };
    }
}

public sealed class MptActionButton : Button
{
    public MptActionButton(string title)
    {
        Content = title;
        MinWidth = 76;
        MinHeight = 32;
    }
}

public sealed class MptPermissionPrompt : Border
{
    public MptPermissionPrompt(string reason)
    {
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(8);
        BorderBrush = MptTheme.Warning;
        BorderThickness = new Thickness(1);
        Child = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap };
    }
}

public sealed class MptEmptyState : TextBlock
{
    public MptEmptyState(string text)
    {
        Text = text;
        Foreground = MptTheme.TextSecondary;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }
}

public sealed class MptErrorState : Border
{
    public MptErrorState(string message)
    {
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(8);
        BorderBrush = MptTheme.Danger;
        BorderThickness = new Thickness(1);
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
    }
}
