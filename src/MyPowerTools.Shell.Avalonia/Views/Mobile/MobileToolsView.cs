using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>工具 tab: search, 用途分组, 手机/电脑筛选 and real favorites.</summary>
internal sealed class MobileToolsView : UserControl
{
    public MobileToolsView(MobileToolsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");
        Content = MobileElements.Page(
            MobileElements.PageTitle(viewModel.Title),
            MobileElements.Body(viewModel.Subtitle),
            BuildSearch(viewModel),
            BuildFilters(viewModel),
            BuildResults(viewModel));
    }

    private static Control BuildSearch(MobileToolsViewModel viewModel)
    {
        var box = MobileElements.SearchBox("搜索工具或想做的事", viewModel.SearchText, text => viewModel.SearchText = text);
        AutomationProperties.SetAutomationId(box, "MobileToolSearch");
        return box;
    }

    private static Control BuildFilters(MobileToolsViewModel viewModel)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Classes.Add("MptMobileFilterBar");
        foreach (var filter in viewModel.Filters)
        {
            var chip = MobileElements.Chip(filter.Label, filter.IsSelected, filter.SelectCommand);
            chip.Classes.Add("MptMobileFilter");
            chip.Classes.Set("active", filter.IsSelected);
            filter.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MobileFilterViewModel.IsSelected))
                {
                    chip.Classes.Set("selected", filter.IsSelected);
                    chip.Classes.Set("active", filter.IsSelected);
                }
            };
            panel.Children.Add(chip);
        }

        var scroller = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroller.Classes.Add("MptMobileFilterScroller");
        return scroller;
    }

    private static Control BuildResults(MobileToolsViewModel viewModel)
    {
        var groups = MobileElements.Items(viewModel.Groups, Group);
        groups.Classes.Add("MptMobileToolGroups");

        var empty = MobileElements.EmptyContainer();
        var emptyTitle = MobileElements.EmptyTitle();
        emptyTitle.Bind(TextBlock.TextProperty, viewModel, nameof(MobileToolsViewModel.EmptyTitle));
        var emptyDetail = MobileElements.EmptyDetail();
        emptyDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileToolsViewModel.EmptyDetail));
        empty.Children.Add(emptyTitle);
        empty.Children.Add(emptyDetail);
        empty.Children.Add(MobileElements.Secondary("查看全部工具", viewModel.ClearSearchCommand));
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileToolsViewModel.IsEmpty));

        var error = MobileElements.Banner("", "MptMobileBannerError");
        var errorText = (TextBlock)error.Child!;
        errorText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileToolsViewModel.ErrorMessage));
        error.Bind(IsVisibleProperty, viewModel, nameof(MobileToolsViewModel.HasError));

        var summary = MobileElements.Caption("");
        summary.Bind(TextBlock.TextProperty, viewModel, nameof(MobileToolsViewModel.ResultSummary));

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(summary);
        stack.Children.Add(error);
        stack.Children.Add(groups);
        stack.Children.Add(empty);
        return stack;
    }

    private static Control Group(MobileToolGroupViewModel group)
    {
        var rows = MobileElements.Items(group.Items, Row);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(MobileElements.SectionTitle(group.Title));
        stack.Children.Add(MobileElements.Card(rows, "MptMobileListCard"));
        return stack;
    }

    private static Control Row(MobileToolItemViewModel item)
    {
        var row = MobileElements.ListRow(item.IconGlyph, item.Title, item.Description, item.StatusLabel, item.ShowDetailCommand, item.AutomationName);
        row.Classes.Add("MptMobileToolRow");
        var star = MobileElements.IconButton(item.FavoriteGlyph, item.FavoriteAutomationName, item.ToggleFavoriteCommand);
        star.Classes.Add("MptMobileStar");
        star.Bind(ContentControl.ContentProperty, item, nameof(MobileToolItemViewModel.FavoriteGlyph));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(row, 0);
        grid.Children.Add(row);
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);
        grid.Classes.Add("MptMobileRowWithAction");
        return grid;
    }
}
