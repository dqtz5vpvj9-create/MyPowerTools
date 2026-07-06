using Avalonia.Controls;
using Avalonia.Media;

namespace MyPowerTools.UI.Controls;

public sealed class MptShellWindow : Window
{
    public MptShellWindow()
    {
        Classes.Add(nameof(MptShellWindow));
    }
}

public sealed class MptSidebar : StackPanel
{
    public MptSidebar()
    {
        Classes.Add(nameof(MptSidebar));
    }
}

public sealed class MptTopBar : Grid
{
    public MptTopBar()
    {
        Classes.Add(nameof(MptTopBar));
    }
}

public sealed class MptSearchBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public MptSearchBox()
    {
        Classes.Add(nameof(MptSearchBox));
    }
}

public sealed class MptButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public MptButton()
    {
        Classes.Add(nameof(MptButton));
    }
}

public sealed class MptIconButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public MptIconButton()
    {
        Classes.Add(nameof(MptIconButton));
    }
}

public sealed class MptTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public MptTextBox()
    {
        Classes.Add(nameof(MptTextBox));
    }
}

public sealed class MptCheckBox : CheckBox
{
    protected override Type StyleKeyOverride => typeof(CheckBox);

    public MptCheckBox()
    {
        Classes.Add(nameof(MptCheckBox));
    }
}

public sealed class MptComboBox : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);

    public MptComboBox()
    {
        Classes.Add(nameof(MptComboBox));
    }
}

public sealed class MptModuleCard : Border
{
    public MptModuleCard()
    {
        Classes.Add(nameof(MptModuleCard));
    }
}

public sealed class MptStatusBadge : Border
{
    public MptStatusBadge()
    {
        Classes.Add(nameof(MptStatusBadge));
    }
}

public sealed class MptCommandItem : Border
{
    public MptCommandItem()
    {
        Classes.Add(nameof(MptCommandItem));
    }
}

public sealed class MptSettingsSection : StackPanel
{
    public MptSettingsSection()
    {
        Classes.Add(nameof(MptSettingsSection));
    }
}

public sealed class MptSettingsField : Border
{
    public MptSettingsField()
    {
        Classes.Add(nameof(MptSettingsField));
    }
}

public sealed class MptLogViewer : ListBox
{
    public MptLogViewer()
    {
        Classes.Add(nameof(MptLogViewer));
    }
}

public sealed class MptLogRow : Border
{
    public MptLogRow()
    {
        Classes.Add(nameof(MptLogRow));
    }
}

public sealed class MptNotificationItem : Border
{
    public MptNotificationItem()
    {
        Classes.Add(nameof(MptNotificationItem));
    }
}

public sealed class MptMetricTile : Border
{
    public MptMetricTile()
    {
        Classes.Add(nameof(MptMetricTile));
    }
}

public sealed class MptActionButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public MptActionButton()
    {
        Classes.Add(nameof(MptActionButton));
    }
}

public sealed class MptPermissionPrompt : Border
{
    public MptPermissionPrompt()
    {
        Classes.Add(nameof(MptPermissionPrompt));
    }
}

public sealed class MptEmptyState : Border
{
    public MptEmptyState()
    {
        Classes.Add(nameof(MptEmptyState));
    }
}

public sealed class MptErrorState : Border
{
    public MptErrorState()
    {
        Classes.Add(nameof(MptErrorState));
    }

    public MptErrorState(string message)
        : this()
    {
        Child = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap
        };
    }
}

public sealed class MptLoadingSkeleton : Border
{
    public MptLoadingSkeleton()
    {
        Classes.Add(nameof(MptLoadingSkeleton));
    }
}

public sealed class MptPageHeader : StackPanel
{
    public MptPageHeader()
    {
        Classes.Add(nameof(MptPageHeader));
    }
}

public sealed class MptActionBar : StackPanel
{
    public MptActionBar()
    {
        Classes.Add(nameof(MptActionBar));
    }
}
