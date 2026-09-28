using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>设备详情: the real record, an explicit reachability check, and the file-transfer surface.</summary>
internal sealed class MobileDeviceDetailView : UserControl
{
    public MobileDeviceDetailView(MobileDeviceDetailViewModel viewModel, IMobileNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigator);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Classes.Add("MptMobileSubnav");
        header.Children.Add(MobileElements.BackButton(new AsyncRelayCommand(() => navigator.GoBackPageAsync()), "设备详情"));

        var feature = new StackPanel { Spacing = 8 };
        feature.Classes.Add("MptMobileToolFeature");
        feature.Children.Add(MobileElements.IconBox("\u25A3", "MptMobileIconBoxLarge"));
        feature.Children.Add(MobileElements.PageTitle(viewModel.Name));

        var state = MobileElements.Text("", "MptMobileRowSubtitle");
        state.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDeviceDetailViewModel.StateDetail));
        feature.Children.Add(state);

        var address = MobileElements.Caption($"地址 · {viewModel.Address}");
        feature.Children.Add(address);

        var pill = MobileElements.Banner("", "MptMobileBannerQuiet");
        var pillText = (TextBlock)pill.Child!;
        pillText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileDeviceDetailViewModel.StateLabel));

        var send = MobileElements.Primary("发送文件", viewModel.SendFileCommand, "发送文件");
        var check = MobileElements.Secondary("检查连接", viewModel.CheckCommand, "检查连接");
        var permissions = MobileElements.ListRow("\u26E8", "允许的操作", "", "", viewModel.ShowActionsCommand, "允许的操作");
        var capability = MobileElements.Caption(viewModel.CapabilityLabel);

        var stack = new StackPanel { Spacing = 14 };
        stack.Children.Add(header);
        stack.Children.Add(feature);
        stack.Children.Add(pill);
        stack.Children.Add(send);
        stack.Children.Add(check);
        stack.Children.Add(MobileElements.Section("设备设置", MobileElements.Card(permissions, "MptMobileListCard")));
        stack.Children.Add(capability);
        Content = MobileElements.Page(stack);
        AutomationProperties.SetName(this, $"设备详情 {viewModel.Name}");
    }
}
