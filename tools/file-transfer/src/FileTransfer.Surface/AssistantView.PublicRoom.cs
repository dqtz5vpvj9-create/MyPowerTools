using Avalonia.Controls;
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
        var body = MobileUi.Stack(12, MobileUi.Body("文件传输助手"),
            MobileUi.Caption("服务 · proxy.lixinrui000.cn"),
            MobileUi.Caption("同一服务的所有已授权用户可见。授权在本机完成，无需另一台设备或连接码。"),
            MobileUi.Caption(identity.PublicRoomState == "public" ? "已取得服务器授权" : "尚未加入公屏"));
        if (identity.AuthorizationError.Length > 0) body.Children.Add(MobileUi.Note(identity.AuthorizationError));
        void Action(string label, string action)
        {
            var button = MobileUi.PrimaryButton(label);
            button.Click += async (_, _) => await RunAsync(async () =>
            {
                button.IsEnabled = false;
                try { await ChangePublicRoomAsync(action); }
                finally { ShowPublicRoomSheet(); }
            });
            body.Children.Add(button);
        }
        if (identity.PublicRoomState != "public" || identity.AuthorizationError.Length > 0)
            Action("申请加入公屏", "join");
        else Action("退出公屏", "leave");
        if (identity.HasPrivateConversation) Action("返回原私人共享会话", "private");
        _sheetScroll.Content = body;
        OpenSheet();
    }
}
