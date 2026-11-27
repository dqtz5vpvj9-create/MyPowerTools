using Avalonia;
using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>设置 page, reached from the 常用 settings button; every row maps to a real preference or record.</summary>
internal sealed class MobileSettingsView : UserControl
{
    public MobileSettingsView(MobileSettingsViewModel viewModel, IMobileNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(navigator);
        DataContext = viewModel;
        Classes.Add("MptMobilePageRoot");

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        header.Classes.Add("MptMobileSubnav");
        header.Children.Add(MobileElements.BackButton(new AsyncRelayCommand(() => navigator.GoBackPageAsync()), "返回"));

        var phone = MobileElements.ListRow("phone", "", "", "连接码", viewModel.ShowPairingCodeCommand, "我的手机");
        var phoneTitle = MobileElements.Text("", "MptMobileRowTitle");
        phoneTitle.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.LocalName));
        var phoneDetail = MobileElements.Text("", "MptMobileRowSubtitle");
        phoneDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.LocalDetail));
        ReplaceRowCopy(phone, phoneTitle, phoneDetail);

        var appearance = MobileElements.ListRow("appearance", "外观", "", "", viewModel.AppearanceCommand, "外观");
        BindRowSubtitle(appearance, viewModel, nameof(MobileSettingsViewModel.AppearanceLabel));

        var permissions = MobileElements.ListRow("shield", "设备权限与审计", "查看已允许的操作与 Broker 记录", "", viewModel.PermissionsCommand, "设备权限与审计");
        var relay = MobileElements.ListRow("cloud", "文件同步", "", "", viewModel.RelayCommand, "文件同步");
        BindRowSubtitle(relay, viewModel, nameof(MobileSettingsViewModel.RelaySummary));

        var notice = MobileElements.Banner("", "MptMobileBannerQuiet");
        var noticeText = (TextBlock)notice.Child!;
        noticeText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.DeviceNotice));
        notice.Bind(IsVisibleProperty, viewModel, nameof(MobileSettingsViewModel.HasDeviceNotice));

        var moduleUpdates = MobileElements.ListRow("update", "模块热更新", "", "检查", viewModel.ModuleUpdatesCommand, "模块热更新");
        BindRowSubtitle(moduleUpdates, viewModel, nameof(MobileSettingsViewModel.ModuleUpdatesSummary));
        var about = MobileElements.ListRow("info", "关于", viewModel.VersionText, "", viewModel.AboutCommand, "关于");

        Content = MobileElements.Page(
            header,
            MobileElements.PageTitle(viewModel.Title),
            notice,
            MobileElements.Section("我的手机", MobileElements.Card(phone, "MptMobileListCard")),
            MobileElements.Section("外观与偏好", MobileElements.Card(appearance, "MptMobileListCard")),
            MobileElements.Section("连接与安全", MobileElements.ListCard(permissions, relay)),
            MobileElements.Section("更新与关于", MobileElements.ListCard(moduleUpdates, about)),
            MobileElements.Caption("My Power Tools · 你的工具，按你的方式。"));
    }

    private static void ReplaceRowCopy(Button row, TextBlock title, TextBlock detail)
    {
        var grid = (Grid)row.Content!;
        var copy = (StackPanel)grid.Children[1];
        copy.Children.Clear();
        copy.Children.Add(title);
        copy.Children.Add(detail);
    }

    private static void BindRowSubtitle(Button row, MobileSettingsViewModel viewModel, string property)
    {
        var grid = (Grid)row.Content!;
        var copy = (StackPanel)grid.Children[1];
        var subtitle = MobileElements.Text("", "MptMobileRowSubtitle");
        subtitle.Bind(TextBlock.TextProperty, viewModel, property);
        copy.Children.Add(subtitle);
    }
}
