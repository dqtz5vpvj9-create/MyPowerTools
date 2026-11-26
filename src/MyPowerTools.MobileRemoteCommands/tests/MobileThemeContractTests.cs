using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// The M1 mobile theme contract: the page must mark its controls with the shared Mobile classes and must
/// not depend on a private palette, so the SDK theme (light/dark, font scaling) can take over. Without
/// the SDK theme the page's own fallback styles must cover every class it uses.
/// </summary>
public sealed class MobileThemeContractTests
{
    [AvaloniaFact]
    public void The_page_root_and_container_use_the_mobile_page_classes()
    {
        using var harness = Harness();
        var view = harness.View;

        Assert.Contains(
            RemoteCommandsMobileTheme.RootClass,
            view.GetLogicalDescendants().OfType<Grid>().SelectMany(grid => grid.Classes).ToArray());

        Assert.Contains(
            RemoteCommandsMobileTheme.PageClass,
            view.GetLogicalDescendants().OfType<StackPanel>().SelectMany(panel => panel.Classes).ToArray());
    }

    [AvaloniaFact]
    public void Command_rows_buttons_and_fields_use_the_shared_mobile_classes()
    {
        using var harness = Harness();
        var view = harness.View;

        Assert.All(
            view.CommandRowButtonsForTests,
            button => Assert.Contains(RemoteCommandsMobileTheme.ListRowClass, button.Classes));
        Assert.Contains(RemoteCommandsMobileTheme.PrimaryClass, view.RunButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.QuietButtonClass, view.ConnectionButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.SecondaryClass, view.RetryButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.BackButtonClass, view.BackButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.IconButtonClass, view.RefreshButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.CloseButtonClass, view.SheetCloseButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.TextButtonClass, view.AddCommandButtonForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.FieldClass, view.CommandLabelFieldForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.CheckClass, view.SecondInputToggleForTests.Classes);

        harness.Click(view.CommandRowButtonsForTests[0]);
        Assert.Contains(RemoteCommandsMobileTheme.FieldClass, view.Input1FieldForTests.Classes);
        Assert.Contains(RemoteCommandsMobileTheme.MonoClass, view.OutputViewerForTests.Classes);
    }

    [AvaloniaFact]
    public void Every_mobile_class_the_page_uses_is_covered_without_the_sdk_theme()
    {
        using var harness = Harness();
        var view = harness.View;

        var used = view.GetLogicalDescendants()
            .OfType<Control>()
            .SelectMany(control => control.Classes)
            .Where(name => name.StartsWith("MptMobile", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(used);

        // The test host loads the real SDK theme, so the SDK owns these classes at runtime. The fallback
        // set still has to cover every one of them, because an older host without the theme gets exactly
        // that style set; the assertion is unconditional so a new class cannot be added without a
        // fallback counterpart.
        Assert.All(used, name => Assert.Contains(name, RemoteCommandsMobileTheme.FallbackClassNames));
    }

    [AvaloniaFact]
    public void Typography_roles_come_from_the_contract_classes()
    {
        using var harness = Harness();
        var view = harness.View;

        var used = view.GetLogicalDescendants().OfType<Control>().SelectMany(control => control.Classes).ToArray();
        Assert.Contains(RemoteCommandsMobileTheme.PageTitleClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.PageSubtitleClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.SectionTitleClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.RowTitleClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.RowSubtitleClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.MetaClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.NoteClass, used);
        Assert.Contains(RemoteCommandsMobileTheme.FieldLabelClass, used);
    }

    [AvaloniaFact]
    public void State_marks_are_vector_icons_with_a_tone_class_instead_of_local_colours()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        var icons = view.StateIconsForTests;
        Assert.NotEmpty(icons);
        Assert.All(icons, icon => Assert.Contains(RemoteCommandsMobileTheme.IconClass, icon.Classes));

        // The finished run shows the success tone through the shared class, not a pinned colour, and the
        // mark itself is the prototype's check geometry.
        var runStateIcon = icons[0];
        Assert.Contains(RemoteCommandsMobileTheme.IconSuccessClass, runStateIcon.Classes);
        Assert.NotNull(runStateIcon.Data);

        // Without the SDK theme the success tone must come from the shared class (the fallback style for
        // it), not from a colour this page pinned on the icon.
        if (!RemoteCommandsMobileTheme.SdkThemeAvailable)
        {
            var expected = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                RemoteCommandsMobilePalette.Current.Brush("Success"));
            var actual = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(
                runStateIcon.GetValue(Avalonia.Controls.Shapes.Shape.StrokeProperty));
            Assert.Equal(expected.Color, actual.Color);
        }
    }

    [AvaloniaFact]
    public void Every_mobile_class_the_page_uses_exists_in_the_shipped_sdk_theme()
    {
        var themeDirectory = Path.Combine(RepoPaths.Root(), "src", "MyPowerTools.AvaloniaSdk", "Themes");
        var themeText = string.Join(
            '\n',
            Directory.EnumerateFiles(themeDirectory, "MptMobile*.axaml").Select(File.ReadAllText));

        Assert.NotEmpty(themeText);

        using var harness = Harness();
        var used = harness.View.GetLogicalDescendants()
            .OfType<Control>()
            .SelectMany(control => control.Classes)
            .Where(name => name.StartsWith("MptMobile", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(used);
        Assert.All(
            used,
            name => Assert.True(
                themeText.Contains(name, StringComparison.Ordinal),
                $"页面使用的 {name} 不在 SDK 移动主题里。"));
    }

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
