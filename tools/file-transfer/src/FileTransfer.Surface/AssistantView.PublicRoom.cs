using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MyPowerTools.AvaloniaSdk.Controls;

namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private async Task ChangePublicRoomAsync(string action)
    {
        await _core.PublicRoomAsync(action);
        if (action is "join" or "private")
        {
            await SwitchConversationAsync(SharedConversationKey, null, "文件传输助手");
            Sync();
        }
    }

    private void ShowPublicRoomSheet()
    {
        _sheetTitle.Text = "公屏与授权";
        var identity = _core.Snapshot.Identity;
        var authorized = identity.PublicRoomState == "public" && identity.AuthorizationError.Length == 0;

        var badge = MobileUi.IconBadge("MptMobileIconDevices", authorized ? "green" : null);
        badge.Width = 44;
        badge.Height = 44;
        badge.CornerRadius = new CornerRadius(13);
        var titleBlock = new TextBlock
        {
            Text = "文件传输助手",
            FontSize = 16,
            LineHeight = 22,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBlock.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
        var statusPill = MobileUi.Pill(authorized ? "已授权" : identity.PublicRoomState == "public-left" ? "已退出" : "未加入", offline: !authorized);
        statusPill.VerticalAlignment = VerticalAlignment.Center;
        var heroGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        heroGrid.Children.Add(badge);
        Grid.SetColumn(titleBlock, 1); heroGrid.Children.Add(titleBlock);
        Grid.SetColumn(statusPill, 2); heroGrid.Children.Add(statusPill);
        var heroCard = MobileUi.Card(heroGrid);
        heroCard.Padding = new Thickness(14, 12);
        heroCard.CornerRadius = new CornerRadius(16);

        var infoCard = MobileUi.ReceiptSurface(MobileUi.Stack(8,
            MobileUi.ReceiptRow("授权状态", identity.PublicRoomState == "public" ? "已取得服务器授权" : identity.PublicRoomState == "public-left" ? "已退出公屏" : "尚未加入公屏"),
            MobileUi.Caption("服务 · proxy.lixinrui000.cn"),
            MobileUi.Caption("同一服务的所有已授权用户可见。授权在本机完成，无需另一台设备或连接码。")));

        var body = MobileUi.Stack(12, heroCard, infoCard);
        if (identity.AuthorizationError.Length > 0)
            body.Children.Add(MobileUi.With(MobileUi.Notice(MobileUi.Caption(identity.AuthorizationError)), "warning"));

        void AddAction(Button button, string action)
        {
            button.Click += async (_, _) => await RunAsync(async () =>
            {
                button.IsEnabled = false;
                try
                {
                    await ChangePublicRoomAsync(action);
                    ShowPublicRoomSheet();
                }
                catch
                {
                    button.IsEnabled = true;
                    throw;
                }
            });
            body.Children.Add(button);
        }

        if (identity.PublicRoomState != "public" || identity.AuthorizationError.Length > 0)
        {
            AddAction(MobileUi.PrimaryButton("申请加入公屏"), "join");
            if (identity.HasPrivateConversation)
                AddAction(MobileUi.SecondaryButton("返回原私人共享会话"), "private");
        }
        else
        {
            if (identity.HasPrivateConversation)
                AddAction(MobileUi.SecondaryButton("返回原私人共享会话"), "private");
            var leave = MobileUi.QuietButton("退出公屏");
            leave.Bind(Button.ForegroundProperty, new DynamicResourceExtension("MptMobileWarningBrush"));
            AddAction(leave, "leave");
        }

        _sheetScroll.Content = body;
        OpenSheet();
    }
}
