using Avalonia;
using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>设置 page, reached from the 常用 avatar; every row maps to a real preference or record.</summary>
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
        header.Children.Add(MobileElements.BackButton(new AsyncRelayCommand(() => navigator.GoBackPageAsync()), "设置"));

        var phone = MobileElements.ListRow("\u25A3", "", "", "连接码", viewModel.ShowPairingCodeCommand, "我的手机");
        var phoneTitle = MobileElements.Text("", "MptMobileRowTitle");
        phoneTitle.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.LocalName));
        var phoneDetail = MobileElements.Text("", "MptMobileRowSubtitle");
        phoneDetail.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.LocalDetail));
        ReplaceRowCopy(phone, phoneTitle, phoneDetail);

        var appearance = MobileElements.ListRow("\u25D0", "外观", "", "", viewModel.AppearanceCommand, "外观");
        BindRowSubtitle(appearance, viewModel, nameof(MobileSettingsViewModel.AppearanceLabel));

        var permissions = MobileElements.ListRow("\u26E8", "设备权限与审计", "查看已允许的操作与 Broker 记录", "", viewModel.PermissionsCommand, "设备权限与审计");
        var relay = MobileElements.ListRow("\u2601", "网盘中转", "", "", viewModel.RelayCommand, "网盘中转");
        BindRowSubtitle(relay, viewModel, nameof(MobileSettingsViewModel.RelaySummary));

        var notice = MobileElements.Banner("", "MptMobileBannerQuiet");
        var noticeText = (TextBlock)notice.Child!;
        noticeText.Bind(TextBlock.TextProperty, viewModel, nameof(MobileSettingsViewModel.DeviceNotice));
        notice.Bind(IsVisibleProperty, viewModel, nameof(MobileSettingsViewModel.HasDeviceNotice));

        var about = MobileElements.ListRow(null, "关于", viewModel.VersionText, "", viewModel.AboutCommand, "关于");

        Content = MobileElements.Page(
            header,
            MobileElements.PageTitle(viewModel.Title),
            MobileElements.Body(viewModel.Subtitle),
            notice,
            MobileElements.Section("我的手机", MobileElements.Card(phone, "MptMobileListCard")),
            MobileElements.Section("偏好", MobileElements.Card(appearance, "MptMobileListCard")),
            MobileElements.Section("连接与安全", MobileElements.ListCard(permissions, relay)),
            MobileElements.Section("关于", MobileElements.Card(about, "MptMobileListCard")),
            MobileElements.Caption("My Power Tools · 你的工具，按你的方式。"));
    }

    /// <summary>Rewrites the copy column of a row built by <see cref="MobileElements.ListRow"/>.</summary>
    private static void ReplaceRowCopy(Button row, TextBlock title, TextBlock subtitle)
    {
        if (((Button)row).Content is not Grid grid || grid.Children.Count < 2 || grid.Children[1] is not StackPanel copy)
        {
            return;
        }

        copy.Children.Clear();
        copy.Children.Add(title);
        copy.Children.Add(subtitle);
    }

    private static void BindRowSubtitle(Button row, object source, string path)
    {
        if (row.Content is not Grid grid || grid.Children.Count < 2 || grid.Children[1] is not StackPanel copy || copy.Children.Count < 2)
        {
            return;
        }

        ((TextBlock)copy.Children[1]).Bind(TextBlock.TextProperty, source, path);
    }
}
