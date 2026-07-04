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
        Classes.Add("MptModuleCard");
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

        Classes.Add("MptStatusBadge");
        Padding = new Thickness(8, 2);
        CornerRadius = new CornerRadius(999);
        BorderBrush = color;
        BorderThickness = new Thickness(1);
        Child = new TextBlock
        {
            Text = state,
            Foreground = color,
            FontSize = MptTheme.FontSizeMeta
        };
    }
}

public sealed class MptCommandItem : Border
{
    public MptCommandItem(string title, string subtitle)
    {
        Classes.Add("MptCommandItem");
        Padding = new Thickness(12);
        CornerRadius = new CornerRadius(8);
        Child = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = subtitle, Foreground = MptTheme.TextSecondary, FontSize = MptTheme.FontSizeMeta }
            }
        };
    }
}

public sealed class MptSettingsSection : StackPanel
{
    public MptSettingsSection(string title)
    {
        Classes.Add("MptSettingsSection");
        Spacing = 10;
        Children.Add(new TextBlock { Text = title, FontSize = MptTheme.FontSizeSection, FontWeight = FontWeight.SemiBold });
    }
}

public sealed class MptSettingsField : Border
{
    public MptSettingsField(string label, Control editor)
    {
        Classes.Add("MptSettingsField");
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(6);
        Background = MptTheme.AppBackground;
        Child = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = label, FontWeight = FontWeight.SemiBold },
                editor
            }
        };
    }
}

public sealed class MptLogViewer : ListBox
{
    public MptLogViewer()
    {
        Classes.Add("MptLogViewer");
        FontFamily = FontFamily.Parse("Consolas");
        FontSize = MptTheme.FontSizeMeta;
    }
}

public sealed class MptLogRow : Border
{
    public MptLogRow(Control content)
    {
        Classes.Add("MptLogRow");
        Padding = new Thickness(10, 8);
        CornerRadius = new CornerRadius(6);
        Background = MptTheme.AppBackground;
        Child = content;
    }
}

public sealed class MptNotificationItem : Border
{
    public MptNotificationItem(string title, string body)
    {
        Classes.Add("MptNotificationItem");
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
        Classes.Add("MptMetricTile");
        Padding = new Thickness(10);
        CornerRadius = new CornerRadius(8);
        Background = MptTheme.AppBackground;
        Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = value, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = label, FontSize = MptTheme.FontSizeMeta, Foreground = MptTheme.TextSecondary }
            }
        };
    }
}

public sealed class MptActionButton : Button
{
    public MptActionButton(string title)
    {
        Classes.Add("MptActionButton");
        Content = title;
        MinWidth = 76;
        MinHeight = 32;
    }
}

public sealed class MptPermissionPrompt : Border
{
    public MptPermissionPrompt(string reason)
    {
        Classes.Add("MptPermissionPrompt");
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
        Classes.Add("MptEmptyState");
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
        Classes.Add("MptErrorState");
        Padding = new Thickness(16);
        CornerRadius = new CornerRadius(8);
        BorderBrush = MptTheme.Danger;
        BorderThickness = new Thickness(1);
        Child = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
    }
}

public sealed class MptLoadingSkeleton : Border
{
    public MptLoadingSkeleton()
    {
        Classes.Add("MptLoadingSkeleton");
        MinHeight = 36;
        CornerRadius = new CornerRadius(6);
        Background = MptTheme.AppBackground;
    }
}

public sealed class MptPageHeader : StackPanel
{
    public MptPageHeader(string title, string subtitle)
    {
        Classes.Add("MptPageHeader");
        Spacing = 4;
        Children.Add(new TextBlock { Text = title, FontSize = MptTheme.FontSizeTitle, FontWeight = FontWeight.SemiBold });
        Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = MptTheme.TextSecondary });
    }
}

public sealed class MptActionBar : StackPanel
{
    public MptActionBar()
    {
        Classes.Add("MptActionBar");
        Orientation = Orientation.Horizontal;
        Spacing = 8;
    }
}
