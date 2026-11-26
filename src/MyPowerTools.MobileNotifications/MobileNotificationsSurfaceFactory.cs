using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// Dotnet-surface factory resolved from the tool route's <c>assembly</c>+<c>type</c> fields. The
/// Android module package points at this type, so the phone page is loaded exactly like any other
/// dynamically catalogued MyPowerTools surface.
/// </summary>
public sealed class MobileNotificationsSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = new MobileNotificationsView(context);
        context.Log(new MptSurfaceLogEntry("info", "远程通知（Android）页面已加载。", DateTimeOffset.Now));
        return view;
    }
}
