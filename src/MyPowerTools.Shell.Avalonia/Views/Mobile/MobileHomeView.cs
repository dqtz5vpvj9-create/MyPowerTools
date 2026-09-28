using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>常用 tab: greeting + avatar, the real 文件互传 hero, real devices, real favorites, real recent.</summary>
internal sealed class MobileHomeView : UserControl
{
    public MobileHomeView(MobileHomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");
        Content = MobileElements.Page(
            BuildHeader(viewModel),
            BuildHero(viewModel),
            BuildDeviceStrip(viewModel),
            BuildFavorites(viewModel),
            BuildRecent(viewModel));
    }

    private static Control BuildHeader(MobileHomeViewModel viewModel)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Classes.Add("MptMobileAppBar");

        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(MobileElements.Text("MY POWER TOOLS", "MptMobileMicro MptMobileMicroLabel"));
        var greeting = MobileElements.Text("", "MptMobileHello");
        greeting.Bind(TextBlock.TextProperty, viewModel, nameof(MobileHomeViewModel.Greeting));
        left.Children.Add(greeting);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var avatar = new Button { Command = viewModel.OpenSettingsCommand };
        avatar.Classes.Add("MptMobileAvatar");
        avatar.Bind(ContentControl.ContentProperty, viewModel, nameof(MobileHomeViewModel.AvatarText));
        AutomationProperties.SetName(avatar, "打开设置");
        Grid.SetColumn(avatar, 1);
        grid.Children.Add(avatar);
        return grid;
    }

    private static Control BuildHero(MobileHomeViewModel viewModel)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(MobileElements.Text("文件传输助手", "MptMobileHeroTitle"));
        stack.Children.Add(MobileElements.Text("文字和文件，在设备间顺手传递。", "MptMobileRowSubtitle"));
        var open = MobileElements.Primary("打开文件传输助手", viewModel.SendFileCommand, "打开文件传输助手");
        stack.Children.Add(open);
        return MobileElements.Card(stack, "MptMobileHero MptMobileHeroCard");
    }

    private static Control BuildDeviceStrip(MobileHomeViewModel viewModel)
    {
        var chips = MobileElements.Items(viewModel.Devices, Chip);
        chips.ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });

        var add = new Button { Content = "+ 添加设备", Command = viewModel.PairDeviceCommand };
        add.Classes.Add("MptMobileChip");
        AutomationProperties.SetName(add, "添加设备");

        var panel = new StackPanel { Spacing = 8 };
        panel.Classes.Add("MptMobileDeviceStrip");
        panel.Children.Add(chips);
        panel.Children.Add(add);

        var notice = MobileElements.Banner("", "MptMobileBannerQuiet");
        notice.Bind(IsVisibleProperty, viewModel, nameof(MobileHomeViewModel.HasDeviceNotice));
        var noticeText = (TextBlock)notice.Child!;
        noticeText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileHomeViewModel.DeviceNotice));

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(panel);
        stack.Children.Add(notice);
        return stack;
    }

    private static Control Chip(MobileDeviceChipViewModel chip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = chip.IconGlyph });
        content.Children.Add(new TextBlock { Text = chip.Name });
        var state = new TextBlock { Text = chip.StateLabel, VerticalAlignment = VerticalAlignment.Center };
        state.Classes.Add("MptMobileRowMeta");
        content.Children.Add(state);

        var button = new Button { Content = content, Command = chip.OpenCommand };
        button.Classes.Add("MptMobileChip");
        if (chip.IsOffline)
        {
            button.Classes.Add("offline");
        }

        if (chip.IsUnknown)
        {
            button.Classes.Add("unknown");
        }

        AutomationProperties.SetName(button, $"{chip.Name} {chip.StateLabel}");
        return button;
    }

    private static Control BuildFavorites(MobileHomeViewModel viewModel)
    {
        var tiles = MobileElements.Items(viewModel.FavoriteTools, Tile);
        tiles.ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Columns = 2 });

        var header = MobileElements.SectionHeader(
            "你的常用",
            MobileElements.TextButton("全部工具 \u2197", viewModel.BrowseToolsCommand, "查看全部工具"));

        var empty = new StackPanel { Spacing = 6 };
        empty.Children.Add(MobileElements.Caption(viewModel.FavoritesEmptyDetail));
        empty.Children.Add(MobileElements.TextButton("去工具库挑一个", viewModel.BrowseToolsCommand));
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileHomeViewModel.ShowsFavoritesEmpty));

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(header);
        stack.Children.Add(tiles);
        stack.Children.Add(empty);
        return stack;
    }

    private static Control Tile(MobileToolItemViewModel item)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(MobileElements.IconBox(item.IconGlyph));
        stack.Children.Add(MobileElements.Text(item.Title, "MptMobileTileTitle"));
        stack.Children.Add(MobileElements.Text(item.StatusLabel, "MptMobileTileDescription MptMobileTileDetail"));

        var button = new Button
        {
            Content = stack,
            Command = item.ShowDetailCommand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        button.Classes.Add("MptMobileTile");
        if (item.IsComputerTool)
        {
            button.Classes.Add("computer");
        }

        AutomationProperties.SetName(button, item.AutomationName);
        return button;
    }

    private static Control BuildRecent(MobileHomeViewModel viewModel)
    {
        var rows = MobileElements.Items(viewModel.Recent, Recent);
        var header = MobileElements.SectionHeader(
            "最近",
            MobileElements.TextButton("查看全部", viewModel.OpenActivityCommand, "查看全部动态"));

        var empty = MobileElements.Caption("还没有传输或工具记录。");
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileHomeViewModel.ShowsRecentEmpty));

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(header);
        stack.Children.Add(MobileElements.Card(rows, "MptMobileListCard"));
        stack.Children.Add(empty);
        return stack;
    }

    private static Control Recent(MobileRecentItemViewModel item) =>
        MobileElements.ListRow(item.IconGlyph, item.Title, item.Detail, item.Meta, item.OpenCommand);
}
