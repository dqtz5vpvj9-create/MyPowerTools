using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace FileTransfer.Surface.Tests;

internal static class ConversationTestNavigation
{
    internal static void Open(Window window, TransferView view, string? name = null)
    {
        name ??= view.Core.Snapshot.Peers.FirstOrDefault(p => p.DeviceId == view.Conversation.SelectedTargetDeviceId)?.Name ?? view.Conversation.SelectedConversationName;
        if (window.Width < AssistantView.PhoneWidth && view.Conversation.ThreadPanel.IsEffectivelyVisible)
            view.Conversation.TryHandleBack();
        Settle(window);
        var button = view.GetLogicalDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "打开会话 " + name);
        Click(window, button);
    }
    internal static void ChooseShare(Window window, TransferView view, string name = "文件传输助手")
    {
        Settle(window);
        Click(window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "打开会话 " + name));
    }
    internal static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }
    internal static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }
}
