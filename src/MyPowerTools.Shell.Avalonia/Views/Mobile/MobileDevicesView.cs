using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>设备 tab: local receiving state, real peers, relay state and the honest empty state.</summary>
internal sealed class MobileDevicesView : UserControl
{
    public MobileDevicesView(MobileDevicesViewModel viewModel, IMobileNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigator);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");
        Content = MobileElements.Page(
            MobileElements.PageTitle(viewModel.Title),
            MobileElements.Body(viewModel.Subtitle),
            BuildLocalCard(viewModel),
            BuildNotice(viewModel),
            BuildError(viewModel),
            BuildPeers(viewModel),
            BuildControlDevices(viewModel),
            BuildRelay(viewModel),
            MobileElements.Caption("配对后的设备会被记住。你可以随时在设备详情中解除连接。"));
    }

    private static Control BuildLocalCard(MobileDevicesViewModel viewModel)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(MobileElements.IconBox("\u25A3"));

        var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var name = MobileElements.Text("", "MptMobileRowTitle");
        name.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.LocalName));
        var detail = MobileElements.Text("", "MptMobileRowSubtitle");
        detail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.LocalDetail));
        copy.Children.Add(name);
        copy.Children.Add(detail);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var code = MobileElements.TextButton("连接码", viewModel.ShowPairingCodeCommand, "显示本机连接码");
        code.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(code, 2);
        grid.Children.Add(code);
        return MobileElements.Card(grid, "MptMobileListCard");
    }

    private static Control BuildNotice(MobileDevicesViewModel viewModel)
    {
        var banner = MobileElements.Banner("", "MptMobileBannerQuiet");
        var text = (TextBlock)banner.Child!;
        text.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.Notice));
        banner.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.HasNotice));
        return banner;
    }

    private static Control BuildError(MobileDevicesViewModel viewModel)
    {
        var banner = MobileElements.Banner("", "MptMobileBannerError");
        var text = (TextBlock)banner.Child!;
        text.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.ErrorMessage));
        banner.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.HasError));
        return banner;
    }

    private static Control BuildPeers(MobileDevicesViewModel viewModel)
    {
        var rows = MobileElements.Items(viewModel.Peers, Peer);
        var list = MobileElements.Card(rows, "MptMobileListCard");
        list.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.HasPeers));

        var empty = MobileElements.EmptyContainer();
        var emptyTitle = MobileElements.EmptyTitle();
        emptyTitle.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.EmptyTitle));
        var emptyDetail = MobileElements.EmptyDetail();
        emptyDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.EmptyDetail));
        empty.Children.Add(emptyTitle);
        empty.Children.Add(emptyDetail);
        var pair = MobileElements.Primary("添加一台设备", viewModel.PairCommand, "添加一台设备");
        pair.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(pair);
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.IsEmpty));

        var add = MobileElements.Secondary("添加一台设备", viewModel.PairCommand, "添加一台设备");
        add.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.HasPeers));

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(MobileElements.SectionTitle("已配对"));
        stack.Children.Add(list);
        stack.Children.Add(empty);
        stack.Children.Add(add);
        return stack;
    }

    private static Control Peer(MobilePeerItemViewModel peer)
    {
        var row = MobileElements.ListRow(peer.IconGlyph, peer.Name, peer.StateDetail, peer.StateLabel, peer.OpenCommand, peer.Name);
        row.Classes.Add("MptMobilePeerRow");
        var check = MobileElements.TextButton("检查连接", peer.CheckCommand, $"检查{peer.Name}");
        check.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(row, 0);
        grid.Children.Add(row);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        grid.Classes.Add("MptMobileRowWithAction");
        return grid;
    }

    /// <summary>电脑工具控制 (G2): imported computers, or the honest connect action.</summary>
    private static Control BuildControlDevices(MobileDevicesViewModel viewModel)
    {
        var rows = MobileElements.Items(viewModel.ControlDevices, ControlDevice);
        var list = MobileElements.Card(rows, "MptMobileListCard");
        list.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.HasControlDevices));

        var empty = MobileElements.EmptyContainer();
        var detail = MobileElements.EmptyDetail();
        detail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.ControlEmptyDetail));
        empty.Children.Add(detail);
        var connect = MobileElements.Primary("连接电脑", viewModel.ConnectComputerCommand, "连接电脑");
        connect.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Children.Add(connect);
        empty.Bind(IsVisibleProperty, viewModel, nameof(MobileDevicesViewModel.ShowsControlEmpty));

        var refresh = MobileElements.TextButton("刷新", viewModel.ReloadControlDevicesCommand, "刷新电脑列表");
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(MobileElements.SectionHeader("电脑工具", refresh));
        section.Children.Add(list);
        section.Children.Add(empty);
        return section;
    }

    private static Control ControlDevice(MobileControlDeviceItemViewModel device) =>
        MobileElements.ListRow(device.IconGlyph, device.Name, device.Subtitle, device.StateLabel, device.OpenCommand, device.Name);

    private static Control BuildRelay(MobileDevicesViewModel viewModel)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(MobileElements.IconBox("\u2601"));

        var copy = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var title = MobileElements.Text("", "MptMobileRowTitle");
        title.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.RelaySummary));
        var detail = MobileElements.Text("", "MptMobileRowSubtitle");
        detail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDevicesViewModel.RelayDetail));
        copy.Children.Add(title);
        copy.Children.Add(detail);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var button = new Button
        {
            Content = grid,
            Command = viewModel.RelayCommand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add("MptMobileListRow");
        AutomationProperties.SetName(button, "文件同步");

        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(MobileElements.SectionTitle("文件同步"));
        section.Children.Add(MobileElements.Card(button, "MptMobileListCard"));
        return section;
    }
}
