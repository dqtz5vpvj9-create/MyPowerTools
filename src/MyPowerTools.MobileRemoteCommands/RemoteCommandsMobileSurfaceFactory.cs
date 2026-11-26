using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// Dotnet-surface factory resolved from the tool route's <c>assembly</c>+<c>type</c> fields in
/// <c>package/ui/tool.json</c> (<c>surface/MyPowerTools.MobileRemoteCommands.dll</c> +
/// <c>MyPowerTools.MobileRemoteCommands.RemoteCommandsMobileSurfaceFactory</c>). The Android CoreCLR host
/// loads this assembly dynamically and calls this method, so the phone page is catalogued and hosted
/// exactly like every other MyPowerTools dotnet surface.
/// </summary>
public sealed class RemoteCommandsMobileSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var view = new RemoteCommandsMobileView(context);
        context.Log(new MptSurfaceLogEntry("info", "远程命令（Android）页面已加载。", DateTimeOffset.Now));
        return view;
    }
}
